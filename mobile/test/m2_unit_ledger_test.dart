import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/money/one_time_charge_sheet.dart';
import 'package:rental_command/features/money/tenant_credit_sheet.dart';
import 'package:rental_command/features/money/tenant_ledger_models.dart';
import 'package:rental_command/features/money/tenant_ledger_view.dart';

void main() {
  test('tenant ledger period range covers the selected month count', () {
    for (final months in [3, 6, 9, 12]) {
      final range = tenantLedgerPeriodRange(DateTime.utc(2027, 2, 15), months);
      final expectedFrom = switch (months) {
        3 => DateTime.utc(2026, 12, 1),
        6 => DateTime.utc(2026, 9, 1),
        9 => DateTime.utc(2026, 6, 1),
        12 => DateTime.utc(2026, 3, 1),
        _ => throw StateError('unexpected period'),
      };

      expect(range.from.year, expectedFrom.year);
      expect(range.from.month, expectedFrom.month);
      expect(range.from.day, expectedFrom.day);
      expect(range.to.year, 2027);
      expect(range.to.month, 2);
      expect(range.to.day, 28);
    }
  });

  test('charge types resolve to server income-category keys', () {
    expect(tenantChargeTypeSystemKey('Rent'), 'rental-income');
    expect(tenantChargeTypeSystemKey('Late fee'), 'late-fee-income');
    expect(
      tenantChargeTypeSystemKey('Utility'),
      'utility-reimbursement-income',
    );
    expect(tenantChargeTypeSystemKey('Pet'), 'pet-income');
    expect(tenantChargeTypeSystemKey('Parking'), 'parking-income');
    expect(tenantChargeTypeSystemKey('Other'), isNull);
  });

  test('credit and reverse actions target original charge rows only', () {
    final rent = _row(type: TenantLedgerEntryType.rentCharge, charge: 100);
    final payment = _row(
      type: TenantLedgerEntryType.paymentReceipt,
      payment: 100,
    );
    final deposit = _row(
      type: TenantLedgerEntryType.depositCharge,
      charge: 500,
    );
    final reversal = _row(type: TenantLedgerEntryType.reversal, charge: 100);
    final alreadyReversed = _row(
      type: TenantLedgerEntryType.rentCharge,
      charge: 100,
      replacedByEntryId: 99,
    );

    expect(tenantLedgerRowCanReceiveCredit(rent), isTrue);
    expect(tenantLedgerRowCanReceiveCredit(payment), isFalse);
    expect(tenantLedgerRowCanReceiveCredit(deposit), isFalse);
    expect(tenantLedgerRowCanReceiveCredit(reversal), isFalse);

    expect(tenantLedgerRowCanReverse(rent), isTrue);
    expect(tenantLedgerRowCanReverse(payment), isFalse);
    expect(tenantLedgerRowCanReverse(deposit), isFalse);
    expect(tenantLedgerRowCanReverse(reversal), isFalse);
    expect(tenantLedgerRowCanReverse(alreadyReversed), isFalse);

    expect(creditEligibleTenantLedgerRow(rent), isTrue);
    expect(creditEligibleTenantLedgerRow(payment), isFalse);
    expect(creditEligibleTenantLedgerRow(deposit), isFalse);
    expect(creditEligibleTenantLedgerRow(reversal), isFalse);
  });

  test('M2 source keeps required server-owned ledger and sheet contracts', () {
    final ledger = File(
      'lib/features/money/tenant_ledger_view.dart',
    ).readAsStringSync();
    final unit = File(
      'lib/features/units/unit_command_center_screen.dart',
    ).readAsStringSync();
    final sheets = [
      File('lib/features/money/one_time_charge_sheet.dart').readAsStringSync(),
      File('lib/features/money/tenant_credit_sheet.dart').readAsStringSync(),
      File('lib/features/money/recurring_charge_sheet.dart').readAsStringSync(),
    ].join('\n');

    for (final text in [
      'Balance due',
      'Past due',
      'Next',
      'Credit',
      'Deposit',
      'Record payment',
      'Add charge',
      'Give credit',
      'Recurring charge',
      'tenant-ledger-periods',
      'Opening',
      'Charges',
      'Payments',
      'Closing',
      'No charges or payments yet. Record the first payment or charge above.',
      'rc.accounting.detail-mode.v1',
      'runningAmountOwed',
      'monthSummary',
    ]) {
      expect(ledger, contains(text), reason: 'Missing ledger contract: $text');
    }

    for (final text in [
      'What is this charge for?',
      'Service period',
      'Category',
      'remaining',
      targetedTenantCreditError,
      'future charges only',
      'Day due',
      'tenantLedgerRepositoryProvider',
    ]) {
      expect(sheets, contains(text), reason: 'Missing sheet contract: $text');
    }

    expect(unit, contains('TenantLedgerView('));
    expect(unit, contains('embedded: true'));
    expect(unit, contains('onDepositTap'));
  });
}

TenantLedgerRow _row({
  required TenantLedgerEntryType type,
  double charge = 0,
  double payment = 0,
  double credit = 0,
  int? reversesEntryId,
  int? replacedByEntryId,
}) => TenantLedgerRow.fromJson({
  'tenantLedgerEntryId': 1,
  'publicId': 'tle-1',
  'sourceType': type.wire,
  'sourceId': 1,
  'effectiveOn': '2027-02-15T00:00:00Z',
  'postedAtUtc': '2027-02-15T00:00:00Z',
  'type': type.wire,
  'description': 'Test entry',
  'chargeAmount': charge,
  'paymentAmount': payment,
  'creditAmount': credit,
  'runningAmountOwed': 0,
  'openAmount': charge,
  'status': 'Posted',
  'currency': 'USD',
  'reversesEntryId': reversesEntryId,
  'replacedByEntryId': replacedByEntryId,
});
