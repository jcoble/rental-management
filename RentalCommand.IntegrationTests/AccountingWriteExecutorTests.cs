using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class AccountingWriteExecutorTests(MigratedPostgreSqlFixture fixture)
{
    private static readonly Guid SessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClaimId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ContinuationId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime BusinessNow =
        new(2099, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    // Frozen legacy fingerprints computed from the command DTO shapes at c9dae23c. These values
    // must never be regenerated from the current command model or a current serialization helper.
    private const string CreateLedgerFingerprint = "48ed7e9b6cdc8b374b48507346caed81e4f855459438c3ae5c0f5a7aaa183ad8";
    private const string UpdateLedgerFingerprint = "4ec0e7c4b1caeafe192b5bdce1fbddb0f0b73db951de23ccd655765f01135eca";
    private const string ConnectFingerprint = "fb16a890f207c64604b110b0803db778c424004a69f17d95d49cb7dc8ae09dd7";
    private const string CancelAutopayFingerprint = "d09d3653bd24cfb1b99b4766df012c2a0e1ac99d2fb072b8dd2d13d19a897cdc";
    private const string PrepareDisconnectFingerprint = "f2aaf57d508701af847c8e3f0c3405be644ce9dac760444ef811492f4ab1cc92";
    private const string FinalizeDisconnectFingerprint = "92711d12b97816763a18ea23e518d0dea48b45fac246faa865924eed0aab5580";
    private const string DirectionFingerprint = "3e742fa87f9fe6ae9961007390b6ba28d9baf3269f69f49aaafb199eddd29eaa";
    private const string PullFingerprint = "51020797a59711b56e1f874c7c140f92a681ce538fa00b59b41aa488e8763aaa";
    private const string ConfirmMappingFingerprint = "5204c8def42f660f622163e6c7613031fddde928dbfd0a56211b421ef7c2fd0b";
    private const string ContinueMappingFingerprint = "52e187e41a1b64a9206c14749eaa7bdf2a659922d7a31e10bcce06b006c591c5";

    [Fact]
    public void LedgerLifecycleAndMapping_PreserveFrozenFingerprints()
    {
        var commands = new (IAtomicCommandData Command, string Fingerprint)[]
        {
            (CreateLedger(), CreateLedgerFingerprint),
            (UpdateLedger(false), UpdateLedgerFingerprint),
            (PrepareConnect(), ConnectFingerprint),
            (CancelAutopay(), CancelAutopayFingerprint),
            (PrepareDisconnect(), PrepareDisconnectFingerprint),
            (FinalizeDisconnect(), FinalizeDisconnectFingerprint),
            (SetDirection(), DirectionFingerprint),
            (ApplyPull(), PullFingerprint),
            (ConfirmMapping(), ConfirmMappingFingerprint),
            (ContinueMapping(), ContinueMappingFingerprint),
        };
        commands.Should().AllSatisfy(item =>
            AtomicCommandFingerprint.Create(item.Command).Should().Be(item.Fingerprint));
    }

    [Fact]
    public async Task FrozenLegacyReceipts_ReplayAllTenAccountingOperationsWithRealAuthorization()
    {
        await using var database = await fixture.CreateContextAsync();
        await SeedFrozenReplayAuthorityAsync(database.Db);
        await using var services = BuildServices(database.ConnectionString);
        var ledgerPublicId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        var createResult = new LedgerAccountMutationResult(
            LedgerAccountMutationOutcome.Applied,
            new LedgerAccountSnapshot(41, ledgerPublicId, 1, "4100", "Frozen income",
                AccountType.Income, NormalBalance.Credit, null, null,
                ScheduleECategory.Other, false, true, false));
        await ReplayAsync(services, "accounting.ledger-account.create", "1:8:ledger-create",
            CreateLedger(), CreateLedgerFingerprint,
            "{\"Outcome\":0,\"Account\":{\"Id\":41,\"PublicId\":\"44444444-4444-4444-4444-444444444444\",\"PortfolioId\":1,\"Code\":\"4100\",\"Name\":\"Frozen income\",\"AccountType\":\"Income\",\"NormalBalance\":\"Credit\",\"ParentAccountId\":null,\"SystemKey\":null,\"ScheduleECategory\":\"Other\",\"IsSystem\":false,\"IsActive\":true,\"HasPostedLines\":false}}",
            createResult, "accounting.ledger-account.mutation.v1",
            db => new CreateLedgerAccountHandler(db).AuthorizeAsync);

        var updateResult = new LedgerAccountMutationResult(
            LedgerAccountMutationOutcome.Applied,
            new LedgerAccountSnapshot(42, ledgerPublicId, 1, "4200", "Frozen update",
                AccountType.Expense, NormalBalance.Debit, 41, null,
                ScheduleECategory.Other, false, true, false));
        await ReplayAsync(services, "accounting.ledger-account.update", "1:8:ledger-update",
            UpdateLedger(false), UpdateLedgerFingerprint,
            "{\"Outcome\":0,\"Account\":{\"Id\":42,\"PublicId\":\"44444444-4444-4444-4444-444444444444\",\"PortfolioId\":1,\"Code\":\"4200\",\"Name\":\"Frozen update\",\"AccountType\":\"Expense\",\"NormalBalance\":\"Debit\",\"ParentAccountId\":41,\"SystemKey\":null,\"ScheduleECategory\":\"Other\",\"IsSystem\":false,\"IsActive\":true,\"HasPostedLines\":false}}",
            updateResult, "accounting.ledger-account.mutation.v1",
            db => new UpdateLedgerAccountHandler(db).AuthorizeAsync);

        await ReplayAsync(services, "accounting.oauth-state.prepare", "1:8:QuickBooks:connect",
            PrepareConnect(), ConnectFingerprint,
            "{\"StateToken\":\"frozen-state\",\"RedirectUri\":\"https://example.test/callback\",\"ExpiresAtUtc\":\"2099-08-21T12:10:00Z\"}",
            new PrepareAccountingConnectResult(
                "frozen-state", "https://example.test/callback", BusinessNow.AddMinutes(10)),
            "rental.accounting-connect.prepare.v1",
            db => new PrepareAccountingConnectHandler(db).AuthorizeAsync);

        await ReplayAsync(services, "tenant-autopay.cancel", "1:8:11:autopay-cancel",
            CancelAutopay(), CancelAutopayFingerprint,
            "{\"Found\":true,\"Applied\":true,\"TenantAccountId\":11}",
            new CancelTenantAutopayResult(true, true, 11), "rental.tenant-autopay.cancel.v1",
            db => new CancelTenantAutopayHandler(db).AuthorizeAsync);

        await ReplayAsync(services, "accounting.connection.disconnect.prepare",
            "1:8:QuickBooks:disconnect-prepare", PrepareDisconnect(), PrepareDisconnectFingerprint,
            "{\"Outcome\":1,\"ConnectionId\":12,\"TokenGeneration\":3,\"PreparedAtUtc\":\"2099-08-21T12:00:00Z\",\"RefreshTokenCipherText\":\"frozen-refresh\"}",
            new PrepareAccountingDisconnectResult(
                PrepareAccountingDisconnectOutcome.Prepared, 12, 3, BusinessNow, "frozen-refresh"),
            "rental.accounting-disconnect.prepare.v1",
            db => new PrepareAccountingDisconnectHandler(db).AuthorizeAsync);

        await ReplayAsync(services, "accounting.connection.disconnect.finalize",
            "1:8:QuickBooks:disconnect-prepare", FinalizeDisconnect(), FinalizeDisconnectFingerprint,
            "{\"Outcome\":1,\"ConnectionId\":12,\"TokenGeneration\":3,\"DisconnectedAtUtc\":\"2099-08-21T12:00:00Z\"}",
            new FinalizeAccountingDisconnectResult(
                FinalizeAccountingDisconnectOutcome.Applied, 12, 3, BusinessNow),
            "rental.accounting-disconnect.finalize.v1",
            db => new FinalizeAccountingDisconnectHandler(db).AuthorizeAsync);

        await ReplayAsync(services, "accounting.connection.direction.set", "1:8:QuickBooks:direction",
            SetDirection(), DirectionFingerprint,
            "{\"Found\":true,\"ConnectionId\":12,\"PullEnabled\":true,\"PushEnabled\":false,\"UpdatedAtUtc\":\"2099-08-21T12:00:00Z\"}",
            new SetAccountingDirectionResult(true, 12, true, false, BusinessNow),
            "rental.accounting-direction.set.v1",
            db => new SetAccountingDirectionHandler(db).AuthorizeAsync);

        await ReplayAsync(services, "accounting.pull.apply",
            "12:22222222222222222222222222222222:batch", ApplyPull(), PullFingerprint,
            "{\"CustomersMapped\":1,\"VendorsMapped\":2,\"AccountsMapped\":3,\"PaymentsImported\":4,\"ExpensesImported\":5,\"NeedsReview\":6}",
            new ApplyAccountingPullResult(1, 2, 3, 4, 5, 6), "accounting.pull.apply.v1",
            db => new ApplyAccountingPullResultHandler(db).AuthorizeAsync);

        await ReplayAsync(services, "accounting.mapping.confirm",
            "1:12:7:269F04B029340016B186403090324E5083DCD7085C7F88E236612D4D37FEB0EE:B150E3C4EE698C9614756FBB203AE61516A2ECD9C973D8A8D7C0337E6594F0C8",
            ConfirmMapping(), ConfirmMappingFingerprint,
            "{\"Outcome\":0,\"MappingId\":51,\"MappingRevision\":2,\"PromotedCount\":3,\"ContinuationId\":\"33333333-3333-3333-3333-333333333333\",\"HasMore\":true}",
            new ConfirmAccountingMappingResult(
                ConfirmAccountingMappingOutcome.Applied, 51, 2, 3, ContinuationId, true),
            "accounting.mapping.confirm.result.v2",
            db => new ConfirmAccountingMappingHandler(db).AuthorizeAsync);

        await ReplayAsync(services, "accounting.mapping.promote.continue",
            "1:12:33333333333333333333333333333333:7:D81913CFBC63EAC7355945E8A6ACB98D4BA34275B1E4F132D29B7AF6A14217D1",
            ContinueMapping(), ContinueMappingFingerprint,
            "{\"Outcome\":0,\"ContinuationId\":\"33333333-3333-3333-3333-333333333333\",\"PromotedCount\":2,\"TotalPromotedCount\":5,\"HasMore\":false}",
            new ContinueAccountingMappingPromotionResult(
                ContinueAccountingMappingPromotionOutcome.Applied, ContinuationId, 2, 5, false),
            "accounting.mapping.promote.continue.result.v1",
            db => new ContinueAccountingMappingPromotionHandler(db).AuthorizeAsync);
    }

    [Fact]
    public async Task FrozenLegacyReceiptReplay_RefusesRevokedSession()
    {
        await using var database = await fixture.CreateContextAsync();
        await SeedFrozenReplayAuthorityAsync(database.Db);
        await database.Db.AuthSessions.Where(row => row.Id == SessionId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, AuthSessionStatus.Revoked)
                .SetProperty(row => row.RevokedAtUtc, BusinessNow));
        await using var services = BuildServices(database.ConnectionString);

        var action = () => ReplayAsync(services, "accounting.mapping.confirm",
            "1:12:7:269F04B029340016B186403090324E5083DCD7085C7F88E236612D4D37FEB0EE:B150E3C4EE698C9614756FBB203AE61516A2ECD9C973D8A8D7C0337E6594F0C8",
            ConfirmMapping(), ConfirmMappingFingerprint,
            "{\"Outcome\":0,\"MappingId\":51,\"MappingRevision\":2,\"PromotedCount\":3,\"ContinuationId\":\"33333333-3333-3333-3333-333333333333\",\"HasMore\":true}",
            new ConfirmAccountingMappingResult(
                ConfirmAccountingMappingOutcome.Applied, 51, 2, 3, ContinuationId, true),
            "accounting.mapping.confirm.result.v2",
            db => new ConfirmAccountingMappingHandler(db).AuthorizeAsync);

        await action.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task CancelAutopay_ExactRetryDoesNotRepeatCancellationOrAudit()
    {
        await using var database = await fixture.CreateContextAsync();
        await SeedFrozenReplayAuthorityAsync(database.Db);
        await using var services = BuildServices(database.ConnectionString);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var command = CancelAutopay();
        var handler = new CancelTenantAutopayHandler(db);
        var write = AccountingWriteSupport.Write(
            command, handler.ExecuteAsync, handler.AuthorizeAsync);
        const string key = "1:8:11:autopay-cancel";

        var first = await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(key, write);
        var replay = await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(key, write);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(first.Value);
        db.ChangeTracker.Clear();
        (await db.TenantAutopayEnrollments.CountAsync(row => row.TenantAccountId == 11))
            .Should().Be(1);
        (await db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(TenantAutopayEnrollment) && row.EntityId == 25))
            .Should().Be(1);
    }

    [Fact]
    public async Task ElevenWrites_ExecuteThroughRecorderWithLegacyIdentitiesContractsAndLocks()
    {
        var expected = new[]
        {
            ("accounting.ledger-account.create", "accounting.ledger-account.mutation.v1",
                new[] { $"AuthSession:{SessionId}", "WorkspaceAccessContext:8", "Portfolio:1" }),
            ("accounting.ledger-account.update", "accounting.ledger-account.mutation.v1",
                new[] { $"AuthSession:{SessionId}", "WorkspaceAccessContext:8", "Portfolio:1", "LedgerAccount:42" }),
            ("accounting.ledger-account.update", "accounting.ledger-account.mutation.v1",
                new[] { $"AuthSession:{SessionId}", "WorkspaceAccessContext:8", "Portfolio:1", "LedgerAccount:42" }),
            ("accounting.oauth-state.prepare", "rental.accounting-connect.prepare.v1",
                new[] { $"AuthSession:{SessionId}", "WorkspaceAccessContext:8", "Portfolio:1" }),
            ("tenant-autopay.cancel", "rental.tenant-autopay.cancel.v1",
                new[] { $"AuthSession:{SessionId}", "WorkspaceAccessContext:8", "TenantAccount:11" }),
            ("accounting.connection.disconnect.prepare", "rental.accounting-disconnect.prepare.v1",
                new[] { $"AuthSession:{SessionId}", "WorkspaceAccessContext:8", "Portfolio:1" }),
            ("accounting.connection.disconnect.finalize", "rental.accounting-disconnect.finalize.v1",
                new[] { $"AuthSession:{SessionId}", "WorkspaceAccessContext:8", "AccountingConnection:12" }),
            ("accounting.connection.direction.set", "rental.accounting-direction.set.v1",
                new[] { $"AuthSession:{SessionId}", "WorkspaceAccessContext:8", "Portfolio:1" }),
            ("accounting.pull.apply", "accounting.pull.apply.v1",
                new[] { "AccountingConnection:12" }),
            ("accounting.mapping.confirm", "accounting.mapping.confirm.result.v2",
                new[] { $"AuthSession:{SessionId}", "WorkspaceAccessContext:8", "AccountingConnection:12" }),
            ("accounting.mapping.promote.continue", "accounting.mapping.promote.continue.result.v1",
                new[] { $"AuthSession:{SessionId}", "WorkspaceAccessContext:8", "AccountingConnection:12" }),
        };

        foreach (var pair in Commands().Zip(expected))
        {
            var write = AccountingWriteSupport.Write(
                pair.First,
                (_, _, _) => Task.FromResult(true),
                (_, _, _) => Task.CompletedTask);
            write.OperationName.Should().Be(pair.Second.Item1);
            write.ResultContract.Should().Be(pair.Second.Item2);
            var recorder = new RecordingWriteExecutor();
            await recorder.ExecuteAsync("recorded-key", write);
            recorder.Acquired.Should().Equal(pair.Second.Item3);
        }
    }

    [Fact]
    public async Task LedgerCreateRecorder_AcquiresConditionalParentBeforeCodeLock()
    {
        await using var database = await fixture.CreateContextAsync();
        var scope = await SeedAuthorityAsync(database.Db, "create-locks");
        var parent = await AddLedgerAsync(database.Db, scope.PortfolioId, "6100", "Parent");
        database.Db.ChangeTracker.Clear();

        foreach (var parentId in new int?[] { parent.Id, null })
        {
            var command = new CreateLedgerAccountCommand(
                scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
                scope.AccessRevision, parentId is null ? "6200" : "6201", "Recorder child",
                AccountType.Expense, parentId, null, ScheduleECategory.Other, true,
                $"create-lock-{parentId?.ToString() ?? "none"}");
            var (context, acquired) = Recorder(database.Db);
            var handler = new CreateLedgerAccountHandler(database.Db);

            await ExecuteRecordedAsync(command, handler.ExecuteAsync, handler.AuthorizeAsync, context);

            acquired.Should().Equal(Prefix(scope)
                .Concat(parentId is null ? [] : new[] { $"LedgerAccount:{parent.Id}" })
                .Append($"LedgerAccountCode:{scope.PortfolioId}"));
        }
    }

    [Fact]
    public async Task LedgerUpdateRecorder_AcquiresDistinctTargetAndEffectiveParentOnce()
    {
        await using var database = await fixture.CreateContextAsync();
        var scope = await SeedAuthorityAsync(database.Db, "update-locks");
        var target = await AddLedgerAsync(database.Db, scope.PortfolioId, "6300", "Target");
        var parent = await AddLedgerAsync(database.Db, scope.PortfolioId, "6301", "Effective parent");
        database.Db.ChangeTracker.Clear();
        var command = new UpdateLedgerAccountCommand(
            scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, target.Id, "Updated target", null, parent.Id, true,
            ScheduleECategory.Other, true, false, "update-lock");
        var (context, acquired) = Recorder(database.Db);
        var handler = new UpdateLedgerAccountHandler(database.Db);

        await ExecuteRecordedAsync(command, handler.ExecuteAsync, handler.AuthorizeAsync, context);

        acquired.Should().Equal(Prefix(scope)
            .Append($"LedgerAccount:{target.Id}")
            .Append($"LedgerAccount:{parent.Id}"));
        acquired.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task DirectionRecorder_AcquiresAuthorizationPrefixThenResolvedConnection()
    {
        await using var database = await fixture.CreateContextAsync();
        var scope = await SeedAuthorityAsync(database.Db, "direction-locks");
        var connection = await AddConnectionAsync(database.Db, scope.PortfolioId);
        database.Db.ChangeTracker.Clear();
        var command = AtomicAccountingLifecycle.DirectionCommand(
            scope, AccountingProvider.QuickBooks, false, true, "direction-lock");
        var (context, acquired) = Recorder(database.Db);
        var handler = new SetAccountingDirectionHandler(database.Db);

        await ExecuteRecordedAsync(command, handler.ExecuteAsync, handler.AuthorizeAsync, context);

        acquired.Should().Equal(Prefix(scope).Append($"AccountingConnection:{connection.Id}"));
    }

    [Fact]
    public async Task LifecycleWrites_ExecuteThenReplayWithoutRepeatingMutation()
    {
        await using var database = await fixture.CreateContextAsync();
        var scope = await SeedAuthorityAsync(database.Db, "lifecycle-replay");
        var connection = await AddConnectionAsync(database.Db, scope.PortfolioId);
        await using var services = BuildServices(database.ConnectionString);
        await using var serviceScope = services.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = serviceScope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>();

        var connect = AtomicAccountingConnect.Command(
            scope, AccountingProvider.QuickBooks, "https://example.test/callback", "connect-replay");
        var connectHandler = new PrepareAccountingConnectHandler(db);
        var connectWrite = AccountingWriteSupport.Write(
            connect, connectHandler.ExecuteAsync, connectHandler.AuthorizeAsync);
        var firstConnect = await writes.ExecuteAsync(
            AtomicAccountingConnect.Identity(connect).IdempotencyKey, connectWrite);
        var replayConnect = await writes.ExecuteAsync(
            AtomicAccountingConnect.Identity(connect).IdempotencyKey, connectWrite);
        replayConnect.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayConnect.Value.Should().BeEquivalentTo(firstConnect.Value);
        (await db.OAuthStates.CountAsync()).Should().Be(1);

        var prepare = AtomicAccountingLifecycle.PrepareDisconnectCommand(
            scope, AccountingProvider.QuickBooks, "disconnect-replay");
        var prepareHandler = new PrepareAccountingDisconnectHandler(db);
        var prepareWrite = AccountingWriteSupport.Write(
            prepare, prepareHandler.ExecuteAsync, prepareHandler.AuthorizeAsync);
        var firstPrepare = await writes.ExecuteAsync(
            AtomicAccountingLifecycle.PrepareDisconnectIdentity(prepare).IdempotencyKey, prepareWrite);
        db.ChangeTracker.Clear();
        var preparedState = await db.AccountingConnections.AsNoTracking()
            .SingleAsync(row => row.Id == connection.Id);
        var replayPrepare = await writes.ExecuteAsync(
            AtomicAccountingLifecycle.PrepareDisconnectIdentity(prepare).IdempotencyKey, prepareWrite);
        replayPrepare.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayPrepare.Value.Should().BeEquivalentTo(firstPrepare.Value);
        db.ChangeTracker.Clear();
        (await db.AccountingConnections.AsNoTracking().SingleAsync(row => row.Id == connection.Id))
            .Should().BeEquivalentTo(preparedState);

        var finalize = AtomicAccountingLifecycle.FinalizeDisconnectCommand(prepare, firstPrepare.Value);
        var finalizeHandler = new FinalizeAccountingDisconnectHandler(db);
        var finalizeWrite = AccountingWriteSupport.Write(
            finalize, finalizeHandler.ExecuteAsync, finalizeHandler.AuthorizeAsync);
        var firstFinalize = await writes.ExecuteAsync(
            AtomicAccountingLifecycle.FinalizeDisconnectIdentity(finalize).IdempotencyKey, finalizeWrite);
        db.ChangeTracker.Clear();
        var finalizedState = await db.AccountingConnections.AsNoTracking()
            .SingleAsync(row => row.Id == connection.Id);
        var replayFinalize = await writes.ExecuteAsync(
            AtomicAccountingLifecycle.FinalizeDisconnectIdentity(finalize).IdempotencyKey, finalizeWrite);
        replayFinalize.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayFinalize.Value.Should().BeEquivalentTo(firstFinalize.Value);
        db.ChangeTracker.Clear();
        (await db.AccountingConnections.AsNoTracking().SingleAsync(row => row.Id == connection.Id))
            .Should().BeEquivalentTo(finalizedState);

        var direction = AtomicAccountingLifecycle.DirectionCommand(
            scope, AccountingProvider.QuickBooks, false, true, "direction-replay");
        var directionHandler = new SetAccountingDirectionHandler(db);
        var directionWrite = AccountingWriteSupport.Write(
            direction, directionHandler.ExecuteAsync, directionHandler.AuthorizeAsync);
        var firstDirection = await writes.ExecuteAsync(
            AtomicAccountingLifecycle.DirectionIdentity(direction).IdempotencyKey, directionWrite);
        db.ChangeTracker.Clear();
        var directedState = await db.AccountingConnections.AsNoTracking()
            .SingleAsync(row => row.Id == connection.Id);
        var replayDirection = await writes.ExecuteAsync(
            AtomicAccountingLifecycle.DirectionIdentity(direction).IdempotencyKey, directionWrite);
        replayDirection.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayDirection.Value.Should().BeEquivalentTo(firstDirection.Value);
        db.ChangeTracker.Clear();
        (await db.AccountingConnections.AsNoTracking().SingleAsync(row => row.Id == connection.Id))
            .Should().BeEquivalentTo(directedState);
        (await db.AccountingConnections.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AllElevenLegacyHandlerArmsThrow()
    {
        var create = CreateLedger();
        var update = UpdateLedger(delete: false);
        var delete = UpdateLedger(delete: true);
        var connect = PrepareConnect();
        var autopay = CancelAutopay();
        var prepare = PrepareDisconnect();
        var finalize = FinalizeDisconnect();
        var direction = SetDirection();
        var pull = ApplyPull();
        var mapping = ConfirmMapping();
        var continuation = ContinueMapping();
        var calls = new Func<Task>[]
        {
            () => new CreateLedgerAccountHandler(null!).HandleAsync(create, null!, default),
            () => new CreateLedgerAccountHandler(null!).AuthorizeReplayAsync(create, null!, default),
            () => new UpdateLedgerAccountHandler(null!).HandleAsync(update, null!, default),
            () => new UpdateLedgerAccountHandler(null!).AuthorizeReplayAsync(update, null!, default),
            () => new UpdateLedgerAccountHandler(null!).HandleAsync(delete, null!, default),
            () => new UpdateLedgerAccountHandler(null!).AuthorizeReplayAsync(delete, null!, default),
            () => new PrepareAccountingConnectHandler(null!).HandleAsync(connect, null!, default),
            () => new PrepareAccountingConnectHandler(null!).AuthorizeReplayAsync(connect, null!, default),
            () => new CancelTenantAutopayHandler(null!).HandleAsync(autopay, null!, default),
            () => new CancelTenantAutopayHandler(null!).AuthorizeReplayAsync(autopay, null!, default),
            () => new PrepareAccountingDisconnectHandler(null!).HandleAsync(prepare, null!, default),
            () => new PrepareAccountingDisconnectHandler(null!).AuthorizeReplayAsync(prepare, null!, default),
            () => new FinalizeAccountingDisconnectHandler(null!).HandleAsync(finalize, null!, default),
            () => new FinalizeAccountingDisconnectHandler(null!).AuthorizeReplayAsync(finalize, null!, default),
            () => new SetAccountingDirectionHandler(null!).HandleAsync(direction, null!, default),
            () => new SetAccountingDirectionHandler(null!).AuthorizeReplayAsync(direction, null!, default),
            () => new ApplyAccountingPullResultHandler(null!).HandleAsync(pull, null!, default),
            () => new ApplyAccountingPullResultHandler(null!).AuthorizeReplayAsync(pull, null!, default),
            () => new ConfirmAccountingMappingHandler(null!).HandleAsync(mapping, null!, default),
            () => new ConfirmAccountingMappingHandler(null!).AuthorizeReplayAsync(mapping, null!, default),
            () => new ContinueAccountingMappingPromotionHandler(null!).HandleAsync(continuation, null!, default),
            () => new ContinueAccountingMappingPromotionHandler(null!).AuthorizeReplayAsync(continuation, null!, default),
        };

        foreach (var call in calls)
        {
            await call.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*shared write executor*");
        }
    }

    private static IAtomicCommandData[] Commands() =>
    [
        CreateLedger(), UpdateLedger(false), UpdateLedger(true), PrepareConnect(), CancelAutopay(),
        PrepareDisconnect(), FinalizeDisconnect(), SetDirection(), ApplyPull(), ConfirmMapping(),
        ContinueMapping(),
    ];

    private static CreateLedgerAccountCommand CreateLedger() => new(
        1, 7, SessionId, 8, 9, "", "Frozen income", AccountType.Income,
        null, 41, null, ScheduleECategory.Other, true, "ledger-create");

    private static UpdateLedgerAccountCommand UpdateLedger(bool delete) => new(
        1, 7, SessionId, 8, 9, 42, delete ? null : "Frozen update", null,
        41, true, ScheduleECategory.Other, null, delete, delete ? "ledger-delete" : "ledger-update");

    private static PrepareAccountingConnectCommand PrepareConnect() => new(
        1, 7, SessionId, 8, 9, AccountingProvider.QuickBooks,
        "https://example.test/callback", "connect");

    private static CancelTenantAutopayCommand CancelAutopay() => new(
        1, 7, SessionId, 8, 9, 10, 11, "autopay-cancel");

    private static PrepareAccountingDisconnectCommand PrepareDisconnect() => new(
        1, 7, SessionId, 8, 9, AccountingProvider.QuickBooks, "disconnect-prepare");

    private static FinalizeAccountingDisconnectCommand FinalizeDisconnect() => new(
        1, 7, SessionId, 8, 9, AccountingProvider.QuickBooks, 12, 3,
        BusinessNow, "disconnect-prepare");

    private static SetAccountingDirectionCommand SetDirection() => new(
        1, 7, SessionId, 8, 9, AccountingProvider.QuickBooks, true, false, "direction");

    private static ApplyAccountingPullResultCommand ApplyPull() => new(
        1, 12, ClaimId, "batch", Array.Empty<ExtCustomerDto>(), Array.Empty<ExtVendorDto>(),
        Array.Empty<ExtAccountDto>(), Array.Empty<ExtPaymentDto>(), Array.Empty<ExtExpenseDto>(),
        "{}", BusinessNow);

    private static ConfirmAccountingMappingCommand ConfirmMapping() => new(
        1, 12, AccountingProvider.QuickBooks, 7, SessionId, 8, 9,
        CapabilityKeys.IntegrationsManage, "Customer", "external-1", "Frozen tenant",
        "Tenant", 10, null, "mapping-confirm", 0, BusinessNow);

    private static ContinueAccountingMappingPromotionCommand ContinueMapping() => new(
        1, 12, ContinuationId, 7, SessionId, 8, 9, CapabilityKeys.IntegrationsManage,
        "mapping-continue", BusinessNow);

    private static async Task ReplayAsync<TCommand, TResult>(
        ServiceProvider services,
        string operation,
        string key,
        TCommand command,
        string legacyFingerprint,
        string legacyResultJson,
        TResult expected,
        string contract,
        Func<RentalCommandDbContext,
            Func<TCommand, IAtomicCommandContext, CancellationToken, Task>> authorize)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var write = AccountingWriteSupport.Write<TCommand, TResult>(
            command,
            (_, _, _) => throw new InvalidOperationException("A stored receipt must not execute."),
            authorize(db));
        write.OperationName.Should().Be(operation);
        write.ResultContract.Should().Be(contract);

        await using (var fixtureDb = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseNpgsql(db.Database.GetConnectionString()).Options))
        {
            fixtureDb.AtomicCommandReceipts.Add(new AtomicCommandReceipt
            {
                Id = Guid.NewGuid(),
                AttemptId = Guid.NewGuid(),
                CommandType = operation,
                IdempotencyKey = key,
                RequestFingerprint = legacyFingerprint,
                Status = AtomicCommandReceiptStatus.Completed,
                ResultContract = contract,
                ResultJson = legacyResultJson,
                StartedAt = BusinessNow,
                CompletedAt = BusinessNow,
            });
            await fixtureDb.SaveChangesAsync();
        }

        var outcome = await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(key, write);
        outcome.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        outcome.Value.Should().BeEquivalentTo(expected);
    }

    private static async Task SeedFrozenReplayAuthorityAsync(RentalCommandDbContext db)
    {
        var authorityPast = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var user = new ApplicationUser
        {
            Id = 7,
            UserName = "accounting-replay@example.test",
            NormalizedUserName = "ACCOUNTING-REPLAY@EXAMPLE.TEST",
            Email = "accounting-replay@example.test",
            NormalizedEmail = "ACCOUNTING-REPLAY@EXAMPLE.TEST",
            DisplayName = "Accounting Replay",
            SecurityStamp = "accounting-replay-stamp",
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = authorityPast,
        };
        db.Add(user);
        await db.SaveChangesAsync();

        var access = new WorkspaceAccessContext
        {
            Id = 8,
            UserId = 7,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = authorityPast,
            UpdatedAtUtc = authorityPast,
        };
        while (access.AccessRevision < 9)
            access.AdvanceRevision(access.AccessRevision);
        var membership = new WorkspaceMembership
        {
            AccessContext = access,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            EffectiveFromUtc = authorityPast,
            CreatedAtUtc = authorityPast,
            UpdatedAtUtc = authorityPast,
        };
        var session = new AuthSession
        {
            Id = SessionId,
            UserId = 7,
            ActiveAccessContext = access,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = authorityPast,
            LastSeenAtUtc = authorityPast,
            ExpiresAtUtc = BusinessNow.AddYears(1),
        };
        db.AddRange(membership, session);
        await db.SaveChangesAsync();
        db.Add(new MembershipRoleAssignment
        {
            WorkspaceMembershipId = membership.Id,
            PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(
                role => role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = authorityPast,
            CreatedAtUtc = authorityPast,
            UpdatedAtUtc = authorityPast,
        });

        var property = new Property
        {
            Id = 20,
            PortfolioId = 1,
            Name = "Frozen Property",
            AddressLine1 = "1 Frozen Way",
            City = "Test",
            State = "PA",
            PostalCode = "19000",
            CreatedAt = authorityPast,
            UpdatedAt = authorityPast,
        };
        var unit = new Unit
        {
            Id = 21,
            PortfolioId = 1,
            Property = property,
            UnitNumber = "1",
            CreatedAt = authorityPast,
            UpdatedAt = authorityPast,
        };
        var tenant = new Tenant
        {
            Id = 10,
            PortfolioId = 1,
            FirstName = "Frozen",
            LastName = "Tenant",
            CreatedAt = authorityPast,
            UpdatedAt = authorityPast,
        };
        var management = new LeaseManagement
        {
            Id = 22,
            PortfolioId = 1,
            Property = property,
            Unit = unit,
            RelationshipNumber = "LM-FROZEN",
            CreatedAtUtc = authorityPast,
            UpdatedAtUtc = authorityPast,
            CreatedByUserId = 7,
            RowVersion = Guid.NewGuid(),
        };
        var party = new LeaseManagementParty
        {
            Id = 23,
            PortfolioId = 1,
            LeaseManagement = management,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2020, 1, 1),
            ChangeReason = "Frozen replay authority",
            CreatedAtUtc = authorityPast,
            CreatedByUserId = 7,
        };
        var account = new TenantAccount
        {
            Id = 11,
            PublicId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            PortfolioId = 1,
            LeaseManagement = management,
            AccountNumber = "TA-FROZEN",
            Currency = "USD",
            OpenedAtUtc = authorityPast,
            CreatedAtUtc = authorityPast,
            CreatedByUserId = 7,
        };
        db.AddRange(property, unit, tenant, management, party, account);
        await db.SaveChangesAsync();
        db.AddRange(
            new TenantUserAccess
            {
                Id = 24,
                PublicId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                PortfolioId = 1,
                AccessContextId = 8,
                ApplicationUserId = 7,
                LeaseManagementPartyId = 23,
                GrantedAtUtc = authorityPast,
                GrantedByUserId = 7,
                Reason = "Frozen replay authority",
            },
            new TenantAutopayEnrollment
            {
                Id = 25,
                PortfolioId = 1,
                TenantAccountId = 11,
                AuthorizingPartyId = 23,
                Provider = "stripe",
                ProviderCustomerId = "cus_frozen",
                ProviderPaymentMethodId = "pm_frozen",
                EnrolledAtUtc = authorityPast,
                CreatedByUserId = 7,
            },
            new AccountingConnection
            {
                Id = 12,
                PortfolioId = 1,
                Provider = AccountingProvider.QuickBooks,
                Status = AccountingConnectionStatus.Connected,
                PullEnabled = true,
                PushEnabled = false,
                CreatedAt = authorityPast,
                UpdatedAt = authorityPast,
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task ExecuteRecordedAsync<TCommand, TResult>(
        TCommand command,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task<TResult>> execute,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorize,
        IAtomicCommandContext context)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var write = AccountingWriteSupport.Write(command, execute, authorize);
        foreach (var writeLock in write.LockPlan.Locks)
            await writeLock.AcquireAsync(context);
        await write.ExecuteAsync(command, context, CancellationToken.None);
    }

    private static (IAtomicCommandContext Context, List<string> Acquired) Recorder(
        RentalCommandDbContext db)
    {
        var acquired = new List<string>();
        var recorder = new Mock<IAtomicCommandContext>();
        recorder.Setup(item => item.ReadDatabaseClockUtcAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTime.UtcNow);
        recorder.Setup(item => item.AcquireLockAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((name, id, _) => acquired.Add($"{name}:{id}"))
            .Returns(Task.CompletedTask);
        recorder.Setup(item => item.AcquireLockAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid, CancellationToken>((name, id, _) => acquired.Add($"{name}:{id}"))
            .Returns(Task.CompletedTask);
        recorder.Setup(item => item.FlushBusinessAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
                new AtomicBusinessFlush(await db.SaveChangesAsync(ct), []));
        return (recorder.Object, acquired);
    }

    private static string[] Prefix(WorkspaceReadScope scope) =>
        [$"AuthSession:{scope.SessionId}", $"WorkspaceAccessContext:{scope.AccessContextId}",
            $"Portfolio:{scope.PortfolioId}"];

    private static async Task<WorkspaceReadScope> SeedAuthorityAsync(
        RentalCommandDbContext db, string suffix)
    {
        var now = DateTime.UtcNow;
        var portfolio = new Portfolio
        {
            Name = $"Accounting {suffix}", ManagementCompanyName = "Recorder", TimeZone = "UTC",
            CreatedAt = now, UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            UserName = $"{suffix}@example.test", NormalizedUserName = $"{suffix}@example.test".ToUpperInvariant(),
            Email = $"{suffix}@example.test", NormalizedEmail = $"{suffix}@example.test".ToUpperInvariant(),
            DisplayName = suffix, CreatedAt = now,
        };
        db.AddRange(portfolio, user);
        await db.SaveChangesAsync();
        var access = new WorkspaceAccessContext
        {
            UserId = user.Id, PortfolioId = portfolio.Id, Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = access, PortfolioId = portfolio.Id, Status = WorkspaceMembershipStatus.Active,
            EffectiveFromUtc = now.AddMinutes(-1), CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, ActiveAccessContext = access,
            Status = AuthSessionStatus.Active, CreatedAtUtc = now, LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        db.AddRange(new MembershipRoleAssignment
        {
            WorkspaceMembership = membership, PortfolioId = portfolio.Id,
            RoleProfileId = AccessCatalog.Roles.Single(role => role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1), CreatedAtUtc = now, UpdatedAtUtc = now,
        }, session);
        await db.SaveChangesAsync();
        return new WorkspaceReadScope(
            portfolio.Id, user.Id, session.Id, access.Id, access.AccessRevision);
    }

    private static async Task<LedgerAccount> AddLedgerAsync(
        RentalCommandDbContext db, int portfolioId, string code, string name)
    {
        var now = DateTime.UtcNow;
        var account = new LedgerAccount
        {
            PublicId = Guid.NewGuid(), PortfolioId = portfolioId, Code = code, Name = name,
            AccountType = AccountType.Expense, NormalBalance = NormalBalance.Debit,
            ScheduleECategory = ScheduleECategory.Other, CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private static async Task<AccountingConnection> AddConnectionAsync(
        RentalCommandDbContext db, int portfolioId)
    {
        var now = DateTime.UtcNow;
        var connection = new AccountingConnection
        {
            PortfolioId = portfolioId, Provider = AccountingProvider.QuickBooks,
            Status = AccountingConnectionStatus.Connected, AccessTokenCipherText = "access",
            RefreshTokenCipherText = "refresh", TokenExpiresAt = now.AddHours(1),
            PullEnabled = true, PushEnabled = false, CreatedAt = now, UpdatedAt = now,
        };
        db.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private static ServiceProvider BuildServices(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(connectionString).UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed class RecordingWriteExecutor : IWriteExecutor
    {
        public List<string> Acquired { get; } = [];

        public async Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            string idempotencyKey,
            TransactionalWrite<TCommand, TResult> write,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            var context = new Mock<IAtomicCommandContext>();
            context.Setup(item => item.AcquireLockAsync(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<string, int, CancellationToken>(
                    (name, id, _) => Acquired.Add($"{name}:{id}"))
                .Returns(Task.CompletedTask);
            context.Setup(item => item.AcquireLockAsync(
                    It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Callback<string, Guid, CancellationToken>(
                    (name, id, _) => Acquired.Add($"{name}:{id}"))
                .Returns(Task.CompletedTask);
            foreach (var writeLock in write.LockPlan.Locks)
                await writeLock.AcquireAsync(context.Object, ct);
            var result = await write.ExecuteAsync(write.Request, context.Object, ct);
            return new(result, AtomicCommandDisposition.Executed, Guid.NewGuid());
        }
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:accounting-write-executor";
        public string? IpAddress => "127.0.0.1";
    }
}
