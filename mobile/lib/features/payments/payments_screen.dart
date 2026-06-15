import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/models.dart';
import '../../core/api/api_exception.dart';
import 'payment_detail_screen.dart';
import 'payments_repository.dart';

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
  @override
  void initState() {
    super.initState();
    Future.microtask(() {
      ref.read(accountingSummaryProvider.notifier).load();
      ref.read(paymentsProvider.notifier).load();
    });
  }

  Future<void> _refresh() async {
    await Future.wait([
      ref.read(accountingSummaryProvider.notifier).refresh(),
      ref.read(paymentsProvider.notifier).refresh(),
    ]);
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
    // Pre-load leases for the dropdown.
    ref.read(leasesForPaymentProvider.notifier).load();
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _CreatePaymentSheet(
        onSaved: () {
          ref.read(paymentsProvider.notifier).refresh();
          ref.read(accountingSummaryProvider.notifier).refresh();
        },
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final summaryAsync = ref.watch(accountingSummaryProvider);
    final paymentsAsync = ref.watch(paymentsProvider);
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(title: const Text('Payments')),
      floatingActionButton: FloatingActionButton(
        heroTag: 'payments-fab',
        onPressed: () => _showCreateSheet(context),
        tooltip: 'Record payment',
        child: const Icon(Icons.add),
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
              data: (list) {
                if (list.isEmpty) {
                  return const SliverFillRemaining(child: _EmptyBody());
                }
                return SliverPadding(
                  padding: const EdgeInsets.fromLTRB(16, 8, 16, 88),
                  sliver: SliverList.separated(
                    itemCount: list.length,
                    separatorBuilder: (context, index) =>
                        const SizedBox(height: 8),
                    itemBuilder: (ctx, i) => _PaymentCard(
                      payment: list[i],
                      colorScheme: colorScheme,
                      theme: theme,
                      onMarkPaid: () => ref
                          .read(paymentsProvider.notifier)
                          .markPaid(list[i].id),
                      onTap: () => _openDetail(ctx, list[i].id),
                    ),
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
    required this.colorScheme,
    required this.theme,
    required this.onMarkPaid,
    required this.onTap,
  });

  final Payment payment;
  final ColorScheme colorScheme;
  final ThemeData theme;
  final VoidCallback onMarkPaid;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final isPaid = payment.status.toLowerCase() == 'paid';

    return Card(
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.all(14),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    // Tenant / lease info
                    Text(
                      payment.tenantName ??
                          (payment.leaseNumber != null
                              ? 'Lease ${payment.leaseNumber}'
                              : 'Payment #${payment.id}'),
                      style: theme.textTheme.titleSmall?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                    if (payment.leaseNumber != null)
                      Text(
                        'Lease ${payment.leaseNumber}',
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: colorScheme.onSurfaceVariant,
                        ),
                      ),
                    const SizedBox(height: 6),
                    Row(
                      children: [
                        Text(
                          _fmtCurrency(payment.amount),
                          style: theme.textTheme.bodyMedium?.copyWith(
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                        const SizedBox(width: 8),
                        _StatusChip(
                          status: payment.status,
                          colorScheme: colorScheme,
                        ),
                        const SizedBox(width: 8),
                        _TypeChip(type: payment.type, colorScheme: colorScheme),
                      ],
                    ),
                    const SizedBox(height: 4),
                    Text(
                      'Due ${_fmtDate(payment.dueDate)}',
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
              ),
              if (!isPaid)
                Padding(
                  padding: const EdgeInsets.only(left: 8),
                  child: FilledButton.tonal(
                    onPressed: onMarkPaid,
                    style: FilledButton.styleFrom(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 10,
                        vertical: 6,
                      ),
                      minimumSize: Size.zero,
                      tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                      textStyle: const TextStyle(fontSize: 12),
                    ),
                    child: const Text('Mark paid'),
                  ),
                ),
            ],
          ),
        ),
      ),
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
  const _EmptyBody();

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
            'No payments yet',
            style: Theme.of(context).textTheme.titleMedium?.copyWith(
              color: colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 4),
          Text(
            'Tap + to record a payment.',
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
    _notesCtrl.dispose();
    super.dispose();
  }

  /// Human-readable label for a lease so the landlord can tell which lease is
  /// which (e.g. "Unit 4B — Jane Smith"). Falls back to the lease number when
  /// the unit/tenant fields aren't populated.
  String _leaseLabel(Lease l) {
    final unit = l.unitNumber;
    final tenant = l.tenantName;
    final parts = <String>[
      if (unit != null && unit.isNotEmpty) 'Unit $unit',
      if (tenant != null && tenant.isNotEmpty) tenant,
    ];
    if (parts.isEmpty) return l.leaseNumber;
    return parts.join(' — ');
  }

  Future<void> _pickDate(BuildContext context) async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _dueDate ?? now,
      firstDate: DateTime(now.year - 2),
      lastDate: DateTime(now.year + 5),
    );
    if (picked != null) setState(() => _dueDate = picked);
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    if (_dueDate == null) {
      setState(() => _error = 'Please select a due date.');
      return;
    }

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      await ref.read(paymentsRepositoryProvider).createPayment({
        'leaseId': _selectedLeaseId,
        'amount': double.tryParse(_amountCtrl.text.trim()) ?? 0,
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
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottomPadding),
      child: Form(
        key: _formKey,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // Header
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'Record Payment',
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
              const SizedBox(height: 16),

              // Lease dropdown
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
                  validator: (v) => v == null ? 'Please select a lease' : null,
                ),
              ),
              const SizedBox(height: 12),

              // Amount
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
              const SizedBox(height: 12),

              // Due date picker
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
              const SizedBox(height: 12),

              // Type + Status row
              Row(
                children: [
                  Expanded(
                    child: DropdownButtonFormField<String>(
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
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: DropdownButtonFormField<String>(
                      initialValue: _status,
                      decoration: const InputDecoration(labelText: 'Status'),
                      items: _statuses
                          .map(
                            (s) => DropdownMenuItem(value: s, child: Text(s)),
                          )
                          .toList(),
                      onChanged: (v) {
                        if (v != null) setState(() => _status = v);
                      },
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 12),

              // Notes (optional)
              TextFormField(
                controller: _notesCtrl,
                maxLines: 2,
                textInputAction: TextInputAction.done,
                decoration: const InputDecoration(
                  labelText: 'Notes (optional)',
                ),
              ),

              if (_error != null) ...[
                const SizedBox(height: 12),
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 12,
                    vertical: 10,
                  ),
                  decoration: BoxDecoration(
                    color: colorScheme.errorContainer,
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Text(
                    _error!,
                    style: TextStyle(
                      color: colorScheme.onErrorContainer,
                      fontSize: 13,
                    ),
                  ),
                ),
              ],

              const SizedBox(height: 20),

              FilledButton(
                onPressed: _saving ? null : _submit,
                child: _saving
                    ? const SizedBox(
                        height: 20,
                        width: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Text('Save Payment'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
