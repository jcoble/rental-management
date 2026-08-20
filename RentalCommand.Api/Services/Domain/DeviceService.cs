using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using NotificationCrudWriteRequest = RentalCommand.Core.Atomic.TransactionalWriteDefaults.AuthorizationScopedRequest<RentalCommand.Api.Services.Domain.NotificationCrudOperation>;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IDeviceService"/>
public class DeviceService : IDeviceService
{
    private readonly RentalCommandDbContext _db;
    private readonly IRequestWriteExecutor _writes;

    public DeviceService(RentalCommandDbContext db, IRequestWriteExecutor writes)
    {
        _db = db;
        _writes = writes;
    }

    /// <inheritdoc/>
    [WriteEntryPoint(WriteEntryPointKind.Transactional)]
    public async Task RegisterAsync(
        WorkspaceReadScope scope,
        string token,
        string platform,
        string operationKey,
        CancellationToken ct = default)
    {
        var request = TransactionalWriteDefaults.Request(
            scope,
            NotificationCrudOperation.DeviceRegister,
            0,
            TokenHash(token),
            operationKey,
            new { Token = token, Platform = platform });
        var write = NotificationCrudWriteSupport.Write(
            request, RegisterDeviceAsync, AuthorizeReplayAsync);
        await _writes.ExecuteAsync(NotificationCrudWriteSupport.IdempotencyKey(request), write, ct);
    }

    /// <inheritdoc/>
    [WriteEntryPoint(WriteEntryPointKind.Transactional)]
    public async Task<bool> UnregisterAsync(
        WorkspaceReadScope scope,
        string token,
        string operationKey,
        CancellationToken ct = default)
    {
        var request = TransactionalWriteDefaults.Request(
            scope,
            NotificationCrudOperation.DeviceUnregister,
            0,
            TokenHash(token),
            operationKey,
            new { Token = token, Platform = (string?)null });
        var write = NotificationCrudWriteSupport.Write(
            request, UnregisterDeviceAsync, AuthorizeReplayAsync);
        var outcome = await _writes.ExecuteAsync(
            NotificationCrudWriteSupport.IdempotencyKey(request), write, ct);
        return outcome.Value.Found;
    }

    private async Task<AtomicNotificationMutationResult> RegisterDeviceAsync(
        NotificationCrudWriteRequest request,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await NotificationCrudWriteSupport.BeginExecutionAsync(request, _db, context, ct);
        using var payload = System.Text.Json.JsonDocument.Parse(request.RequestJson);
        var token = payload.RootElement.GetProperty("Token").GetString()?.Trim() ?? string.Empty;
        var platform = payload.RootElement.GetProperty("Platform").GetString()?.Trim().ToLowerInvariant()
            ?? string.Empty;
        if (token.Length is 0 or > 500 || platform is not ("ios" or "android" or "web"))
        {
            throw new InvalidOperationException("A valid device token and platform are required.");
        }

        var row = await _db.DeviceTokens.SingleOrDefaultAsync(candidate =>
            candidate.PortfolioId == request.PortfolioId && candidate.Token == token, ct);
        var operation = row is null ? AuditLogOperation.Created : AuditLogOperation.Updated;
        if (row is null)
        {
            row = new DeviceToken
            {
                PortfolioId = request.PortfolioId,
                Token = token,
                CreatedAt = now,
            };
            _db.Add(row);
        }

        row.UserId = request.ActorUserId;
        row.Platform = platform;
        row.LastSeenAt = now;
        context.BindSemanticAudit(
            row,
            TransactionalWriteDefaults.Audit(
                request,
                nameof(DeviceToken),
                operation,
                "Push notification device registered",
                operation == AuditLogOperation.Created ? 0 : row.Id));
        await context.FlushBusinessAsync(ct);
        return new AtomicNotificationMutationResult(true, true, row.Id, 1);
    }

    private async Task<AtomicNotificationMutationResult> UnregisterDeviceAsync(
        NotificationCrudWriteRequest request,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        _ = await NotificationCrudWriteSupport.BeginExecutionAsync(request, _db, context, ct);
        using var payload = System.Text.Json.JsonDocument.Parse(request.RequestJson);
        var token = payload.RootElement.GetProperty("Token").GetString()?.Trim() ?? string.Empty;
        if (token.Length is 0 or > 500)
        {
            throw new InvalidOperationException("A valid device token is required.");
        }

        var row = await _db.DeviceTokens.SingleOrDefaultAsync(candidate =>
            candidate.PortfolioId == request.PortfolioId
            && candidate.UserId == request.ActorUserId
            && candidate.Token == token, ct);
        if (row is null)
        {
            return new AtomicNotificationMutationResult(false, false, 0, 0);
        }

        _db.Remove(row);
        context.BindSemanticAudit(
            row,
            TransactionalWriteDefaults.Audit(
                request,
                nameof(DeviceToken),
                AuditLogOperation.Deleted,
                "Push notification device unregistered",
                row.Id));
        await context.FlushBusinessAsync(ct);
        return new AtomicNotificationMutationResult(true, true, row.Id, 1);
    }

    private Task AuthorizeReplayAsync(
        NotificationCrudWriteRequest request,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        NotificationCrudWriteSupport.AuthorizeReplayAsync(request, _db, context, ct);

    private static string TokenHash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())))
            .ToLowerInvariant()[..24];
}
