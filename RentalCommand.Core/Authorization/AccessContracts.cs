using RentalCommand.Core.Enums;
using RentalCommand.Core.Entities;

namespace RentalCommand.Core.Authorization;

/// <summary>One authoritative database projection for the selected signed-in access context.</summary>
public sealed record ActiveAccessContext(
    Guid SessionId,
    int UserId,
    int AccessContextId,
    int PortfolioId,
    long AccessRevision,
    WorkspaceExperience? LastAuthorizedExperience,
    int? WorkspaceMembershipId,
    WorkspaceExperience? DefaultExperience);

public interface IActiveAccessContextResolver
{
    Task<ActiveAccessContext> ResolveAsync(
        Guid sessionId,
        int userId,
        int accessContextId,
        long presentedAccessRevision,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}

public interface IWorkspaceAuthorizationEvaluator
{
    Task<bool> HasCapabilityAsync(
        ActiveAccessContext accessContext,
        string capabilityKey,
        WorkspaceAuthorizationTarget? target,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Proves that one effective assignment grants any requested capability and covers the typed
    /// target. The evaluator performs the decision as one SQL statement; callers must not loop over
    /// capability keys and accidentally split the proof across queries.
    /// </summary>
    Task<bool> HasAnyCapabilityAsync(
        ActiveAccessContext accessContext,
        IReadOnlyCollection<string> capabilityKeys,
        WorkspaceAuthorizationTarget? target,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Required typed target for every capability decision. A bare policy invocation has no target and
/// therefore fails closed; callers cannot reinterpret a property capability as workspace-wide.
/// </summary>
public abstract record WorkspaceAuthorizationTarget(int PortfolioId);

public sealed record WorkspaceCapabilityAuthorizationTarget(int PortfolioId)
    : WorkspaceAuthorizationTarget(PortfolioId);

public sealed record PropertyCapabilityAuthorizationTarget(int PortfolioId, int PropertyId)
    : WorkspaceAuthorizationTarget(PortfolioId);

/// <summary>
/// A property-target capability that is only valid when the same effective assignment covers every
/// property. Use this for workspace-wide commands that cannot be attached to one property, such as
/// creating an unattached tenant or rotating the public application link.
/// </summary>
public sealed record PortfolioWidePropertyCapabilityAuthorizationTarget(int PortfolioId)
    : WorkspaceAuthorizationTarget(PortfolioId);

/// <summary>
/// A Unit-backed property capability target. The evaluator resolves the Unit's owning Property and
/// proves both the requested capability and that Property's scope on the same assignment in SQL.
/// </summary>
public sealed record UnitCapabilityAuthorizationTarget(int PortfolioId, int UnitId)
    : WorkspaceAuthorizationTarget(PortfolioId);

/// <summary>
/// An application-backed property capability target. Selected-property assignments require the
/// application's Property; an unattached application requires an AllProperties assignment.
/// </summary>
public sealed record RentalApplicationCapabilityAuthorizationTarget(int PortfolioId, int ApplicationId)
    : WorkspaceAuthorizationTarget(PortfolioId);

/// <summary>
/// A Tenant-backed property capability target. The evaluator resolves property scope through the
/// canonical LeaseManagementParty relationship; unattached tenants require an AllProperties assignment.
/// </summary>
public sealed record TenantCapabilityAuthorizationTarget(int PortfolioId, int TenantId)
    : WorkspaceAuthorizationTarget(PortfolioId);

/// <summary>Canonical property scope resolved through the relationship's owning Property.</summary>
public sealed record LeaseManagementCapabilityAuthorizationTarget(int PortfolioId, int LeaseManagementId)
    : WorkspaceAuthorizationTarget(PortfolioId);

/// <summary>
/// Exact Agreement target. Both the Agreement and its parent relationship must belong to the
/// workspace and the same property-scoped assignment that grants the requested capability.
/// </summary>
public sealed record LeaseAgreementCapabilityAuthorizationTarget(int PortfolioId, int LeaseAgreementId)
    : WorkspaceAuthorizationTarget(PortfolioId);

/// <summary>
/// Exact Addendum target. Both the Addendum and its parent relationship must belong to the
/// workspace and the same property-scoped assignment that grants the requested capability.
/// </summary>
public sealed record LeaseAddendumCapabilityAuthorizationTarget(int PortfolioId, int LeaseAddendumId)
    : WorkspaceAuthorizationTarget(PortfolioId);

public sealed record WorkOrderCapabilityAuthorizationTarget(int PortfolioId, int WorkOrderId)
    : WorkspaceAuthorizationTarget(PortfolioId);

public interface IMembershipAssignmentScopeValidator
{
    Task ValidateAsync(
        IReadOnlyCollection<int> assignmentIds,
        CancellationToken cancellationToken = default);
}

public sealed class AccessAuthorityMutationException : InvalidOperationException
{
    public AccessAuthorityMutationException(string message) : base(message) { }
}

public sealed class AccessContextUnavailableException : UnauthorizedAccessException
{
    public AccessContextUnavailableException() : base("The selected access context is not active for this session.") { }
}

public sealed class StaleAccessRevisionException : UnauthorizedAccessException
{
    public StaleAccessRevisionException(long presentedRevision, long currentRevision)
        : base("The access context changed. Refresh the access envelope before retrying the mutation.")
    {
        PresentedRevision = presentedRevision;
        CurrentRevision = currentRevision;
    }

    public long PresentedRevision { get; }
    public long CurrentRevision { get; }
}

/// <summary>
/// Client shell envelope. It describes identity, experiences, assignments, and navigation affordances,
/// but it is never authoritative for record access; every record query is reauthorized in SQL.
/// </summary>
public sealed record AccessEnvelope(
    AccessIdentitySummary Identity,
    SelectedAccessContextSummary SelectedContext,
    WorkspaceExperience DefaultExperience,
    IReadOnlyList<WorkspaceExperience> AvailableExperiences,
    IReadOnlyList<AssignmentSummary> Assignments,
    IReadOnlyList<NavigationCapabilitySummary> Navigation);

public sealed record AccessIdentitySummary(int UserId, string DisplayName, string? Email);

public sealed record SelectedAccessContextSummary(
    int AccessContextId,
    int PortfolioId,
    string WorkspaceName,
    long AccessRevision,
    WorkspaceExperience ActiveExperience);

public sealed record AssignmentSummary(
    int AssignmentId,
    string RoleProfileKey,
    string RoleProfileName,
    MembershipRoleAssignmentStatus Status,
    AssignmentScopeSummary Scope);

public sealed record AssignmentScopeSummary(
    MembershipRoleAssignmentScopeKind Kind,
    int SelectedPropertyCount,
    IReadOnlyList<ScopedPropertySummary> SelectedProperties);

public sealed record ScopedPropertySummary(int PropertyId, string Name);

public sealed record NavigationCapabilitySummary(
    WorkspaceExperience Experience,
    IReadOnlyList<string> CapabilityKeys);

/// <summary>
/// One effective login option. TotalEffectiveContexts is projected by SQL so callers can auto-select
/// exactly one context or require an opaque selection challenge without loading unfiltered rows.
/// </summary>
public sealed record EffectiveAccessContextOption(
    int AccessContextId,
    int PortfolioId,
    string WorkspaceName,
    long AccessRevision,
    WorkspaceExperience DefaultExperience,
    int TotalEffectiveContexts);

public interface IEffectiveAccessContextSelectionQuery
{
    Task<IReadOnlyList<EffectiveAccessContextOption>> ListAsync(
        int userId,
        int? selectedAccessContextId,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}

public interface IAccessEnvelopeQuery
{
    Task<AccessEnvelope?> GetAsync(
        Guid sessionId,
        int userId,
        int accessContextId,
        long accessRevision,
        DateTime effectiveAtUtc,
        CancellationToken cancellationToken = default);
}
