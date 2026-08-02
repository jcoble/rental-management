import 'package:flutter/material.dart';

enum LedgerTypeTone {
  charge,
  payment,
  credit,
  expense,
  bank,
  loan,
  owner,
  reversal,
}

const _labels = <String, String>{
  'charge': 'Charge',
  'tenantcharge': 'Charge',
  'rentcharge': 'Charge',
  'addendumcharge': 'Charge',
  'latefeecharge': 'Charge',
  'depositcharge': 'Charge',
  'manualcharge': 'Charge',
  'payment': 'Payment received',
  'paymentreceived': 'Payment received',
  'paymentreceipt': 'Payment received',
  'tenantreceipt': 'Payment received',
  'securitydepositreceipt': 'Payment received',
  'credit': 'Credit',
  'adjustment': 'Credit',
  'tenantconcession': 'Credit',
  'receivablewriteoff': 'Credit',
  'refund': 'Credit',
  'securitydepositrefund': 'Credit',
  'securitydepositapplication': 'Credit',
  'expense': 'Expense',
  'expensepayment': 'Expense',
  'capitalpurchase': 'Expense',
  'depreciation': 'Expense',
  'bill': 'Bill',
  'billincurred': 'Bill',
  'billpayment': 'Bill',
  'banktransfer': 'Bank transfer',
  'bankmovement': 'Bank transfer',
  'providersettlement': 'Bank transfer',
  'transferin': 'Bank transfer',
  'transferout': 'Bank transfer',
  'loanpayment': 'Loan payment',
  'owneractivity': 'Owner activity',
  'ownercontribution': 'Owner activity',
  'ownerdistribution': 'Owner activity',
  'openingbalance': 'Owner activity',
  'reversal': 'Reversal',
};

String _key(String value) =>
    value.replaceAll(RegExp(r'[\s_-]'), '').toLowerCase();
String ledgerTypeLabel(String type) => _labels[_key(type)] ?? 'Activity';

LedgerTypeTone ledgerTypeTone(String type) {
  final key = _key(type);
  if ({
    'charge',
    'tenantcharge',
    'rentcharge',
    'addendumcharge',
    'latefeecharge',
    'depositcharge',
    'manualcharge',
  }.contains(key)) {
    return LedgerTypeTone.charge;
  }
  if ({
    'payment',
    'paymentreceived',
    'paymentreceipt',
    'tenantreceipt',
    'securitydepositreceipt',
  }.contains(key)) {
    return LedgerTypeTone.payment;
  }
  if ({
    'credit',
    'adjustment',
    'tenantconcession',
    'receivablewriteoff',
    'refund',
    'securitydepositrefund',
    'securitydepositapplication',
  }.contains(key)) {
    return LedgerTypeTone.credit;
  }
  if ({
    'expense',
    'expensepayment',
    'capitalpurchase',
    'depreciation',
    'bill',
    'billincurred',
    'billpayment',
  }.contains(key)) {
    return LedgerTypeTone.expense;
  }
  if ({
    'banktransfer',
    'bankmovement',
    'providersettlement',
    'transferin',
    'transferout',
  }.contains(key)) {
    return LedgerTypeTone.bank;
  }
  if (key == 'loanpayment') {
    return LedgerTypeTone.loan;
  }
  if (key == 'reversal') {
    return LedgerTypeTone.reversal;
  }
  return LedgerTypeTone.owner;
}

class LedgerTypeBadge extends StatelessWidget {
  const LedgerTypeBadge({super.key, required this.type});
  final String type;
  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    final tone = ledgerTypeTone(type);
    final color = switch (tone) {
      LedgerTypeTone.payment => colors.tertiary,
      LedgerTypeTone.credit || LedgerTypeTone.bank => colors.primary,
      LedgerTypeTone.reversal => colors.outline,
      LedgerTypeTone.expense || LedgerTypeTone.loan => colors.error,
      _ => colors.secondary,
    };
    return Chip(
      visualDensity: VisualDensity.compact,
      label: Text(ledgerTypeLabel(type)),
      side: BorderSide(color: color.withAlpha(100)),
      backgroundColor: color.withAlpha(24),
    );
  }
}
