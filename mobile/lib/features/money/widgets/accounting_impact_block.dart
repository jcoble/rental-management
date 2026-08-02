import 'package:flutter/material.dart';
import '../../accounting/accounting_models.dart';
import '../money_format.dart';

class AccountingImpactBlock extends StatelessWidget {
  const AccountingImpactBlock({
    super.key,
    required this.journal,
    this.onOpenJournal,
  });
  final JournalDetail journal;
  final VoidCallback? onOpenJournal;
  @override
  Widget build(BuildContext context) => Card.outlined(
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Accounting impact',
            style: Theme.of(context).textTheme.titleMedium,
          ),
          const SizedBox(height: 8),
          for (final line in journal.lines)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 3),
              child: Row(
                children: [
                  Expanded(
                    child: Text('${line.accountCode} ${line.accountName}'),
                  ),
                  Text(
                    line.debitAmount != 0
                        ? 'Debit ${moneyFmt(line.debitAmount)}'
                        : 'Credit ${moneyFmt(line.creditAmount)}',
                  ),
                ],
              ),
            ),
          const Divider(),
          Row(
            children: [
              Expanded(
                child: Text(journal.isBalanced ? 'Balanced' : 'Needs review'),
              ),
              Text(
                'Debit ${moneyFmt(journal.totalDebits)} · Credit ${moneyFmt(journal.totalCredits)}',
              ),
            ],
          ),
          if (onOpenJournal != null)
            Align(
              alignment: Alignment.centerRight,
              child: TextButton(
                onPressed: onOpenJournal,
                child: const Text('View journal entry'),
              ),
            ),
        ],
      ),
    ),
  );
}
