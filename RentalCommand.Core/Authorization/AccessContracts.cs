using RentalCommand.Core.Enums;

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
        int? propertyId,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional resource passed to the dynamic capability policy for property-scoped checks.</summary>
public interface ICapabilityAuthorizationResource
{
    int PortfolioId { get; }
    int? PropertyId { get; }
}

public sealed record PropertyCapabilityAuthorizationResource(int PortfolioId, int PropertyId)
    : ICapabilityAuthorizationResource
{
    int? ICapabilityAuthorizationResource.PropertyId => PropertyId;
}

public interface IMembershipAssignmentScopeValidator
{
    Task ValidateAsync(
        int assignmentId,
        MembershipRoleAssignmentScopeKind scopeKind,
        CancellationToken cancellationToken = default);
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
