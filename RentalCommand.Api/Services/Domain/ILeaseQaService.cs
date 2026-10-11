using RentalCommand.Api.DTOs;

using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

public interface ILeaseQaService
{
    Task<LeaseQuestionResponse?> AskManagementAsync(
        WorkspaceReadScope access,
        int leaseManagementId,
        string question,
        CancellationToken ct = default);

    Task<LeaseQuestionResponse?> AskAsync(
        int portfolioId, int leaseManagementId, string question, CancellationToken ct = default);
}
