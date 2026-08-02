import 'package:flutter/material.dart';
import '../money_format.dart';
import '../money_repository.dart';
import 'ledger_type_badge.dart';

class MonthGroup extends StatelessWidget {
  const MonthGroup({
    super.key,
    required this.summary,
    required this.rows,
    this.onRowTap,
  });
  final TenantMonthSummary summary;
  final List<TenantLedgerRow> rows;
  final ValueChanged<TenantLedgerRow>? onRowTap;
  @override
  Widget build(BuildContext context) => Card.outlined(
    child: Column(
      children: [
        ListTile(
          title: Text('${_months[summary.month - 1]} ${summary.year}'),
          subtitle: Text(
            'Opening amount owed ${moneyFmt(summary.openingBalance)}',
          ),
        ),
        for (final row in rows)
          ListTile(
            onTap: onRowTap == null ? null : () => onRowTap!(row),
            leading: LedgerTypeBadge(type: row.type),
            title: Text(row.description),
            subtitle: Text(
              'Effective ${row.effectiveOn.month}/${row.effectiveOn.day}/${row.effectiveOn.year} · Entered ${row.postedAtUtc.month}/${row.postedAtUtc.day}/${row.postedAtUtc.year}',
            ),
            trailing: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                Text(
                  moneyFmt(
                    row.chargeAmount != 0
                        ? row.chargeAmount
                        : -(row.paymentAmount + row.creditAmount),
                  ),
                ),
                Text(
                  'Owed ${moneyFmt(row.runningAmountOwed)}',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ],
            ),
          ),
        const Divider(),
        Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            children: [
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  const Text('Charges'),
                  Text(moneyFmt(summary.chargeAmount)),
                ],
              ),
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  const Text('Payments'),
                  Text(moneyFmt(summary.paymentAmount)),
                ],
              ),
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  const Text('Credits'),
                  Text(moneyFmt(summary.creditAmount)),
                ],
              ),
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  const Text('Closing amount owed'),
                  Text(moneyFmt(summary.closingBalance)),
                ],
              ),
            ],
          ),
        ),
      ],
    ),
  );
}

const _months = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
];
