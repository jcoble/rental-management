using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.Engine.Workers;
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

    // Frozen caller-reachable fingerprints. These values must never be regenerated from the current
    // command model or a current serialization helper.
    private const string CreateLedgerFingerprint = "8cbf039718e0a7e4360042f7d6b7444fd3320b6ae3576565551430ba3662604e";
    private const string UpdateLedgerFingerprint = "4ec0e7c4b1caeafe192b5bdce1fbddb0f0b73db951de23ccd655765f01135eca";
    private const string ConnectFingerprint = "fb16a890f207c64604b110b0803db778c424004a69f17d95d49cb7dc8ae09dd7";
    private const string CancelAutopayFingerprint = "d09d3653bd24cfb1b99b4766df012c2a0e1ac99d2fb072b8dd2d13d19a897cdc";
    private const string PrepareDisconnectFingerprint = "f2aaf57d508701af847c8e3f0c3405be644ce9dac760444ef811492f4ab1cc92";
    private const string FinalizeDisconnectFingerprint = "92711d12b97816763a18ea23e518d0dea48b45fac246faa865924eed0aab5580";
    private const string DirectionFingerprint = "3e742fa87f9fe6ae9961007390b6ba28d9baf3269f69f49aaafb199eddd29eaa";
    private const string PullFingerprint = "51020797a59711b56e1f874c7c140f92a681ce538fa00b59b41aa488e8763aaa";
    private const string WorkerPullFingerprint = "c1cd18bed932bf1f935d28c8d9de0e1ccfb6715ad3a742b53ec4b61c0979075a";
    private const string ConfirmMappingFingerprint = "5204c8def42f660f622163e6c7613031fddde928dbfd0a56211b421ef7c2fd0b";
    private const string ContinueMappingFingerprint = "52e187e41a1b64a9206c14749eaa7bdf2a659922d7a31e10bcce06b006c591c5";
    private const string AutomaticContinuationFingerprint = "841f77d9d29c5318016a23aeef8bb60caf85d0216997b5079cf5029d868f3477";

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
        var scope = FrozenScope();

        var createResult = new LedgerAccountMutationResult(
            LedgerAccountMutationOutcome.Applied,
            new LedgerAccountSnapshot(41, ledgerPublicId, 1, "4100", "Frozen income",
                AccountType.Income, NormalBalance.Credit, null, null,
                ScheduleECategory.Other, false, true, false));
        await SeedReceiptAsync(services, "accounting.ledger-account.create", "1:8:ledger-create",
            CreateLedgerFingerprint,
            "{\"Outcome\":0,\"Account\":{\"Id\":41,\"PublicId\":\"44444444-4444-4444-4444-444444444444\",\"PortfolioId\":1,\"Code\":\"4100\",\"Name\":\"Frozen income\",\"AccountType\":\"Income\",\"NormalBalance\":\"Credit\",\"ParentAccountId\":null,\"SystemKey\":null,\"ScheduleECategory\":\"Other\",\"IsSystem\":false,\"IsActive\":true,\"HasPostedLines\":false}}",
            "accounting.ledger-account.mutation.v1");

        var updateResult = new LedgerAccountMutationResult(
            LedgerAccountMutationOutcome.Applied,
            new LedgerAccountSnapshot(42, ledgerPublicId, 1, "4200", "Frozen update",
                AccountType.Expense, NormalBalance.Debit, 41, null,
                ScheduleECategory.Other, false, true, false));
        await SeedReceiptAsync(services, "accounting.ledger-account.update", "1:8:ledger-update",
            UpdateLedgerFingerprint,
            "{\"Outcome\":0,\"Account\":{\"Id\":42,\"PublicId\":\"44444444-4444-4444-4444-444444444444\",\"PortfolioId\":1,\"Code\":\"4200\",\"Name\":\"Frozen update\",\"AccountType\":\"Expense\",\"NormalBalance\":\"Debit\",\"ParentAccountId\":41,\"SystemKey\":null,\"ScheduleECategory\":\"Other\",\"IsSystem\":false,\"IsActive\":true,\"HasPostedLines\":false}}",
            "accounting.ledger-account.mutation.v1");

        await SeedReceiptAsync(services, "accounting.oauth-state.prepare", "1:8:QuickBooks:connect",
            ConnectFingerprint,
            "{\"StateToken\":\"frozen-state\",\"RedirectUri\":\"https://example.test/callback\",\"ExpiresAtUtc\":\"2099-08-21T12:10:00Z\"}",
            "rental.accounting-connect.prepare.v1");

        await SeedReceiptAsync(services, "tenant-autopay.cancel", "1:8:11:autopay-cancel",
            CancelAutopayFingerprint,
            "{\"Found\":true,\"Applied\":true,\"TenantAccountId\":11}",
            "rental.tenant-autopay.cancel.v1");

        await SeedReceiptAsync(services, "accounting.connection.disconnect.prepare",
            "1:8:QuickBooks:disconnect-prepare", PrepareDisconnectFingerprint,
            "{\"Outcome\":1,\"ConnectionId\":12,\"TokenGeneration\":3,\"PreparedAtUtc\":\"2099-08-21T12:00:00Z\",\"RefreshTokenCipherText\":\"frozen-refresh\"}",
            "rental.accounting-disconnect.prepare.v1");

        await SeedReceiptAsync(services, "accounting.connection.disconnect.finalize",
            "1:8:QuickBooks:disconnect-prepare", FinalizeDisconnectFingerprint,
            "{\"Outcome\":1,\"ConnectionId\":12,\"TokenGeneration\":3,\"DisconnectedAtUtc\":\"2099-08-21T12:00:00Z\"}",
            "rental.accounting-disconnect.finalize.v1");

        await SeedReceiptAsync(services, "accounting.connection.direction.set", "1:8:QuickBooks:direction",
            DirectionFingerprint,
            "{\"Found\":true,\"ConnectionId\":12,\"PullEnabled\":true,\"PushEnabled\":false,\"UpdatedAtUtc\":\"2099-08-21T12:00:00Z\"}",
            "rental.accounting-direction.set.v1");

        await SeedReceiptAsync(services, "accounting.mapping.confirm",
            "1:12:7:269F04B029340016B186403090324E5083DCD7085C7F88E236612D4D37FEB0EE:B150E3C4EE698C9614756FBB203AE61516A2ECD9C973D8A8D7C0337E6594F0C8",
            ConfirmMappingFingerprint,
            "{\"Outcome\":0,\"MappingId\":51,\"MappingRevision\":2,\"PromotedCount\":3,\"ContinuationId\":\"33333333-3333-3333-3333-333333333333\",\"HasMore\":true}",
            "accounting.mapping.confirm.result.v2");

        await SeedReceiptAsync(services, "accounting.mapping.promote.continue",
            "1:12:33333333333333333333333333333333:7:D81913CFBC63EAC7355945E8A6ACB98D4BA34275B1E4F132D29B7AF6A14217D1",
            ContinueMappingFingerprint,
            "{\"Outcome\":0,\"ContinuationId\":\"33333333-3333-3333-3333-333333333333\",\"PromotedCount\":2,\"TotalPromotedCount\":5,\"HasMore\":false}",
            "accounting.mapping.promote.continue.result.v1");

        var automaticContinuation = new ContinueAccountingMappingPromotionCommand(
            1, 12, ContinuationId, 7, SessionId, 8, 9, CapabilityKeys.IntegrationsManage,
            "batch:1:B150E3C4EE698C9614756FBB203AE61516A2ECD9C973D8A8D7C0337E6594F0C8",
            BusinessNow);
        AtomicCommandFingerprint.Create(automaticContinuation)
            .Should().Be(AutomaticContinuationFingerprint);
        await SeedReceiptAsync(services, "accounting.mapping.promote.continue",
            "1:12:33333333333333333333333333333333:7:88F9D285419326CD7380548E46FA528BFF24AEA17B7805749D6994E3A7F8C6CC",
            AutomaticContinuationFingerprint,
            "{\"Outcome\":0,\"ContinuationId\":\"33333333-3333-3333-3333-333333333333\",\"PromotedCount\":2,\"TotalPromotedCount\":5,\"HasMore\":false}",
            "accounting.mapping.promote.continue.result.v1");

        await using var callerScope = services.CreateAsyncScope();
        var db = callerScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = callerScope.ServiceProvider.GetRequiredService<IWriteExecutor>();
        var controller = CreateAccountingController(db, writes);
        InstallAccessContext(controller);
        var created = await controller.CreateChartOfAccounts(
            new CreateChartOfAccountsRequest
            {
                Name = "Frozen income", ParentAccountId = 41,
                ScheduleECategory = ScheduleECategory.Other, IsActive = true,
            },
            "ledger-create", CancellationToken.None);
        ((CreatedAtActionResult)created.Result!).Value.Should().BeEquivalentTo(
            ToChartRow(createResult.Account!));
        var updated = await controller.PatchChartOfAccounts(42,
            new PatchChartOfAccountsRequest
            {
                Name = "Frozen update", ParentAccountId = 41,
                ScheduleECategory = ScheduleECategory.Other,
            }, "ledger-update", CancellationToken.None);
        ((OkObjectResult)updated.Result!).Value.Should().BeEquivalentTo(ToChartRow(updateResult.Account!));

        var connections = callerScope.ServiceProvider.GetRequiredService<AccountingConnectionService>();
        (await connections.StartConnectAsync(
            scope, AccountingProvider.QuickBooks, "https://example.test/callback", "connect",
            CancellationToken.None)).Should().Be("https://provider.test/frozen-state");
        var portal = new PortalService(db, new NoopLeaseQaService(), new FixedTimeProvider(), writes);
        (await portal.CancelAutopayAsync(
            FrozenAccess(), 10, 11, "autopay-cancel", CancellationToken.None))!
            .Active.Should().BeFalse();
        await connections.DisconnectAsync(
            scope, AccountingProvider.QuickBooks, "disconnect-prepare", CancellationToken.None);
        await connections.SetDirectionAsync(
            scope, AccountingProvider.QuickBooks, true, false, "direction", CancellationToken.None);

        var confirmed = await connections.ConfirmMappingAsync(
            scope, AccountingProvider.QuickBooks, new ConfirmAccountingMappingRequest
            {
                ExternalType = "Customer", ExternalId = "external-1",
                ExternalDisplayName = "Frozen tenant", LocalEntityType = "Tenant",
                LocalEntityId = 10, ClientOperationId = "mapping-confirm", ExpectedRevision = 0,
            }, CancellationToken.None);
        confirmed.Should().BeEquivalentTo(new ConfirmAccountingMappingResponse
        {
            MappingId = 51, MappingRevision = 2, Promoted = 5,
            ContinuationId = null, HasMore = false,
        });
        (await connections.ContinueMappingPromotionAsync(
            scope, AccountingProvider.QuickBooks, ContinuationId,
            new ContinueAccountingMappingPromotionRequest { ClientOperationId = "mapping-continue" },
            CancellationToken.None)).Should().BeEquivalentTo(
            new ContinueAccountingMappingPromotionResponse
            {
                Promoted = 2, TotalPromoted = 5, HasMore = false,
            });

        await ReplayPullThroughWorkerAsync(services);

        db.ChangeTracker.Clear();
        (await db.LedgerAccounts.CountAsync()).Should().Be(0);
        (await db.OAuthStates.CountAsync()).Should().Be(0);
        (await db.TenantAutopayEnrollments.CountAsync(row => row.Id == 25)).Should().Be(1);
        (await db.AccountingConnections.CountAsync(row => row.Id == 12)).Should().Be(1);
        (await db.AccountingEntityMappings.CountAsync()).Should().Be(0);
        (await db.AccountingSyncMaps.CountAsync()).Should().Be(0);
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

        await SeedReceiptAsync(services, "accounting.mapping.confirm",
            "1:12:7:269F04B029340016B186403090324E5083DCD7085C7F88E236612D4D37FEB0EE:B150E3C4EE698C9614756FBB203AE61516A2ECD9C973D8A8D7C0337E6594F0C8",
            ConfirmMappingFingerprint,
            "{\"Outcome\":0,\"MappingId\":51,\"MappingRevision\":2,\"PromotedCount\":3,\"ContinuationId\":\"33333333-3333-3333-3333-333333333333\",\"HasMore\":true}",
            "accounting.mapping.confirm.result.v2");
        await using var scope = services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountingConnectionService>();
        var action = () => service.ConfirmMappingAsync(
            FrozenScope(), AccountingProvider.QuickBooks, new ConfirmAccountingMappingRequest
            {
                ExternalType = "Customer", ExternalId = "external-1",
                ExternalDisplayName = "Frozen tenant", LocalEntityType = "Tenant",
                LocalEntityId = 10, ClientOperationId = "mapping-confirm", ExpectedRevision = 0,
            }, CancellationToken.None);

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
        var handler = new CancelTenantAutopayRule(db);
        var write = AccountingWriteSupport.Write(
            command, handler.ExecuteAsync, handler.AuthorizeAsync);
        const string key = "1:8:11:autopay-cancel";

        var first = await scope.ServiceProvider.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync(key, write);
        var replay = await scope.ServiceProvider.GetRequiredService<IWriteExecutor>()
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
            var handler = new CreateLedgerAccountRule(database.Db);

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
        var handler = new UpdateLedgerAccountRule(database.Db);

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
        var handler = new SetAccountingDirectionRule(database.Db);

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
        var writes = serviceScope.ServiceProvider.GetRequiredService<IWriteExecutor>();

        var connect = AtomicAccountingConnect.Command(
            scope, AccountingProvider.QuickBooks, "https://example.test/callback", "connect-replay");
        var connectHandler = new PrepareAccountingConnectRule(db);
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
        var prepareHandler = new PrepareAccountingDisconnectRule(db);
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
        var finalizeHandler = new FinalizeAccountingDisconnectRule(db);
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
        var directionHandler = new SetAccountingDirectionRule(db);
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

    private static IAtomicCommandData[] Commands() =>
    [
        CreateLedger(), UpdateLedger(false), UpdateLedger(true), PrepareConnect(), CancelAutopay(),
        PrepareDisconnect(), FinalizeDisconnect(), SetDirection(), ApplyPull(), ConfirmMapping(),
        ContinueMapping(),
    ];

    private static CreateLedgerAccountCommand CreateLedger() => new(
        1, 7, SessionId, 8, 9, "", "Frozen income", AccountType.Expense,
        41, null, ScheduleECategory.Other, true, "ledger-create");

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

    private static async Task SeedReceiptAsync(
        ServiceProvider services,
        string operation,
        string key,
        string legacyFingerprint,
        string legacyResultJson,
        string contract)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
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
    }

    private static async Task ReplayPullThroughWorkerAsync(ServiceProvider services)
    {
        const string batchIdentity =
            "7061120C675CE6BCA0986EB89DBEB63CB3CD006CA0C438EA014B4FEA8D4C4C83";
        const string stepKey =
            "12:22222222222222222222222222222222:7061120C675CE6BCA0986EB89DBEB63CB3CD006CA0C438EA014B4FEA8D4C4C83";
        var command = new ApplyAccountingPullResultCommand(
            1, 12, ClaimId, batchIdentity, Array.Empty<ExtCustomerDto>(),
            Array.Empty<ExtVendorDto>(), Array.Empty<ExtAccountDto>(),
            Array.Empty<ExtPaymentDto>(), Array.Empty<ExtExpenseDto>(), "{}", BusinessNow);
        AtomicCommandFingerprint.Create(command).Should().Be(WorkerPullFingerprint);
        await SeedReceiptAsync(services, "accounting.pull.apply", stepKey, WorkerPullFingerprint,
            "{\"CustomersMapped\":1,\"VendorsMapped\":2,\"AccountsMapped\":3,\"PaymentsImported\":4,\"ExpensesImported\":5,\"NeedsReview\":6}",
            "accounting.pull.apply.v1");

        var worker = new AccountingPullWorker(
            services, NullLogger<AccountingPullWorker>.Instance);
        var cycle = typeof(AccountingPullWorker).GetMethod(
            "ExecuteCycleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await using var scope = services.CreateAsyncScope();
        var invocation = (Task<int>)cycle.Invoke(
            worker, [scope.ServiceProvider, CancellationToken.None])!;
        (await invocation).Should().Be(9);
    }

    private static AccountingController CreateAccountingController(
        RentalCommandDbContext db, IWriteExecutor writes) => new(
        Mock.Of<IAccountingService>(),
        Mock.Of<IScheduleEService>(),
        Mock.Of<IOwnerStatementService>(),
        Mock.Of<IOwnerStatementEmailService>(),
        Mock.Of<IReportsService>(),
        Mock.Of<IAccountingLedgerReadModelService>(),
        writes,
        db,
        new FixedTimeProvider());

    private static ChartOfAccountsRow ToChartRow(LedgerAccountSnapshot account) => new()
    {
        Id = account.Id,
        PublicId = account.PublicId,
        Code = account.Code,
        Name = account.Name,
        AccountType = account.AccountType,
        NormalBalance = account.NormalBalance,
        ParentAccountId = account.ParentAccountId,
        SystemKey = account.SystemKey,
        ScheduleECategory = account.ScheduleECategory,
        IsSystem = account.IsSystem,
        IsActive = account.IsActive,
        HasPostedLines = account.HasPostedLines,
    };

    private static WorkspaceReadScope FrozenScope() => new(1, 7, SessionId, 8, 9);

    private static ActiveAccessContext FrozenAccess() => new(
        SessionId, 7, 8, 1, 9, WorkspaceExperience.Management, 1,
        WorkspaceExperience.Management);

    private static void InstallAccessContext(ControllerBase controller)
    {
        var http = new DefaultHttpContext();
        http.Items[CanonicalAccessContextHttpItem.Key] = FrozenAccess();
        controller.ControllerContext = new ControllerContext { HttpContext = http };
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
        var provider = new Mock<IAccountingProvider>();
        provider.SetupGet(item => item.Provider).Returns(AccountingProvider.QuickBooks);
        provider.SetupGet(item => item.Capabilities).Returns(
            new AccountingCapabilities(false, false, false, false, false, false, false));
        provider.Setup(item => item.BuildAuthorizeUrl(
                It.IsAny<AccountingAppSettings>(), It.IsAny<string>(), It.IsAny<string>(), null))
            .Returns<AccountingAppSettings, string, string, string?>(
                (_, _, state, _) => $"https://provider.test/{state}");
        var options = new Mock<IOptionsMonitor<QuickBooksOptions>>();
        options.SetupGet(item => item.CurrentValue).Returns(new QuickBooksOptions
        {
            ClientId = "frozen-client",
            ClientSecret = "frozen-secret",
            RedirectUri = "https://example.test/callback",
        });
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider());
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(options.Object);
        services.AddSingleton<IAccountingProvider>(provider.Object);
        services.AddSingleton<AccountingAppSettingsResolver>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IAccountingConnectionClaimStore, FrozenClaimStore>();
        services.AddScoped<AccountingTokenService>();
        services.AddScoped<AccountingImportService>();
        services.AddScoped<AccountingConnectionService>();
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

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(BusinessNow);
    }

    private sealed class FrozenClaimStore(IDataProtectionProvider dataProtection)
        : IAccountingConnectionClaimStore
    {
        public Task<IReadOnlyList<AccountingConnectionClaim>> ClaimPullAsync(
            string claimOwner, TimeSpan leaseDuration, int batchSize,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AccountingConnectionClaim>>(
            [new AccountingConnectionClaim(new AccountingConnection
            {
                Id = 12,
                PortfolioId = 1,
                Provider = AccountingProvider.QuickBooks,
                Status = AccountingConnectionStatus.Connected,
                ExternalAccountId = "frozen-realm",
                AccessTokenCipherText = dataProtection
                    .CreateProtector("RentalCommand.Accounting.v1").Protect("frozen-access"),
                PullEnabled = true,
                PushEnabled = false,
                CreatedAt = BusinessNow,
                UpdatedAt = BusinessNow,
            }, new AccountingWorkerFence(AccountingWorkerOperation.Pull, ClaimId))]);

        public Task<IReadOnlyList<AccountingConnectionClaim>> ClaimTokenRefreshAsync(
            string claimOwner, TimeSpan refreshHorizon, TimeSpan leaseDuration, int batchSize,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<AccountingConnectionClaim?> ClaimInlineTokenRotationAsync(
            int connectionId, Guid? pullClaimToken, string claimOwner,
            TimeSpan leaseDuration, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> MarkPullFailedAsync(
            int id, Guid claimToken, TimeSpan retryDelay, string error,
            CancellationToken ct = default) => throw new InvalidOperationException(error);

        public Task<int> MarkTokenRotationRecoveryRequiredAsync(
            int id, Guid claimToken, string error,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> CompleteTokenRotationAsync(
            int id, Guid claimToken, Guid? parentPullClaimToken,
            string accessTokenCipherText, string refreshTokenCipherText,
            DateTime expiresAtUtc, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> ReconcileAbandonedTokenRotationsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
