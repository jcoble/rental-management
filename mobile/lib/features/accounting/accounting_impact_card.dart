import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../money/money_format.dart';
import 'accounting_help.dart';
import 'accounting_help_tip.dart';
import 'accounting_book_models.dart';
import 'accounting_books_repository.dart';

/// Shared-preferences key used by the web and mobile accounting detail mode.
///
/// The preference is intentionally read by the owning shell/settings lane and
/// passed to this presentational widget as [AccountingDetailMode].
const accountingDetailModePreferenceKey = 'rc.accounting.detail-mode.v1';

enum AccountingDetailMode { simple, advanced }

/// Read-only, source-backed accounting context for a business detail surface.
///
/// The card does not calculate balances or totals. It loads the server's
/// source-journal summaries and the corresponding server journal detail, then
/// renders each posted line directly.
class AccountingImpactCard extends ConsumerStatefulWidget {
  const AccountingImpactCard({
    super.key,
    required this.sourceId,
    this.sourceType,
    this.sourceTypes = const <JournalSourceType>[],
    this.detailMode = AccountingDetailMode.simple,
    this.authorized,
  }) : assert(
         sourceType != null || sourceTypes.length > 0,
         'Provide sourceType or sourceTypes.',
       );

  final int sourceId;
  final JournalSourceType? sourceType;
  final List<JournalSourceType> sourceTypes;
  final AccountingDetailMode detailMode;

  /// A null value follows the authenticated session capability. Tests and a
  /// parent that already resolved authorization may provide an explicit value.
  final bool? authorized;

  List<JournalSourceType> get lookupTypes =>
      sourceTypes.isNotEmpty ? sourceTypes : <JournalSourceType>[sourceType!];

  @override
  ConsumerState<AccountingImpactCard> createState() =>
      _AccountingImpactCardState();
}

class _AccountingImpactCardState extends ConsumerState<AccountingImpactCard> {
  Future<List<_AccountingJournal>>? _journalsFuture;

  @override
  void didUpdateWidget(covariant AccountingImpactCard oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.sourceId != widget.sourceId ||
        oldWidget.sourceType != widget.sourceType ||
        !listEquals(oldWidget.sourceTypes, widget.sourceTypes)) {
      _journalsFuture = null;
    }
  }

  @override
  Widget build(BuildContext context) {
    final auth = ref.watch(authControllerProvider);
    final canView = widget.authorized ?? _canViewAccounting(auth);
    if (!canView) return const SizedBox.shrink();

    final future = _journalsFuture ??= _loadJournals();
    return FutureBuilder<List<_AccountingJournal>>(
      future: future,
      builder: (context, snapshot) {
        if (snapshot.connectionState == ConnectionState.waiting) {
          return const _AccountingImpactLoadingCard();
        }
        final journals = snapshot.data;
        if (snapshot.hasError || journals == null || journals.isEmpty) {
          // Unauthorized, unavailable, and source-without-journal states do
          // not disclose whether a journal exists on the business detail.
          return const SizedBox.shrink();
        }
        return _AccountingImpactCardContent(
          journals: journals,
          detailMode: widget.detailMode,
          onOpenJournal: (journal) => _openJournal(context, journal),
        );
      },
    );
  }

  bool _canViewAccounting(AuthState auth) =>
      auth is AuthStateAuthenticated &&
      auth.hasCapability('money.balances.read');

  Future<List<_AccountingJournal>> _loadJournals() async {
    final repository = ref.read(accountingBooksRepositoryProvider);
    final sourceTypes = widget.lookupTypes
        .where((sourceType) => sourceType.wire != null)
        .toSet()
        .toList(growable: false);
    if (sourceTypes.isEmpty) return const <_AccountingJournal>[];

    final summariesByType = await Future.wait(
      sourceTypes.map(
        (sourceType) => repository.sourceJournals(
          sourceType: sourceType,
          sourceId: widget.sourceId,
        ),
      ),
    );
    final summaries = [
      for (final sourceSummaries in summariesByType) ...sourceSummaries,
    ];
    summaries.sort((left, right) {
      final effective = right.effectiveOn.compareTo(left.effectiveOn);
      if (effective != 0) return effective;
      return right.postedAtUtc.compareTo(left.postedAtUtc);
    });

    final journals = await Future.wait(
      summaries.map(
        (summary) async => _AccountingJournal(
          detail: await repository.journalDetail(summary.publicId),
          isReversal: summary.isReversal,
        ),
      ),
    );
    return journals
        .where(
          (journal) => journal.detail.lines.any(
            (line) => line.debitAmount > 0 || line.creditAmount > 0,
          ),
        )
        .toList(growable: false);
  }

  Future<void> _openJournal(
    BuildContext context,
    _AccountingJournal journal,
  ) async {
    await showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => _AccountingJournalDetailSheet(
        journal: journal,
        detailMode: widget.detailMode,
      ),
    );
  }
}

class _AccountingImpactCardContent extends StatelessWidget {
  const _AccountingImpactCardContent({
    required this.journals,
    required this.detailMode,
    required this.onOpenJournal,
  });

  final List<_AccountingJournal> journals;
  final AccountingDetailMode detailMode;
  final ValueChanged<_AccountingJournal> onOpenJournal;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Card(
      key: const Key('accounting-impact-card'),
      margin: const EdgeInsets.only(top: 20),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 14, 16, 10),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    'Accounting impact',
                    style: theme.textTheme.titleSmall?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                const AccountingHelpTipButton(
                  topic: AccountingHelpTopic.accountingImpact,
                ),
              ],
            ),
            const SizedBox(height: 10),
            for (var index = 0; index < journals.length; index++) ...[
              if (index > 0) ...[
                const SizedBox(height: 10),
                Divider(color: colorScheme.outlineVariant),
                const SizedBox(height: 6),
              ],
              if (journals.length > 1)
                Padding(
                  padding: const EdgeInsets.only(bottom: 5),
                  child: Text(
                    _journalHeading(journals[index]),
                    style: theme.textTheme.labelMedium?.copyWith(
                      color: colorScheme.onSurfaceVariant,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
              if (detailMode == AccountingDetailMode.simple)
                for (final line in _impactLines(journals[index]))
                  _ImpactLineRow(line: line)
              else
                for (final line in journals[index].detail.lines) ...[
                  if (line.debitAmount > 0)
                    _AdvancedLineRow(
                      code: line.accountCode,
                      accountName: line.accountName,
                      label: 'Debit',
                      amount: line.debitAmount,
                    ),
                  if (line.creditAmount > 0)
                    _AdvancedLineRow(
                      code: line.accountCode,
                      accountName: line.accountName,
                      label: 'Credit',
                      amount: line.creditAmount,
                    ),
                ],
              Align(
                alignment: Alignment.centerLeft,
                child: TextButton(
                  key: ValueKey(
                    'accounting-impact-view-${journals[index].detail.publicId}',
                  ),
                  onPressed: () => onOpenJournal(journals[index]),
                  style: TextButton.styleFrom(
                    padding: const EdgeInsets.symmetric(vertical: 6),
                    minimumSize: Size.zero,
                    tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                  ),
                  child: const Text('View accounting record →'),
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }

  String _journalHeading(_AccountingJournal journal) {
    final detail = journal.detail;
    final description = detail.description.trim();
    if (description.isNotEmpty) return description;
    return detail.sourceType.wire ?? 'Accounting record';
  }
}

class _AccountingImpactLoadingCard extends StatelessWidget {
  const _AccountingImpactLoadingCard();

  @override
  Widget build(BuildContext context) => Card(
    key: const Key('accounting-impact-loading'),
    margin: const EdgeInsets.only(top: 20),
    child: Padding(
      padding: const EdgeInsets.fromLTRB(16, 14, 16, 16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          const _SkeletonLine(widthFactor: .36),
          const SizedBox(height: 14),
          const _SkeletonLine(widthFactor: .86),
          const SizedBox(height: 8),
          const _SkeletonLine(widthFactor: .72),
        ],
      ),
    ),
  );
}

class _SkeletonLine extends StatelessWidget {
  const _SkeletonLine({required this.widthFactor});

  final double widthFactor;

  @override
  Widget build(BuildContext context) => FractionallySizedBox(
    widthFactor: widthFactor,
    alignment: Alignment.centerLeft,
    child: DecoratedBox(
      decoration: BoxDecoration(
        color: Theme.of(context).colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(4),
      ),
      child: const SizedBox(height: 14),
    ),
  );
}

class _ImpactLineRow extends StatelessWidget {
  const _ImpactLineRow({required this.line});

  final _ImpactLine line;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 3),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(child: Text(line.label, style: theme.textTheme.bodyMedium)),
          const SizedBox(width: 12),
          Text(
            moneyFmt(line.isDecrease ? -line.amount : line.amount),
            style: theme.textTheme.bodyMedium?.copyWith(
              color: line.isDecrease
                  ? colorScheme.error
                  : colorScheme.onSurface,
              fontWeight: FontWeight.w600,
            ),
          ),
        ],
      ),
    );
  }
}

// Fallback until M1's journal_detail_sheet.dart is available for the
// integrator to swap in.
class _AccountingJournalDetailSheet extends StatelessWidget {
  const _AccountingJournalDetailSheet({
    required this.journal,
    required this.detailMode,
  });

  final _AccountingJournal journal;
  final AccountingDetailMode detailMode;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final detail = journal.detail;
    return SingleChildScrollView(
      padding: const EdgeInsets.fromLTRB(20, 12, 20, 28),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'Accounting record',
                  style: theme.textTheme.titleLarge?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
                ),
              ),
              const AccountingHelpTipButton(
                topic: AccountingHelpTopic.journalDetail,
              ),
            ],
          ),
          const SizedBox(height: 6),
          Text(
            detail.description.trim().isEmpty
                ? 'Posted journal entry'
                : detail.description,
            style: theme.textTheme.bodyLarge,
          ),
          const SizedBox(height: 4),
          Text(
            '${dateFmt(detail.effectiveOn)} · ${detail.sourceType.wire ?? 'Accounting'}',
            style: theme.textTheme.bodySmall?.copyWith(
              color: Theme.of(context).colorScheme.onSurfaceVariant,
            ),
          ),
          if (journal.isReversal) ...[
            const SizedBox(height: 10),
            const Chip(
              avatar: Icon(Icons.undo_rounded, size: 16),
              label: Text('Reversal'),
            ),
          ],
          const SizedBox(height: 18),
          if (detailMode == AccountingDetailMode.simple)
            for (final line in _impactLines(journal)) _ImpactLineRow(line: line)
          else
            for (final line in detail.lines) ...[
              if (line.debitAmount > 0)
                _AdvancedLineRow(
                  code: line.accountCode,
                  accountName: line.accountName,
                  label: 'Debit',
                  amount: line.debitAmount,
                ),
              if (line.creditAmount > 0)
                _AdvancedLineRow(
                  code: line.accountCode,
                  accountName: line.accountName,
                  label: 'Credit',
                  amount: line.creditAmount,
                ),
            ],
          const Divider(height: 28),
          _ServerAmountRow(label: 'Total debits', amount: detail.totalDebits),
          _ServerAmountRow(label: 'Total credits', amount: detail.totalCredits),
          const SizedBox(height: 8),
          Text(
            detail.isBalanced ? 'Balanced' : 'Out of balance',
            style: theme.textTheme.labelLarge?.copyWith(
              color: detail.isBalanced
                  ? Theme.of(context).colorScheme.primary
                  : Theme.of(context).colorScheme.error,
              fontWeight: FontWeight.w700,
            ),
          ),
        ],
      ),
    );
  }
}

class _AdvancedLineRow extends StatelessWidget {
  const _AdvancedLineRow({
    required this.code,
    required this.accountName,
    required this.label,
    required this.amount,
  });

  final String code;
  final String accountName;
  final String label;
  final double amount;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 5),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(
            child: Text(
              [
                code,
                accountName,
              ].where((value) => value.trim().isNotEmpty).join(' · '),
              style: theme.textTheme.bodyMedium,
            ),
          ),
          const SizedBox(width: 12),
          Text('$label ${moneyFmt(amount)}'),
        ],
      ),
    );
  }
}

class _ServerAmountRow extends StatelessWidget {
  const _ServerAmountRow({required this.label, required this.amount});

  final String label;
  final double amount;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 2),
    child: Row(
      children: [
        Expanded(child: Text(label)),
        Text(
          moneyFmt(amount),
          style: const TextStyle(fontWeight: FontWeight.w600),
        ),
      ],
    ),
  );
}

class _AccountingJournal {
  const _AccountingJournal({required this.detail, required this.isReversal});

  final JournalDetail detail;
  final bool isReversal;
}

class _ImpactLine {
  const _ImpactLine({
    required this.label,
    required this.amount,
    required this.isDecrease,
  });

  final String label;
  final double amount;
  final bool isDecrease;
}

List<_ImpactLine> _impactLines(_AccountingJournal journal) {
  final lines = <_ImpactLine>[];
  for (final line in journal.detail.lines) {
    if (line.debitAmount > 0) {
      lines.add(
        _ImpactLine(
          label: _plainImpactLabel(
            journal.detail.sourceType,
            line,
            isDebit: true,
          ),
          amount: line.debitAmount,
          isDecrease: _isDecrease(
            journal.detail.sourceType,
            line,
            isDebit: true,
          ),
        ),
      );
    }
    if (line.creditAmount > 0) {
      lines.add(
        _ImpactLine(
          label: _plainImpactLabel(
            journal.detail.sourceType,
            line,
            isDebit: false,
          ),
          amount: line.creditAmount,
          isDecrease: _isDecrease(
            journal.detail.sourceType,
            line,
            isDebit: false,
          ),
        ),
      );
    }
  }
  return lines;
}

String _plainImpactLabel(
  JournalSourceType sourceType,
  JournalDetailLine line, {
  required bool isDebit,
}) {
  final role = _accountRole(sourceType, line);
  final account = _plainAccountLabel(sourceType, line, role);
  return '$account ${_isDecrease(sourceType, line, isDebit: isDebit) ? 'decreased' : 'increased'}';
}

bool _isDecrease(
  JournalSourceType sourceType,
  JournalDetailLine line, {
  required bool isDebit,
}) {
  final role = _accountRole(sourceType, line);
  return switch (role) {
    _AccountRole.asset || _AccountRole.expense => !isDebit,
    _AccountRole.liability ||
    _AccountRole.equity ||
    _AccountRole.income => isDebit,
    _AccountRole.unknown => !isDebit,
  };
}

String _plainAccountLabel(
  JournalSourceType sourceType,
  JournalDetailLine line,
  _AccountRole role,
) {
  final name = line.accountName.trim();
  final lower = name.toLowerCase();
  if (_isCashAccount(lower)) {
    return sourceType == JournalSourceType.securityDepositReceipt ||
            sourceType == JournalSourceType.securityDepositRefund
        ? 'Deposit trust cash'
        : 'Operating cash';
  }
  if (_isReceivableAccount(lower)) return 'Tenant accounts receivable';
  if (_isDepositPayable(lower)) return 'Deposit owed back to tenant';
  if (sourceType == JournalSourceType.billIncurred ||
      sourceType == JournalSourceType.billPayment) {
    if (role == _AccountRole.liability) return 'Bills still payable';
  }
  return name.isEmpty ? 'Accounting account' : name;
}

enum _AccountRole { asset, liability, equity, income, expense, unknown }

_AccountRole _accountRole(
  JournalSourceType sourceType,
  JournalDetailLine line,
) {
  final lower = line.accountName.trim().toLowerCase();
  if (_isCashAccount(lower) || _isReceivableAccount(lower)) {
    return _AccountRole.asset;
  }
  if (_isExpenseAccount(lower)) return _AccountRole.expense;
  if (_isDebtAccount(lower) || _isPayableAccount(lower)) {
    return _AccountRole.liability;
  }
  if (_isEquityAccount(lower)) return _AccountRole.equity;
  if (_isIncomeAccount(lower)) return _AccountRole.income;

  return switch (sourceType) {
    JournalSourceType.expensePayment ||
    JournalSourceType.billIncurred ||
    JournalSourceType.capitalPurchase ||
    JournalSourceType.depreciation => _AccountRole.expense,
    JournalSourceType.tenantCharge => _AccountRole.income,
    JournalSourceType.ownerContribution ||
    JournalSourceType.ownerDistribution => _AccountRole.equity,
    JournalSourceType.securityDepositApplication => _AccountRole.asset,
    _ => _AccountRole.unknown,
  };
}

bool _isCashAccount(String value) =>
    value.contains('cash') ||
    value.contains('bank') ||
    value.contains('checking') ||
    value.contains('savings');

bool _isReceivableAccount(String value) =>
    value.contains('receivable') || value == 'a/r' || value.contains(' a/r');

bool _isDepositPayable(String value) =>
    value.contains('security deposit') || value.contains('deposit payable');

bool _isPayableAccount(String value) =>
    value.contains('payable') || value.contains('liabilit');

bool _isDebtAccount(String value) {
  if (value.contains('interest') || value.contains('escrow')) return false;
  return value.contains('mortgage') ||
      value.contains('loan') ||
      value.contains('principal') ||
      value.contains('note payable') ||
      value.contains('debt');
}

bool _isExpenseAccount(String value) =>
    value.contains('expense') ||
    value.contains('interest') ||
    value.contains('escrow') ||
    value.contains('repair') ||
    value.contains('maintenance') ||
    value.contains('depreciation') ||
    value.contains('insurance') ||
    value.contains('utilities');

bool _isEquityAccount(String value) =>
    value.contains('equity') ||
    value.contains('owner') ||
    value.contains('capital');

bool _isIncomeAccount(String value) =>
    value.contains('income') ||
    value.contains('revenue') ||
    value.contains('rent') ||
    value.contains('fee');
