import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test('global receipt flow chooses a charge target before receipt entry', () {
    final flow = File(
      'lib/features/payments/tenant_account_receipt_flow.dart',
    ).readAsStringSync();
    final sheet = File(
      'lib/features/payments/payments_screen.dart',
    ).readAsStringSync();

    expect(flow, contains('listTenantChargesPage('));
    expect(flow, contains('const _ReceiptChargeTarget.unapplied()'));
    expect(
      flow,
      contains('targetChargeEntryId: target.charge?.tenantLedgerEntryId'),
    );
    expect(flow, contains('initialAmount: target.charge?.openAmount'));
    expect(flow, contains('initialLeaveUnapplied: target.unapplied'));
    expect(flow, isNot(contains('.where((charge)')));

    expect(sheet, contains('bool initialLeaveUnapplied = false'));
    expect(sheet, contains('widget.initialLeaveUnapplied'));
    expect(sheet, contains('targetChargeEntryId: widget.targetChargeEntryId'));
  });
}
