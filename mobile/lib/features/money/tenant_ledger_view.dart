import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../../core/api/api_exception.dart';
import '../../core/time/app_clock.dart';
import '../accounting/accounting_book_models.dart';
import '../deposits/deposits_repository.dart';
import '../units/units_repository.dart';
import 'money_format.dart';
import 'one_time_charge_sheet.dart';
import 'record_payment_sheet.dart';
import 'recurring_charge_sheet.dart';
import 'tenant_credit_sheet.dart';
import 'tenant_ledger_models.dart';
import 'tenant_ledger_repository.dart';

const accountingDetailModeStorageKey = 'rc.accounting.detail-mode.v1';

enum TenantLedgerDetailMode { simple, advanced }

enum TenantLedgerFilter {
  all('All'),
  open('Open'),
  payments('Payments'),
  credits('Credits');

  const TenantLedgerFilter(this.label);

  final String label;
}

typedef TenantLedgerPageKey = ({
  int tenantAccountId,
  int months,
  TenantLedgerFilter filter,
});

typedef TenantLedgerSummaryKey = ({int tenantAccountId, int months});

abstract interface class AccountingDetailModeStore {
  Future<TenantLedgerDetailMode> read();

  Future<void> write(TenantLedgerDetailMode mode);
}

class SecureAccountingDetailModeStore implements AccountingDetailModeStore {
  SecureAccountingDetailModeStore([FlutterSecureStorage? storage])
    : _storage = storage ?? const FlutterSecureStorage();

  final FlutterSecureStorage _storage;

  @override
  Future<TenantLedgerDetailMode> read() async {
    final value = await _storage.read(key: accountingDetailModeStorageKey);
    return value == TenantLedgerDetailMode.advanced.name
        ? TenantLedgerDetailMode.advanced
        : TenantLedgerDetailMode.simple;
  }

  @override
  Future<void> write(TenantLedgerDetailMode mode) =>
      _storage.write(key: accountingDetailModeStorageKey, value: mode.name);
}

final accountingDetailModeStoreProvider = Provider<AccountingDetailModeStore>(
  (ref) => SecureAccountingDetailModeStore(),
);

DateTime _dateOnly(DateTime value) => DateUtils.dateOnly(value.toUtc());

({DateTime from, DateTime to}) tenantLedgerPeriodRange(
  DateTime now,
  int months,
) {
  final today = _dateOnly(now);
  final from = DateTime(today.year, today.month - months + 1, 1);
  final to = DateTime(today.year, today.month + 1, 0);
  return (from: from, to: to);
}

Future<DateTime> _businessDate(Future<DateTime> Function() readDate) async {
  try {
    return await readDate();
  } catch (_) {
    return DateTime.now();
  }
}

final tenantLedgerPageProvider = FutureProvider.autoDispose
    .family<AccountingPage<TenantLedgerRow>, TenantLedgerPageKey>((
      ref,
      key,
    ) async {
      final range = tenantLedgerPeriodRange(
        await _businessDate(() => ref.read(appNowProvider.future)),
        key.months,
      );
      final query = switch (key.filter) {
        TenantLedgerFilter.all => const TenantLedgerQuery(
          take: 200,
          sort: '-effectiveOn',
        ),
        TenantLedgerFilter.open => const TenantLedgerQuery(
          take: 200,
          sort: '-effectiveOn',
          openOnly: true,
        ),
        TenantLedgerFilter.payments => const TenantLedgerQuery(
          take: 200,
          sort: '-effectiveOn',
          entryType: TenantLedgerEntryType.paymentReceipt,
        ),
        TenantLedgerFilter.credits => const TenantLedgerQuery(
          take: 200,
          sort: '-effectiveOn',
          entryType: TenantLedgerEntryType.credit,
        ),
      };
      return ref
          .read(tenantLedgerRepositoryProvider)
          .ledger(
            key.tenantAccountId,
            query: TenantLedgerQuery(
              skip: query.skip,
              take: query.take,
              sort: query.sort,
              entryType: query.entryType,
              effectiveFrom: range.from,
              effectiveTo: range.to,
              openOnly: query.openOnly,
              settledOnly: query.settledOnly,
            ),
          );
    });

final tenantMonthSummaryProvider = FutureProvider.autoDispose
    .family<List<TenantMonthSummary>, TenantLedgerSummaryKey>((ref, key) async {
      final range = tenantLedgerPeriodRange(
        await _businessDate(() => ref.read(appNowProvider.future)),
        key.months,
      );
      return ref
          .read(tenantLedgerRepositoryProvider)
          .monthSummary(
            key.tenantAccountId,
            query: TenantMonthSummaryQuery(from: range.from, to: range.to),
          );
    });

final tenantLedgerSummaryProvider = FutureProvider.autoDispose
    .family<TenantLedgerPeriodSummary, TenantLedgerSummaryKey>(
      (ref, key) => ref
          .read(tenantLedgerRepositoryProvider)
          .ledgerSummary(
            key.tenantAccountId,
            query: TenantLedgerPeriodSummaryQuery(months: key.months),
          ),
    );

final tenantRecurringChargesProvider = FutureProvider.autoDispose
    .family<AccountingPage<RecurringTenantChargeRow>, int>(
      (ref, tenantAccountId) => ref
          .read(tenantLedgerRepositoryProvider)
          .recurringCharges(
            tenantAccountId,
            query: const RecurringTenantChargeQuery(take: 100),
          ),
    );

final tenantDepositProvider = FutureProvider.autoDispose
    .family<TenantAccountDeposit?, int>((ref, tenantAccountId) async {
      try {
        return await ref
            .read(depositsRepositoryProvider)
            .getDeposit(tenantAccountId);
      } on ApiException catch (error) {
        if (error.statusCode == 403 || error.statusCode == 404) return null;
        rethrow;
      }
    });

class TenantLedgerView extends ConsumerStatefulWidget {
  const TenantLedgerView({
    super.key,
    required this.dashboard,
    this.embedded = false,
    this.onScan,
    this.onSaved,
    this.onDepositTap,
  });

  final UnitDashboard dashboard;
  final bool embedded;
  final VoidCallback? onScan;
  final VoidCallback? onSaved;
  final VoidCallback? onDepositTap;

  @override
  ConsumerState<TenantLedgerView> createState() => _TenantLedgerViewState();
}

class _TenantLedgerViewState extends ConsumerState<TenantLedgerView> {
  static const _periods = <int>[3, 6, 9, 12];

  int _months = 12;
  TenantLedgerFilter _filter = TenantLedgerFilter.all;
  TenantLedgerDetailMode _detailMode = TenantLedgerDetailMode.simple;

  int? get _tenantAccountId => widget.dashboard.tenantAccountId;

  @override
  void initState() {
    super.initState();
    Future.microtask(_loadDetailMode);
  }

  Future<void> _loadDetailMode() async {
    try {
      final mode = await ref.read(accountingDetailModeStoreProvider).read();
      if (mounted) setState(() => _detailMode = mode);
    } catch (_) {
      // Simple mode is the safe default when secure storage is unavailable.
    }
  }

  Future<void> _setDetailMode(bool advanced) async {
    final mode = advanced
        ? TenantLedgerDetailMode.advanced
        : TenantLedgerDetailMode.simple;
    setState(() => _detailMode = mode);
    try {
      await ref.read(accountingDetailModeStoreProvider).write(mode);
    } catch (_) {
      // Keep the current session usable even when preference persistence fails.
    }
  }

  void _invalidateMoney() {
    ref.invalidate(tenantLedgerPageProvider);
    ref.invalidate(tenantMonthSummaryProvider);
    ref.invalidate(tenantLedgerSummaryProvider);
    ref.invalidate(tenantRecurringChargesProvider);
    ref.invalidate(tenantDepositProvider);
    widget.onSaved?.call();
  }

  Future<void> _recordPayment() async {
    final accountId = _tenantAccountId;
    if (accountId == null) return;
    final result = await showRecordPaymentSheet(
      context,
      ref,
      tenantAccountId: accountId,
      tenantName: widget.dashboard.header.currentTenantName,
      rentalLabel: _rentalLabel,
    );
    if (result != null) _invalidateMoney();
  }

  Future<void> _addCharge() async {
    final accountId = _tenantAccountId;
    if (accountId == null) return;
    final result = await showOneTimeChargeSheet(
      context,
      ref,
      tenantAccountId: accountId,
    );
    if (result != null) _invalidateMoney();
  }

  Future<void> _giveCredit([TenantLedgerRow? target]) async {
    final accountId = _tenantAccountId;
    if (accountId == null) return;
    final result = await showTenantCreditSheet(
      context,
      ref,
      tenantAccountId: accountId,
      initialTarget: target,
    );
    if (result != null) _invalidateMoney();
  }

  Future<void> _openRecurring() async {
    final accountId = _tenantAccountId;
    if (accountId == null) return;
    final result = await showRecurringChargeSheet(
      context,
      ref,
      tenantAccountId: accountId,
      leaseAgreementId: widget.dashboard.currentLease?.id,
      propertyId: widget.dashboard.unit.propertyId,
      unitId: widget.dashboard.unit.id,
    );
    if (result != null) _invalidateMoney();
  }

  Future<void> _showRowActions(TenantLedgerRow row) async {
    final action = await showModalBottomSheet<_TenantLedgerRowAction>(
      context: context,
      showDragHandle: true,
      useSafeArea: true,
      builder: (sheetContext) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (tenantLedgerRowCanReceiveCredit(row))
              ListTile(
                leading: const Icon(Icons.redeem_outlined),
                title: const Text('Give credit'),
                onTap: () => Navigator.of(
                  sheetContext,
                ).pop(_TenantLedgerRowAction.giveCredit),
              ),
            if (tenantLedgerRowCanReverse(row))
              ListTile(
                leading: const Icon(Icons.undo_outlined),
                title: const Text('Reverse charge'),
                onTap: () => Navigator.of(
                  sheetContext,
                ).pop(_TenantLedgerRowAction.reverse),
              ),
          ],
        ),
      ),
    );
    if (action != null) await _runRowAction(row, action);
  }

  Future<void> _runRowAction(
    TenantLedgerRow row,
    _TenantLedgerRowAction action,
  ) async {
    switch (action) {
      case _TenantLedgerRowAction.giveCredit:
        await _giveCredit(row);
      case _TenantLedgerRowAction.reverse:
        await _reverseCharge(row);
    }
  }

  Future<void> _reverseCharge(TenantLedgerRow row) async {
    final reason = await showModalBottomSheet<String>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      useSafeArea: true,
      builder: (_) => const _ReverseChargeSheet(),
    );
    if (reason == null || reason.trim().isEmpty || !mounted) return;

    try {
      final effectiveOn = DateUtils.dateOnly(
        await _businessDate(() => ref.read(appNowProvider.future)),
      );
      final result = await ref
          .read(tenantLedgerRepositoryProvider)
          .reverseCharge(
            _tenantAccountId!,
            row.tenantLedgerEntryId,
            ReverseTenantChargeInput(
              effectiveOn: effectiveOn,
              reason: reason.trim(),
            ),
            operationKey:
                'mobile-reverse-charge-${row.tenantLedgerEntryId}-${DateTime.now().microsecondsSinceEpoch}',
          );
      if (!mounted) return;
      if (result.error?.trim().isNotEmpty == true || !result.applied) {
        _showError(result.error ?? 'The charge was not reversed.');
        return;
      }
      _showMessage('Charge reversed.');
      _invalidateMoney();
    } on ApiException catch (error) {
      if (mounted) _showError(error.message);
    }
  }

  void _showError(String message) {
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(message)));
  }

  void _showMessage(String message) {
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(message)));
  }

  String get _rentalLabel {
    final property = widget.dashboard.propertyName.trim();
    final unit = widget.dashboard.unit.unitNumber.trim();
    if (property.isEmpty && unit.isEmpty) return '';
    if (unit.isEmpty) return property;
    return property.isEmpty ? 'Unit $unit' : '$property · Unit $unit';
  }

  @override
  Widget build(BuildContext context) {
    final accountId = _tenantAccountId;
    if (accountId == null) {
      return const _TenantLedgerNoAccount();
    }

    final key = (tenantAccountId: accountId, months: _months, filter: _filter);
    final summaryKey = (tenantAccountId: accountId, months: _months);
    final ledgerAsync = ref.watch(tenantLedgerPageProvider(key));
    final monthAsync = ref.watch(tenantMonthSummaryProvider(summaryKey));
    final summaryAsync = ref.watch(tenantLedgerSummaryProvider(summaryKey));
    final depositAsync = ref.watch(tenantDepositProvider(accountId));

    final children = <Widget>[
      _TenantLedgerHeader(
        tenantName: widget.dashboard.header.currentTenantName,
        rentalLabel: _rentalLabel,
        detailMode: _detailMode,
        onAdvancedChanged: _setDetailMode,
      ),
      const SizedBox(height: 12),
      _TenantLedgerSummaryStrip(
        summary: summaryAsync.value,
        fallbackBalance:
            widget.dashboard.tenantAccountCondition.receivableBalance,
        pastDue: widget.dashboard.tenantAccountCondition.pastDueAmount,
        rows: ledgerAsync.value?.items ?? const [],
        deposit: depositAsync.value,
        onDepositTap:
            widget.onDepositTap ??
            () => _showMessage(
              'Open Security Deposits to view the full deposit account.',
            ),
      ),
      const SizedBox(height: 16),
      _TenantLedgerActions(
        onRecordPayment: _recordPayment,
        onAddCharge: _addCharge,
        onGiveCredit: () => _giveCredit(),
        onRecurring: _openRecurring,
        onScan: widget.onScan,
      ),
      const SizedBox(height: 16),
      SegmentedButton<int>(
        key: const Key('tenant-ledger-periods'),
        segments: [
          for (final period in _periods)
            ButtonSegment<int>(value: period, label: Text('$period')),
        ],
        selected: {_months},
        onSelectionChanged: (selection) =>
            setState(() => _months = selection.first),
      ),
      const SizedBox(height: 12),
      SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: Row(
          children: [
            for (final filter in TenantLedgerFilter.values) ...[
              FilterChip(
                label: Text(filter.label),
                selected: filter == _filter,
                onSelected: (_) => setState(() => _filter = filter),
              ),
              const SizedBox(width: 8),
            ],
          ],
        ),
      ),
      const SizedBox(height: 16),
      _TenantLedgerContent(
        ledgerAsync: ledgerAsync,
        monthAsync: monthAsync,
        summaryAsync: summaryAsync,
        detailMode: _detailMode,
        onRetry: () {
          ref.invalidate(tenantLedgerPageProvider(key));
          ref.invalidate(tenantMonthSummaryProvider(summaryKey));
          ref.invalidate(tenantLedgerSummaryProvider(summaryKey));
        },
        onRowActions: _showRowActions,
        onRowAction: _runRowAction,
      ),
    ];

    if (widget.embedded) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: children,
      );
    }

    return ListView(
      key: const PageStorageKey('tenant-ledger-view'),
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      children: children,
    );
  }
}

enum _TenantLedgerRowAction { giveCredit, reverse }

bool tenantLedgerRowCanReceiveCredit(TenantLedgerRow row) =>
    row.type != TenantLedgerEntryType.depositCharge &&
    row.type != TenantLedgerEntryType.paymentReceipt &&
    row.type != TenantLedgerEntryType.reversal &&
    row.chargeAmount > 0;

bool tenantLedgerRowCanReverse(TenantLedgerRow row) =>
    row.type != TenantLedgerEntryType.depositCharge &&
    row.type != TenantLedgerEntryType.reversal &&
    row.reversesEntryId == null &&
    row.replacedByEntryId == null &&
    row.chargeAmount > 0;

class _TenantLedgerNoAccount extends StatelessWidget {
  const _TenantLedgerNoAccount();

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(20),
      child: Text(
        'No tenant account exists for this rental.',
        style: Theme.of(context).textTheme.bodyLarge,
      ),
    ),
  );
}

class _TenantLedgerHeader extends StatelessWidget {
  const _TenantLedgerHeader({
    required this.tenantName,
    required this.rentalLabel,
    required this.detailMode,
    required this.onAdvancedChanged,
  });

  final String? tenantName;
  final String rentalLabel;
  final TenantLedgerDetailMode detailMode;
  final ValueChanged<bool> onAdvancedChanged;

  @override
  Widget build(BuildContext context) {
    final name = tenantName?.trim();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text('Tenant ledger', style: Theme.of(context).textTheme.titleLarge),
        if (name?.isNotEmpty == true || rentalLabel.isNotEmpty) ...[
          const SizedBox(height: 4),
          Text(
            [
              if (name?.isNotEmpty == true) name!,
              rentalLabel,
            ].where((value) => value.isNotEmpty).join(' · '),
            style: Theme.of(context).textTheme.bodyMedium,
          ),
        ],
        const SizedBox(height: 8),
        Align(
          alignment: Alignment.centerRight,
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Text('Advanced detail'),
              Switch.adaptive(
                key: const Key('tenant-ledger-detail-mode'),
                value: detailMode == TenantLedgerDetailMode.advanced,
                onChanged: onAdvancedChanged,
              ),
            ],
          ),
        ),
      ],
    );
  }
}

class _TenantLedgerActions extends StatelessWidget {
  const _TenantLedgerActions({
    required this.onRecordPayment,
    required this.onAddCharge,
    required this.onGiveCredit,
    required this.onRecurring,
    required this.onScan,
  });

  final VoidCallback onRecordPayment;
  final VoidCallback onAddCharge;
  final VoidCallback onGiveCredit;
  final VoidCallback onRecurring;
  final VoidCallback? onScan;

  @override
  Widget build(BuildContext context) => Wrap(
    spacing: 8,
    runSpacing: 8,
    children: [
      FilledButton.icon(
        key: const Key('tenant-ledger-record-payment'),
        onPressed: onRecordPayment,
        icon: const Icon(Icons.add_card_outlined),
        label: const Text('Record payment'),
      ),
      OutlinedButton.icon(
        key: const Key('tenant-ledger-add-charge'),
        onPressed: onAddCharge,
        icon: const Icon(Icons.request_quote_outlined),
        label: const Text('Add charge'),
      ),
      PopupMenuButton<_TenantLedgerOverflowAction>(
        key: const Key('tenant-ledger-overflow'),
        tooltip: 'More ledger actions',
        onSelected: (action) {
          switch (action) {
            case _TenantLedgerOverflowAction.giveCredit:
              onGiveCredit();
            case _TenantLedgerOverflowAction.recurringCharge:
              onRecurring();
            case _TenantLedgerOverflowAction.scanPayment:
              onScan?.call();
          }
        },
        itemBuilder: (_) => [
          const PopupMenuItem(
            value: _TenantLedgerOverflowAction.giveCredit,
            child: Text('Give credit'),
          ),
          const PopupMenuItem(
            value: _TenantLedgerOverflowAction.recurringCharge,
            child: Text('Recurring charge'),
          ),
          if (onScan != null)
            const PopupMenuItem(
              value: _TenantLedgerOverflowAction.scanPayment,
              child: Text('Scan payment'),
            ),
        ],
        child: const Padding(
          padding: EdgeInsets.symmetric(horizontal: 10, vertical: 12),
          child: Icon(Icons.more_horiz),
        ),
      ),
    ],
  );
}

enum _TenantLedgerOverflowAction { giveCredit, recurringCharge, scanPayment }

class _TenantLedgerSummaryStrip extends StatelessWidget {
  const _TenantLedgerSummaryStrip({
    required this.summary,
    required this.fallbackBalance,
    required this.pastDue,
    required this.rows,
    required this.deposit,
    required this.onDepositTap,
  });

  final TenantLedgerPeriodSummary? summary;
  final double fallbackBalance;
  final double pastDue;
  final List<TenantLedgerRow> rows;
  final TenantAccountDeposit? deposit;
  final VoidCallback onDepositTap;

  TenantLedgerRow? get _nextRow {
    for (final row in rows) {
      if (row.dueOn != null) return row;
    }
    return null;
  }

  String _money(double? value) => value == null ? '—' : moneyFmt(value);

  @override
  Widget build(BuildContext context) {
    final next = _nextRow;
    return SizedBox(
      height: 88,
      child: ListView(
        scrollDirection: Axis.horizontal,
        children: [
          _SummaryChip(
            label: 'Balance due',
            value: _money(summary?.endingBalance ?? fallbackBalance),
          ),
          _SummaryChip(label: 'Past due', value: _money(pastDue)),
          _SummaryChip(
            label: 'Next',
            value: next?.dueOn == null
                ? '—'
                : '${dateFmt(next!.dueOn!)} · ${_money(next.openAmount)}',
          ),
          _SummaryChip(label: 'Credit', value: _money(summary?.creditAmount)),
          _SummaryChip(
            label: 'Deposit',
            value: deposit == null ? '—' : moneyFmt(deposit!.heldBalance),
            onTap: deposit == null ? null : onDepositTap,
            trailing: deposit == null ? null : const Icon(Icons.chevron_right),
          ),
        ],
      ),
    );
  }
}

class _SummaryChip extends StatelessWidget {
  const _SummaryChip({
    required this.label,
    required this.value,
    this.onTap,
    this.trailing,
  });

  final String label;
  final String value;
  final VoidCallback? onTap;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) => Card(
    margin: const EdgeInsets.only(right: 8),
    child: InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(12),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(12, 8, 8, 8),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Text(label, style: Theme.of(context).textTheme.labelMedium),
                const SizedBox(height: 2),
                Text(
                  value,
                  style: Theme.of(
                    context,
                  ).textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w700),
                ),
              ],
            ),
            if (trailing != null) ...[const SizedBox(width: 2), trailing!],
          ],
        ),
      ),
    ),
  );
}

class _TenantLedgerContent extends StatelessWidget {
  const _TenantLedgerContent({
    required this.ledgerAsync,
    required this.monthAsync,
    required this.summaryAsync,
    required this.detailMode,
    required this.onRetry,
    required this.onRowActions,
    required this.onRowAction,
  });

  final AsyncValue<AccountingPage<TenantLedgerRow>> ledgerAsync;
  final AsyncValue<List<TenantMonthSummary>> monthAsync;
  final AsyncValue<TenantLedgerPeriodSummary> summaryAsync;
  final TenantLedgerDetailMode detailMode;
  final VoidCallback onRetry;
  final ValueChanged<TenantLedgerRow> onRowActions;
  final Future<void> Function(TenantLedgerRow, _TenantLedgerRowAction)
  onRowAction;

  @override
  Widget build(BuildContext context) {
    final error = ledgerAsync.error ?? monthAsync.error ?? summaryAsync.error;
    if (error != null) {
      return _TenantLedgerError(error: error, onRetry: onRetry);
    }
    if (!ledgerAsync.hasValue ||
        !monthAsync.hasValue ||
        !summaryAsync.hasValue) {
      return const _TenantLedgerSkeleton();
    }

    final rows = ledgerAsync.value!.items;
    final summaries = monthAsync.value!;
    if (rows.isEmpty) {
      return const Card(
        child: Padding(
          padding: EdgeInsets.all(20),
          child: Text(
            'No charges or payments yet. Record the first payment or charge above.',
          ),
        ),
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        for (final summary in summaries)
          _TenantLedgerMonthCard(
            summary: summary,
            rows: rows
                .where(
                  (row) =>
                      row.effectiveOn.year == summary.year &&
                      row.effectiveOn.month == summary.month,
                )
                .toList(growable: false),
            detailMode: detailMode,
            onRowActions: onRowActions,
            onRowAction: onRowAction,
          ),
        if (summaries.isEmpty)
          _TenantLedgerMonthCard(
            summary: null,
            rows: rows,
            detailMode: detailMode,
            onRowActions: onRowActions,
            onRowAction: onRowAction,
          ),
      ],
    );
  }
}

class _TenantLedgerMonthCard extends StatelessWidget {
  const _TenantLedgerMonthCard({
    required this.summary,
    required this.rows,
    required this.detailMode,
    required this.onRowActions,
    required this.onRowAction,
  });

  final TenantMonthSummary? summary;
  final List<TenantLedgerRow> rows;
  final TenantLedgerDetailMode detailMode;
  final ValueChanged<TenantLedgerRow> onRowActions;
  final Future<void> Function(TenantLedgerRow, _TenantLedgerRowAction)
  onRowAction;

  @override
  Widget build(BuildContext context) {
    final title = summary == null
        ? 'Ledger entries'
        : '${_monthName(summary!.month)} ${summary!.year}';
    return Card(
      margin: const EdgeInsets.only(bottom: 12),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(12, 12, 12, 4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(title, style: Theme.of(context).textTheme.titleMedium),
            if (summary != null) ...[
              const SizedBox(height: 10),
              Wrap(
                spacing: 12,
                runSpacing: 4,
                children: [
                  _MonthValue(
                    label: 'Opening',
                    value: moneyFmt(summary!.openingBalance),
                  ),
                  _MonthValue(
                    label: 'Charges',
                    value: moneyFmt(summary!.chargeAmount),
                  ),
                  _MonthValue(
                    label: 'Payments',
                    value: moneyFmt(summary!.paymentAmount),
                  ),
                  _MonthValue(
                    label: 'Closing',
                    value: moneyFmt(summary!.closingBalance),
                  ),
                ],
              ),
            ],
            if (rows.isEmpty && summary != null)
              const Padding(
                padding: EdgeInsets.symmetric(vertical: 12),
                child: Text('No entries in this period.'),
              ),
            for (final row in rows)
              _TenantLedgerRowCard(
                row: row,
                detailMode: detailMode,
                onActions: () => onRowActions(row),
                onSelectedAction: (action) => onRowAction(row, action),
              ),
          ],
        ),
      ),
    );
  }
}

class _MonthValue extends StatelessWidget {
  const _MonthValue({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text(label, style: Theme.of(context).textTheme.labelSmall),
      Text(value, style: Theme.of(context).textTheme.bodyMedium),
    ],
  );
}

class _TenantLedgerRowCard extends StatelessWidget {
  const _TenantLedgerRowCard({
    required this.row,
    required this.detailMode,
    required this.onActions,
    required this.onSelectedAction,
  });

  final TenantLedgerRow row;
  final TenantLedgerDetailMode detailMode;
  final VoidCallback onActions;
  final ValueChanged<_TenantLedgerRowAction> onSelectedAction;

  String get _title => row.description.trim().isEmpty
      ? _entryTypeLabel(row.type)
      : row.description.trim();

  ({String label, double? amount}) get _amount {
    if (row.chargeAmount != 0) {
      return (label: 'Charge', amount: row.chargeAmount);
    }
    if (row.paymentAmount != 0) {
      return (label: 'Payment', amount: row.paymentAmount);
    }
    if (row.creditAmount != 0) {
      return (label: 'Credit', amount: row.creditAmount);
    }
    return (label: 'Amount', amount: null);
  }

  @override
  Widget build(BuildContext context) {
    final amount = _amount;
    final canShowActions =
        tenantLedgerRowCanReceiveCredit(row) || tenantLedgerRowCanReverse(row);
    final detailLines = <String>[
      if (row.paymentMethod?.trim().isNotEmpty == true) row.paymentMethod!,
      if (row.reference?.trim().isNotEmpty == true) 'Ref ${row.reference}',
      if (row.categoryName?.trim().isNotEmpty == true) row.categoryName!,
      if (row.recurringScheduleContext?.trim().isNotEmpty == true)
        row.recurringScheduleContext!,
      if (row.servicePeriodStartOn != null && row.servicePeriodEndOn != null)
        'Service ${dateFmt(row.servicePeriodStartOn!)} – ${dateFmt(row.servicePeriodEndOn!)}',
    ];
    if (detailMode == TenantLedgerDetailMode.advanced) {
      if (row.accountLabel?.trim().isNotEmpty == true) {
        detailLines.add('Account ${row.accountLabel}');
      }
      if (row.journalEntryPublicId?.trim().isNotEmpty == true) {
        detailLines.add('Journal ${row.journalEntryPublicId}');
      }
      if (row.sourceDocumentContext?.trim().isNotEmpty == true) {
        detailLines.add(row.sourceDocumentContext!);
      }
    }

    return Card(
      margin: const EdgeInsets.only(top: 8),
      child: InkWell(
        onLongPress: canShowActions ? onActions : null,
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 10),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Padding(
                padding: EdgeInsets.only(top: 3),
                child: Icon(Icons.receipt_long_outlined, size: 20),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      _title,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: Theme.of(context).textTheme.bodyLarge?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                    const SizedBox(height: 3),
                    Text(
                      '${dateFmt(row.effectiveOn)} · ${amount.label} · ${row.status.isEmpty ? 'Posted' : row.status}',
                      style: Theme.of(context).textTheme.bodySmall,
                    ),
                    if (row.dueOn != null)
                      Text(
                        'Due ${dateFmt(row.dueOn!)}',
                        style: Theme.of(context).textTheme.bodySmall,
                      ),
                    if (detailLines.isNotEmpty)
                      Text(
                        detailLines.join(' · '),
                        maxLines: 3,
                        overflow: TextOverflow.ellipsis,
                        style: Theme.of(context).textTheme.bodySmall,
                      ),
                  ],
                ),
              ),
              const SizedBox(width: 8),
              Column(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  Text(
                    amount.amount == null ? '—' : moneyFmt(amount.amount!),
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  const SizedBox(height: 3),
                  Text(
                    'Bal ${moneyFmt(row.runningAmountOwed)}',
                    style: Theme.of(context).textTheme.labelSmall,
                  ),
                  if (canShowActions)
                    PopupMenuButton<_TenantLedgerRowAction>(
                      padding: EdgeInsets.zero,
                      onSelected: onSelectedAction,
                      itemBuilder: (_) => [
                        if (tenantLedgerRowCanReceiveCredit(row))
                          const PopupMenuItem(
                            value: _TenantLedgerRowAction.giveCredit,
                            child: Text('Give credit'),
                          ),
                        if (tenantLedgerRowCanReverse(row))
                          const PopupMenuItem(
                            value: _TenantLedgerRowAction.reverse,
                            child: Text('Reverse charge'),
                          ),
                      ],
                      child: const Icon(Icons.more_vert, size: 20),
                    ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _TenantLedgerSkeleton extends StatelessWidget {
  const _TenantLedgerSkeleton();

  @override
  Widget build(BuildContext context) => Column(
    children: [
      for (var index = 0; index < 3; index++)
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                _SkeletonBar(widthFactor: index == 0 ? .42 : .3),
                const SizedBox(height: 12),
                _SkeletonBar(widthFactor: .88),
                const SizedBox(height: 8),
                _SkeletonBar(widthFactor: .65),
              ],
            ),
          ),
        ),
    ],
  );
}

class _SkeletonBar extends StatelessWidget {
  const _SkeletonBar({required this.widthFactor});

  final double widthFactor;

  @override
  Widget build(BuildContext context) => FractionallySizedBox(
    alignment: Alignment.centerLeft,
    widthFactor: widthFactor,
    child: Container(
      height: 14,
      decoration: BoxDecoration(
        color: Theme.of(context).colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(8),
      ),
    ),
  );
}

class _TenantLedgerError extends StatelessWidget {
  const _TenantLedgerError({required this.error, required this.onRetry});

  final Object error;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final unavailable =
        error is ApiException &&
        {401, 403, 404}.contains((error as ApiException).statusCode);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              unavailable
                  ? 'Money view unavailable'
                  : 'Unable to load tenant ledger',
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 6),
            Text(
              unavailable
                  ? 'This money view is not available for this rental.'
                  : error is ApiException
                  ? (error as ApiException).message
                  : 'Try again when the connection is available.',
            ),
            const SizedBox(height: 12),
            Align(
              alignment: Alignment.centerLeft,
              child: OutlinedButton(
                onPressed: onRetry,
                child: const Text('Retry'),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _ReverseChargeSheet extends StatefulWidget {
  const _ReverseChargeSheet();

  @override
  State<_ReverseChargeSheet> createState() => _ReverseChargeSheetState();
}

class _ReverseChargeSheetState extends State<_ReverseChargeSheet> {
  final _controller = TextEditingController();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Padding(
    padding: EdgeInsets.fromLTRB(
      20,
      8,
      20,
      24 + MediaQuery.viewInsetsOf(context).bottom,
    ),
    child: Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text('Reverse charge', style: Theme.of(context).textTheme.titleLarge),
        const SizedBox(height: 8),
        const Text('Posted history stays unchanged; a reversal is added.'),
        const SizedBox(height: 16),
        TextField(
          controller: _controller,
          autofocus: true,
          maxLines: 3,
          decoration: const InputDecoration(
            labelText: 'Reason',
            hintText: 'Explain why this charge is being reversed',
          ),
        ),
        const SizedBox(height: 16),
        FilledButton(
          onPressed: () {
            if (_controller.text.trim().isEmpty) return;
            Navigator.of(context).pop(_controller.text.trim());
          },
          child: const Text('Reverse charge'),
        ),
      ],
    ),
  );
}

String _monthName(int month) =>
    const [
      '',
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
    ][month < 0
        ? 0
        : month > 12
        ? 12
        : month];

String _entryTypeLabel(TenantLedgerEntryType type) => switch (type) {
  TenantLedgerEntryType.openingBalance => 'Opening balance',
  TenantLedgerEntryType.rentCharge => 'Rent charge',
  TenantLedgerEntryType.addendumCharge => 'Additional charge',
  TenantLedgerEntryType.lateFeeCharge => 'Late fee',
  TenantLedgerEntryType.depositCharge => 'Deposit charge',
  TenantLedgerEntryType.manualCharge => 'Charge',
  TenantLedgerEntryType.paymentReceipt => 'Payment',
  TenantLedgerEntryType.credit => 'Credit',
  TenantLedgerEntryType.adjustment => 'Adjustment',
  TenantLedgerEntryType.refund => 'Refund',
  TenantLedgerEntryType.transferIn => 'Transfer in',
  TenantLedgerEntryType.transferOut => 'Transfer out',
  TenantLedgerEntryType.reversal => 'Reversal',
  TenantLedgerEntryType.unknown => 'Ledger entry',
};
