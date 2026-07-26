import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/presentation/plain_english_labels.dart';

void main() {
  test('translates every tenant ledger entry type', () {
    expect(
      {
        for (final value in const [
          'OpeningBalance',
          'RentCharge',
          'AddendumCharge',
          'LateFeeCharge',
          'DepositCharge',
          'ManualCharge',
          'PaymentReceipt',
          'Credit',
          'Adjustment',
          'Refund',
          'TransferIn',
          'TransferOut',
          'Reversal',
        ])
          value: tenantLedgerEntryLabel(value),
      },
      {
        'OpeningBalance': 'Opening balance',
        'RentCharge': 'Rent charged',
        'AddendumCharge': 'Lease add-on charged',
        'LateFeeCharge': 'Late fee charged',
        'DepositCharge': 'Deposit charged',
        'ManualCharge': 'Manual charge',
        'PaymentReceipt': 'Payment received',
        'Credit': 'Credit',
        'Adjustment': 'Adjustment',
        'Refund': 'Refund',
        'TransferIn': 'Transfer received',
        'TransferOut': 'Transfer sent',
        'Reversal': 'Reversal',
      },
    );
  });

  test('uses understandable Unit condition wording', () {
    expect(plainEnglishLabel('NotAvailable'), 'Not available');
    expect(plainEnglishLabel('NoAccount'), 'No tenant account');
    expect(plainEnglishLabel('NoGoverningAgreement'), 'No signed lease');
    expect(plainEnglishLabel('RentReady'), 'Ready to rent');
    expect(plainEnglishLabel('AwaitingVacancy'), 'Waiting for move-out');
    expect(plainEnglishLabel('PossessionScheduled'), 'Move-in scheduled');
  });

  test(
    'falls back to sentence-style words without changing blank semantics',
    () {
      expect(plainEnglishLabel('SomeNewStatus'), 'Some new status');
      expect(plainEnglishLabel('WAITING_FOR_VENDOR'), 'Waiting for vendor');
      expect(plainEnglishLabel('', fallback: 'Open'), 'Open');
    },
  );
}
