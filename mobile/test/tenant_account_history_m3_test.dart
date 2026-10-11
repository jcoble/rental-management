import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/portal/tenant_account_history_screen.dart';
import 'package:rental_command/features/portal/tenant_portal_repository.dart';

void main() {
  test('tenant history maps every row to the fixed public vocabulary', () {
    expect(tenantPortalLedgerLabel(_entry('RentCharge')), 'Rent charge');
    expect(tenantPortalLedgerLabel(_entry('LateFeeCharge')), 'Late fee');
    expect(
      tenantPortalLedgerLabel(_entry('PaymentReceipt')),
      'Payment received — thank you',
    );
    expect(tenantPortalLedgerLabel(_entry('Credit')), 'Credit');
    expect(tenantPortalLedgerLabel(_entry('Refund')), 'Refund');
    expect(tenantPortalLedgerLabel(_entry('JournalCorrection')), 'Correction');
  });

  test('statement HTML keeps server balances and hides management fields', () {
    final html = tenantPortalStatementHtml(
      account: _account(),
      history: _history(),
    );

    expect(html, contains('Current balance'));
    expect(html, contains('Past due'));
    expect(html, contains('Next due'));
    expect(html, contains('Deposit held'));
    expect(html, contains('Balance'));
    expect(html, contains('USD 1,250.00'));
    expect(html, contains('Payment received — thank you'));
    expect(html, isNot(contains('TA-41-private')));
    expect(html, isNot(contains('PaymentReceipt')));
    expect(html, isNot(contains('Debit')));
    expect(html, isNot(contains('journal')));
  });

  testWidgets(
    'tenant portal renders server balances and shares a safe statement',
    (tester) async {
      Uint8List? sharedBytes;
      String? sharedFileName;
      String? sharedMimeType;
      Future<void> sharer({
        required Uint8List bytes,
        required String fileName,
        String mimeType = 'application/pdf',
      }) async {
        sharedBytes = bytes;
        sharedFileName = fileName;
        sharedMimeType = mimeType;
      }

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            tenantPortalAccountsPageProvider.overrideWith(
              (ref, request) async => PortalTenantAccountPage(
                items: [_account()],
                totalCount: 1,
                skip: 0,
                take: 20,
              ),
            ),
            tenantPortalAccountProvider.overrideWith(
              (ref, tenantAccountId) async => _account(),
            ),
            tenantPortalAccountHistoryProvider.overrideWith(
              (ref, request) async => _history(),
            ),
            tenantAutopayStatusProvider.overrideWith(
              (ref, tenantAccountId) async => const AutopayStatus(
                tenantAccountId: 41,
                active: false,
                onlinePaymentsAvailable: false,
              ),
            ),
            tenantStatementSharerProvider.overrideWithValue(sharer),
          ],
          child: const MaterialApp(
            home: TenantAccountHistoryScreen(initialTenantAccountId: 41),
          ),
        ),
      );

      await tester.pumpAndSettle();

      expect(
        find.byKey(const Key('tenant-portal-balance-header')),
        findsOneWidget,
      );
      expect(find.byKey(const Key('tenant-portal-past-due')), findsOneWidget);
      expect(find.byKey(const Key('tenant-portal-next-due')), findsOneWidget);
      expect(
        find.byKey(const Key('tenant-portal-deposit-held')),
        findsOneWidget,
      );
      expect(
        find.byKey(
          const Key('tenant-portal-history-label-1'),
          skipOffstage: false,
        ),
        findsOneWidget,
      );
      await tester.drag(
        find.byKey(const Key('account-history-list')),
        const Offset(0, -800),
      );
      await tester.pumpAndSettle();
      expect(
        find.byKey(
          const Key('tenant-portal-history-label-2'),
          skipOffstage: false,
        ),
        findsOneWidget,
      );
      expect(
        find.byKey(
          const Key('tenant-portal-history-balance-1'),
          skipOffstage: false,
        ),
        findsOneWidget,
      );
      expect(
        find.byKey(
          const Key('tenant-portal-history-balance-2'),
          skipOffstage: false,
        ),
        findsOneWidget,
      );
      expect(find.text('PaymentReceipt'), findsNothing);
      expect(find.text('Debit'), findsNothing);

      await tester.ensureVisible(
        find.byKey(const Key('tenant-portal-share-statement')),
      );
      await tester.tap(find.byKey(const Key('tenant-portal-share-statement')));
      await tester.pumpAndSettle();

      expect(sharedBytes, isNotNull);
      expect(sharedFileName, 'tenant-account-statement.html');
      expect(sharedMimeType, 'text/html');
    },
  );
}

PortalTenantAccount _account() => PortalTenantAccount.fromJson({
  'tenantAccountId': 41,
  'leaseManagementId': 17,
  'propertyName': 'Main Street',
  'unitNumber': '2B',
  'accountNumber': 'TA-41-private',
  'relationshipNumber': 'LM-17-private',
  'lifecycle': 'Occupied',
  'currency': 'USD',
  'receivableBalance': 1250,
  'unappliedCredit': 0,
  'pastDueAmount': 200,
  'pastDueCount': 1,
  'nextDueOn': '2026-08-01',
  'nextDueAmount': 1250,
  'condition': 'PastDue',
  'deposit': {
    'tenantAccountId': 41,
    'leaseManagementId': 17,
    'securityDepositAccountId': 71,
    'originatingAgreementId': 81,
    'currency': 'USD',
    'createdAtUtc': '2026-01-01T00:00:00Z',
    'effectiveNowUtc': '2026-07-20T00:00:00Z',
    'businessDate': '2026-07-20',
    'totalReceived': 1000,
    'totalDeductions': 0,
    'totalRefunded': 0,
    'totalTransferredIn': 0,
    'totalTransferredOut': 0,
    'netAdjustments': 0,
    'heldBalance': 1000,
    'status': 'Held',
  },
});

PortalTenantAccountHistory _history() => PortalTenantAccountHistory(
  tenantAccountId: 41,
  leaseManagementId: 17,
  currency: 'USD',
  businessDate: DateTime(2026, 7, 20),
  period: 'all',
  periodFrom: DateTime(2026, 7, 1),
  periodTo: DateTime(2026, 7, 20),
  currentDue: 1250,
  beginningBalance: 0,
  closingBalance: 1250,
  items: [
    _entry('RentCharge', id: 1, signedAmount: 1250, runningBalance: 1250),
    _entry('PaymentReceipt', id: 2, signedAmount: -0, runningBalance: 1250),
  ],
  totalCount: 2,
  skip: 0,
  take: 20,
);

PortalTenantAccountHistoryItem _entry(
  String entryType, {
  int id = 900,
  double signedAmount = 25,
  double runningBalance = 1250,
}) => PortalTenantAccountHistoryItem(
  tenantLedgerEntryId: id,
  entryType: entryType,
  direction: signedAmount < 0 ? 'Credit' : 'Debit',
  displayType: 'Management-only display type',
  description: 'Debit journal description must stay private',
  effectiveOn: DateTime(2026, 7, 1),
  dueOn: null,
  postedAtUtc: DateTime.utc(2026, 7, 1),
  signedAmount: signedAmount,
  runningBalance: runningBalance,
  openAmount: signedAmount.abs(),
  payable: false,
  isFocused: false,
);
