using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public class LeaseQaService : ILeaseQaService
{
    private readonly RentalCommandDbContext _db;
    private readonly ILlmProvider _llm;

    public LeaseQaService(RentalCommandDbContext db, ILlmProvider llm)
    {
        _db = db;
        _llm = llm;
    }

    public async Task<LeaseQuestionResponse?> AskAsync(
        int portfolioId,
        int leaseId,
        string question,
        CancellationToken ct = default)
    {
        question = question.Trim();
        if (question.Length == 0) return null;

        var lease = await _db.Leases
            .AsNoTracking()
            .Include(l => l.Tenant)
            .Include(l => l.Property)
            .Include(l => l.Unit)
            .FirstOrDefaultAsync(l => l.Id == leaseId && l.PortfolioId == portfolioId, ct);
        if (lease == null) return null;

        var sources = new List<string>
        {
            $"Lease number: {lease.LeaseNumber}",
            $"Dates: {lease.StartDate:MMMM d, yyyy} through {lease.EndDate:MMMM d, yyyy}",
            $"Monthly rent: {lease.MonthlyRent:C}",
            $"Security deposit: {lease.SecurityDeposit:C}",
            $"Late fee: {lease.LateFeeAmount:C}",
            $"Rent due day: {lease.RentDueDay}",
        };
        if (!string.IsNullOrWhiteSpace(lease.Notes))
        {
            sources.Add($"Lease notes: {lease.Notes}");
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
            var notes = sources.FirstOrDefault(s => s.StartsWith("Lease notes:", StringComparison.OrdinalIgnoreCase));
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
}
