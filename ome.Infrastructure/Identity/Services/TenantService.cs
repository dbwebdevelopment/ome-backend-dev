using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ome.Core.Domain.Entities.Tenants;
using ome.Core.Interfaces.Services;
using ome.Infrastructure.Persistence.Context;

namespace ome.Infrastructure.Identity.Services;

public class TenantService(
    IHttpContextAccessor httpContextAccessor,
    IMemoryCache memoryCache,
    IServiceProvider serviceProvider,
    ILogger<TenantService> logger)
    : ITenantService {

    private Guid _currentTenantId = Guid.Empty;
    private const string TenantIdClaimType = "tenant_id";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public Guid GetCurrentTenantId() {
        if (_currentTenantId != Guid.Empty)
            return _currentTenantId;

        try
        {
            var httpContext = httpContextAccessor.HttpContext;

            if (httpContext == null)
                return Guid.Empty;

            var tenantClaim = httpContext.User.FindFirst(TenantIdClaimType);

            if (tenantClaim != null && Guid.TryParse(tenantClaim.Value, out var tenantId))
            {
                _currentTenantId = tenantId;
                return tenantId;
            }

            var tenantIdFromQuery = httpContext.Request.Query["tenantId"].ToString();

            if (!string.IsNullOrEmpty(tenantIdFromQuery) && Guid.TryParse(tenantIdFromQuery, out tenantId))
            {
                _currentTenantId = tenantId;
                return tenantId;
            }

            var tenantIdFromHeader = httpContext.Request.Headers["X-TenantId"].ToString();

            if (string.IsNullOrEmpty(tenantIdFromHeader) || !Guid.TryParse(tenantIdFromHeader, out tenantId))
            {
                return Guid.Empty;
            }

            _currentTenantId = tenantId;
            return tenantId;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fehler beim Ermitteln der aktuellen TenantId");
            return Guid.Empty;
        }
    }

    public void SetCurrentTenantId(Guid tenantId) {
        _currentTenantId = tenantId;
    }

    public async Task<Tenant?> GetCurrentTenantAsync(CancellationToken cancellationToken = default) {
        var tenantId = GetCurrentTenantId();

        if (tenantId == Guid.Empty)
            return null;

        var cacheKey = $"Tenant_{tenantId}";

        if (memoryCache.TryGetValue(cacheKey, out Tenant? tenant))
            return tenant;

        using var scope = serviceProvider.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.IsActive && !t.IsDeleted, cancellationToken);

        if (tenant != null)
        {
            memoryCache.Set(cacheKey, tenant, CacheDuration);
        }

        return tenant;
    }

    public async Task<string?> GetConnectionStringAsync(Guid tenantId, CancellationToken cancellationToken = default) {
        var cacheKey = $"TenantConnectionString_{tenantId}";

        if (memoryCache.TryGetValue(cacheKey, out string? connectionString))
            return connectionString;

        using var scope = serviceProvider.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.IsActive && !t.IsDeleted, cancellationToken);

        if (tenant == null || string.IsNullOrEmpty(tenant.ConnectionString))
        {
            return null;
        }

        memoryCache.Set(cacheKey, tenant.ConnectionString, CacheDuration);
        return tenant.ConnectionString;
    }

    public async Task<bool> TenantExistsAsync(Guid tenantId, CancellationToken cancellationToken = default) {
        var cacheKey = $"TenantExists_{tenantId}";

        if (memoryCache.TryGetValue(cacheKey, out bool exists))
            return exists;

        using (var scope = serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            exists = await dbContext.Tenants
                .AsNoTracking()
                .AnyAsync(t => t.Id == tenantId && t.IsActive && !t.IsDeleted, cancellationToken);
        }

        memoryCache.Set(cacheKey, exists, CacheDuration);

        return exists;
    }
}