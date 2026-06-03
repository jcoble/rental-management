using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Engine.Services;
using RentalCommand.TestCommon;

namespace RentalCommand.Engine.Tests.Automation;

public class DailyBriefingDeliveryServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task EnqueueDueAsync_WithConfiguredSmsAndEmail_QueuesDailyBriefingMessagesOncePerDay()
    {
        var briefing = new StubBriefingService(new BriefingResponse
        {
            Date = new DateTime(2026, 6, 3),
            GeneratedAt = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc),
            Summary = "You have one urgent maintenance item.",
            LlmEnhanced = true,
            Bullets =
            [
                new BriefingBullet("Emergency: Ceiling leak", "Unit 3 needs attention", "Maintenance", "critical", "WorkOrder", 42),
            ],
        });

        var options = Options.Create(new NotificationsConfig
        {
            EnableDailyBriefingMessages = true,
            DailyBriefing = new DailyBriefingOptions
            {
                SendHourLocal = 8,
                SmsRecipients = ["+15551234567"],
                EmailRecipients = ["owner@example.com"],
            },
        });

        var sut = new DailyBriefingDeliveryService(
            _ctx.Db,
            briefing,
            options,
            NullLogger<DailyBriefingDeliveryService>.Instance);

        var first = await sut.EnqueueDueAsync(new DateTime(2026, 6, 3, 13, 0, 0, DateTimeKind.Utc));
        var second = await sut.EnqueueDueAsync(new DateTime(2026, 6, 3, 14, 0, 0, DateTimeKind.Utc));

        first.Should().Be(2);
        second.Should().Be(0);
        _ctx.Db.OutboxMessages.Should().HaveCount(2);
        _ctx.Db.OutboxMessages.Select(m => m.MessageType).Should().BeEquivalentTo(["sms", "email"]);

        var smsPayload = JsonDocument.Parse(_ctx.Db.OutboxMessages.Single(m => m.MessageType == "sms").Payload).RootElement;
        smsPayload.GetProperty("purpose").GetString().Should().Be("daily-briefing");
        smsPayload.GetProperty("date").GetString().Should().Be("2026-06-03");
        smsPayload.GetProperty("to").GetString().Should().Be("+15551234567");
        smsPayload.GetProperty("message").GetString().Should().Contain("urgent maintenance");
    }

    private sealed class StubBriefingService : IDailyBriefingService
    {
        private readonly BriefingResponse _response;

        public StubBriefingService(BriefingResponse response) => _response = response;

        public Task<BriefingResponse> ComposeAsync(int portfolioId, CancellationToken ct = default)
            => Task.FromResult(_response);
    }
}
