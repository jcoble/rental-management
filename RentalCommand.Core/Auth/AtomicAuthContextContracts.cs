namespace RentalCommand.Core.Auth;

public sealed record AtomicWorkspaceInvitationActivation(
    int PortfolioId,
    int WorkspaceMembershipId,
    int AccessContextId,
    int InvitedUserId,
    DateTime AcceptedAtUtc);

public sealed record AtomicEffectiveLoginContext(
    int AccessContextId,
    int PortfolioId,
    long AccessRevision,
    int TotalEffectiveContexts);
