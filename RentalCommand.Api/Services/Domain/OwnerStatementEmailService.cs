using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IOwnerStatementEmailService"/>
public class OwnerStatementEmailService : IOwnerStatementEmailService
{
    private readonly RentalCommandDbContext _db;
    private readonly IOwnerStatementService _statements;
    private readonly IMessagePublisher _publisher;
    private readonly ILogger<OwnerStatementEmailService> _logger;
    private readonly TimeProvider _timeProvider;

    public OwnerStatementEmailService(
        RentalCommandDbContext db,
        IOwnerStatementService statements,
        IMessagePublisher publisher,
        ILogger<OwnerStatementEmailService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _statements = statements;
        _publisher = publisher;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public async Task<StatementEmailResult> SendOwnerStatementAsync(
        WorkspaceReadScope scope,
        int ownerId,
        int year,
        CancellationToken ct = default)
    {
        var authorizedProperties = _db.Properties
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                CapabilityKeys.MoneyOwnerReportsRead,
                _timeProvider.UtcNow());
        var owner = await LoadOwnerAsync(scope.PortfolioId, ownerId, authorizedProperties, ct);
        if (owner is null)
            return new StatementEmailResult(false, "Owner not found in portfolio");
        if (string.IsNullOrWhiteSpace(owner.Email))
            return new StatementEmailResult(false, "Owner has no email address");

        var report = await _statements.GetForOwnerAsync(scope, ownerId, year, ct);
        if (report is null)
            return new StatementEmailResult(false, "No statement data for that year");

        return await EnqueueAsync(
            scope.PortfolioId,
            owner,
            year,
            report,
            OutboxIdempotency.Create("owner-statement", scope.PortfolioId, ownerId, year, owner.Email),
            ct);
    }

    private Task<OwnerEmailRecipient?> LoadOwnerAsync(
        int portfolioId,
        int ownerId,
        IQueryable<Property> authorizedProperties,
        CancellationToken ct) =>
        _db.OwnerEntities
            .AsNoTracking()
            .Where(o =>
                o.PortfolioId == portfolioId &&
                o.Id == ownerId &&
                o.DeletedAt == null &&
                authorizedProperties.Any(property => property.OwnerEntityId == o.Id))
            .Select(o => new OwnerEmailRecipient(o.Id, o.Name, o.Email))
            .FirstOrDefaultAsync(ct);

    private async Task<StatementEmailResult> EnqueueAsync(
        int portfolioId,
        OwnerEmailRecipient owner,
        int year,
        DTOs.OwnerStatementReport report,
        string idempotencyKey,
        CancellationToken ct)
    {
        var body = RenderStatementText(report);
        var subject = $"Your {year} owner statement";

        await _publisher.PublishAsync(
            portfolioId,
            "email",
            idempotencyKey,
            new { to = owner.Email, subject, body },
            ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Enqueued owner statement email for owner {OwnerId} ({Name}), year {Year}, portfolio {PortfolioId}.",
            owner.Id, owner.Name, year, portfolioId);

        return new StatementEmailResult(true);
    }

    private sealed record OwnerEmailRecipient(int Id, string Name, string? Email);

    // ── Plain-text renderer ──────────────────────────────────────────────────────────────────────

    private static string RenderStatementText(DTOs.OwnerStatementReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Owner Statement – {report.Year}");
        sb.AppendLine($"Owner: {report.OwnerName}");
        sb.AppendLine(new string('-', 60));

        if (report.Properties.Count == 0)
        {
            sb.AppendLine("No properties with activity for this year.");
        }
        else
        {
            // Column header
            sb.AppendLine($"{"Property",-30} {"Income",12} {"Expenses",12} {"Mgmt Fee",12} {"Net",12}");
            sb.AppendLine(new string('-', 80));

            foreach (var line in report.Properties)
            {
                var name = line.PropertyName.Length > 29
                    ? line.PropertyName[..29]
                    : line.PropertyName;

                sb.AppendLine(
                    $"{name,-30} {FormatMoney(line.RentalIncome),12} {FormatMoney(line.Expenses),12} " +
                    $"{FormatMoney(line.ManagementFee),12} {FormatMoney(line.NetToOwner),12}");
            }

            sb.AppendLine(new string('-', 80));
            sb.AppendLine(
                $"{"TOTAL",-30} {FormatMoney(report.TotalIncome),12} {FormatMoney(report.TotalExpenses),12} " +
                $"{FormatMoney(report.TotalManagementFee),12} {FormatMoney(report.TotalNetToOwner),12}");
            sb.AppendLine(
                $"{"DISTRIBUTED",-30} {"",12} {"",12} {"",12} {FormatMoney(report.TotalDistributed),12}");
            sb.AppendLine(
                $"{"UNDISTRIBUTED",-30} {"",12} {"",12} {"",12} {FormatMoney(report.Undistributed),12}");
        }

        sb.AppendLine();
        sb.AppendLine("This statement was generated by Rental Command.");

        return sb.ToString();
    }

    private static string FormatMoney(decimal amount) => amount.ToString("C2",
        System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
