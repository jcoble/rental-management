using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface ILeaseQaService
{
    Task<LeaseQuestionResponse?> AskAsync(int portfolioId, int leaseId, string question, CancellationToken ct = default);
}
