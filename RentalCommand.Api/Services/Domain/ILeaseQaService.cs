using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface ILeaseQaService
{
    Task<LeaseQuestionResponse?> AskManagementAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        string question,
        CancellationToken ct = default);

    Task<LeaseQuestionResponse?> AskAsync(
        int portfolioId, int leaseManagementId, string question, CancellationToken ct = default);
}
