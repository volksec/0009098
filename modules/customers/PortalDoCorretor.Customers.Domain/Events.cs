using PortalDoCorretor.SharedKernel.Domain;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.Customers.Domain;

public sealed record CustomerRegistered(TenantId TenantId, CustomerId CustomerId, CustomerKind Kind)
    : DomainEvent(TenantId);

public sealed record CustomerUpdated(TenantId TenantId, CustomerId CustomerId, string Section)
    : DomainEvent(TenantId);

public sealed record CustomerBlocked(TenantId TenantId, CustomerId CustomerId, string Reason)
    : DomainEvent(TenantId);

public sealed record CustomerDeleted(
    TenantId TenantId, CustomerId CustomerId, string Reason, Guid BatchId) : DomainEvent(TenantId);

public sealed record CustomerRestored(TenantId TenantId, CustomerId CustomerId)
    : DomainEvent(TenantId);

public sealed record ConsentGranted(TenantId TenantId, CustomerId CustomerId, AccessPurpose Purpose)
    : DomainEvent(TenantId);

public sealed record ConsentRevoked(TenantId TenantId, CustomerId CustomerId, AccessPurpose Purpose)
    : DomainEvent(TenantId);

public sealed record AssetRegistered(
    TenantId TenantId, CustomerId CustomerId, AssetId AssetId, AssetKind Kind)
    : DomainEvent(TenantId);
