using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

/// <summary>One SQL projection for all effective login contexts belonging to a verified user.</summary>
public sealed class EffectiveAccessContextSelectionQuery : IEffectiveAccessContextSelectionQuery
{
    private readonly RentalCommandDbContext _db;

    public EffectiveAccessContextSelectionQuery(RentalCommandDbContext db) => _db = db;

    public async Task<IReadOnlyList<EffectiveAccessContextOption>> ListAsync(
        int userId,
        int? selectedAccessContextId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            return Array.Empty<EffectiveAccessContextOption>();
        }

        var rows = await _db.Database.SqlQuery<EffectiveAccessContextOptionRow>($"""
                WITH security_clock AS MATERIALIZED (
                    SELECT clock_timestamp() AS "UtcNow"
                )
                SELECT option.*
                FROM security_clock
                CROSS JOIN LATERAL rc_list_effective_access_contexts(
                    {userId},
                    security_clock."UtcNow") option
                WHERE {selectedAccessContextId}::integer IS NULL
                   OR option."AccessContextId" = {selectedAccessContextId}
                """)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new EffectiveAccessContextOption(
                row.AccessContextId,
                row.PortfolioId,
                row.WorkspaceName,
                row.AccessRevision,
                Enum.TryParse<WorkspaceExperience>(row.DefaultExperience, out var experience)
                    ? experience
                    : throw new InvalidOperationException(
                        $"Unknown workspace experience '{row.DefaultExperience}'."),
                row.TotalEffectiveContexts))
            .ToArray();
    }

    private sealed class EffectiveAccessContextOptionRow
    {
        public int AccessContextId { get; init; }
        public int PortfolioId { get; init; }
        public string WorkspaceName { get; init; } = string.Empty;
        public long AccessRevision { get; init; }
        public string DefaultExperience { get; init; } = string.Empty;
        public int TotalEffectiveContexts { get; init; }
    }
}
