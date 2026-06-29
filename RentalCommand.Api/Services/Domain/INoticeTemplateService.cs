using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface INoticeTemplateService
{
    Task<IReadOnlyList<NoticeTemplateResponse>> ListAsync(int portfolioId, CancellationToken ct = default);
    Task<NoticeTemplateResponse> GetAsync(int portfolioId, string noticeType, CancellationToken ct = default);
    Task<NoticeTemplateResponse> UpsertAsync(int portfolioId, string noticeType, UpsertNoticeTemplateRequest request, CancellationToken ct = default);
}
