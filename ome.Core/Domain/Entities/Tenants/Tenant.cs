using ome.Core.Domain.Entities.Common;

namespace ome.Core.Domain.Entities.Tenants;

/// <summary>
/// Repräsentiert einen Mandanten/Tenant im System
/// </summary>
public class Tenant: BaseEntity, IAggregateRoot {
    public string Name { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string KeycloakGroupId { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public string? ConnectionString { get; set; }
}