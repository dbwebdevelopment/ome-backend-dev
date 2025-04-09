using ome.Core.Domain.Entities.Tenants;

namespace ome.Core.Interfaces.Services;

/// <summary>
/// Service zum Zugriff auf Tenant-Informationen
/// </summary>
public interface ITenantService {
    Guid GetCurrentTenantId();
    void SetCurrentTenantId(Guid tenantId);
    Task<Tenant?> GetCurrentTenantAsync(CancellationToken cancellationToken = default);
    Task<string?> GetConnectionStringAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<bool> TenantExistsAsync(Guid tenantId, CancellationToken cancellationToken = default);
}