using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using ome.Core.Domain.Entities.Common;
using ome.Core.Domain.Entities.Tenants;
using ome.Core.Domain.Entities.Users;
using ome.Core.Interfaces.Services;
using ome.Infrastructure.Persistence.Interceptors;

namespace ome.Infrastructure.Persistence.Context;

public class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    ITenantService tenantService,
    ICurrentUserService currentUserService,
    ILogger<ApplicationDbContext> logger,
    AuditSaveChangesInterceptor auditInterceptor,
    TenantSaveChangesInterceptor tenantInterceptor)
    : DbContext(options) {
    private readonly ICurrentUserService? _currentUserService = currentUserService;
    private readonly ITenantService? _tenantService = tenantService;

    public DbSet<Tenant> Tenants { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<UserRole> UserRoles { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) {
        optionsBuilder
            .LogTo(message => logger.LogDebug(message), [
                RelationalEventId.CommandExecuted
            ])
            .AddInterceptors(auditInterceptor)
            .AddInterceptors(tenantInterceptor);

        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);

        // Anwenden der Entity Konfigurationen
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        // Globaler Query Filter für Multi-Tenancy bei allen TenantEntity-Ableitungen
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(e => typeof(TenantEntity).IsAssignableFrom(e.ClrType)))
        {
            // Einfügen einer Methode zum Filtern nach Tenant
            var method = typeof(ApplicationDbContext).GetMethod(
                nameof(ApplyTenantFilter),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var genericMethod = method!.MakeGenericMethod(entityType.ClrType);
            genericMethod.Invoke(this, [modelBuilder]);
        }

        // Globaler Query Filter für Soft Delete bei allen Entities
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(e => typeof(BaseEntity).IsAssignableFrom(e.ClrType)))
        {
            // Einfügen einer Methode zum Filtern nach IsDeleted
            var method = typeof(ApplicationDbContext).GetMethod(
                nameof(ApplySoftDeleteFilter),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var genericMethod = method!.MakeGenericMethod(entityType.ClrType);
            genericMethod.Invoke(this, [modelBuilder]);
        }
    }

    // Private Methode zum Anwenden des Tenant-Filters
    private void ApplyTenantFilter<T>(ModelBuilder modelBuilder) where T : TenantEntity {
        modelBuilder.Entity<T>().HasQueryFilter(e =>
            !e.IsDeleted && e.TenantId == _tenantService!.GetCurrentTenantId());
    }

    // Private Methode zum Anwenden des Soft-Delete-Filters
    private void ApplySoftDeleteFilter<T>(ModelBuilder modelBuilder) where T : BaseEntity {
        modelBuilder.Entity<T>().HasQueryFilter(e => !e.IsDeleted);
    }
}