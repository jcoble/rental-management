using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public class LeaseQaService : ILeaseQaService
{
    private readonly RentalCommandDbContext _db;
    private readonly ILlmProvider _llm;
    private readonly ILeaseManagementQueryService _leaseManagements;

    public LeaseQaService(
        RentalCommandDbContext db,
        ILlmProvider llm,
        ILeaseManagementQueryService leaseManagements)
    {
        _db = db;
        _llm = llm;
        _leaseManagements = leaseManagements;
    }

    /// <summary>
    /// Staff-facing Q&amp;A. Agreement facts and current capability/property scope are admitted by
    /// one SQL statement; this path never performs an authorization precheck followed by a broader
    /// Agreement read.
    /// </summary>
    public async Task<LeaseQuestionResponse?> AskManagementAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        string question,
        CancellationToken ct = default)
    {
        question = question.Trim();
        if (question.Length == 0) return null;

        var agreement = await _leaseManagements.GetLeaseQaAgreementAsync(
            access, leaseManagementId, ct);
        return agreement is null
            ? null
            : await AnswerAsync(agreement, question, ct);
    }

    public async Task<LeaseQuestionResponse?> AskAsync(
        int portfolioId,
        int leaseManagementId,
        string question,
        CancellationToken ct = default)
    {
        question = question.Trim();
        if (question.Length == 0) return null;

        var agreement = await BuildGoverningAgreementQuery(portfolioId, leaseManagementId)
            .SingleOrDefaultAsync(ct);
        if (agreement == null) return null;

        return await AnswerAsync(new LeaseQaAgreementFacts(
            agreement.LeaseAgreementId,
            agreement.LeaseManagementId,
            agreement.AgreementNumber,
            agreement.TermStartOn,
            agreement.TermEndOn,
            agreement.BaseRentAmount,
            agreement.SecurityDepositObligation,
            agreement.LateFeeAmount,
            agreement.RentDueDay,
            agreement.TermsPayload,
            agreement.ExecutedStoredFileId,
            agreement.ExecutedFileName), question, ct);
    }

    private async Task<LeaseQuestionResponse> AnswerAsync(
        LeaseQaAgreementFacts agreement,
        string question,
        CancellationToken ct)
    {
        var sources = new List<string>
        {
            $"Agreement number: {agreement.AgreementNumber}",
            agreement.TermEndOn.HasValue
                ? $"Dates: {agreement.TermStartOn:MMMM d, yyyy} through {agreement.TermEndOn:MMMM d, yyyy}"
                : $"Dates: month-to-month beginning {agreement.TermStartOn:MMMM d, yyyy}",
            $"Monthly rent: {agreement.BaseRentAmount:C}",
            $"Security deposit: {agreement.SecurityDepositObligation:C}",
            $"Late fee: {agreement.LateFeeAmount:C}",
            $"Rent due day: {agreement.RentDueDay}",
            $"Executed document: {agreement.ExecutedFileName} (stored file {agreement.ExecutedStoredFileId})",
        };
        if (!string.IsNullOrWhiteSpace(agreement.TermsPayload) && agreement.TermsPayload != "{}")
        {
            sources.Add($"Agreement terms: {agreement.TermsPayload}");
        }

        var fallback = BuildFallbackAnswer(question, sources);

        try
        {
            var prompt = "Answer this tenant lease question using only the lease facts below. "
                       + "If the lease facts do not answer it, say that the lease does not specify and suggest asking the landlord. "
                       + "Keep the answer short and plain-English.\n\n"
                       + $"Lease facts:\n- {string.Join("\n- ", sources)}\n\n"
                       + $"Question: {question}";
            var answer = (await _llm.ChatAsync(prompt, ct)).Trim();
            if (!string.IsNullOrWhiteSpace(answer))
            {
                return new LeaseQuestionResponse
                {
                    Answer = answer,
                    LlmEnhanced = true,
                    Sources = sources
                };
            }
        }
        catch
        {
            // Deterministic fallback keeps the feature useful when no LLM key is configured.
        }

        return new LeaseQuestionResponse
        {
            Answer = fallback,
            LlmEnhanced = false,
            Sources = sources
        };
    }

    private static string BuildFallbackAnswer(string question, IReadOnlyList<string> sources)
    {
        var q = question.ToLowerInvariant();
        if (q.Contains("rent") || q.Contains("due") || q.Contains("pay"))
        {
            return FindSource(sources, "Monthly rent", "Rent due day");
        }

        if (q.Contains("deposit") || q.Contains("security"))
        {
            return FindSource(sources, "Security deposit");
        }

        if (q.Contains("late") || q.Contains("fee"))
        {
            return FindSource(sources, "Late fee");
        }

        if (q.Contains("end") || q.Contains("expire") || q.Contains("move out") || q.Contains("move-out"))
        {
            return FindSource(sources, "Dates");
        }

        if (q.Contains("dog") || q.Contains("cat") || q.Contains("pet") || q.Contains("smok") || q.Contains("parking"))
        {
            var notes = sources.FirstOrDefault(s => s.StartsWith("Agreement terms:", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(notes))
            {
                return notes;
            }
            return "The stored lease fields do not specify that. Ask the landlord to confirm.";
        }

        return "I can answer from the stored lease dates, rent, security deposit, late fee, rent due day, and notes. The stored lease does not specify this detail.";
    }

    private static string FindSource(IReadOnlyList<string> sources, params string[] prefixes)
    {
        var matches = sources
            .Where(s => prefixes.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return matches.Count == 0
            ? "The stored lease fields do not specify that."
            : string.Join(" ", matches);
    }

    internal IQueryable<GoverningAgreementReadRow> BuildGoverningAgreementQuery(
        int portfolioId,
        int leaseManagementId) =>
        from status in _db.LeaseAgreementStatusProjections.AsNoTracking()
        join agreement in _db.LeaseAgreements.AsNoTracking()
            on new { status.PortfolioId, AgreementId = status.AgreementId }
            equals new { agreement.PortfolioId, AgreementId = agreement.Id }
        where status.PortfolioId == portfolioId
            && status.LeaseManagementId == leaseManagementId
            && status.IsGoverning
            && agreement.FullyExecutedAtUtc != null
            && agreement.VoidedAtUtc == null
            && agreement.ExecutedArtifact != null
            && agreement.ExecutedArtifact.ArtifactKind == LegalDocumentArtifactKind.ExecutedAgreement
            && agreement.ExecutedArtifact.StoredFile != null
            && agreement.ExecutedArtifact.StoredFile.PortfolioId == portfolioId
            && agreement.ExecutedArtifact.StoredFile.DeletedAt == null
        select new GoverningAgreementReadRow(
            agreement.Id,
            agreement.LeaseManagementId,
            agreement.AgreementNumber,
            agreement.TermStartOn,
            agreement.TermEndOn,
            agreement.BaseRentAmount,
            agreement.SecurityDepositObligation,
            agreement.LateFeeAmount,
            agreement.RentDueDay,
            agreement.TermsPayload,
            agreement.ExecutedArtifact!.StoredFileId,
            agreement.ExecutedArtifact.StoredFile!.FileName);

    internal sealed record GoverningAgreementReadRow(
        int LeaseAgreementId,
        int LeaseManagementId,
        string AgreementNumber,
        DateOnly TermStartOn,
        DateOnly? TermEndOn,
        decimal BaseRentAmount,
        decimal SecurityDepositObligation,
        decimal LateFeeAmount,
        short RentDueDay,
        string TermsPayload,
        int ExecutedStoredFileId,
        string ExecutedFileName);
}
