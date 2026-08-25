import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/presentation/plain_english_labels.dart';
import '../../core/theme/app_tokens.dart';
import '../home/mobile_domain_chrome.dart';
import '../../core/presentation/formatting.dart';
import 'accounting_book_models.dart';
import 'accounting_books_repository.dart';
import 'accounting_help.dart';
import 'accounting_help_tip.dart';
import 'journal_detail_sheet.dart';

class GeneralLedgerFilterState {
  const GeneralLedgerFilterState({
    this.search = '',
    this.accountId,
    this.propertyId,
    this.unitId,
    this.sourceType,
    this.effectiveFrom,
    this.effectiveTo,
  });

  final String search;
  final int? accountId;
  final int? propertyId;
  final int? unitId;
  final JournalSourceType? sourceType;
  final DateTime? effectiveFrom;
  final DateTime? effectiveTo;

  GeneralLedgerQuery get query => GeneralLedgerQuery(
    search: search.trim().isEmpty ? null : search.trim(),
    accountId: accountId,
    propertyId: propertyId,
    unitId: unitId,
    sourceType: sourceType,
    effectiveFrom: effectiveFrom,
    effectiveTo: effectiveTo,
  );

  GeneralLedgerFilterState copyWith({
    String? search,
    int? accountId,
    int? propertyId,
    int? unitId,
    JournalSourceType? sourceType,
    DateTime? effectiveFrom,
    DateTime? effectiveTo,
    bool clearAccountId = false,
    bool clearPropertyId = false,
    bool clearUnitId = false,
    bool clearSourceType = false,
    bool clearEffectiveFrom = false,
    bool clearEffectiveTo = false,
  }) => GeneralLedgerFilterState(
    search: search ?? this.search,
    accountId: clearAccountId ? null : accountId ?? this.accountId,
    propertyId: clearPropertyId ? null : propertyId ?? this.propertyId,
    unitId: clearUnitId ? null : unitId ?? this.unitId,
    sourceType: clearSourceType ? null : sourceType ?? this.sourceType,
    effectiveFrom: clearEffectiveFrom
        ? null
        : effectiveFrom ?? this.effectiveFrom,
    effectiveTo: clearEffectiveTo ? null : effectiveTo ?? this.effectiveTo,
  );

  @override
  bool operator ==(Object other) {
    return other is GeneralLedgerFilterState &&
        other.search == search &&
        other.accountId == accountId &&
        other.propertyId == propertyId &&
        other.unitId == unitId &&
        other.sourceType == sourceType &&
        other.effectiveFrom == effectiveFrom &&
        other.effectiveTo == effectiveTo;
  }

  @override
  int get hashCode => Object.hash(
    search,
    accountId,
    propertyId,
    unitId,
    sourceType,
    effectiveFrom,
    effectiveTo,
  );
}

final generalLedgerPageProvider = FutureProvider.autoDispose
    .family<AccountingPage<GeneralLedgerRow>, GeneralLedgerFilterState>((
      ref,
      filter,
    ) {
      return ref
          .watch(accountingBooksRepositoryProvider)
          .generalLedger(query: filter.query);
    });

class GeneralLedgerView extends ConsumerStatefulWidget {
  const GeneralLedgerView({super.key, this.embedded = true});

  final bool embedded;

  @override
  ConsumerState<GeneralLedgerView> createState() => _GeneralLedgerViewState();
}

class _GeneralLedgerViewState extends ConsumerState<GeneralLedgerView> {
  final _searchController = TextEditingController();
  GeneralLedgerFilterState _filter = const GeneralLedgerFilterState();

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final chartAsync = ref.watch(accountingChartOfAccountsProvider);
    final ledgerAsync = ref.watch(generalLedgerPageProvider(_filter));
    final body = Column(
      children: [
        _LedgerControls(
          filter: _filter,
          chartAsync: chartAsync,
          searchController: _searchController,
          onAccountChanged: (accountId) {
            setState(() {
              _filter = accountId == null
                  ? _filter.copyWith(clearAccountId: true)
                  : _filter.copyWith(accountId: accountId);
            });
          },
          onSearch: (value) =>
              setState(() => _filter = _filter.copyWith(search: value.trim())),
          onFilterPressed: _openFilterSheet,
        ),
        if (_filter.accountId == null) const _NoAccountBanner(),
        Expanded(
          child: _LedgerResults(
            asyncPage: ledgerAsync,
            hasSelectedAccount: _filter.accountId != null,
            onRetry: () => ref.invalidate(generalLedgerPageProvider(_filter)),
          ),
        ),
      ],
    );

    if (widget.embedded) return body;
    return Scaffold(
      appBar: mobileDomainRootAppBar(
        context,
        title: const Text('General ledger'),
      ),
      body: body,
    );
  }

  Future<void> _openFilterSheet() async {
    final result = await showModalBottomSheet<GeneralLedgerFilterState>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      useSafeArea: true,
      builder: (_) => _LedgerFilterSheet(initial: _filter),
    );
    if (!mounted || result == null) return;
    _searchController.text = result.search;
    setState(() => _filter = result);
  }
}

class _LedgerControls extends ConsumerWidget {
  const _LedgerControls({
    required this.filter,
    required this.chartAsync,
    required this.searchController,
    required this.onAccountChanged,
    required this.onSearch,
    required this.onFilterPressed,
  });

  final GeneralLedgerFilterState filter;
  final AsyncValue<AccountingPage<ChartOfAccountsRow>> chartAsync;
  final TextEditingController searchController;
  final ValueChanged<int?> onAccountChanged;
  final ValueChanged<String> onSearch;
  final VoidCallback onFilterPressed;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final accounts =
        chartAsync.asData?.value.items ?? const <ChartOfAccountsRow>[];
    final mode = ref.watch(accountingDetailModeProvider);
    final accountValue =
        accounts.any((account) => account.id == filter.accountId)
        ? filter.accountId
        : null;
    final activeFilterCount = [
      filter.propertyId,
      filter.unitId,
      filter.sourceType,
      filter.effectiveFrom,
      filter.effectiveTo,
    ].where((value) => value != null).length;

    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 4),
      child: Column(
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'General ledger',
                  style: Theme.of(context).textTheme.titleMedium?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
                ),
              ),
              const AccountingHelpTipButton(
                topic: AccountingHelpTopic.generalLedger,
              ),
            ],
          ),
          DropdownButtonFormField<int?>(
            key: const Key('general-ledger-account-selector'),
            initialValue: accountValue,
            isExpanded: true,
            decoration: const InputDecoration(
              labelText: 'Account or category',
              prefixIcon: Icon(Icons.account_balance_outlined),
            ),
            items: [
              const DropdownMenuItem<int?>(
                value: null,
                child: Text('All accounts'),
              ),
              for (final account in accounts)
                DropdownMenuItem<int?>(
                  value: account.id,
                  child: Text(
                    mode == AccountingDetailMode.advanced
                        ? '${account.code} ${account.name}'
                        : account.name,
                  ),
                ),
            ],
            onChanged: chartAsync.hasValue ? onAccountChanged : null,
          ),
          const SizedBox(height: 8),
          Row(
            children: [
              Expanded(
                child: TextField(
                  controller: searchController,
                  key: const Key('general-ledger-search'),
                  textInputAction: TextInputAction.search,
                  decoration: const InputDecoration(
                    hintText: 'Search ledger',
                    prefixIcon: Icon(Icons.search),
                  ),
                  onSubmitted: onSearch,
                ),
              ),
              const SizedBox(width: 8),
              Badge(
                isLabelVisible: activeFilterCount > 0,
                label: Text('$activeFilterCount'),
                child: IconButton.filledTonal(
                  key: const Key('general-ledger-filter-button'),
                  tooltip: 'Filter ledger',
                  onPressed: onFilterPressed,
                  icon: const Icon(Icons.tune),
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          const Align(
            alignment: Alignment.centerRight,
            child: AccountingDetailModeToggle(compact: true),
          ),
        ],
      ),
    );
  }
}

class _NoAccountBanner extends StatelessWidget {
  const _NoAccountBanner();

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Container(
      key: const Key('general-ledger-no-account-banner'),
      margin: const EdgeInsets.fromLTRB(16, 8, 16, 4),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: cs.secondaryContainer,
        borderRadius: M3Shape.radiusLarge,
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(Icons.info_outline, color: cs.onSecondaryContainer),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              'Choose one category or account to see its running balance. Every account has its own balance, so there is no single running balance for the whole general ledger.',
              style: TextStyle(color: cs.onSecondaryContainer),
            ),
          ),
        ],
      ),
    );
  }
}

class _LedgerResults extends StatelessWidget {
  const _LedgerResults({
    required this.asyncPage,
    required this.hasSelectedAccount,
    required this.onRetry,
  });

  final AsyncValue<AccountingPage<GeneralLedgerRow>> asyncPage;
  final bool hasSelectedAccount;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    return RefreshIndicator(
      onRefresh: () async => onRetry(),
      child: asyncPage.when(
        loading: () => const _LedgerLoading(),
        error: (error, _) => _LedgerError(
          message: error is ApiException
              ? error.message
              : "Couldn't load the general ledger.",
          onRetry: onRetry,
        ),
        data: (page) {
          if (page.items.isEmpty) return const _LedgerEmpty();
          return ListView.separated(
            padding: const EdgeInsets.fromLTRB(16, 8, 16, 28),
            itemCount: page.items.length,
            separatorBuilder: (_, _) => const SizedBox(height: 8),
            itemBuilder: (_, index) => _GeneralLedgerCard(
              row: page.items[index],
              hasSelectedAccount: hasSelectedAccount,
            ),
          );
        },
      ),
    );
  }
}

class _GeneralLedgerCard extends ConsumerWidget {
  const _GeneralLedgerCard({
    required this.row,
    required this.hasSelectedAccount,
  });

  final GeneralLedgerRow row;
  final bool hasSelectedAccount;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mode = ref.watch(accountingDetailModeProvider);
    final contextLabel = [
      if (row.propertyId != null) 'Property #${row.propertyId}',
      if (row.unitId != null) 'Unit #${row.unitId}',
    ].join(' · ');

    return Card(
      key: Key('general-ledger-row-${row.lineId}'),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: row.journalEntryPublicId.isEmpty
            ? null
            : () => showJournalDetailSheet(context, row.journalEntryPublicId),
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 14, 16, 14),
          child: mode == AccountingDetailMode.simple
              ? _SimpleLedgerCardContent(
                  row: row,
                  contextLabel: contextLabel,
                  hasSelectedAccount: hasSelectedAccount,
                )
              : _AdvancedLedgerCardContent(
                  row: row,
                  contextLabel: contextLabel,
                  hasSelectedAccount: hasSelectedAccount,
                ),
        ),
      ),
    );
  }
}

class _SimpleLedgerCardContent extends StatelessWidget {
  const _SimpleLedgerCardContent({
    required this.row,
    required this.contextLabel,
    required this.hasSelectedAccount,
  });

  final GeneralLedgerRow row;
  final String contextLabel;
  final bool hasSelectedAccount;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(
              child: Text(
                row.description.isEmpty
                    ? 'Accounting activity'
                    : row.description,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: theme.textTheme.titleMedium?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
              ),
            ),
            const SizedBox(width: 8),
            Text(
              _increaseLabel(row),
              style: theme.textTheme.titleSmall?.copyWith(
                color: cs.primary,
                fontWeight: FontWeight.w800,
              ),
            ),
          ],
        ),
        const SizedBox(height: 4),
        Text(
          [
            row.accountName,
            if (contextLabel.isNotEmpty) contextLabel,
          ].join(' · '),
          maxLines: 2,
          overflow: TextOverflow.ellipsis,
          style: theme.textTheme.bodySmall?.copyWith(
            color: cs.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: 10),
        Row(
          children: [
            Expanded(child: Text(shortDateFmt(row.effectiveOn.toLocal()))),
            _SimpleAmount(label: 'Increase', value: _increase(row)),
            const SizedBox(width: 12),
            _SimpleAmount(label: 'Decrease', value: _decrease(row)),
          ],
        ),
        if (hasSelectedAccount && row.runningBalance != null) ...[
          const SizedBox(height: 8),
          Align(
            alignment: Alignment.centerRight,
            child: Text(
              'Balance ${_signedMoney(row.runningBalance!)}',
              style: theme.textTheme.labelLarge?.copyWith(
                color: cs.primary,
                fontWeight: FontWeight.w800,
              ),
            ),
          ),
        ],
      ],
    );
  }
}

class _SimpleAmount extends StatelessWidget {
  const _SimpleAmount({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.end,
      children: [
        Text(label, style: Theme.of(context).textTheme.labelSmall),
        Text(
          value,
          style: Theme.of(context).textTheme.bodyMedium?.copyWith(
            fontWeight: FontWeight.w700,
            fontFeatures: const [FontFeature.tabularFigures()],
          ),
        ),
      ],
    );
  }
}

class _AdvancedLedgerCardContent extends StatelessWidget {
  const _AdvancedLedgerCardContent({
    required this.row,
    required this.contextLabel,
    required this.hasSelectedAccount,
  });

  final GeneralLedgerRow row;
  final String contextLabel;
  final bool hasSelectedAccount;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final entries = <String>[
      'Effective ${dateFmt(row.effectiveOn.toLocal())}',
      'Posted ${dateFmt(row.postedAtUtc.toLocal())}',
      'Journal ${row.journalEntryPublicId}',
      '${row.accountCode} ${row.accountName}'.trim(),
      if (contextLabel.isNotEmpty) contextLabel,
      'Debit ${_amount(row.debitAmount)}',
      'Credit ${_amount(row.creditAmount)}',
      if (hasSelectedAccount && row.runningBalance != null)
        'Balance ${_signedMoney(row.runningBalance!)}',
    ];
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          row.description.isEmpty ? 'Accounting activity' : row.description,
          style: theme.textTheme.titleMedium?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        const SizedBox(height: 8),
        for (final entry in entries)
          Padding(
            padding: const EdgeInsets.only(bottom: 3),
            child: Text(entry, style: theme.textTheme.bodySmall),
          ),
      ],
    );
  }
}

class _LedgerLoading extends StatelessWidget {
  const _LedgerLoading();

  @override
  Widget build(BuildContext context) {
    final color = Theme.of(context).colorScheme.surfaceContainerHigh;
    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 28),
      children: [
        for (var index = 0; index < 3; index++)
          Container(
            key: index == 0 ? const Key('ledger-loading') : null,
            height: 116,
            margin: const EdgeInsets.only(bottom: 8),
            decoration: BoxDecoration(
              color: color,
              borderRadius: BorderRadius.circular(16),
            ),
          ),
      ],
    );
  }
}

class _LedgerEmpty extends StatelessWidget {
  const _LedgerEmpty();

  @override
  Widget build(BuildContext context) => ListView(
    padding: const EdgeInsets.fromLTRB(16, 80, 16, 24),
    children: [
      const Center(
        child: Text('No accounting activity for these filters yet.'),
      ),
    ],
  );
}

class _LedgerError extends StatelessWidget {
  const _LedgerError({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => ListView(
    padding: const EdgeInsets.fromLTRB(16, 80, 16, 24),
    children: [
      Icon(Icons.error_outline, color: Theme.of(context).colorScheme.error),
      const SizedBox(height: 12),
      Center(child: Text(message, textAlign: TextAlign.center)),
      const SizedBox(height: 12),
      Center(
        child: FilledButton.tonal(
          onPressed: onRetry,
          child: const Text('Retry'),
        ),
      ),
    ],
  );
}

class _LedgerFilterSheet extends StatefulWidget {
  const _LedgerFilterSheet({required this.initial});

  final GeneralLedgerFilterState initial;

  @override
  State<_LedgerFilterSheet> createState() => _LedgerFilterSheetState();
}

class _LedgerFilterSheetState extends State<_LedgerFilterSheet> {
  late final TextEditingController _propertyController;
  late final TextEditingController _unitController;
  late JournalSourceType? _sourceType;
  late DateTime? _effectiveFrom;
  late DateTime? _effectiveTo;

  @override
  void initState() {
    super.initState();
    _propertyController = TextEditingController(
      text: widget.initial.propertyId?.toString() ?? '',
    );
    _unitController = TextEditingController(
      text: widget.initial.unitId?.toString() ?? '',
    );
    _sourceType = widget.initial.sourceType;
    _effectiveFrom = widget.initial.effectiveFrom;
    _effectiveTo = widget.initial.effectiveTo;
  }

  @override
  void dispose() {
    _propertyController.dispose();
    _unitController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final sourceTypes = JournalSourceType.values
        .where((source) => source.wire != null)
        .toList(growable: false);
    return SingleChildScrollView(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 28),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text('Filter ledger', style: Theme.of(context).textTheme.titleLarge),
          const SizedBox(height: 16),
          TextField(
            controller: _propertyController,
            keyboardType: TextInputType.number,
            decoration: const InputDecoration(
              labelText: 'Property ID',
              prefixIcon: Icon(Icons.home_work_outlined),
            ),
          ),
          const SizedBox(height: 10),
          TextField(
            controller: _unitController,
            keyboardType: TextInputType.number,
            decoration: const InputDecoration(
              labelText: 'Unit ID',
              prefixIcon: Icon(Icons.door_front_door_outlined),
            ),
          ),
          const SizedBox(height: 10),
          DropdownButtonFormField<JournalSourceType?>(
            initialValue: _sourceType,
            isExpanded: true,
            decoration: const InputDecoration(
              labelText: 'Source type',
              prefixIcon: Icon(Icons.link_outlined),
            ),
            items: [
              const DropdownMenuItem<JournalSourceType?>(
                value: null,
                child: Text('All source types'),
              ),
              for (final source in sourceTypes)
                DropdownMenuItem<JournalSourceType?>(
                  value: source,
                  child: Text(plainEnglishLabel(source.wire!)),
                ),
            ],
            onChanged: (value) => setState(() => _sourceType = value),
          ),
          const SizedBox(height: 10),
          _DateFilterButton(
            label: 'Effective date from',
            value: _effectiveFrom,
            onPressed: () async {
              final value = await showDatePicker(
                context: context,
                firstDate: DateTime(2000),
                lastDate: DateTime(2100),
                initialDate: _effectiveFrom ?? DateTime.now(),
              );
              if (value != null) setState(() => _effectiveFrom = value);
            },
          ),
          const SizedBox(height: 10),
          _DateFilterButton(
            label: 'Effective date to',
            value: _effectiveTo,
            onPressed: () async {
              final value = await showDatePicker(
                context: context,
                firstDate: DateTime(2000),
                lastDate: DateTime(2100),
                initialDate: _effectiveTo ?? DateTime.now(),
              );
              if (value != null) setState(() => _effectiveTo = value);
            },
          ),
          const SizedBox(height: 18),
          Row(
            children: [
              Expanded(
                child: OutlinedButton(
                  onPressed: () => Navigator.of(context).pop(
                    widget.initial.copyWith(
                      clearPropertyId: true,
                      clearUnitId: true,
                      clearSourceType: true,
                      clearEffectiveFrom: true,
                      clearEffectiveTo: true,
                    ),
                  ),
                  child: const Text('Clear'),
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: FilledButton(
                  onPressed: () => Navigator.of(context).pop(
                    widget.initial.copyWith(
                      propertyId: int.tryParse(_propertyController.text.trim()),
                      unitId: int.tryParse(_unitController.text.trim()),
                      sourceType: _sourceType,
                      effectiveFrom: _effectiveFrom,
                      effectiveTo: _effectiveTo,
                      clearPropertyId: _propertyController.text.trim().isEmpty,
                      clearUnitId: _unitController.text.trim().isEmpty,
                      clearSourceType: _sourceType == null,
                      clearEffectiveFrom: _effectiveFrom == null,
                      clearEffectiveTo: _effectiveTo == null,
                    ),
                  ),
                  child: const Text('Apply filters'),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

class _DateFilterButton extends StatelessWidget {
  const _DateFilterButton({
    required this.label,
    required this.value,
    required this.onPressed,
  });

  final String label;
  final DateTime? value;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    return OutlinedButton.icon(
      onPressed: onPressed,
      icon: const Icon(Icons.event_outlined),
      label: Align(
        alignment: Alignment.centerLeft,
        child: Text(value == null ? label : '$label · ${dateFmt(value!)}'),
      ),
    );
  }
}

String _increaseLabel(GeneralLedgerRow row) {
  final amount = _increase(row);
  if (amount != '—') return '+$amount';
  final decrease = _decrease(row);
  return decrease == '—' ? '—' : '-$decrease';
}

String _increase(GeneralLedgerRow row) {
  if (row.normalBalance == NormalBalance.debit) {
    return _amount(row.debitAmount);
  }
  if (row.normalBalance == NormalBalance.credit) {
    return _amount(row.creditAmount);
  }
  return '—';
}

String _decrease(GeneralLedgerRow row) {
  if (row.normalBalance == NormalBalance.debit) {
    return _amount(row.creditAmount);
  }
  if (row.normalBalance == NormalBalance.credit) {
    return _amount(row.debitAmount);
  }
  return '—';
}

String _amount(num value) => value == 0 ? '—' : _signedMoney(value);

String _signedMoney(num value) =>
    value < 0 ? '-${moneyFmt(value.abs())}' : moneyFmt(value);
