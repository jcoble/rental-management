using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Notices;

public class NoticeTemplateServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task List_returns_all_five_types_with_available_fields()
    {
        var service = new NoticeTemplateService(_ctx.Db);

        var list = await service.ListAsync(1, default);

        Assert.Equal(5, list.Count);
        Assert.All(list, t => Assert.False(t.HasTemplate));
        var late = list.Single(t => t.NoticeType == "LateRentNotice");
        Assert.Contains("overdue_amount", late.AvailableFields);
    }

    [Fact]
    public async Task Upsert_creates_then_updates_single_active_template()
    {
        var service = new NoticeTemplateService(_ctx.Db);

        await service.UpsertAsync(1, "RenewalOffer",
            new UpsertNoticeTemplateRequest { Subject = "S1", Body = "B1" }, default);
        var afterCreate = await service.GetAsync(1, "RenewalOffer", default);
        Assert.True(afterCreate.HasTemplate);
        Assert.Equal("S1", afterCreate.Subject);

        await service.UpsertAsync(1, "RenewalOffer",
            new UpsertNoticeTemplateRequest { Subject = "S2", Body = "B2" }, default);
        var afterUpdate = await service.GetAsync(1, "RenewalOffer", default);
        Assert.Equal("S2", afterUpdate.Subject);

        Assert.Equal(1, _ctx.Db.NoticeTemplates.Count(t => t.PortfolioId == 1 && t.NoticeType == "RenewalOffer" && t.IsActive));
    }

    [Fact]
    public async Task Upsert_rejects_unknown_type()
    {
        var service = new NoticeTemplateService(_ctx.Db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpsertAsync(1, "BogusType",
                new UpsertNoticeTemplateRequest { Subject = "x", Body = "y" }, default));
    }

    [Fact]
    public async Task Upsert_rejects_empty_body()
    {
        var service = new NoticeTemplateService(_ctx.Db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpsertAsync(1, "RenewalOffer",
                new UpsertNoticeTemplateRequest { Subject = "Valid Subject", Body = "" }, default));
    }

    [Fact]
    public async Task Upsert_rejects_empty_subject()
    {
        var service = new NoticeTemplateService(_ctx.Db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpsertAsync(1, "RenewalOffer",
                new UpsertNoticeTemplateRequest { Subject = "   ", Body = "Valid body" }, default));
    }
}
