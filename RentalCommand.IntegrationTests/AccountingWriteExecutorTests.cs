using FluentAssertions;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Data.Accounting;

namespace RentalCommand.IntegrationTests;

public sealed class AccountingWriteExecutorTests
{
    private static readonly Guid SessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClaimId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ContinuationId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime BusinessNow =
        new(2099, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LedgerLifecycleAndMapping_PreserveFrozenFingerprints()
    {
        AtomicCommandFingerprint.Create(CreateLedger()).Should().Be(
            "48ed7e9b6cdc8b374b48507346caed81e4f855459438c3ae5c0f5a7aaa183ad8");
        AtomicCommandFingerprint.Create(PrepareDisconnect()).Should().Be(
            "f2aaf57d508701af847c8e3f0c3405be644ce9dac760444ef811492f4ab1cc92");
        AtomicCommandFingerprint.Create(ConfirmMapping()).Should().Be(
            "5204c8def42f660f622163e6c7613031fddde928dbfd0a56211b421ef7c2fd0b");
    }

    [Fact]
    public void ElevenWrites_PreserveIdentitiesResultContractsAndPublishedLockPlans()
    {
        var expected = new[]
        {
            ("accounting.ledger-account.create", "accounting.ledger-account.mutation.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "Portfolio" }),
            ("accounting.ledger-account.update", "accounting.ledger-account.mutation.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "Portfolio", "LedgerAccount" }),
            ("accounting.ledger-account.update", "accounting.ledger-account.mutation.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "Portfolio", "LedgerAccount" }),
            ("accounting.oauth-state.prepare", "rental.accounting-connect.prepare.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "Portfolio" }),
            ("tenant-autopay.cancel", "rental.tenant-autopay.cancel.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "TenantAccount" }),
            ("accounting.connection.disconnect.prepare", "rental.accounting-disconnect.prepare.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "Portfolio" }),
            ("accounting.connection.disconnect.finalize", "rental.accounting-disconnect.finalize.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "AccountingConnection" }),
            ("accounting.connection.direction.set", "rental.accounting-direction.set.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "Portfolio" }),
            ("accounting.pull.apply", "accounting.pull.apply.v1",
                new[] { "AccountingConnection" }),
            ("accounting.mapping.confirm", "accounting.mapping.confirm.result.v2",
                new[] { "AuthSession", "WorkspaceAccessContext", "AccountingConnection" }),
            ("accounting.mapping.promote.continue", "accounting.mapping.promote.continue.result.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "AccountingConnection" }),
        };

        Commands().Zip(expected).Should().AllSatisfy(pair =>
        {
            var write = AccountingWriteSupport.Write(
                pair.First,
                (_, _, _) => Task.FromResult(true),
                (_, _, _) => Task.CompletedTask);
            write.OperationName.Should().Be(pair.Second.Item1);
            write.ResultContract.Should().Be(pair.Second.Item2);
            write.LockPlan.Locks.Select(item => item.LockNamespace)
                .Should().Equal(pair.Second.Item3);
        });
    }

    [Fact]
    public void DynamicLockTails_RemainAtTheirLegacyRulePositions()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var ledgerSource = File.ReadAllText(Path.Combine(root, "RentalCommand.Data", "Accounting",
            "LedgerAccountCommandHandlers.cs"));
        var create = Slice(ledgerSource, "public async Task<LedgerAccountMutationResult> ExecuteAsync(\n        CreateLedgerAccountCommand",
            "public Task AuthorizeReplayAsync", 0);
        create.IndexOf("AcquireLockAsync(\"LedgerAccount\", parentAccountId", StringComparison.Ordinal)
            .Should().BeLessThan(create.IndexOf("AcquireLockAsync(\"LedgerAccountCode\"", StringComparison.Ordinal));

        var lifecycleSource = File.ReadAllText(Path.Combine(root, "RentalCommand.Api", "Services", "Domain",
            "AtomicAccountingLifecycleMutations.cs"));
        var prepare = Slice(lifecycleSource,
            "public async Task<PrepareAccountingDisconnectResult> ExecuteAsync",
            "public Task AuthorizeReplayAsync", 0);
        prepare.Should().Contain("ResolveAndLockConnectionIdAsync");
        var direction = Slice(lifecycleSource,
            "public async Task<SetAccountingDirectionResult> ExecuteAsync",
            "public Task AuthorizeReplayAsync", 0);
        direction.Should().Contain("ResolveAndLockConnectionIdAsync");

        var mapping = AccountingWriteSupport.Write(
            ConfirmMapping(), (_, _, _) => Task.FromResult(true), (_, _, _) => Task.CompletedTask);
        mapping.LockPlan.Locks.Select(item => item.LockNamespace).Should()
            .Equal("AuthSession", "WorkspaceAccessContext", "AccountingConnection");
        mapping.LockPlan.Locks.Should().NotContain(item => item.LockNamespace == "Portfolio");
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
        BusinessNow, "disconnect-finalize");

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

    private static string Slice(string source, string startText, string endText, int startIndex)
    {
        var start = source.IndexOf(startText, startIndex, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = source.IndexOf(endText, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return source[start..end];
    }
}
