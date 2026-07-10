using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Conversations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Conversations;

public sealed class SendConversationMessageHandler
    : IAtomicCommandHandler<SendConversationMessageCommand, SendConversationMessageResult>
{
    private const int PreviewMaxLength = 280;

    public async Task<SendConversationMessageResult> HandleAsync(
        SendConversationMessageCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (command.SenderRole != ConversationSenderRole.Landlord)
        {
            throw new InvalidOperationException("This command accepts landlord-to-tenant messages only.");
        }

        Conversation conversation;
        Tenant tenant;
        if (command.ConversationId is { } conversationId)
        {
            await attempt.Locking.AcquireAsync(AtomicLockResource.Conversation, conversationId, ct);
            var existing = await attempt.Persistence.Query<Conversation>()
                .Include(candidate => candidate.Tenant)
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == conversationId
                        && candidate.PortfolioId == command.PortfolioId,
                    ct);
            if (existing?.Tenant is null)
            {
                return NotFound();
            }

            conversation = existing;
            tenant = existing.Tenant;
            conversation.LastMessageAt = command.OccurredAtUtc;
            conversation.LastMessagePreview = Preview(command.Body);
            conversation.TenantUnreadCount += 1;
        }
        else
        {
            var target = await attempt.Persistence.Query<Tenant>()
                .SingleOrDefaultAsync(candidate => candidate.Id == command.TenantId
                    && candidate.PortfolioId == command.PortfolioId
                    && candidate.DeletedAt == null, ct);
            if (target is null)
            {
                return NotFound();
            }

            tenant = target;
            conversation = new Conversation
            {
                PortfolioId = command.PortfolioId,
                TenantId = tenant.Id,
                Subject = command.Subject,
                StartedByLandlord = true,
                CreatedAt = command.OccurredAtUtc,
                LastMessageAt = command.OccurredAtUtc,
                LastMessagePreview = Preview(command.Body),
                TenantUnreadCount = 1,
            };
            attempt.Persistence.Add(conversation);
        }

        var channels = NormalizeChannels(command.RequestedChannels, tenant);
        var message = new ConversationMessage
        {
            Conversation = conversation,
            SenderRole = ConversationSenderRole.Landlord,
            Body = command.Body,
            Channels = string.Join(',', channels),
            CreatedAt = command.OccurredAtUtc,
        };
        attempt.Persistence.Add(message);
        await attempt.FlushBusinessAsync(ct);

        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(ConversationMessage),
            message.Id,
            AuditLogOperation.Created,
            NewValues: JsonSerializer.Serialize(new
            {
                message.ConversationId,
                SenderRole = message.SenderRole.ToString(),
                message.Channels,
            }),
            ChangeReason: "Landlord conversation message committed with recipient destinations."));

        StageDestinationIntents(attempt, command, tenant, conversation, message, channels);
        var notifications = await CreatePortalNotificationsAsync(attempt, command, conversation, channels, ct);
        if (notifications.Count > 0)
        {
            attempt.Persistence.AddRange(notifications);
            await attempt.FlushBusinessAsync(ct);
        }

        return new SendConversationMessageResult(
            SendConversationMessageOutcome.Applied,
            conversation.Id,
            message.Id,
            notifications.Select(notification => notification.Id).ToArray());
    }

    private static async Task<List<Notification>> CreatePortalNotificationsAsync(
        IAtomicWriteAttempt attempt,
        SendConversationMessageCommand command,
        Conversation conversation,
        IReadOnlyCollection<string> channels,
        CancellationToken ct)
    {
        if (!channels.Contains("Portal", StringComparer.Ordinal))
        {
            return [];
        }

        var tenantUserId = await attempt.Persistence.Query<ApplicationUser>()
            .Where(user => user.PortfolioId == command.PortfolioId && user.TenantId == conversation.TenantId)
            .OrderBy(user => user.Id)
            .Select(user => (int?)user.Id)
            .FirstOrDefaultAsync(ct);
        if (tenantUserId is null)
        {
            return [];
        }

        return
        [
            new Notification
            {
                PortfolioId = command.PortfolioId,
                UserId = tenantUserId,
                Type = "TenantNotice",
                Title = conversation.Subject,
                Message = Preview(command.Body) ?? conversation.Subject,
                Severity = "Info",
                ActionUrl = $"/portal/messages?conversation={conversation.Id}",
                RelatedEntityType = nameof(Conversation),
                RelatedEntityId = conversation.Id,
                CreatedAt = command.OccurredAtUtc,
            },
        ];
    }

    private static void StageDestinationIntents(
        IAtomicWriteAttempt attempt,
        SendConversationMessageCommand command,
        Tenant tenant,
        Conversation conversation,
        ConversationMessage message,
        IReadOnlyCollection<string> channels)
    {
        if (channels.Contains("Email", StringComparer.Ordinal))
        {
            attempt.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new { to = tenant.Email, subject = conversation.Subject, body = command.Body }),
                IdempotencyKey = $"conversation:{conversation.Id}:message:{message.Id}:email:{DestinationHash(tenant.Email!)}",
                CreatedAtUtc = command.OccurredAtUtc,
                NextAttemptAtUtc = command.OccurredAtUtc,
            });
        }

        if (channels.Contains("Sms", StringComparer.Ordinal))
        {
            attempt.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "sms",
                Payload = JsonSerializer.Serialize(new { to = tenant.Phone, message = command.Body }),
                IdempotencyKey = $"conversation:{conversation.Id}:message:{message.Id}:sms:{DestinationHash(tenant.Phone!)}",
                CreatedAtUtc = command.OccurredAtUtc,
                NextAttemptAtUtc = command.OccurredAtUtc,
            });
        }
    }

    private static List<string> NormalizeChannels(IEnumerable<string> requested, Tenant tenant)
    {
        var normalized = requested
            .Where(channel => !string.IsNullOrWhiteSpace(channel))
            .Select(channel => channel.Trim().ToLowerInvariant() switch
            {
                "portal" => "Portal",
                "email" => "Email",
                "sms" => "Sms",
                _ => null,
            })
            .Where(channel => channel is not null)
            .Select(channel => channel!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (string.IsNullOrWhiteSpace(tenant.Email)) normalized.Remove("Email");
        if (string.IsNullOrWhiteSpace(tenant.Phone)) normalized.Remove("Sms");
        return normalized;
    }

    private static string DestinationHash(string destination) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destination.Trim().ToLowerInvariant())));

    private static string? Preview(string body) => string.IsNullOrEmpty(body)
        ? null
        : body.Length <= PreviewMaxLength ? body : body[..PreviewMaxLength];

    private static SendConversationMessageResult NotFound() => new(
        SendConversationMessageOutcome.NotFound, 0, 0, []);
}
