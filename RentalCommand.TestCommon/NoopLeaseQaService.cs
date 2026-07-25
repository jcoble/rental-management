using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.TestCommon;

/// <summary>
/// No-op <see cref="ILeaseQaService"/> for tests that construct <c>PortalService</c> but do not
/// exercise the tenant lease Q&amp;A path. Returns no answer.
/// </summary>
public sealed class NoopLeaseQaService : ILeaseQaService
{
    public Task<LeaseQuestionResponse?> AskManagementAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        string question,
        CancellationToken ct = default)
        => Task.FromResult<LeaseQuestionResponse?>(null);

    public Task<LeaseQuestionResponse?> AskAsync(
        int portfolioId, int leaseId, string question, CancellationToken ct = default)
        => Task.FromResult<LeaseQuestionResponse?>(null);
}
