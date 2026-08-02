import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_exception.dart';
import '../../core/presentation/plain_english_labels.dart';
import '../money/money_format.dart';
import 'accounting_book_models.dart';
import 'accounting_books_repository.dart';
import 'accounting_help.dart';
import 'accounting_help_tip.dart';

/// Shared with the web detail-mode preference contract.
const accountingDetailModeStorageKey = 'rc.accounting.detail-mode.v1';

enum AccountingDetailMode { simple, advanced }

extension AccountingDetailModeLabel on AccountingDetailMode {
  String get wire => name;

  String get label =>
      this == AccountingDetailMode.simple ? 'Simple' : 'Advanced';
}

class AccountingDetailModePreferences {
  const AccountingDetailModePreferences([
    this._storage = const FlutterSecureStorage(),
  ]);

  final FlutterSecureStorage _storage;

  Future<String?> read() => _storage.read(key: accountingDetailModeStorageKey);

  Future<void> write(AccountingDetailMode mode) =>
      _storage.write(key: accountingDetailModeStorageKey, value: mode.wire);
}

final accountingDetailModePreferencesProvider =
    Provider<AccountingDetailModePreferences>(
      (ref) => const AccountingDetailModePreferences(),
    );

class AccountingDetailModeNotifier extends Notifier<AccountingDetailMode> {
  @override
  AccountingDetailMode build() {
    unawaited(_restore());
    return AccountingDetailMode.simple;
  }

  Future<void> _restore() async {
    try {
      final stored = await ref
          .read(accountingDetailModePreferencesProvider)
          .read();
      if (!ref.mounted) return;
      state = stored == AccountingDetailMode.advanced.wire
          ? AccountingDetailMode.advanced
          : AccountingDetailMode.simple;
    } catch (_) {
      // A missing platform storage plugin must leave the safe default usable.
    }
  }

  Future<void> setMode(AccountingDetailMode mode) async {
    state = mode;
    try {
      await ref.read(accountingDetailModePreferencesProvider).write(mode);
    } catch (_) {
      // The in-memory choice still applies for this session when persistence is unavailable.
    }
  }
}

final accountingDetailModeProvider =
    NotifierProvider<AccountingDetailModeNotifier, AccountingDetailMode>(
      AccountingDetailModeNotifier.new,
    );

/// The chart is server-owned. It is also used by the journal sheet so simple
/// mode can describe a debit/credit with the account's server normal balance.
final accountingChartOfAccountsProvider =
    FutureProvider.autoDispose<AccountingPage<ChartOfAccountsRow>>((ref) {
      return ref
          .watch(accountingBooksRepositoryProvider)
          .chartOfAccounts(
            query: const ChartOfAccountsQuery(take: 200, activeOnly: true),
          );
    });

final journalDetailProvider = FutureProvider.autoDispose
    .family<JournalDetail, String>((ref, publicId) {
      return ref
          .watch(accountingBooksRepositoryProvider)
          .journalDetail(publicId);
    });

Future<void> showJournalDetailSheet(
  BuildContext context,
  String journalPublicId,
) {
  return showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    useSafeArea: true,
    builder: (_) => JournalDetailSheet(journalPublicId: journalPublicId),
  );
}

class JournalDetailSheet extends ConsumerWidget {
  const JournalDetailSheet({super.key, required this.journalPublicId});

  final String journalPublicId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final detailAsync = ref.watch(journalDetailProvider(journalPublicId));
    final chartAsync = ref.watch(accountingChartOfAccountsProvider);

    return ConstrainedBox(
      constraints: BoxConstraints(
        maxHeight: MediaQuery.sizeOf(context).height * 0.9,
      ),
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 28),
        child: detailAsync.when(
          loading: () => const _JournalDetailSkeleton(),
          error: (error, _) => _JournalDetailError(
            message: error is ApiException
                ? error.message
                : "Couldn't load this accounting record.",
            onRetry: () =>
                ref.invalidate(journalDetailProvider(journalPublicId)),
          ),
          data: (detail) => _JournalDetailBody(
            detail: detail,
            chart: chartAsync.asData?.value,
          ),
        ),
      ),
    );
  }
}

class _JournalDetailBody extends ConsumerWidget {
  const _JournalDetailBody({required this.detail, required this.chart});

  final JournalDetail detail;
  final AccountingPage<ChartOfAccountsRow>? chart;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mode = ref.watch(accountingDetailModeProvider);
    final accountById = <int, ChartOfAccountsRow>{
      for (final account in chart?.items ?? const <ChartOfAccountsRow>[])
        account.id: account,
    };
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Text(
                'Accounting record',
                style: theme.textTheme.labelLarge?.copyWith(
                  color: cs.onSurfaceVariant,
                  fontWeight: FontWeight.w700,
                ),
              ),
            ),
            const AccountingHelpTipButton(
              topic: AccountingHelpTopic.journalDetail,
            ),
          ],
        ),
        const SizedBox(height: 4),
        Text(
          dateFmt(detail.effectiveOn.toLocal()),
          style: theme.textTheme.headlineSmall?.copyWith(
            fontWeight: FontWeight.w800,
          ),
        ),
        const SizedBox(height: 4),
        Text(
          detail.description.isEmpty
              ? plainEnglishLabel(detail.sourceType.wire ?? 'Accounting record')
              : detail.description,
          style: theme.textTheme.titleMedium?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        const SizedBox(height: 20),
        Text(
          mode == AccountingDetailMode.simple ? 'What this did' : 'Detail',
          style: theme.textTheme.titleSmall?.copyWith(
            fontWeight: FontWeight.w800,
          ),
        ),
        const SizedBox(height: 8),
        if (mode == AccountingDetailMode.simple)
          _SimpleJournalImpact(detail: detail, accountById: accountById)
        else
          _AdvancedJournalLines(detail: detail),
        const SizedBox(height: 16),
        _BalancedRow(isBalanced: detail.isBalanced),
        const SizedBox(height: 20),
        _JournalMetadata(detail: detail),
      ],
    );
  }
}

class _SimpleJournalImpact extends StatelessWidget {
  const _SimpleJournalImpact({required this.detail, required this.accountById});

  final JournalDetail detail;
  final Map<int, ChartOfAccountsRow> accountById;

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        for (final line in detail.lines)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 6),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(
                  child: Text(
                    _simpleLineLabel(line, accountById[line.accountId]),
                    style: Theme.of(context).textTheme.bodyMedium,
                  ),
                ),
                const SizedBox(width: 12),
                Text(
                  _lineAmount(line),
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    fontWeight: FontWeight.w700,
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                ),
              ],
            ),
          ),
      ],
    );
  }
}

class _AdvancedJournalLines extends StatelessWidget {
  const _AdvancedJournalLines({required this.detail});

  final JournalDetail detail;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return Column(
      children: [
        for (final line in detail.lines)
          Container(
            width: double.infinity,
            margin: const EdgeInsets.only(bottom: 8),
            padding: const EdgeInsets.all(12),
            decoration: BoxDecoration(
              color: cs.surfaceContainerHigh,
              borderRadius: BorderRadius.circular(16),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  '${line.accountCode} ${line.accountName}'.trim(),
                  style: theme.textTheme.titleSmall?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
                ),
                if ((line.memo ?? '').trim().isNotEmpty) ...[
                  const SizedBox(height: 2),
                  Text(
                    line.memo!.trim(),
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: cs.onSurfaceVariant,
                    ),
                  ),
                ],
                const SizedBox(height: 8),
                Row(
                  children: [
                    Expanded(child: Text('Dr  ${_amount(line.debitAmount)}')),
                    Expanded(child: Text('Cr  ${_amount(line.creditAmount)}')),
                  ],
                ),
              ],
            ),
          ),
      ],
    );
  }
}

class _BalancedRow extends StatelessWidget {
  const _BalancedRow({required this.isBalanced});

  final bool isBalanced;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Row(
      children: [
        Icon(
          isBalanced ? Icons.check_circle_outline : Icons.warning_amber_rounded,
          color: isBalanced ? cs.primary : cs.error,
          size: 20,
        ),
        const SizedBox(width: 8),
        Text(
          isBalanced ? 'Balanced ✓' : 'Not balanced',
          style: Theme.of(context).textTheme.titleSmall?.copyWith(
            color: isBalanced ? cs.primary : cs.error,
            fontWeight: FontWeight.w800,
          ),
        ),
      ],
    );
  }
}

class _JournalMetadata extends StatelessWidget {
  const _JournalMetadata({required this.detail});

  final JournalDetail detail;

  @override
  Widget build(BuildContext context) {
    final sourceLabel = plainEnglishLabel(
      detail.sourceType.wire ?? 'Accounting record',
    );
    final sourceReference = detail.sourceBusinessKey.trim().isEmpty
        ? '$sourceLabel #${detail.sourceId}'
        : detail.sourceBusinessKey.trim();
    final sourceUri = Uri.tryParse(sourceReference);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _MetadataRow(label: 'Source record', value: sourceReference),
        if (sourceUri != null && sourceUri.hasScheme)
          Align(
            alignment: Alignment.centerLeft,
            child: TextButton.icon(
              onPressed: () => launchUrl(sourceUri),
              icon: const Icon(Icons.open_in_new, size: 18),
              label: const Text('Open source record'),
            ),
          ),
        if (detail.actor?.trim().isNotEmpty == true)
          _MetadataRow(label: 'Entered by', value: detail.actor!.trim()),
        _MetadataRow(
          label: 'Effective',
          value: dateFmt(detail.effectiveOn.toLocal()),
        ),
        _MetadataRow(
          label: 'Posted',
          value: dateFmt(detail.postedAtUtc.toLocal()),
        ),
        if (detail.bankReconciliationEvidence != null)
          _BankEvidenceRow(evidence: detail.bankReconciliationEvidence!),
        if (detail.reversesJournalEntryPublicId != null)
          _MetadataRow(
            label: 'Reversal',
            value: 'Reverses ${detail.reversesJournalEntryPublicId}',
          ),
        if (detail.reversalPublicIds.isNotEmpty)
          _MetadataRow(
            label: 'Reversed by',
            value: detail.reversalPublicIds.join(', '),
          ),
        if (detail.documentIds.isNotEmpty)
          _MetadataRow(
            label: 'Source documents',
            value: detail.documentIds.map((id) => '#$id').join(', '),
          ),
        if (detail.auditLink?.trim().isNotEmpty == true)
          _MetadataRow(label: 'Audit link', value: detail.auditLink!.trim()),
      ],
    );
  }
}

class _BankEvidenceRow extends StatelessWidget {
  const _BankEvidenceRow({required this.evidence});

  final BankReconciliationEvidence evidence;

  @override
  Widget build(BuildContext context) {
    final parts = <String>[
      if ((evidence.bankAccountLabel ?? '').trim().isNotEmpty)
        evidence.bankAccountLabel!.trim(),
      if (evidence.status?.trim().isNotEmpty == true) evidence.status!.trim(),
      if (evidence.matchedOn != null)
        'matched ${dateFmt(evidence.matchedOn!.toLocal())}',
    ];
    return _MetadataRow(
      label: 'Bank match',
      value: parts.isEmpty ? '—' : parts.join(' · '),
    );
  }
}

class _MetadataRow extends StatelessWidget {
  const _MetadataRow({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 5),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 112,
            child: Text(
              label,
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
          ),
          Expanded(child: SelectableText(value)),
        ],
      ),
    );
  }
}

class AccountingDetailModeToggle extends ConsumerWidget {
  const AccountingDetailModeToggle({super.key, this.compact = false});

  final bool compact;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mode = ref.watch(accountingDetailModeProvider);
    return Wrap(
      alignment: WrapAlignment.end,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        SegmentedButton<AccountingDetailMode>(
          key: const Key('accounting-detail-mode-toggle'),
          showSelectedIcon: false,
          style: compact
              ? const ButtonStyle(visualDensity: VisualDensity.compact)
              : null,
          segments: const [
            ButtonSegment(
              value: AccountingDetailMode.simple,
              label: Text('Simple'),
            ),
            ButtonSegment(
              value: AccountingDetailMode.advanced,
              label: Text('Advanced'),
            ),
          ],
          selected: {mode},
          onSelectionChanged: (selection) {
            unawaited(
              ref
                  .read(accountingDetailModeProvider.notifier)
                  .setMode(selection.single),
            );
          },
        ),
        const AccountingHelpTipButton(topic: AccountingHelpTopic.detailMode),
      ],
    );
  }
}

String _simpleLineLabel(JournalDetailLine line, ChartOfAccountsRow? account) {
  final direction = _lineDirection(line, account?.normalBalance);
  return direction == null
      ? line.accountName
      : '${line.accountName} $direction';
}

String? _lineDirection(JournalDetailLine line, NormalBalance? normalBalance) {
  if (normalBalance == null || normalBalance == NormalBalance.unknown) {
    return null;
  }
  final isDebit = line.debitAmount != 0;
  if (line.debitAmount == 0 && line.creditAmount == 0) return null;
  final increases = isDebit == (normalBalance == NormalBalance.debit);
  return increases ? 'increased' : 'decreased';
}

String _lineAmount(JournalDetailLine line) => line.debitAmount != 0
    ? _amount(line.debitAmount)
    : _amount(line.creditAmount);

String _amount(num value) => value == 0 ? '—' : _signedMoney(value);

String _signedMoney(num value) =>
    value < 0 ? '-${moneyFmt(value.abs())}' : moneyFmt(value);

class _JournalDetailSkeleton extends StatelessWidget {
  const _JournalDetailSkeleton();

  @override
  Widget build(BuildContext context) {
    final color = Theme.of(context).colorScheme.surfaceContainerHigh;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        for (final width in [120.0, 180.0, 260.0, 320.0, 280.0, 200.0])
          Padding(
            padding: const EdgeInsets.only(bottom: 12),
            child: Container(
              height: width == 320 ? 72 : 18,
              width: width,
              decoration: BoxDecoration(
                color: color,
                borderRadius: BorderRadius.circular(8),
              ),
            ),
          ),
      ],
    );
  }
}

class _JournalDetailError extends StatelessWidget {
  const _JournalDetailError({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 48),
      child: Column(
        children: [
          Icon(Icons.error_outline, color: Theme.of(context).colorScheme.error),
          const SizedBox(height: 12),
          Text(message, textAlign: TextAlign.center),
          const SizedBox(height: 12),
          FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
        ],
      ),
    );
  }
}
