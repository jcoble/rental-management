import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/portal/tenant_account_history_screen.dart';
import 'package:rental_command/features/portal/tenant_portal_repository.dart';

void main() {
  testWidgets(
    'direct account history loads focused entry when unrelated portal sources fail',
    (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            tenantPortalSnapshotProvider.overrideWith(
              (ref) => Future<TenantPortalSnapshot>.error(
                const FormatException('unrelated snapshot parser failed'),
              ),
            ),
            tenantPortalAccountsPageProvider.overrideWith(
              (ref, request) => Future<PortalTenantAccountPage>.error(
                const FormatException('unrelated account picker parser failed'),
              ),
            ),
            tenantPortalAccountHistoryProvider.overrideWith((
              ref,
              request,
            ) async {
              expect(request.tenantAccountId, 8);
              expect(request.focusedEntryId, 872);
              expect(request.period, TenantAccountHistoryPeriod.all);
              return _history872();
            }),
            tenantAutopayStatusProvider.overrideWith(
              (ref, tenantAccountId) async => AutopayStatus(
                tenantAccountId: tenantAccountId,
                active: false,
                onlinePaymentsAvailable: false,
              ),
            ),
          ],
          child: const MaterialApp(
            home: TenantAccountHistoryScreen(
              initialTenantAccountId: 8,
              initialTenantLedgerEntryId: 872,
            ),
          ),
        ),
      );

      await tester.pumpAndSettle();

      expect(find.byKey(const Key('account-history-row-872')), findsOneWidget);
      expect(
        find.byKey(const Key('account-history-current-due')),
        findsOneWidget,
      );
      expect(find.byKey(const Key('account-history-page-error')), findsNothing);
      expect(find.byKey(const Key('account-history-error')), findsNothing);
    },
  );

  test('direct money history keeps required financial values contractual', () {
    expect(
      () => PortalTenantAccountHistory.fromJson({
        'tenantAccountId': 8,
        'leaseManagementId': 8,
        'currency': 'USD',
        'businessDate': '2027-01-29',
        'period': 'all',
        'periodFrom': null,
        'periodTo': '2027-01-29',
        'currentDue': null,
        'beginningBalance': 0,
        'closingBalance': 1650,
        'totalCount': 1,
        'skip': 0,
        'take': 20,
        'items': const [],
      }),
      throwsA(isA<TypeError>()),
    );
  });
}

PortalTenantAccountHistory _history872() => PortalTenantAccountHistory(
  tenantAccountId: 8,
  leaseManagementId: 8,
  currency: 'USD',
  businessDate: DateTime(2027, 1, 29),
  period: 'all',
  periodFrom: null,
  periodTo: DateTime(2027, 1, 29),
  currentDue: 1650,
  beginningBalance: 0,
  closingBalance: 1650,
  items: [
    PortalTenantAccountHistoryItem(
      tenantLedgerEntryId: 872,
      entryType: 'RentCharge',
      direction: 'Debit',
      displayType: 'Rent charge',
      description: 'January rent',
      effectiveOn: DateTime(2027, 1, 1),
      dueOn: DateTime(2027, 1, 1),
      postedAtUtc: DateTime.utc(2027, 1, 1, 12),
      signedAmount: 1650,
      runningBalance: 1650,
      openAmount: 1650,
      payable: true,
      isFocused: true,
    ),
  ],
  totalCount: 1,
  skip: 0,
  take: 20,
);
