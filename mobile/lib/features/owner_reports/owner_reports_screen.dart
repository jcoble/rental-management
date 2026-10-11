import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/files/document_opener.dart';
import '../../core/time/app_clock.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../accounting/accounting_repository.dart';
import '../accounting/accounting_book_models.dart';
import '../accounting/accounting_impact_card.dart';
import '../home/mobile_domain_chrome.dart';
import 'owner_reports_repository.dart';
import '../../core/presentation/formatting.dart';

// ── Helpers ───────────────────────────────────────────────────────────────────

String _fmtDate(DateTime value) {
  final local = value.toLocal();
  return '${local.month}/${local.day}/${local.year}';
}

// ── Screen ────────────────────────────────────────────────────────────────────

/// Owner reports screen — year selector + owner list.
///
/// Tap an owner to open a detail bottom sheet with per-property financials.
class OwnerReportsScreen extends ConsumerStatefulWidget {
  const OwnerReportsScreen({super.key});

  @override
  ConsumerState<OwnerReportsScreen> createState() => _OwnerReportsScreenState();
}

class _OwnerReportsScreenState extends ConsumerState<OwnerReportsScreen> {
  int _selectedYear = DateTime.now().year;
  DateTime _selectedMonth = DateTime.utc(
    DateTime.now().year,
    DateTime.now().month,
  );
  int _currentYear = DateTime.now().year;
  String? _clockError;

  /// Year for the accountant packet. Defaults to the previous calendar year,
  /// since that's the tax year landlords usually hand off.
  int _packetYear = DateTime.now().year - 1;

  @override
  void initState() {
    super.initState();
    Future.microtask(_initializePeriodsAndLoad);
  }

  Future<void> _initializePeriodsAndLoad() async {
    try {
      final appNow = await ref.read(appNowProvider.future);
      if (!mounted) return;
      final month = DateTime.utc(appNow.year, appNow.month);
      setState(() {
        _clockError = null;
        _currentYear = appNow.year;
        _selectedYear = appNow.year;
        _selectedMonth = month;
        _packetYear = appNow.year - 1;
      });
      await ref.read(ownerSummariesProvider.notifier).load(year: appNow.year);
      await ref.read(monthlyReportsProvider.notifier).load(month: month);
    } on DioException catch (error) {
      if (!mounted) return;
      setState(
        () => _clockError = ApiException.fromDioException(error).message,
      );
    } catch (error) {
      if (!mounted) return;
      setState(() => _clockError = error.toString());
    }
  }

  void _retryClock() {
    setState(() => _clockError = null);
    Future.microtask(_initializePeriodsAndLoad);
  }

  Future<void> _refresh() async {
    await ref.read(monthlyReportsProvider.notifier).refresh();
    await ref.read(ownerSummariesProvider.notifier).refresh();
  }

  void _changeYear(int year) {
    setState(() => _selectedYear = year);
    ref.read(ownerSummariesProvider.notifier).load(year: year);
  }

  void _changeMonth(int offset) {
    final next = DateTime.utc(
      _selectedMonth.year,
      _selectedMonth.month + offset,
    );
    setState(() => _selectedMonth = next);
    ref.read(monthlyReportsProvider.notifier).load(month: next);
  }

  /// Fetches the year-end packet PDF bytes (authed) and hands them to the OS
  /// viewer via [DocumentOpener] (FileProvider `content://` URI, share fallback).
  Future<void> _openYearEndPacket(int year) async {
    final messenger = ScaffoldMessenger.of(context);
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text('Preparing your $year packet…')));
    try {
      final bytes = await ref
          .read(accountingRepositoryProvider)
          .yearEndPacketBytes(year);
      await DocumentOpener.openBytes(
        bytes: bytes,
        fileName: 'year-end-$year.pdf',
      );
      messenger.hideCurrentSnackBar();
    } on ApiException catch (e) {
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  void _showStatement(OwnerSummary owner) {
    ref
        .read(ownerStatementProvider.notifier)
        .load(owner.ownerId, _selectedYear);
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _OwnerStatementSheet(ownerName: owner.ownerName),
    );
  }

  @override
  Widget build(BuildContext context) {
    final asyncState = ref.watch(ownerSummariesProvider);
    final monthlyReports = ref.watch(monthlyReportsProvider);
    final clockError = _clockError;
    final now = _currentYear;
    final yearSelector = _YearSelector(
      selected: _selectedYear,
      years: List.generate(5, (i) => now - i),
      onChanged: _changeYear,
    );

    return Scaffold(
      appBar: mobileDomainRootAppBar(
        context,
        title: const Text('Reports'),
        actions: [yearSelector, const SizedBox(width: 8)],
      ),
      body: Column(
        children: [
          MobileDomainEmbeddedToolbar(children: [yearSelector]),
          Expanded(
            child: clockError != null
                ? ListView(
                    physics: const AlwaysScrollableScrollPhysics(),
                    padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
                    children: [
                      _ErrorBody(message: clockError, onRetry: _retryClock),
                    ],
                  )
                : RefreshIndicator(
                    onRefresh: _refresh,
                    child: asyncState.when(
                      loading: () =>
                          const Center(child: CircularProgressIndicator()),
                      error: (e, _) => _ErrorBody(
                        message: e is ApiException ? e.message : e.toString(),
                        onRetry: () =>
                            ref.read(ownerSummariesProvider.notifier).refresh(),
                      ),
                      data: (list) {
                        final packetCard = _YearEndPacketCard(
                          year: _packetYear,
                          years: List.generate(5, (i) => now - 1 - i),
                          onYearChanged: (y) => setState(() => _packetYear = y),
                          onOpen: () => _openYearEndPacket(_packetYear),
                        );
                        final monthlySection = _MonthlyCloseReportsSection(
                          state: monthlyReports,
                          selectedMonth: _selectedMonth,
                          onPrevious: () => _changeMonth(-1),
                          onNext: () => _changeMonth(1),
                          onRetry: () => ref
                              .read(monthlyReportsProvider.notifier)
                              .load(month: _selectedMonth),
                        );
                        if (list.isEmpty) {
                          return ListView(
                            physics: const AlwaysScrollableScrollPhysics(),
                            padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
                            children: [
                              monthlySection,
                              const SizedBox(height: 16),
                              packetCard,
                              const SizedBox(height: 24),
                              _EmptyBody(year: _selectedYear),
                            ],
                          );
                        }
                        return ListView.separated(
                          physics: const AlwaysScrollableScrollPhysics(),
                          padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
                          itemCount: list.length + 2,
                          separatorBuilder: (context, index) {
                            if (index <= 1) return const SizedBox(height: 16);
                            return const MobileM3ListDivider();
                          },
                          itemBuilder: (_, i) {
                            if (i == 0) {
                              return monthlySection;
                            }
                            if (i == 1) {
                              return packetCard;
                            }
                            final owner = list[i - 2];
                            return _OwnerSummaryListItem(
                              owner: owner,
                              position:
                                  MobileM3ListItemPositionForIndex.forIndex(
                                    i - 2,
                                    list.length,
                                  ),
                              onTap: () => _showStatement(owner),
                            );
                          },
                        );
                      },
                    ),
                  ),
          ),
        ],
      ),
    );
  }
}

class _MonthlyCloseReportsSection extends StatelessWidget {
  const _MonthlyCloseReportsSection({
    required this.state,
    required this.selectedMonth,
    required this.onPrevious,
    required this.onNext,
    required this.onRetry,
  });

  final MonthlyReportsState state;
  final DateTime selectedMonth;
  final VoidCallback onPrevious;
  final VoidCallback onNext;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            Expanded(
              child: Text(
                'Monthly close',
                style: theme.textTheme.titleLarge?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
              ),
            ),
            IconButton(
              tooltip: 'Previous month',
              onPressed: onPrevious,
              icon: const Icon(Icons.chevron_left),
            ),
            Text(
              _fmtMonth(selectedMonth),
              style: theme.textTheme.labelLarge?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            IconButton(
              tooltip: 'Next month',
              onPressed: onNext,
              icon: const Icon(Icons.chevron_right),
            ),
          ],
        ),
        const SizedBox(height: 8),
        if (state.loading)
          const Padding(
            padding: EdgeInsets.symmetric(vertical: 24),
            child: Center(child: CircularProgressIndicator()),
          )
        else if (state.error != null)
          _ErrorBody(message: state.error!, onRetry: onRetry)
        else
          Material(
            color: cs.surfaceContainerLow,
            borderRadius: M3Shape.radiusLarge,
            clipBehavior: Clip.antiAlias,
            child: Column(
              children: [
                for (var index = 0; index < state.results.length; index++) ...[
                  _ReportResultTile(result: state.results[index]),
                  if (index < state.results.length - 1)
                    const MobileM3ListDivider(),
                ],
                if (state.results.isEmpty)
                  Padding(
                    padding: const EdgeInsets.all(16),
                    child: Text(
                      'No monthly reports are available.',
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color: cs.onSurfaceVariant,
                      ),
                    ),
                  ),
              ],
            ),
          ),
      ],
    );
  }
}

class _ReportResultTile extends StatelessWidget {
  const _ReportResultTile({required this.result});

  final ReportRunResult result;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final metrics = _metricsFor(result.data);

    return ListTile(
      contentPadding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
      leading: MobileM3LeadingIcon(
        icon: _iconFor(result.entry.key),
        backgroundColor: cs.secondaryContainer,
        foregroundColor: cs.onSecondaryContainer,
      ),
      title: Text(
        result.entry.title,
        maxLines: 2,
        overflow: TextOverflow.ellipsis,
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w700,
        ),
      ),
      subtitle: Text(
        metrics.isEmpty ? result.entry.description : metrics.join('  |  '),
        maxLines: 3,
        overflow: TextOverflow.ellipsis,
      ),
      trailing: const Icon(Icons.chevron_right),
      onTap: () => _showReportSheet(context, result),
    );
  }

  static IconData _iconFor(String key) => switch (key) {
    'cash-flow' => Icons.waterfall_chart,
    'income-expense-statement' || 'property-pnl-summary' => Icons.bar_chart,
    'general-ledger' || 'rent-ledger' => Icons.receipt_long,
    'rent-roll' => Icons.home_work_outlined,
    'security-deposit-register' => Icons.account_balance_wallet_outlined,
    'delinquency' => Icons.warning_amber_outlined,
    'owner-distributions' => Icons.payments_outlined,
    _ => Icons.summarize_outlined,
  };
}

void _showReportSheet(BuildContext context, ReportRunResult result) {
  showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) => _ReportDetailSheet(result: result),
  );
}

class _ReportDetailSheet extends ConsumerWidget {
  const _ReportDetailSheet({required this.result});

  final ReportRunResult result;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final state = ref.watch(monthlyReportsProvider);
    final currentResult = _currentReportResult(state, result);
    final rows = _detailRows(currentResult.data);
    final sections = ownerReportDisplaySections(currentResult);
    final paging = state.pagingFor(currentResult.entry.key);

    return SafeArea(
      child: DraggableScrollableSheet(
        expand: false,
        initialChildSize: 0.78,
        minChildSize: 0.4,
        maxChildSize: 0.95,
        builder: (context, controller) {
          return ListView(
            controller: controller,
            padding: const EdgeInsets.fromLTRB(20, 20, 20, 28),
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      currentResult.entry.title,
                      style: theme.textTheme.titleLarge?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                  IconButton(
                    icon: const Icon(Icons.close),
                    onPressed: () => Navigator.of(context).pop(),
                  ),
                ],
              ),
              Text(
                currentResult.entry.description,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 16),
              for (final row in rows) _ReportDetailRow(row: row),
              for (final section in sections)
                _ReportRowsSection(section: section),
              _ReportPagingControls(
                result: currentResult,
                paging: paging,
                loading: state.loading,
              ),
            ],
          );
        },
      ),
    );
  }
}

ReportRunResult _currentReportResult(
  MonthlyReportsState state,
  ReportRunResult fallback,
) {
  for (final result in state.results) {
    if (result.entry.key == fallback.entry.key) return result;
  }
  return fallback;
}

class _ReportDetailRow extends StatelessWidget {
  const _ReportDetailRow({required this.row});

  final MapEntry<String, Object?> row;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 9),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(
            child: Text(
              _label(row.key),
              style: theme.textTheme.bodyMedium?.copyWith(
                color: cs.onSurfaceVariant,
              ),
            ),
          ),
          const SizedBox(width: 16),
          Flexible(
            child: Text(
              ownerReportDisplayValue(row.value, key: row.key),
              textAlign: TextAlign.right,
              style: theme.textTheme.bodyMedium?.copyWith(
                fontFeatures: const [FontFeature.tabularFigures()],
                fontWeight: FontWeight.w700,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

List<String> _metricsFor(Map<String, dynamic> data) {
  final rows = _detailRows(data);
  return rows
      .where(
        (entry) =>
            entry.key.toLowerCase().contains('total') ||
            entry.key.toLowerCase().contains('count') ||
            entry.key == 'from' ||
            entry.key == 'to' ||
            entry.key == 'year',
      )
      .take(3)
      .map(
        (entry) =>
            '${_label(entry.key)} ${ownerReportDisplayValue(entry.value, key: entry.key)}',
      )
      .toList();
}

List<MapEntry<String, Object?>> _detailRows(Map<String, dynamic> data) {
  const preferred = [
    'from',
    'to',
    'asOf',
    'generatedAt',
    'year',
    'totalIncome',
    'totalExpense',
    'totalExpenses',
    'totalNet',
    'totalBalance',
    'closingBalance',
    'totalOutstanding',
    'totalCurrentBalance',
    'totalHeld',
    'totalDistributed',
    'totalUndistributed',
    'leaseCount',
    'totalCount',
    'openCount',
    'completedCount',
  ];
  final entries = <MapEntry<String, Object?>>[];
  for (final key in preferred) {
    if (data.containsKey(key) && _isScalar(data[key])) {
      entries.add(MapEntry(key, data[key]));
    }
  }
  if (entries.isNotEmpty) return entries;
  return data.entries
      .where((entry) => _isScalar(entry.value))
      .take(12)
      .toList();
}

class OwnerReportDisplaySection {
  const OwnerReportDisplaySection({required this.title, required this.rows});

  final String title;
  final List<OwnerReportDisplayRow> rows;
}

class OwnerReportDisplayRow {
  const OwnerReportDisplayRow({required this.title, required this.details});

  final String title;
  final List<String> details;
}

@visibleForTesting
List<OwnerReportDisplaySection> ownerReportDisplaySections(
  ReportRunResult result,
) {
  final sections = switch (result.entry.key) {
    'income-expense-statement' || 'cash-flow' => [
      _section(
        'Months',
        _maps(result.data['months']).map(_cashFlowMonthRow).toList(),
      ),
    ],
    'property-pnl-summary' => [
      _section('Properties', _maps(result.data['rows']).map(_pnlRow).toList()),
    ],
    'general-ledger' => [
      _section(
        'Entries',
        _maps(result.data['entries']).map(_generalLedgerRow).toList(),
      ),
    ],
    'rent-roll' => [
      _section(
        'Rentals',
        _maps(result.data['rows']).map(_rentRollRow).toList(),
      ),
    ],
    'rent-ledger' => [
      _section('Leases', _rentLedgerRows(_maps(result.data['leases']))),
    ],
    'delinquency' => [
      _section(
        'Past due',
        _maps(result.data['rows']).map(_delinquencyRow).toList(),
      ),
    ],
    'security-deposit-register' => [
      _section(
        'Deposits',
        _maps(result.data['rows']).map(_securityDepositRow).toList(),
      ),
    ],
    'owner-distributions' => [
      _section(
        'Owners',
        _maps(result.data['rows']).map(_ownerDistributionRow).toList(),
      ),
    ],
    _ => const <OwnerReportDisplaySection>[],
  };
  return sections.where((section) => section.rows.isNotEmpty).toList();
}

OwnerReportDisplaySection _section(
  String title,
  List<OwnerReportDisplayRow> rows,
) => OwnerReportDisplaySection(title: title, rows: rows);

List<Map<String, dynamic>> _maps(Object? value) {
  if (value is! List) return const [];
  return value.whereType<Map<String, dynamic>>().toList();
}

OwnerReportDisplayRow _cashFlowMonthRow(Map<String, dynamic> row) =>
    OwnerReportDisplayRow(
      title: _text(row, ['label', 'monthKey']),
      details: [
        _detail(row, 'Income', 'income'),
        _detail(row, 'Expenses', 'expense'),
        _detail(row, 'Net', 'net'),
      ],
    );

OwnerReportDisplayRow _pnlRow(Map<String, dynamic> row) =>
    OwnerReportDisplayRow(
      title: _text(row, ['propertyName']),
      details: [
        _detail(row, 'Income', 'income'),
        _detail(row, 'Expenses', 'expense'),
        _detail(row, 'Net', 'net'),
      ],
    );

OwnerReportDisplayRow _generalLedgerRow(Map<String, dynamic> row) =>
    OwnerReportDisplayRow(
      title: [
        _text(row, ['date']),
        _text(row, ['type']),
        _text(row, ['description']),
      ].where((part) => part.isNotEmpty).join(' · '),
      details: [
        _detail(row, 'Category', 'category'),
        _detail(row, 'Amount', 'amount'),
        _detail(row, 'Balance', 'runningBalance'),
      ],
    );

OwnerReportDisplayRow _rentRollRow(Map<String, dynamic> row) =>
    OwnerReportDisplayRow(
      title: _propertyUnitTitle(row),
      details: [
        _detail(row, 'Tenant', 'tenantName'),
        _detail(row, 'Rent', 'monthlyRent'),
        _detail(row, 'Deposit', 'securityDeposit'),
        _detail(row, 'Status', 'statusName'),
      ],
    );

List<OwnerReportDisplayRow> _rentLedgerRows(List<Map<String, dynamic>> leases) {
  final rows = <OwnerReportDisplayRow>[];
  for (final lease in leases) {
    rows.add(
      OwnerReportDisplayRow(
        title: _propertyUnitTenantTitle(lease),
        details: [
          _detail(lease, 'Charged', 'totalCharged'),
          _detail(lease, 'Credits', 'totalCredits'),
          _detail(lease, 'Balance', 'balance'),
        ],
      ),
    );
    for (final entry in _maps(lease['entries'])) {
      rows.add(
        OwnerReportDisplayRow(
          title: [
            _text(entry, ['date']),
            _text(entry, ['type']),
            _text(entry, ['description']),
          ].where((part) => part.isNotEmpty).join(' · '),
          details: [
            _detail(entry, 'Charge', 'charge'),
            _detail(entry, 'Credit', 'credit'),
            _detail(entry, 'Balance', 'balance'),
          ],
        ),
      );
    }
  }
  return rows;
}

OwnerReportDisplayRow _delinquencyRow(Map<String, dynamic> row) {
  final buckets = row['buckets'] is Map<String, dynamic>
      ? row['buckets'] as Map<String, dynamic>
      : const <String, dynamic>{};
  return OwnerReportDisplayRow(
    title: _propertyUnitTenantTitle(row),
    details: [
      _detail(row, 'Total', 'amount', fallbackKeys: ['total']),
      _detail(row, 'Oldest days', 'oldestOverdueDays'),
      _detail(buckets, 'Current', 'amount', fallbackKeys: ['current']),
      _detail(buckets, '31-60', 'amount', fallbackKeys: ['days31To60']),
      _detail(buckets, '61-90', 'amount', fallbackKeys: ['days61To90']),
      _detail(buckets, '90+', 'amount', fallbackKeys: ['over90']),
    ],
  );
}

OwnerReportDisplayRow _securityDepositRow(Map<String, dynamic> row) =>
    OwnerReportDisplayRow(
      title: _propertyUnitTenantTitle(row),
      details: [
        _detail(row, 'Held', 'held'),
        _detail(
          row,
          'Deductions',
          'deductions',
          fallbackKeys: ['totalDeductions'],
        ),
        _detail(row, 'Returned', 'returned', fallbackKeys: ['totalReturned']),
        _detail(
          row,
          'Current balance',
          'currentBalance',
          fallbackKeys: ['totalCurrentBalance', 'balance'],
        ),
      ],
    );

OwnerReportDisplayRow _ownerDistributionRow(Map<String, dynamic> row) =>
    OwnerReportDisplayRow(
      title: _text(row, ['ownerName']),
      details: [
        _detail(row, 'Net to owner', 'netToOwner'),
        _detail(row, 'Distributed', 'totalDistributed'),
        _detail(
          row,
          'Undistributed',
          'undistributed',
          fallbackKeys: ['totalUndistributed'],
        ),
      ],
    );

String _propertyUnitTitle(Map<String, dynamic> row) => [
  _text(row, ['propertyName']),
  if (_text(row, ['unitNumber']).isNotEmpty)
    'Unit ${_text(row, ['unitNumber'])}',
].where((part) => part.isNotEmpty).join(' · ');

String _propertyUnitTenantTitle(Map<String, dynamic> row) => [
  _propertyUnitTitle(row),
  _text(row, ['tenantName']),
].where((part) => part.isNotEmpty).join(' · ');

String _detail(
  Map<String, dynamic> row,
  String label,
  String key, {
  List<String> fallbackKeys = const [],
}) {
  final value = _firstValue(row, [key, ...fallbackKeys]);
  return '$label ${ownerReportDisplayValue(value, key: key)}';
}

Object? _firstValue(Map<String, dynamic> row, List<String> keys) {
  for (final key in keys) {
    if (row.containsKey(key)) return row[key];
  }
  return null;
}

String _text(Map<String, dynamic> row, List<String> keys) {
  final value = _firstValue(row, keys);
  return value?.toString() ?? '';
}

class _ReportRowsSection extends StatelessWidget {
  const _ReportRowsSection({required this.section});

  final OwnerReportDisplaySection section;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return Padding(
      padding: const EdgeInsets.only(top: 14),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            section.title,
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w700,
            ),
          ),
          const SizedBox(height: 8),
          Material(
            color: cs.surfaceContainerLow,
            borderRadius: M3Shape.radiusLarge,
            clipBehavior: Clip.antiAlias,
            child: Column(
              children: [
                for (var index = 0; index < section.rows.length; index++) ...[
                  ListTile(
                    title: Text(
                      section.rows[index].title,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    subtitle: Text(section.rows[index].details.join('\n')),
                    isThreeLine: section.rows[index].details.length > 1,
                  ),
                  if (index < section.rows.length - 1)
                    const MobileM3ListDivider(),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _ReportPagingControls extends ConsumerWidget {
  const _ReportPagingControls({
    required this.result,
    required this.paging,
    required this.loading,
  });

  final ReportRunResult result;
  final ReportPaging paging;
  final bool loading;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final params = result.entry.params.toSet();
    if (!params.contains('skip') || !params.contains('take')) {
      return const SizedBox.shrink();
    }
    final totalCount = result.data['totalCount'];
    final total = totalCount is num ? totalCount.toInt() : null;
    final hasNext = total == null ? true : paging.skip + paging.take < total;
    final pageEnd = total == null
        ? paging.skip + paging.take
        : (paging.skip + paging.take > total
              ? total
              : paging.skip + paging.take);
    return Padding(
      padding: const EdgeInsets.only(top: 16),
      child: Row(
        children: [
          Expanded(
            child: Text(
              'Rows ${paging.skip + 1}-$pageEnd',
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ),
          TextButton(
            onPressed: loading || paging.skip == 0
                ? null
                : () => ref
                      .read(monthlyReportsProvider.notifier)
                      .pageReport(result.entry.key, -1),
            child: const Text('Previous'),
          ),
          const SizedBox(width: 8),
          FilledButton.tonal(
            onPressed: loading || !hasNext
                ? null
                : () => ref
                      .read(monthlyReportsProvider.notifier)
                      .pageReport(result.entry.key, 1),
            child: const Text('Next'),
          ),
        ],
      ),
    );
  }
}

bool _isScalar(Object? value) =>
    value == null || value is String || value is num || value is bool;

@visibleForTesting
String ownerReportDisplayValue(Object? value, {String? key}) {
  if (value is num) {
    if (_isMoneyMetricKey(key)) return moneyFmt(value.toDouble());
    return _fmtNumber(value);
  }
  return value?.toString() ?? '-';
}

bool _isMoneyMetricKey(String? key) {
  final normalized = key?.toLowerCase() ?? '';
  if (normalized.isEmpty) return false;
  if (normalized == 'year' ||
      normalized == 'month' ||
      normalized == 'skip' ||
      normalized == 'take' ||
      normalized == 'days' ||
      normalized.endsWith('id') ||
      normalized.endsWith('count') ||
      normalized.contains('percent')) {
    return false;
  }
  return normalized.contains('amount') ||
      normalized.contains('balance') ||
      normalized.contains('income') ||
      normalized.contains('expense') ||
      normalized.contains('net') ||
      normalized.contains('rent') ||
      normalized.contains('deposit') ||
      normalized.contains('deduction') ||
      normalized.contains('returned') ||
      normalized.contains('held') ||
      normalized.contains('distributed') ||
      normalized.contains('charged') ||
      normalized.contains('credits');
}

String _fmtNumber(num value) {
  if (value is int) return value.toString();
  final asDouble = value.toDouble();
  if (asDouble.isFinite && asDouble == asDouble.truncateToDouble()) {
    return asDouble.toInt().toString();
  }
  return value.toString();
}

String _label(String key) {
  final spaced = key
      .replaceAllMapped(RegExp(r'([a-z0-9])([A-Z])'), (m) => '${m[1]} ${m[2]}')
      .replaceAll('-', ' ');
  return spaced.isEmpty
      ? spaced
      : spaced[0].toUpperCase() + spaced.substring(1);
}

String _fmtMonth(DateTime value) {
  const months = [
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
  return '${months[value.month - 1]} ${value.year}';
}

// ── Year Selector ─────────────────────────────────────────────────────────────

class _YearSelector extends StatelessWidget {
  const _YearSelector({
    required this.selected,
    required this.years,
    required this.onChanged,
  });

  final int selected;
  final List<int> years;
  final ValueChanged<int> onChanged;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return DropdownButton<int>(
      value: selected,
      underline: const SizedBox.shrink(),
      style: Theme.of(context).textTheme.bodyMedium?.copyWith(
        color: cs.onSurface,
        fontWeight: FontWeight.w600,
      ),
      items: years
          .map((y) => DropdownMenuItem(value: y, child: Text('$y')))
          .toList(),
      onChanged: (v) {
        if (v != null) onChanged(v);
      },
    );
  }
}

// ── Year-end Packet Card ──────────────────────────────────────────────────────

/// Plain-language affordance to download the accountant packet PDF.
class _YearEndPacketCard extends StatelessWidget {
  const _YearEndPacketCard({
    required this.year,
    required this.years,
    required this.onYearChanged,
    required this.onOpen,
  });

  final int year;
  final List<int> years;
  final ValueChanged<int> onYearChanged;
  final VoidCallback onOpen;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return AnimatedContainer(
      duration: M3Motion.medium2,
      curve: M3Motion.emphasizedDecelerate,
      decoration: BoxDecoration(
        color: cs.surfaceContainerHigh,
        borderRadius: M3Shape.radiusLargeIncreased,
        border: Border.all(color: cs.outlineVariant.withValues(alpha: 0.36)),
      ),
      clipBehavior: Clip.antiAlias,
      child: Material(
        color: Colors.transparent,
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  MobileM3LeadingIcon(
                    icon: Icons.picture_as_pdf_outlined,
                    backgroundColor: cs.primaryContainer,
                    foregroundColor: cs.onPrimaryContainer,
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Text(
                      'Year-end packet (PDF)',
                      style: theme.textTheme.titleSmall?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 6),
              Text(
                'Hand your accountant a clean PDF: Schedule E, P&L, '
                'cash flow, rent roll.',
                style: theme.textTheme.bodySmall?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 14),
              Row(
                children: [
                  Text(
                    'Tax year',
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: cs.onSurfaceVariant,
                    ),
                  ),
                  const SizedBox(width: 10),
                  DropdownButton<int>(
                    value: year,
                    underline: const SizedBox.shrink(),
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: cs.onSurface,
                      fontWeight: FontWeight.w600,
                    ),
                    dropdownColor: cs.surface,
                    items: years
                        .map(
                          (y) => DropdownMenuItem(value: y, child: Text('$y')),
                        )
                        .toList(),
                    onChanged: (v) {
                      if (v != null) onYearChanged(v);
                    },
                  ),
                ],
              ),
              const SizedBox(height: 10),
              SizedBox(
                width: double.infinity,
                child: FilledButton.icon(
                  style: FilledButton.styleFrom(
                    minimumSize: const Size.fromHeight(56),
                    textStyle: theme.textTheme.labelLarge,
                  ),
                  onPressed: onOpen,
                  icon: const Icon(Icons.download_outlined, size: 20),
                  label: const Text('Download packet'),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

// ── Owner Summary List Item ───────────────────────────────────────────────────

class _OwnerSummaryListItem extends StatelessWidget {
  const _OwnerSummaryListItem({
    required this.owner,
    required this.position,
    required this.onTap,
  });

  final OwnerSummary owner;
  final MobileM3ListItemPosition position;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final isPositive = owner.netToOwner >= 0;

    return MobileM3ListItem(
      position: position,
      leading: MobileM3LeadingIcon(
        icon: Icons.person_outline,
        backgroundColor: cs.primaryContainer,
        foregroundColor: cs.onPrimaryContainer,
      ),
      title: Text(
        owner.ownerName,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w700,
        ),
      ),
      supporting: [
        Text(
          'Distributed ${moneyFmt(owner.totalDistributed)}',
          style: theme.textTheme.bodySmall?.copyWith(
            color: cs.onSurfaceVariant,
          ),
        ),
        Text(
          'Undistributed ${moneyFmt(owner.undistributed)}',
          style: theme.textTheme.bodySmall?.copyWith(
            color: cs.onSurfaceVariant,
          ),
        ),
      ],
      trailing: Row(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.center,
        children: [
          Text(
            moneyFmt(owner.netToOwner),
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w700,
              color: isPositive ? null : cs.error,
            ),
          ),
          const SizedBox(width: 8),
          Icon(Icons.chevron_right, color: cs.onSurfaceVariant, size: 18),
        ],
      ),
      onTap: onTap,
    );
  }
}

// ── Owner Statement Bottom Sheet ──────────────────────────────────────────────

class _OwnerStatementSheet extends ConsumerWidget {
  const _OwnerStatementSheet({required this.ownerName});

  final String ownerName;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final asyncState = ref.watch(ownerStatementProvider);
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;

    return Padding(
      padding: EdgeInsets.fromLTRB(0, 20, 0, 20 + bottomPadding),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // Header
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 20),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    ownerName,
                    style: theme.textTheme.titleLarge?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                IconButton(
                  icon: const Icon(Icons.close),
                  onPressed: () => Navigator.of(context).pop(),
                ),
              ],
            ),
          ),
          const Divider(height: 16),
          Flexible(
            child: asyncState.when(
              loading: () => const Padding(
                padding: EdgeInsets.all(40),
                child: Center(child: CircularProgressIndicator()),
              ),
              error: (e, _) => Padding(
                padding: const EdgeInsets.all(20),
                child: Text(
                  e is ApiException ? e.message : e.toString(),
                  style: TextStyle(color: cs.error),
                  textAlign: TextAlign.center,
                ),
              ),
              data: (stmt) {
                if (stmt == null) {
                  return const Padding(
                    padding: EdgeInsets.all(20),
                    child: Center(child: CircularProgressIndicator()),
                  );
                }
                return SingleChildScrollView(
                  padding: const EdgeInsets.symmetric(horizontal: 20),
                  child: _StatementBody(statement: stmt),
                );
              },
            ),
          ),
        ],
      ),
    );
  }
}

class _StatementBody extends ConsumerWidget {
  const _StatementBody({required this.statement});

  final OwnerStatement statement;

  OwnerDistributionQuery get _distributionQuery => OwnerDistributionQuery(
    ownerEntityId: statement.ownerId,
    year: statement.year,
  );

  Future<void> _refreshAfterDistribution(WidgetRef ref) async {
    ref.invalidate(ownerDistributionsProvider(_distributionQuery));
    await ref
        .read(ownerStatementProvider.notifier)
        .load(statement.ownerId, statement.year);
    await ref.read(ownerSummariesProvider.notifier).refresh();
  }

  Future<void> _recordDistribution(BuildContext context, WidgetRef ref) async {
    final input = await showDialog<CreateOwnerDistributionInput>(
      context: context,
      builder: (_) => _RecordDistributionDialog(statement: statement),
    );
    if (input == null || !context.mounted) return;

    final messenger = ScaffoldMessenger.of(context);
    try {
      await ref.read(ownerReportsRepositoryProvider).createDistribution(input);
      await _refreshAfterDistribution(ref);
      if (!context.mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          const SnackBar(content: Text('Owner distribution recorded.')),
        );
    } on ApiException catch (e) {
      if (!context.mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  Future<void> _deleteDistribution(
    BuildContext context,
    WidgetRef ref,
    OwnerDistribution distribution,
  ) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (_) => AlertDialog(
        title: const Text('Delete distribution?'),
        content: Text(
          'Remove the ${moneyFmt(distribution.amount)} '
          '${distribution.method.label} distribution from '
          '${_fmtDate(distribution.date)}?',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
    if (confirmed != true || !context.mounted) return;

    final messenger = ScaffoldMessenger.of(context);
    try {
      await ref
          .read(ownerReportsRepositoryProvider)
          .deleteDistribution(distribution.id);
      await _refreshAfterDistribution(ref);
      if (!context.mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          const SnackBar(content: Text('Owner distribution deleted.')),
        );
    } on ApiException catch (e) {
      if (!context.mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final distributionsAsync = ref.watch(
      ownerDistributionsProvider(_distributionQuery),
    );
    final contributionsAsync = ref.watch(
      ownerContributionsProvider(_distributionQuery),
    );

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          crossAxisAlignment: CrossAxisAlignment.center,
          children: [
            Expanded(
              child: Text(
                '${statement.year} Annual Statement',
                style: theme.textTheme.bodySmall?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              ),
            ),
            FilledButton.tonalIcon(
              onPressed: () => _recordDistribution(context, ref),
              icon: const Icon(Icons.payments_outlined, size: 18),
              label: const Text('Record distribution'),
            ),
          ],
        ),
        const SizedBox(height: 12),

        // Totals card
        Card(
          color: cs.primaryContainer,
          child: Padding(
            padding: const EdgeInsets.all(14),
            child: Column(
              children: [
                _StatRow(
                  label: 'Total Income',
                  value: moneyFmt(statement.totalIncome),
                  theme: theme,
                  textColor: cs.onPrimaryContainer,
                ),
                _StatRow(
                  label: 'Total Expenses',
                  value: moneyFmt(statement.totalExpenses),
                  theme: theme,
                  textColor: cs.onPrimaryContainer,
                ),
                _StatRow(
                  label: 'Management Fee',
                  value: moneyFmt(statement.totalManagementFee),
                  theme: theme,
                  textColor: cs.onPrimaryContainer,
                ),
                const Divider(height: 12),
                _StatRow(
                  label: 'Net to Owner',
                  value: moneyFmt(statement.totalNetToOwner),
                  theme: theme,
                  textColor: cs.onPrimaryContainer,
                  bold: true,
                ),
                _StatRow(
                  label: 'Distributed',
                  value: moneyFmt(statement.totalDistributed),
                  theme: theme,
                  textColor: cs.onPrimaryContainer,
                ),
                const Divider(height: 12),
                _StatRow(
                  label: 'Undistributed',
                  value: moneyFmt(statement.undistributed),
                  theme: theme,
                  textColor: cs.onPrimaryContainer,
                  bold: true,
                ),
              ],
            ),
          ),
        ),

        const SizedBox(height: 16),

        _DistributionsSection(
          distributionsAsync: distributionsAsync,
          onDelete: (distribution) =>
              _deleteDistribution(context, ref, distribution),
        ),

        const SizedBox(height: 16),
        _OwnerContributionsSection(contributionsAsync: contributionsAsync),

        const SizedBox(height: 16),

        // Per-property table
        if (statement.properties.isNotEmpty) ...[
          Text(
            'By Property',
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w700,
            ),
          ),
          const SizedBox(height: 8),
          Card(
            child: Padding(
              padding: const EdgeInsets.all(12),
              child: Table(
                columnWidths: const {
                  0: FlexColumnWidth(2),
                  1: FlexColumnWidth(1.5),
                  2: FlexColumnWidth(1.5),
                  3: FlexColumnWidth(1.5),
                },
                children: [
                  TableRow(
                    children: [
                      _TableHeader(text: 'Property'),
                      _TableHeader(text: 'Income', align: TextAlign.right),
                      _TableHeader(text: 'Expenses', align: TextAlign.right),
                      _TableHeader(text: 'Net', align: TextAlign.right),
                    ],
                  ),
                  ...statement.properties.map(
                    (p) => TableRow(
                      children: [
                        _TableCell(text: p.propertyName),
                        _TableCell(
                          text: moneyFmt(p.rentalIncome),
                          align: TextAlign.right,
                        ),
                        _TableCell(
                          text: moneyFmt(p.expenses),
                          align: TextAlign.right,
                        ),
                        _TableCell(
                          text: moneyFmt(p.netToOwner),
                          align: TextAlign.right,
                          bold: true,
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
        const SizedBox(height: 16),
      ],
    );
  }
}

class _OwnerContributionsSection extends StatelessWidget {
  const _OwnerContributionsSection({required this.contributionsAsync});
  final AsyncValue<List<OwnerDistribution>> contributionsAsync;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      Text(
        'Contributions',
        style: Theme.of(
          context,
        ).textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w700),
      ),
      const SizedBox(height: 8),
      contributionsAsync.when(
        loading: () => const _OwnerDistributionSkeleton(),
        error: (error, _) =>
            Text(error is ApiException ? error.message : error.toString()),
        data: (items) => Column(
          children: [
            for (final item in items)
              ExpansionTile(
                title: Text(moneyFmt(item.amount)),
                subtitle: Text(
                  '${_fmtDate(item.date)} • ${item.propertyName ?? 'Your rentals'}',
                ),
                children: [
                  Padding(
                    padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
                    child: AccountingImpactCard(
                      sourceType: JournalSourceType.ownerContribution,
                      sourceId: item.id,
                    ),
                  ),
                ],
              ),
            if (items.isEmpty)
              const Padding(
                padding: EdgeInsets.all(16),
                child: Text('No owner contributions recorded for this year.'),
              ),
          ],
        ),
      ),
    ],
  );
}

class _DistributionsSection extends StatelessWidget {
  const _DistributionsSection({
    required this.distributionsAsync,
    required this.onDelete,
  });

  final AsyncValue<List<OwnerDistribution>> distributionsAsync;
  final ValueChanged<OwnerDistribution> onDelete;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          'Distributions',
          style: theme.textTheme.titleSmall?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        const SizedBox(height: 8),
        Card(
          child: distributionsAsync.when(
            loading: () => const Padding(
              padding: EdgeInsets.all(16),
              child: Column(
                children: [
                  _OwnerDistributionSkeleton(),
                  SizedBox(height: 8),
                  _OwnerDistributionSkeleton(),
                ],
              ),
            ),
            error: (e, _) => Padding(
              padding: const EdgeInsets.all(16),
              child: Text(
                e is ApiException ? e.message : e.toString(),
                style: TextStyle(color: cs.error),
              ),
            ),
            data: (items) {
              if (items.isEmpty) {
                return Padding(
                  padding: const EdgeInsets.all(16),
                  child: Text(
                    'No distributions recorded for this year.',
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: cs.onSurfaceVariant,
                    ),
                  ),
                );
              }

              return Column(
                children: [
                  for (var i = 0; i < items.length; i++) ...[
                    _DistributionTile(
                      distribution: items[i],
                      onDelete: () => onDelete(items[i]),
                    ),
                    if (i != items.length - 1)
                      const MobileM3ListDivider(indent: 16),
                  ],
                ],
              );
            },
          ),
        ),
      ],
    );
  }
}

class _OwnerDistributionSkeleton extends StatelessWidget {
  const _OwnerDistributionSkeleton();

  @override
  Widget build(BuildContext context) => Container(
    height: 64,
    decoration: BoxDecoration(
      color: Theme.of(context).colorScheme.surfaceContainerHighest,
      borderRadius: BorderRadius.circular(12),
    ),
  );
}

class _DistributionTile extends StatelessWidget {
  const _DistributionTile({required this.distribution, required this.onDelete});

  final OwnerDistribution distribution;
  final VoidCallback onDelete;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final subtitleParts = [
      _fmtDate(distribution.date),
      distribution.method.label,
      if (distribution.propertyName != null &&
          distribution.propertyName!.trim().isNotEmpty)
        distribution.propertyName!,
    ];

    return ExpansionTile(
      leading: CircleAvatar(
        backgroundColor: cs.secondaryContainer,
        foregroundColor: cs.onSecondaryContainer,
        child: const Icon(Icons.payments_outlined, size: 20),
      ),
      title: Text(
        moneyFmt(distribution.amount),
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w700,
        ),
      ),
      subtitle: Text(
        [
          subtitleParts.join(' • '),
          if (distribution.memo != null && distribution.memo!.trim().isNotEmpty)
            distribution.memo!,
        ].join('\n'),
      ),
      trailing: IconButton(
        tooltip: 'Delete distribution',
        icon: const Icon(Icons.delete_outline),
        color: cs.error,
        onPressed: onDelete,
      ),
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
          child: AccountingImpactCard(
            sourceType: JournalSourceType.ownerDistribution,
            sourceId: distribution.id,
          ),
        ),
      ],
    );
  }
}

class _RecordDistributionDialog extends StatefulWidget {
  const _RecordDistributionDialog({required this.statement});

  final OwnerStatement statement;

  @override
  State<_RecordDistributionDialog> createState() =>
      _RecordDistributionDialogState();
}

class _RecordDistributionDialogState extends State<_RecordDistributionDialog> {
  final _amountController = TextEditingController();
  final _memoController = TextEditingController();
  DateTime _date = DateTime.now();
  DistributionMethod _method = DistributionMethod.ach;
  int _propertyId = -1;
  String? _amountError;

  @override
  void dispose() {
    _amountController.dispose();
    _memoController.dispose();
    super.dispose();
  }

  Future<void> _pickDate() async {
    final picked = await showDatePicker(
      context: context,
      initialDate: _date,
      firstDate: DateTime(_date.year - 5),
      lastDate: DateTime(_date.year + 1),
    );
    if (picked != null) setState(() => _date = picked);
  }

  void _submit() {
    final amount = double.tryParse(_amountController.text.trim());
    if (amount == null || amount <= 0) {
      setState(() => _amountError = 'Enter an amount greater than zero.');
      return;
    }

    final memo = _memoController.text.trim();
    Navigator.of(context).pop(
      CreateOwnerDistributionInput(
        ownerEntityId: widget.statement.ownerId,
        propertyId: _propertyId <= 0 ? null : _propertyId,
        date: _date,
        amount: amount,
        method: _method,
        memo: memo.isEmpty ? null : memo,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final propertyItems = [
      const DropdownMenuItem<int>(value: -1, child: Text('No property')),
      ...widget.statement.properties
          .where((p) => p.propertyId > 0)
          .map(
            (p) => DropdownMenuItem<int>(
              value: p.propertyId,
              child: Text(p.propertyName),
            ),
          ),
    ];

    return AlertDialog(
      title: const Text('Record distribution'),
      content: SizedBox(
        width: 520,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                'Record cash actually paid to this owner. This does not '
                'create an expense or reduce property income.',
                style: theme.textTheme.bodySmall?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _amountController,
                autofocus: true,
                keyboardType: const TextInputType.numberWithOptions(
                  decimal: true,
                ),
                decoration: InputDecoration(
                  labelText: 'Amount',
                  prefixText: '\$ ',
                  border: const OutlineInputBorder(),
                  errorText: _amountError,
                ),
                onChanged: (_) {
                  if (_amountError != null) {
                    setState(() => _amountError = null);
                  }
                },
              ),
              const SizedBox(height: 12),
              DropdownButtonFormField<DistributionMethod>(
                initialValue: _method,
                decoration: const InputDecoration(
                  labelText: 'Method',
                  border: OutlineInputBorder(),
                ),
                items: DistributionMethod.values
                    .map(
                      (method) => DropdownMenuItem(
                        value: method,
                        child: Text(method.label),
                      ),
                    )
                    .toList(),
                onChanged: (value) {
                  if (value != null) setState(() => _method = value);
                },
              ),
              const SizedBox(height: 12),
              DropdownButtonFormField<int>(
                initialValue: _propertyId,
                decoration: const InputDecoration(
                  labelText: 'Property',
                  border: OutlineInputBorder(),
                ),
                items: propertyItems,
                onChanged: (value) {
                  if (value != null) setState(() => _propertyId = value);
                },
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _memoController,
                minLines: 2,
                maxLines: 4,
                textCapitalization: TextCapitalization.sentences,
                decoration: const InputDecoration(
                  labelText: 'Memo (optional)',
                  border: OutlineInputBorder(),
                ),
              ),
              const SizedBox(height: 12),
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'Date: ${_date.toIso8601String().split('T').first}',
                      style: theme.textTheme.bodyMedium,
                    ),
                  ),
                  TextButton(onPressed: _pickDate, child: const Text('Change')),
                ],
              ),
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(onPressed: _submit, child: const Text('Record')),
      ],
    );
  }
}

class _StatRow extends StatelessWidget {
  const _StatRow({
    required this.label,
    required this.value,
    required this.theme,
    required this.textColor,
    this.bold = false,
  });

  final String label;
  final String value;
  final ThemeData theme;
  final Color textColor;
  final bool bold;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 3),
      child: Row(
        children: [
          Expanded(
            child: Text(
              label,
              style: theme.textTheme.bodySmall?.copyWith(
                color: textColor.withValues(alpha: 0.8),
              ),
            ),
          ),
          Text(
            value,
            style: theme.textTheme.bodyMedium?.copyWith(
              color: textColor,
              fontWeight: bold ? FontWeight.w700 : null,
            ),
          ),
        ],
      ),
    );
  }
}

class _TableHeader extends StatelessWidget {
  const _TableHeader({required this.text, this.align = TextAlign.left});

  final String text;
  final TextAlign align;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 6),
      child: Text(
        text,
        textAlign: align,
        style: Theme.of(context).textTheme.labelSmall?.copyWith(
          color: Theme.of(context).colorScheme.onSurfaceVariant,
        ),
      ),
    );
  }
}

class _TableCell extends StatelessWidget {
  const _TableCell({
    required this.text,
    this.align = TextAlign.left,
    this.bold = false,
  });

  final String text;
  final TextAlign align;
  final bool bold;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Text(
        text,
        textAlign: align,
        style: Theme.of(context).textTheme.bodySmall?.copyWith(
          fontWeight: bold ? FontWeight.w700 : null,
        ),
        overflow: TextOverflow.ellipsis,
      ),
    );
  }
}

// ── Empty / Error ─────────────────────────────────────────────────────────────

class _EmptyBody extends StatelessWidget {
  const _EmptyBody({required this.year});

  final int year;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.bar_chart_outlined, size: 48, color: cs.onSurfaceVariant),
          const SizedBox(height: 12),
          Text(
            'No owner data for $year',
            style: Theme.of(
              context,
            ).textTheme.titleMedium?.copyWith(color: cs.onSurfaceVariant),
          ),
          const SizedBox(height: 4),
          Text(
            'Try selecting a different year.',
            style: TextStyle(color: cs.onSurfaceVariant),
          ),
        ],
      ),
    );
  }
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.error_outline, size: 40, color: cs.error),
            const SizedBox(height: 12),
            Text(
              message,
              textAlign: TextAlign.center,
              style: TextStyle(color: cs.error),
            ),
            const SizedBox(height: 16),
            FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}
