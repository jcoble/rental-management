import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/features/accounting/accounting_models.dart';
import 'package:rental_command/features/accounting/accounting_repository.dart';
import 'package:rental_command/features/money/money_repository.dart';
import 'package:rental_command/features/money/money_screen.dart';
import 'package:rental_command/features/money/transaction_models.dart';
import 'package:rental_command/features/scan/scan_repository.dart';

void main() {
  test('tenant account option preserves exact receipt command context', () {
    final option = TenantAccountOption.fromJson({
      'tenantAccountId': 42,
      'leaseManagementId': 84,
      'relationshipNumber': 'REL-84',
      'propertyName': 'Maple Court',
      'unitNumber': '2B',
      'primaryTenantName': 'Jordan Lee',
    });

    expect(option.tenantAccountId, 42);
    expect(option.leaseManagementId, 84);
  });

  testWidgets('management payment capability exposes only Add payment', (
    tester,
  ) async {
    await _pumpMoney(
      tester,
      experience: WorkspaceExperience.management,
      capabilities: const {'money.payments.manage'},
    );

    await tester.tap(find.byTooltip('Scan / Add'));
    await tester.pumpAndSettle();

    expect(find.text('Add payment'), findsOneWidget);
    expect(find.text('Add expense'), findsNothing);
  });

  testWidgets('management expense capability exposes only Add expense', (
    tester,
  ) async {
    await _pumpMoney(
      tester,
      experience: WorkspaceExperience.management,
      capabilities: const {'money.expenses.manage'},
    );

    await tester.tap(find.byTooltip('Scan / Add'));
    await tester.pumpAndSettle();

    expect(find.text('Add payment'), findsNothing);
    expect(find.text('Add expense'), findsOneWidget);
  });

  testWidgets('non-management experience cannot use stale money mutation cap', (
    tester,
  ) async {
    await _pumpMoney(
      tester,
      experience: WorkspaceExperience.leasing,
      capabilities: const {'money.payments.manage'},
    );

    await tester.tap(find.byTooltip('Scan / Add'));
    await tester.pumpAndSettle();

    expect(find.text('Add payment'), findsNothing);
    expect(find.text('Add expense'), findsNothing);
  });
}

Future<void> _pumpMoney(
  WidgetTester tester, {
  required WorkspaceExperience experience,
  required Set<String> capabilities,
}) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        authControllerProvider.overrideWith(
          () => _StaticAuthController(
            _authenticatedState(experience, capabilities),
          ),
        ),
        accountingRepositoryProvider.overrideWithValue(
          _FakeAccountingRepository(),
        ),
        moneyRepositoryProvider.overrideWithValue(_FakeMoneyRepository()),
      ],
      child: const MaterialApp(
        home: MoneyScreen(initialView: MoneyScreenView.ledger),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

AuthStateAuthenticated _authenticatedState(
  WorkspaceExperience experience,
  Set<String> capabilities,
) {
  return AuthStateAuthenticated(
    const AuthUser(
      id: 1,
      email: 'test@example.test',
      displayName: 'Test user',
      emailVerified: true,
    ),
    AccessEnvelope(
      identity: const AccessIdentity(userId: 1, displayName: 'Test user'),
      selectedContext: SelectedAccessContext(
        accessContextId: 1,
        portfolioId: 1,
        workspaceName: 'Test workspace',
        accessRevision: 1,
        activeExperience: experience,
      ),
      defaultExperience: experience,
      availableExperiences: [experience],
      assignments: const [],
      navigation: [
        NavigationCapabilities(
          experience: experience,
          capabilityKeys: capabilities.toList(growable: false),
        ),
      ],
    ),
    activeExperience: experience,
  );
}

class _StaticAuthController extends AuthController {
  _StaticAuthController(this.initialState);

  final AuthState initialState;

  @override
  AuthState build() => initialState;
}

class _FakeAccountingRepository extends AccountingRepository {
  _FakeAccountingRepository() : super(Dio());

  @override
  Future<MoneySnapshot> snapshot() async {
    return const MoneySnapshot(
      periodLabel: 'July 2026',
      collected: 0,
      spent: 0,
      net: 0,
      pastDueAmount: 0,
      pastDueCount: 0,
      collectedLast30Days: 0,
      spentLast30Days: 0,
      netLast30Days: 0,
      explanations: MoneySnapshotExplanations(
        collected: '',
        spent: '',
        net: '',
        pastDue: '',
      ),
    );
  }
}

class _FakeMoneyRepository extends MoneyRepository {
  _FakeMoneyRepository() : super(Dio());

  @override
  Future<AccountingTransactionsPage> transactions({
    required int skip,
    required int take,
    TransactionsFilter filter = const TransactionsFilter(),
  }) async {
    return AccountingTransactionsPage(
      items: const [],
      totalCount: 0,
      skip: skip,
      take: take,
    );
  }
}
