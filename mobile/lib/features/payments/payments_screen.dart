import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/models.dart';
import '../../core/api/api_exception.dart';
import '../../core/widgets/mobile_grid_controls.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'payment_detail_screen.dart';
import 'payment_lease_labels.dart';
import 'payments_repository.dart';

Future<void> showCreatePaymentSheet(
  BuildContext context,
  WidgetRef ref, {
  VoidCallback? onSaved,
}) async {
  ref.read(leasesForPaymentProvider.notifier).load();
  await showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) => _CreatePaymentSheet(
      onSaved: () {
        ref.read(paymentsProvider.notifier).refresh();
        ref.read(accountingSummaryProvider.notifier).refresh();
        onSaved?.call();
      },
    ),
  );
}

// ── Helpers ───────────────────────────────────────────────────────────────────

String _fmtCurrency(double amount) {
  final isNegative = amount < 0;
  final abs = amount.abs();
  final parts = abs.toStringAsFixed(2).split('.');
  final intPart = parts[0];
  final decPart = parts[1];
  final buf = StringBuffer();
  final len = intPart.length;
  for (var i = 0; i < len; i++) {
    if (i > 0 && (len - i) % 3 == 0) buf.write(',');
    buf.write(intPart[i]);
  }
  return '\$${isNegative ? '-' : ''}$buf.$decPart';
}

String _fmtDate(DateTime d) {
  const months = [
    '',
    'Jan',
    'Feb',
    'Mar',
    'Apr',
    'May',
    'Jun',
    'Jul',
    'Aug',
    'Sep',
    'Oct',
    'Nov',
    'Dec',
  ];
  return '${months[d.month]} ${d.day}, ${d.year}';
}

// ── Entry widget ──────────────────────────────────────────────────────────────

/// Top-level payments screen.
///
/// Shows accounting summary cards (Collected / Outstanding / Overdue) and
/// a scrollable list of payments. Each non-Paid row has a "Mark paid" action.
/// A FAB opens the create-payment sheet.
class PaymentsScreen extends ConsumerStatefulWidget {
  const PaymentsScreen({super.key});

  @override
  ConsumerState<PaymentsScreen> createState() => _PaymentsScreenState();
}

class _PaymentsScreenState extends ConsumerState<PaymentsScreen> {
  static const _pageSize = 20;

  final _searchCtrl = TextEditingController();
  String? _search;
  String _sort = '-createdAt';
  String _period = MobileGridPeriod.all;
  int _skip = 0;

  PaymentListQuery get _query {
    final dateRange = mobileGridDateRangeForPeriod(_period);
    return PaymentListQuery(
      skip: _skip,
      take: _pageSize,
      search: _search,
      sort: _sort,
      dueFrom: dateRange.from,
      dueTo: dateRange.to,
    );
  }

  @override
  void initState() {
    super.initState();
    Future.microtask(() {
      ref.read(accountingSummaryProvider.notifier).load();
      ref.read(paymentsProvider.notifier).load();
    });
  }

  @override
  void dispose() {
    _searchCtrl.dispose();
    super.dispose();
  }

  Future<void> _refresh() async {
    await Future.wait([
      ref.read(accountingSummaryProvider.notifier).refresh(),
      ref.read(paymentsProvider.notifier).refresh(),
      ref.refresh(paymentsPageProvider(_query).future),
    ]);
  }

  void _submitSearch([String? value]) {
    final next = (value ?? _searchCtrl.text).trim();
    setState(() {
      _search = next.isEmpty ? null : next;
      _skip = 0;
    });
  }

  void _clearSearch() {
    _searchCtrl.clear();
    _submitSearch('');
  }

  void _setSort(String? sort) {
    if (sort == null || sort == _sort) return;
    setState(() {
      _sort = sort;
      _skip = 0;
    });
  }

  void _setPeriod(String? period) {
    if (period == null || period == _period) return;
    setState(() {
      _period = period;
      _skip = 0;
    });
  }

  /// Opens the full payment detail screen (I9: the standalone list was a
  /// dead-end — every other payment row in the app drills in). Refreshes the
  /// list + summary on return so a mark-paid / edit made on the detail screen is
  /// reflected here.
  Future<void> _openDetail(BuildContext context, int paymentId) async {
    await Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => PaymentDetailScreen(paymentId: paymentId),
      ),
    );
    if (!mounted) return;
    await Future.wait([
      ref.read(paymentsProvider.notifier).refresh(),
      ref.read(accountingSummaryProvider.notifier).refresh(),
    ]);
  }

  void _showCreateSheet(BuildContext context) {
    showCreatePaymentSheet(
      context,
      ref,
      onSaved: () => ref.invalidate(paymentsPageProvider),
    );
  }

  @override
  Widget build(BuildContext context) {
    final summaryAsync = ref.watch(accountingSummaryProvider);
    final paymentsAsync = ref.watch(paymentsPageProvider(_query));
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(title: const Text('Payments')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'payments-fab',
        primaryAction: MobileQuickAction(
          label: 'Record payment',
          icon: Icons.add,
          onPressed: () => _showCreateSheet(context),
        ),
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: CustomScrollView(
          physics: const AlwaysScrollableScrollPhysics(),
          slivers: [
            // ── Summary cards ──────────────────────────────────────────────
            SliverToBoxAdapter(
              child: summaryAsync.when(
                loading: () => const Padding(
                  padding: EdgeInsets.all(20),
                  child: Center(child: CircularProgressIndicator()),
                ),
                error: (e, _) => Padding(
                  padding: const EdgeInsets.fromLTRB(16, 16, 16, 0),
                  child: _ErrorBanner(
                    message: e is ApiException ? e.message : e.toString(),
                    onRetry: () =>
                        ref.read(accountingSummaryProvider.notifier).refresh(),
                  ),
                ),
                data: (summary) => _SummaryCards(
                  summary: summary,
                  colorScheme: colorScheme,
                  theme: theme,
                ),
              ),
            ),

            // ── Payments list ──────────────────────────────────────────────
            paymentsAsync.when(
              loading: () => const SliverFillRemaining(
                child: Center(child: CircularProgressIndicator()),
              ),
              error: (e, _) => SliverFillRemaining(
                child: _ErrorBody(
                  message: e is ApiException ? e.message : e.toString(),
                  onRetry: () => ref.read(paymentsProvider.notifier).refresh(),
                ),
              ),
              data: (page) {
                if (page.items.isEmpty) {
                  return SliverFillRemaining(
                    child: ListView(
                      physics: const AlwaysScrollableScrollPhysics(),
                      padding: const EdgeInsets.fromLTRB(16, 8, 16, 88),
                      children: [
                        _PaymentsGridControls(
                          searchController: _searchCtrl,
                          sort: _sort,
                          period: _period,
                          onSearch: _submitSearch,
                          onClearSearch: _clearSearch,
                          onSortChanged: _setSort,
                          onPeriodChanged: _setPeriod,
                        ),
                        _EmptyBody(hasSearch: (_search ?? '').isNotEmpty),
                      ],
                    ),
                  );
                }
                return SliverPadding(
                  padding: const EdgeInsets.fromLTRB(16, 8, 16, 88),
                  sliver: SliverList.separated(
                    itemCount: page.items.length + 2,
                    separatorBuilder: (context, index) {
                      if (index == 0) return const SizedBox(height: 12);
                      if (index == page.items.length) {
                        return const SizedBox(height: 14);
                      }
                      return const MobileM3ListDivider();
                    },
                    itemBuilder: (ctx, i) {
                      if (i == 0) {
                        return _PaymentsGridControls(
                          searchController: _searchCtrl,
                          sort: _sort,
                          period: _period,
                          onSearch: _submitSearch,
                          onClearSearch: _clearSearch,
                          onSortChanged: _setSort,
                          onPeriodChanged: _setPeriod,
                        );
                      }

                      if (i == page.items.length + 1) {
                        return MobileGridPagingBar(
                          totalCount: page.totalCount,
                          skip: page.skip,
                          itemCount: page.items.length,
                          previousTooltip: 'Previous payments page',
                          nextTooltip: 'Next payments page',
                          onPrevious: page.hasPrevious
                              ? () => setState(() {
                                  _skip = (_skip - _pageSize).clamp(0, _skip);
                                })
                              : null,
                          onNext: page.hasNext
                              ? () => setState(() => _skip += _pageSize)
                              : null,
                        );
                      }

                      final payment = page.items[i - 1];
                      return _PaymentCard(
                        payment: payment,
                        position: MobileM3ListItemPositionForIndex.forIndex(
                          i - 1,
                          page.items.length,
                        ),
                        colorScheme: colorScheme,
                        theme: theme,
                        onMarkPaid: () async {
                          await ref
                              .read(paymentsProvider.notifier)
                              .markPaid(payment.id);
                          ref.invalidate(paymentsPageProvider);
                          ref
                              .read(accountingSummaryProvider.notifier)
                              .refresh();
                        },
                        onTap: () => _openDetail(ctx, payment.id),
                      );
                    },
                  ),
                );
              },
            ),
          ],
        ),
      ),
    );
  }
}

class _PaymentsGridControls extends StatelessWidget {
  const _PaymentsGridControls({
    required this.searchController,
    required this.sort,
    required this.period,
    required this.onSearch,
    required this.onClearSearch,
    required this.onSortChanged,
    required this.onPeriodChanged,
  });

  final TextEditingController searchController;
  final String sort;
  final String period;
  final ValueChanged<String> onSearch;
  final VoidCallback onClearSearch;
  final ValueChanged<String?> onSortChanged;
  final ValueChanged<String?> onPeriodChanged;

  @override
  Widget build(BuildContext context) {
    return MobileGridControlsBar(
      keyPrefix: 'payments',
      padding: EdgeInsets.zero,
      searchController: searchController,
      searchLabel: 'Search payments',
      onSearch: onSearch,
      onClearSearch: onClearSearch,
      sort: sort,
      defaultSort: '-createdAt',
      sortLabel: 'Sort payments',
      sortOptions: const [
        MobileGridControlOption(value: '-createdAt', label: 'Entered recently'),
        MobileGridControlOption(value: '-dueDate', label: 'Due newest'),
        MobileGridControlOption(value: 'dueDate', label: 'Due oldest'),
        MobileGridControlOption(value: '-amount', label: 'Amount high-low'),
        MobileGridControlOption(value: 'amount', label: 'Amount low-high'),
        MobileGridControlOption(value: 'status', label: 'Status'),
      ],
      onSortChanged: onSortChanged,
      period: period,
      defaultPeriod: MobileGridPeriod.all,
      periodLabel: 'Due period',
      onPeriodChanged: onPeriodChanged,
    );
  }
}

// ── Summary Cards ─────────────────────────────────────────────────────────────

class _SummaryCards extends StatelessWidget {
  const _SummaryCards({
    required this.summary,
    required this.colorScheme,
    required this.theme,
  });

  final AccountingSummary summary;
  final ColorScheme colorScheme;
  final ThemeData theme;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 8),
      child: Column(
        children: [
          Row(
            children: [
              Expanded(
                child: _SummaryCard(
                  label: 'Collected',
                  amount: summary.collected,
                  color: colorScheme.primaryContainer,
                  textColor: colorScheme.onPrimaryContainer,
                  theme: theme,
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: _SummaryCard(
                  label: 'Outstanding',
                  amount: summary.outstanding,
                  color: colorScheme.secondaryContainer,
                  textColor: colorScheme.onSecondaryContainer,
                  theme: theme,
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: _SummaryCard(
                  label: 'Overdue',
                  amount: summary.overdue,
                  color: colorScheme.errorContainer,
                  textColor: colorScheme.onErrorContainer,
                  theme: theme,
                ),
              ),
            ],
          ),
          if (summary.snapshot.title.isNotEmpty) ...[
            const SizedBox(height: 10),
            Card(
              child: Padding(
                padding: const EdgeInsets.all(14),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      summary.snapshot.title,
                      style: theme.textTheme.titleSmall?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    if (summary.snapshot.summary.isNotEmpty) ...[
                      const SizedBox(height: 4),
                      Text(
                        summary.snapshot.summary,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: colorScheme.onSurfaceVariant,
                        ),
                      ),
                    ],
                    for (final bullet in summary.snapshot.bullets.take(3)) ...[
                      const SizedBox(height: 8),
                      Text('- $bullet', style: theme.textTheme.bodySmall),
                    ],
                  ],
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _SummaryCard extends StatelessWidget {
  const _SummaryCard({
    required this.label,
    required this.amount,
    required this.color,
    required this.textColor,
    required this.theme,
  });

  final String label;
  final double amount;
  final Color color;
  final Color textColor;
  final ThemeData theme;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 12),
      decoration: BoxDecoration(
        color: color,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            label,
            style: theme.textTheme.labelSmall?.copyWith(color: textColor),
          ),
          const SizedBox(height: 4),
          Text(
            _fmtCurrency(amount),
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w700,
              color: textColor,
            ),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
          ),
        ],
      ),
    );
  }
}

// ── Payment Card ──────────────────────────────────────────────────────────────

class _PaymentCard extends StatelessWidget {
  const _PaymentCard({
    required this.payment,
    required this.position,
    required this.colorScheme,
    required this.theme,
    required this.onMarkPaid,
    required this.onTap,
  });

  final Payment payment;
  final MobileM3ListItemPosition position;
  final ColorScheme colorScheme;
  final ThemeData theme;
  final VoidCallback onMarkPaid;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final isPaid = payment.status.toLowerCase() == 'paid';
    final leaseDisplay = formatPaymentLeaseDisplay(payment);
    final title = leaseDisplay.isNotEmpty
        ? leaseDisplay
        : payment.tenantName ?? 'Payment #${payment.id}';
    final subtitle = leaseDisplay.isNotEmpty ? payment.tenantName : null;

    return MobileM3ListItem(
      position: position,
      leading: MobileM3LeadingIcon(
        icon: isPaid ? Icons.check_circle_outline : Icons.payments_outlined,
        backgroundColor: isPaid
            ? colorScheme.primaryContainer
            : colorScheme.secondaryContainer,
        foregroundColor: isPaid
            ? colorScheme.onPrimaryContainer
            : colorScheme.onSecondaryContainer,
      ),
      title: Text(
        title,
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w600,
        ),
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
      ),
      supporting: [
        if (subtitle != null && subtitle.isNotEmpty)
          Text(
            subtitle,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: theme.textTheme.bodySmall?.copyWith(
              color: colorScheme.onSurfaceVariant,
            ),
          ),
        Text(
          'Due ${_fmtDate(payment.dueDate)}',
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: theme.textTheme.bodySmall?.copyWith(
            color: colorScheme.onSurfaceVariant,
          ),
        ),
      ],
      meta: Wrap(
        spacing: 8,
        runSpacing: 4,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          Text(
            _fmtCurrency(payment.amount),
            style: theme.textTheme.bodyMedium?.copyWith(
              fontWeight: FontWeight.w800,
            ),
          ),
          _StatusChip(status: payment.status, colorScheme: colorScheme),
          _TypeChip(type: payment.type, colorScheme: colorScheme),
        ],
      ),
      actions: [
        if (!isPaid)
          FilledButton.tonal(
            onPressed: onMarkPaid,
            style: FilledButton.styleFrom(
              minimumSize: const Size(48, 48),
              padding: const EdgeInsets.symmetric(horizontal: 12),
              textStyle: theme.textTheme.labelSmall,
            ),
            child: const Text('Mark paid'),
          ),
      ],
      onTap: onTap,
    );
  }
}

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.status, required this.colorScheme});

  final String status;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    final lower = status.toLowerCase();
    final Color bg;
    final Color fg;
    if (lower == 'paid') {
      bg = colorScheme.primaryContainer;
      fg = colorScheme.onPrimaryContainer;
    } else if (lower == 'late' || lower == 'overdue') {
      bg = colorScheme.errorContainer;
      fg = colorScheme.onErrorContainer;
    } else {
      bg = colorScheme.surfaceContainerHighest;
      fg = colorScheme.onSurfaceVariant;
    }
    return _Chip(label: status, bg: bg, fg: fg);
  }
}

class _TypeChip extends StatelessWidget {
  const _TypeChip({required this.type, required this.colorScheme});

  final String type;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return _Chip(
      label: paymentTypeLabel(type),
      bg: colorScheme.secondaryContainer,
      fg: colorScheme.onSecondaryContainer,
    );
  }
}

class _Chip extends StatelessWidget {
  const _Chip({required this.label, required this.bg, required this.fg});

  final String label;
  final Color bg;
  final Color fg;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        label,
        style: TextStyle(fontSize: 11, fontWeight: FontWeight.w600, color: fg),
      ),
    );
  }
}

// ── Empty / Error ─────────────────────────────────────────────────────────────

class _EmptyBody extends StatelessWidget {
  const _EmptyBody({required this.hasSearch});

  final bool hasSearch;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(
            Icons.receipt_long_outlined,
            size: 48,
            color: colorScheme.onSurfaceVariant,
          ),
          const SizedBox(height: 12),
          Text(
            hasSearch ? 'No matching payments' : 'No payments yet',
            style: Theme.of(context).textTheme.titleMedium?.copyWith(
              color: colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 4),
          Text(
            hasSearch
                ? 'Try another method, reference or note.'
                : 'Tap + to record a payment.',
            textAlign: TextAlign.center,
            style: TextStyle(color: colorScheme.onSurfaceVariant),
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
    final colorScheme = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.error_outline, size: 40, color: colorScheme.error),
            const SizedBox(height: 12),
            Text(
              message,
              textAlign: TextAlign.center,
              style: TextStyle(color: colorScheme.error),
            ),
            const SizedBox(height: 16),
            FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}

class _ErrorBanner extends StatelessWidget {
  const _ErrorBanner({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
      decoration: BoxDecoration(
        color: colorScheme.errorContainer,
        borderRadius: BorderRadius.circular(8),
      ),
      child: Row(
        children: [
          Icon(Icons.error_outline, size: 16, color: colorScheme.error),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              message,
              style: TextStyle(
                color: colorScheme.onErrorContainer,
                fontSize: 13,
              ),
            ),
          ),
          TextButton(onPressed: onRetry, child: const Text('Retry')),
        ],
      ),
    );
  }
}

// ── Create Payment Bottom Sheet ───────────────────────────────────────────────

class _CreatePaymentSheet extends ConsumerStatefulWidget {
  const _CreatePaymentSheet({required this.onSaved});

  final VoidCallback onSaved;

  @override
  ConsumerState<_CreatePaymentSheet> createState() =>
      _CreatePaymentSheetState();
}

class _CreatePaymentSheetState extends ConsumerState<_CreatePaymentSheet> {
  final _formKey = GlobalKey<FormState>();
  final _amountCtrl = TextEditingController();
  final _amountPaidCtrl = TextEditingController();
  final _notesCtrl = TextEditingController();

  int? _selectedLeaseId;
  DateTime? _dueDate;
  String _type = 'Rent';
  String _status = 'Scheduled';
  bool _saving = false;
  String? _error;

  static const _types = [
    'Rent',
    'SecurityDeposit',
    'LateFee',
    'Utility',
    'Other',
  ];
  static const _statuses = ['Scheduled', 'Paid', 'Partial', 'Late', 'Waived'];

  @override
  void dispose() {
    _amountCtrl.dispose();
    _amountPaidCtrl.dispose();
    _notesCtrl.dispose();
    super.dispose();
  }

  String _leaseLabel(Lease l) {
    return formatLeasePickerLabel(l);
  }

  Future<void> _pickDate(BuildContext context) async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _dueDate ?? now,
      firstDate: DateTime(now.year - 2),
      lastDate: DateTime(now.year + 5),
    );
    if (picked != null) {
      setState(() {
        _dueDate = picked;
        if (_error == 'Please select a due date.') _error = null;
      });
    }
  }

  String? _partialAmountError() {
    if (_status != 'Partial') return null;
    final amount = double.tryParse(_amountCtrl.text.trim());
    final amountPaid = double.tryParse(_amountPaidCtrl.text.trim());
    if (amountPaid == null) {
      return 'Amount paid is required for a partial payment';
    }
    if (amountPaid <= 0) {
      return 'Amount paid must be greater than zero';
    }
    if (amount != null && amountPaid >= amount) {
      return 'Amount paid must be less than the full amount';
    }
    return null;
  }

  bool _validateDetailsStep() {
    if (_dueDate == null) {
      setState(() => _error = 'Please select a due date.');
      return false;
    }
    if (_error == 'Please select a due date.') {
      setState(() => _error = null);
    }
    return true;
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    if (!_validateDetailsStep()) return;

    final amount = double.tryParse(_amountCtrl.text.trim()) ?? 0;
    // Partial-payment split: the server requires an Amount paid strictly between
    // 0 and the full Amount (mirrors web paymentSchema.superRefine /
    // PaymentService.NormalizeAmountPaid). Surface it inline before submit.
    final isPartial = _status == 'Partial';
    final amountPaid = isPartial
        ? double.tryParse(_amountPaidCtrl.text.trim())
        : null;
    if (isPartial) {
      if (amountPaid == null) {
        setState(
          () => _error = 'Amount paid is required for a partial payment',
        );
        return;
      }
      if (amountPaid <= 0) {
        setState(() => _error = 'Amount paid must be greater than zero');
        return;
      }
      if (amountPaid >= amount) {
        setState(
          () => _error = 'Amount paid must be less than the full amount',
        );
        return;
      }
    }

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      await ref.read(paymentsRepositoryProvider).createPayment({
        'leaseId': _selectedLeaseId,
        'amount': amount,
        // amountPaid only travels for a Partial payment; null for any other
        // status (matches the web client + server normalization).
        'amountPaid': isPartial ? amountPaid : null,
        'dueDate': _dueDate!.toIso8601String().split('T').first,
        'type': _type,
        'status': _status,
        if (_notesCtrl.text.trim().isNotEmpty) 'notes': _notesCtrl.text.trim(),
      });

      widget.onSaved();
      if (mounted) Navigator.of(context).pop();
    } on ApiException catch (e) {
      setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final leasesAsync = ref.watch(leasesForPaymentProvider);
    final colorScheme = Theme.of(context).colorScheme;
    const gap = SizedBox(height: 12);

    return Form(
      key: _formKey,
      child: TabbedFormSheet(
        title: 'Record Payment',
        saveLabel: 'Save Payment',
        saving: _saving,
        error: _error,
        onSave: _submit,
        tabs: [
          TabbedFormStepSpec(
            label: 'Details',
            validate: _validateDetailsStep,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                leasesAsync.when(
                  loading: () => const Center(
                    child: Padding(
                      padding: EdgeInsets.symmetric(vertical: 12),
                      child: CircularProgressIndicator(),
                    ),
                  ),
                  error: (e, _) => Text(
                    'Could not load leases: ${e is ApiException ? e.message : e}',
                    style: TextStyle(color: colorScheme.error, fontSize: 13),
                  ),
                  data: (leases) => DropdownButtonFormField<int>(
                    initialValue: _selectedLeaseId,
                    decoration: const InputDecoration(labelText: 'Lease'),
                    items: leases
                        .map(
                          (l) => DropdownMenuItem(
                            value: l.id,
                            child: Text(
                              _leaseLabel(l),
                              overflow: TextOverflow.ellipsis,
                            ),
                          ),
                        )
                        .toList(),
                    onChanged: (v) => setState(() => _selectedLeaseId = v),
                    validator: (v) =>
                        v == null ? 'Please select a lease' : null,
                  ),
                ),
                gap,
                TextFormField(
                  controller: _amountCtrl,
                  keyboardType: const TextInputType.numberWithOptions(
                    decimal: true,
                  ),
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(
                    labelText: 'Amount',
                    prefixText: '\$',
                  ),
                  validator: (v) {
                    if (v == null || v.trim().isEmpty) {
                      return 'Amount is required';
                    }
                    if (double.tryParse(v.trim()) == null) {
                      return 'Enter a valid number';
                    }
                    return null;
                  },
                ),
                gap,
                InkWell(
                  onTap: () => _pickDate(context),
                  borderRadius: BorderRadius.circular(4),
                  child: InputDecorator(
                    decoration: InputDecoration(
                      labelText: 'Due date',
                      suffixIcon: const Icon(
                        Icons.calendar_today_outlined,
                        size: 18,
                      ),
                      errorText:
                          (_dueDate == null &&
                              _error != null &&
                              _error!.contains('due date'))
                          ? 'Required'
                          : null,
                    ),
                    child: Text(
                      _dueDate != null ? _fmtDate(_dueDate!) : 'Select date',
                      style: _dueDate != null
                          ? null
                          : TextStyle(color: colorScheme.onSurfaceVariant),
                    ),
                  ),
                ),
                gap,
                DropdownButtonFormField<String>(
                  initialValue: _type,
                  decoration: const InputDecoration(labelText: 'Type'),
                  items: _types
                      .map(
                        (t) => DropdownMenuItem(
                          value: t,
                          child: Text(paymentTypeLabel(t)),
                        ),
                      )
                      .toList(),
                  onChanged: (v) {
                    if (v != null) setState(() => _type = v);
                  },
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Status',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                DropdownButtonFormField<String>(
                  initialValue: _status,
                  decoration: const InputDecoration(labelText: 'Status'),
                  items: _statuses
                      .map((s) => DropdownMenuItem(value: s, child: Text(s)))
                      .toList(),
                  onChanged: (v) {
                    if (v != null) setState(() => _status = v);
                  },
                ),
                if (_status == 'Partial') ...[
                  gap,
                  TextFormField(
                    controller: _amountPaidCtrl,
                    keyboardType: const TextInputType.numberWithOptions(
                      decimal: true,
                    ),
                    textInputAction: TextInputAction.next,
                    decoration: const InputDecoration(
                      labelText: 'Amount paid (so far)',
                      prefixText: '\$',
                      helperText:
                          'How much was collected. The rest stays owed.',
                    ),
                    validator: (_) => _partialAmountError(),
                  ),
                ],
                gap,
                TextFormField(
                  controller: _notesCtrl,
                  maxLines: 2,
                  textInputAction: TextInputAction.done,
                  decoration: const InputDecoration(
                    labelText: 'Notes (optional)',
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
