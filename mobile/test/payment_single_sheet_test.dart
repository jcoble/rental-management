import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/realtime/realtime_providers.dart';
import 'package:rental_command/core/time/app_clock.dart';
import 'package:rental_command/features/accounting/accounting_book_models.dart';
import 'package:rental_command/features/accounting/accounting_books_repository.dart';
import 'package:rental_command/features/accounting/accounting_models.dart';
import 'package:rental_command/features/accounting/accounting_repository.dart';
import 'package:rental_command/features/home/home_access_providers.dart';
import 'package:rental_command/features/home/home_shell.dart';
import 'package:rental_command/features/money/money_screen.dart';
import 'package:rental_command/features/money/overdue_screen.dart';
import 'package:rental_command/features/onboarding/getting_started_provider.dart';
import 'package:rental_command/features/onboarding/onboarding_repository.dart';
import 'package:rental_command/features/payments/payments_repository.dart'
    hide MoneySnapshot;
import 'package:rental_command/features/payments/payments_screen.dart';

void main() {
  testWidgets('rent still owed tile opens who is behind', (tester) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          accountingBooksRepositoryProvider.overrideWithValue(
            _FakeBooksRepository(),
          ),
          moneySnapshotProvider.overrideWith(
            (ref) => Future.error(StateError('money unavailable')),
          ),
          pastDueProvider.overrideWith(_StaticPastDueNotifier.new),
        ],
        child: const MaterialApp(
          home: MoneyScreen(initialView: MoneyScreenView.overview),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Rent still owed'), findsOneWidget);
    await tester.tap(find.text('Rent still owed'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));

    expect(find.byType(OverdueScreen), findsOneWidget);
  });

  testWidgets('receipt sheet shows three essentials and hides the rest', (
    tester,
  ) async {
    await _pumpReceiptSheet(tester, _FakePaymentsRepository());

    expect(find.text('Amount'), findsOneWidget);
    expect(find.text('How they paid'), findsOneWidget);
    expect(find.text('Received on'), findsOneWidget);
    expect(find.text('Description'), findsNothing);

    expect(find.text('Payer name (optional)'), findsNothing);
    expect(find.text('Reference (optional)'), findsNothing);
    expect(find.text('Notes'), findsNothing);

    await tester.tap(find.text('More details'));
    await tester.pumpAndSettle();

    expect(find.text('Payer name (optional)'), findsOneWidget);
    expect(find.text('Reference (optional)'), findsOneWidget);
    expect(find.text('Notes'), findsOneWidget);
    expect(find.text('Leave this payment unapplied'), findsOneWidget);
  });

  testWidgets('receipt sheet posts amount method date with default description',
      (tester) async {
    final repository = _FakePaymentsRepository();
    await _pumpReceiptSheet(tester, repository);

    await tester.enterText(
      find.byKey(const Key('tenant-receipt-amount')),
      '1200',
    );
    await tester.enterText(
      find.byKey(const Key('tenant-receipt-method')),
      'Check',
    );
    await tester.tap(find.byKey(const Key('tenant-receipt-submit')));
    await tester.pumpAndSettle();

    final input = repository.lastInput!;
    expect(repository.lastTenantAccountId, 42);
    expect(input.amount, 1200);
    expect(input.description, 'Rent payment');
    expect(input.paymentMethodSummary, 'Check');
    expect(input.effectiveOn, DateTime(2027, 1, 5));
    expect(input.allocateOldestCharges, isTrue);
    expect(input.targetChargeEntryId, isNull);
    expect(input.payerName, isNull);
    expect(input.externalReference, isNull);
  });

  testWidgets('today section exposes rent came in', (tester) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          authControllerProvider.overrideWith(
            () => _StaticAuthController(_authenticated()),
          ),
          appNowProvider.overrideWith((ref) async => DateTime(2026, 8, 25, 9)),
          homeBriefingProvider.overrideWith(
            (ref) => Future.error(StateError('briefing unavailable')),
          ),
          homeLatestMessagesProvider.overrideWith((ref) async => const []),
          homeFieldQueueProvider.overrideWith((ref) async => const []),
          moneySnapshotProvider.overrideWith((ref) async => _snapshot),
          sandboxStateProvider.overrideWith(
            (ref) => Future.error(StateError('sandbox unavailable')),
          ),
          gettingStartedSignalsProvider.overrideWith(
            (ref) => Future.error(StateError('signals unavailable')),
          ),
          realtimeWatcherProvider.overrideWith((ref) {}),
        ],
        child: const MaterialApp(home: HomeShell()),
      ),
    );
    await tester.pump();
    await tester.pump();

    await tester.dragUntilVisible(
      find.text('Rent came in'),
      find.byType(CustomScrollView).first,
      const Offset(0, -200),
    );

    expect(find.text('Rent came in'), findsOneWidget);
  });
}

const _snapshot = MoneySnapshot(
  periodLabel: 'August 2026',
  collected: 4200,
  spent: 900,
  net: 3300,
  pastDueAmount: 975,
  pastDueCount: 2,
  collectedLast30Days: 4200,
  spentLast30Days: 900,
  netLast30Days: 3300,
  explanations: MoneySnapshotExplanations(
    collected: 'Money in',
    spent: 'Money out',
    net: 'Money kept',
    pastDue: 'Two tenants are behind.',
  ),
);

Future<void> _pumpReceiptSheet(
  WidgetTester tester,
  _FakePaymentsRepository repository,
) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        paymentsRepositoryProvider.overrideWithValue(repository),
        appNowProvider.overrideWith((ref) async => DateTime.utc(2027, 1, 5, 5)),
      ],
      child: MaterialApp(
        home: Consumer(
          builder: (context, ref, _) => Scaffold(
            body: Center(
              child: FilledButton(
                onPressed: () => showRecordTenantReceiptSheet(
                  context,
                  ref,
                  tenantAccountId: 42,
                  leaseManagementId: 7,
                ),
                child: const Text('Open receipt sheet'),
              ),
            ),
          ),
        ),
      ),
    ),
  );

  await tester.tap(find.text('Open receipt sheet'));
  await tester.pumpAndSettle();
}

class _FakeBooksRepository extends AccountingBooksRepository {
  _FakeBooksRepository() : super(Dio());

  @override
  Future<MoneyPositionResponse> moneyPosition({
    DateTime? from,
    DateTime? to,
  }) async => MoneyPositionResponse(
    asOfUtc: DateTime.utc(2026, 8, 25),
    fromUtc: DateTime.utc(2026, 8, 1),
    toUtc: DateTime.utc(2026, 8, 31),
    totalCashOnHand: 5000,
    tenantDepositsHeld: 1000,
    cashAfterTenantDeposits: 4000,
    rentStillOwed: 975,
    loanBalance: 120000,
    bookEquity: 30000,
    cashReceived: 4200,
    cashPaid: 900,
    netCashMovement: 3300,
    profitOrLoss: 3300,
  );
}

class _FakePaymentsRepository extends PaymentsRepository {
  _FakePaymentsRepository() : super(Dio());

  int? lastTenantAccountId;
  RecordTenantReceiptInput? lastInput;

  @override
  Future<RecordTenantReceiptResult> recordReceipt(
    int tenantAccountId,
    RecordTenantReceiptInput input, {
    required String operationKey,
  }) async {
    lastTenantAccountId = tenantAccountId;
    lastInput = input;
    return const RecordTenantReceiptResult(
      tenantAccountId: 42,
      ledgerEntryId: 1,
      paymentAttemptId: 1,
      amount: 1200,
      allocatedAmount: 1200,
      allocationCount: 1,
      replayed: false,
    );
  }
}

class _StaticPastDueNotifier extends PastDueNotifier {
  @override
  PastDueState build() => const PastDueState();
}

class _StaticAuthController extends AuthController {
  _StaticAuthController(this.initialState);

  final AuthState initialState;

  @override
  AuthState build() => initialState;
}

AuthStateAuthenticated _authenticated() {
  const experience = WorkspaceExperience.management;
  return AuthStateAuthenticated(
    const AuthUser(
      id: 1,
      email: 'manager@example.test',
      displayName: 'Test manager',
      emailVerified: true,
    ),
    AccessEnvelope(
      identity: const AccessIdentity(userId: 1, displayName: 'Test manager'),
      selectedContext: SelectedAccessContext(
        accessContextId: 1,
        portfolioId: 1,
        workspaceName: 'Test workspace',
        accessRevision: 1,
        activeExperience: experience,
      ),
      defaultExperience: experience,
      availableExperiences: const [experience],
      assignments: const [],
      navigation: const [
        NavigationCapabilities(
          experience: experience,
          capabilityKeys: ['money.payments.manage'],
        ),
      ],
    ),
    activeExperience: experience,
  );
}
