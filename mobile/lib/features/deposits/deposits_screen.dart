import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/files/document_opener.dart';
import '../../core/models/models.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'deposits_repository.dart';

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

// ── Screen ────────────────────────────────────────────────────────────────────

/// Security deposits screen — list of all holdings with status chips.
///
/// Tap a row to open a detail bottom sheet with deductions and return actions.
/// FAB opens the create deposit sheet.
class DepositsScreen extends ConsumerStatefulWidget {
  const DepositsScreen({super.key});

  @override
  ConsumerState<DepositsScreen> createState() => _DepositsScreenState();
}

class _DepositsScreenState extends ConsumerState<DepositsScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(depositsProvider.notifier).load());
  }

  Future<void> _refresh() => ref.read(depositsProvider.notifier).refresh();

  void _showCreateSheet() {
    ref.read(leasesForDepositProvider.notifier).load();
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _CreateDepositSheet(
        onSaved: () => ref.read(depositsProvider.notifier).refresh(),
      ),
    );
  }

  void _showDetail(SecurityDeposit deposit) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _DepositDetailSheet(deposit: deposit),
    );
  }

  @override
  Widget build(BuildContext context) {
    final asyncState = ref.watch(depositsProvider);

    return Scaffold(
      appBar: mobileDomainRootAppBar(
        context,
        title: const Text('Security Deposits'),
      ),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'deposits-fab',
        primaryAction: MobileQuickAction(
          label: 'Add deposit',
          icon: Icons.add,
          onPressed: _showCreateSheet,
        ),
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: asyncState.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(
            message: e is ApiException ? e.message : e.toString(),
            onRetry: () => ref.read(depositsProvider.notifier).refresh(),
          ),
          data: (list) {
            if (list.isEmpty) {
              return CustomScrollView(
                physics: const AlwaysScrollableScrollPhysics(),
                slivers: [const SliverFillRemaining(child: _EmptyBody())],
              );
            }
            return ListView.separated(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 88),
              itemCount: list.length,
              separatorBuilder: (context, index) => const SizedBox(height: 8),
              itemBuilder: (_, i) => _DepositCard(
                deposit: list[i],
                onTap: () => _showDetail(list[i]),
              ),
            );
          },
        ),
      ),
    );
  }
}

// ── Deposit Card ──────────────────────────────────────────────────────────────

class _DepositCard extends StatelessWidget {
  const _DepositCard({required this.deposit, required this.onTap});

  final SecurityDeposit deposit;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final isReturned = deposit.status.toLowerCase() == 'returned';

    return Card(
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(14),
          child: Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Text(
                          deposit.leaseNumber != null
                              ? 'Lease ${deposit.leaseNumber}'
                              : 'Deposit #${deposit.id}',
                          style: theme.textTheme.titleSmall?.copyWith(
                            fontWeight: FontWeight.w600,
                          ),
                        ),
                        const SizedBox(width: 8),
                        _StatusChip(status: deposit.status),
                      ],
                    ),
                    const SizedBox(height: 4),
                    Row(
                      children: [
                        Text(
                          _fmtCurrency(deposit.amount),
                          style: theme.textTheme.bodyMedium?.copyWith(
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                        if (deposit.totalDeductions > 0) ...[
                          const SizedBox(width: 6),
                          Text(
                            '− ${_fmtCurrency(deposit.totalDeductions)} deductions',
                            style: theme.textTheme.bodySmall?.copyWith(
                              color: cs.error,
                            ),
                          ),
                        ],
                      ],
                    ),
                    const SizedBox(height: 2),
                    Text(
                      isReturned
                          ? 'Returned ${deposit.returnedAt != null ? _fmtDate(deposit.returnedAt!) : ''}'
                          : 'Held since ${_fmtDate(deposit.heldAt)}',
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: cs.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
              ),
              Icon(Icons.chevron_right, color: cs.onSurfaceVariant),
            ],
          ),
        ),
      ),
    );
  }
}

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.status});

  final String status;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final lower = status.toLowerCase();
    final Color bg;
    final Color fg;
    if (lower == 'held') {
      bg = cs.primaryContainer;
      fg = cs.onPrimaryContainer;
    } else if (lower == 'returned') {
      bg = cs.secondaryContainer;
      fg = cs.onSecondaryContainer;
    } else if (lower == 'partial') {
      bg = cs.tertiaryContainer;
      fg = cs.onTertiaryContainer;
    } else {
      bg = cs.surfaceContainerHighest;
      fg = cs.onSurfaceVariant;
    }
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        status,
        style: TextStyle(fontSize: 11, fontWeight: FontWeight.w600, color: fg),
      ),
    );
  }
}

// ── Deposit Detail Sheet ──────────────────────────────────────────────────────

class _DepositDetailSheet extends ConsumerStatefulWidget {
  const _DepositDetailSheet({required this.deposit});

  final SecurityDeposit deposit;

  @override
  ConsumerState<_DepositDetailSheet> createState() =>
      _DepositDetailSheetState();
}

class _DepositDetailSheetState extends ConsumerState<_DepositDetailSheet> {
  late SecurityDeposit _deposit;

  @override
  void initState() {
    super.initState();
    _deposit = widget.deposit;
  }

  bool get _isReturned => _deposit.status.toLowerCase() == 'returned';

  void _addDeductionDialog() {
    showDialog<void>(
      context: context,
      builder: (ctx) => _AddDeductionDialog(
        onSave: (reason, amount, notes) async {
          final data = <String, dynamic>{
            'reason': reason,
            'amount': amount,
            if (notes != null && notes.isNotEmpty) 'notes': notes,
          };
          await ref
              .read(depositsProvider.notifier)
              .addDeduction(_deposit.id, data);
          // Refresh local state from provider.
          ref.read(depositsProvider).whenData((list) {
            final updated = list.where((d) => d.id == _deposit.id).firstOrNull;
            if (updated != null && mounted) {
              setState(() => _deposit = updated);
            }
          });
        },
      ),
    );
  }

  /// Fetches the move-out statement PDF bytes (authed) and hands them to the OS
  /// viewer via [DocumentOpener] (FileProvider `content://` URI, share fallback).
  Future<void> _openMoveOutStatement() async {
    final messenger = ScaffoldMessenger.of(context);
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(
        const SnackBar(content: Text('Preparing move-out statement…')),
      );
    try {
      final bytes = await ref
          .read(depositsRepositoryProvider)
          .moveOutStatementBytes(_deposit.id);
      await DocumentOpener.openBytes(
        bytes: bytes,
        fileName: 'deposit-${_deposit.id}-move-out-statement.pdf',
      );
      messenger.hideCurrentSnackBar();
    } on ApiException catch (e) {
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  void _processReturnDialog() {
    final notesCtrl = TextEditingController();
    showDialog<void>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Process Return'),
        content: TextField(
          controller: notesCtrl,
          maxLines: 3,
          decoration: const InputDecoration(labelText: 'Notes (optional)'),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () async {
              Navigator.of(ctx).pop();
              await ref
                  .read(depositsProvider.notifier)
                  .processReturn(
                    _deposit.id,
                    notes: notesCtrl.text.trim().isEmpty
                        ? null
                        : notesCtrl.text.trim(),
                  );
              ref.read(depositsProvider).whenData((list) {
                final updated = list
                    .where((d) => d.id == _deposit.id)
                    .firstOrNull;
                if (updated != null && mounted) {
                  setState(() => _deposit = updated);
                }
              });
            },
            child: const Text('Confirm Return'),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottomPadding),
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
                    _deposit.leaseNumber != null
                        ? 'Lease ${_deposit.leaseNumber}'
                        : 'Deposit #${_deposit.id}',
                    style: theme.textTheme.titleLarge?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                _StatusChip(status: _deposit.status),
                const SizedBox(width: 8),
                IconButton(
                  icon: const Icon(Icons.close),
                  onPressed: () => Navigator.of(context).pop(),
                ),
              ],
            ),
            const SizedBox(height: 16),

            // Amounts
            _DetailRow(
              label: 'Deposit held',
              value: _fmtCurrency(_deposit.amount),
            ),
            if (_deposit.totalDeductions > 0)
              _DetailRow(
                label: 'Total deductions',
                value: '− ${_fmtCurrency(_deposit.totalDeductions)}',
                valueColor: cs.error,
              ),
            _DetailRow(
              label: 'Net refund',
              value: _fmtCurrency(_deposit.netRefund),
              bold: true,
            ),
            if (_deposit.returnedAmount != null)
              _DetailRow(
                label: 'Amount returned',
                value: _fmtCurrency(_deposit.returnedAmount!),
              ),
            const Divider(height: 24),

            // Dates
            _DetailRow(label: 'Held since', value: _fmtDate(_deposit.heldAt)),
            if (_deposit.returnedAt != null)
              _DetailRow(
                label: 'Returned on',
                value: _fmtDate(_deposit.returnedAt!),
              ),
            if (_deposit.notes != null && _deposit.notes!.isNotEmpty)
              _DetailRow(label: 'Notes', value: _deposit.notes!),

            // Deductions
            if (_deposit.deductions.isNotEmpty) ...[
              const Divider(height: 24),
              Text(
                'Deductions',
                style: theme.textTheme.titleSmall?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
              ),
              const SizedBox(height: 8),
              ...(_deposit.deductions.map(
                (d) => Padding(
                  padding: const EdgeInsets.symmetric(vertical: 4),
                  child: Row(
                    children: [
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(d.reason, style: theme.textTheme.bodyMedium),
                            if (d.notes != null && d.notes!.isNotEmpty)
                              Text(
                                d.notes!,
                                style: theme.textTheme.bodySmall?.copyWith(
                                  color: cs.onSurfaceVariant,
                                ),
                              ),
                          ],
                        ),
                      ),
                      Text(
                        _fmtCurrency(d.amount),
                        style: theme.textTheme.bodyMedium?.copyWith(
                          color: cs.error,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                    ],
                  ),
                ),
              )),
            ],

            const SizedBox(height: 20),

            // Actions (only if not yet returned)
            if (!_isReturned) ...[
              OutlinedButton.icon(
                icon: const Icon(Icons.remove_circle_outline, size: 18),
                label: const Text('Add Deduction'),
                onPressed: _addDeductionDialog,
              ),
              const SizedBox(height: 10),
              FilledButton.icon(
                icon: const Icon(Icons.assignment_return_outlined, size: 18),
                label: const Text('Process Return'),
                onPressed: _processReturnDialog,
              ),
              const SizedBox(height: 10),
            ],

            // Move-out statement is useful before and after the return.
            OutlinedButton.icon(
              icon: const Icon(Icons.picture_as_pdf_outlined, size: 18),
              label: const Text('Move-out statement (PDF)'),
              onPressed: _openMoveOutStatement,
            ),
          ],
        ),
      ),
    );
  }
}

class _DetailRow extends StatelessWidget {
  const _DetailRow({
    required this.label,
    required this.value,
    this.valueColor,
    this.bold = false,
  });

  final String label;
  final String value;
  final Color? valueColor;
  final bool bold;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 3),
      child: Row(
        children: [
          Expanded(
            child: Text(
              label,
              style: theme.textTheme.bodySmall?.copyWith(
                color: cs.onSurfaceVariant,
              ),
            ),
          ),
          Text(
            value,
            style: theme.textTheme.bodyMedium?.copyWith(
              color: valueColor,
              fontWeight: bold ? FontWeight.w700 : null,
            ),
          ),
        ],
      ),
    );
  }
}

// ── Add Deduction Dialog ──────────────────────────────────────────────────────

class _AddDeductionDialog extends StatefulWidget {
  const _AddDeductionDialog({required this.onSave});

  final Future<void> Function(String reason, double amount, String? notes)
  onSave;

  @override
  State<_AddDeductionDialog> createState() => _AddDeductionDialogState();
}

class _AddDeductionDialogState extends State<_AddDeductionDialog> {
  final _formKey = GlobalKey<FormState>();
  final _reasonCtrl = TextEditingController();
  final _amountCtrl = TextEditingController();
  final _notesCtrl = TextEditingController();
  bool _saving = false;
  String? _error;

  @override
  void dispose() {
    _reasonCtrl.dispose();
    _amountCtrl.dispose();
    _notesCtrl.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      await widget.onSave(
        _reasonCtrl.text.trim(),
        double.parse(_amountCtrl.text.trim()),
        _notesCtrl.text.trim().isEmpty ? null : _notesCtrl.text.trim(),
      );
      if (mounted) Navigator.of(context).pop();
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } catch (e) {
      if (mounted) setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return AlertDialog(
      title: const Text('Add Deduction'),
      content: Form(
        key: _formKey,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            TextFormField(
              controller: _reasonCtrl,
              textInputAction: TextInputAction.next,
              decoration: const InputDecoration(labelText: 'Reason'),
              validator: (v) =>
                  v == null || v.trim().isEmpty ? 'Required' : null,
            ),
            const SizedBox(height: 10),
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
                if (v == null || v.trim().isEmpty) return 'Required';
                if (double.tryParse(v.trim()) == null) {
                  return 'Enter a valid number';
                }
                return null;
              },
            ),
            const SizedBox(height: 10),
            TextFormField(
              controller: _notesCtrl,
              maxLines: 2,
              decoration: const InputDecoration(labelText: 'Notes (optional)'),
            ),
            if (_error != null) ...[
              const SizedBox(height: 8),
              Text(_error!, style: TextStyle(color: cs.error, fontSize: 13)),
            ],
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: _saving ? null : () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: _saving ? null : _submit,
          child: _saving
              ? const SizedBox(
                  width: 16,
                  height: 16,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : const Text('Save'),
        ),
      ],
    );
  }
}

// ── Create Deposit Sheet ──────────────────────────────────────────────────────

class _CreateDepositSheet extends ConsumerStatefulWidget {
  const _CreateDepositSheet({required this.onSaved});

  final VoidCallback onSaved;

  @override
  ConsumerState<_CreateDepositSheet> createState() =>
      _CreateDepositSheetState();
}

class _CreateDepositSheetState extends ConsumerState<_CreateDepositSheet> {
  final _formKey = GlobalKey<FormState>();
  final _amountCtrl = TextEditingController();
  final _notesCtrl = TextEditingController();
  int? _selectedLeaseId;
  bool _saving = false;
  String? _error;

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

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final data = <String, dynamic>{
        'leaseId': _selectedLeaseId,
        if (_amountCtrl.text.trim().isNotEmpty)
          'amount': double.tryParse(_amountCtrl.text.trim()),
        if (_notesCtrl.text.trim().isNotEmpty) 'notes': _notesCtrl.text.trim(),
      };
      await ref.read(depositsRepositoryProvider).createDeposit(data);
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
    final leasesAsync = ref.watch(leasesForDepositProvider);
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
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
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'Record Security Deposit',
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

              // Lease picker
              leasesAsync.when(
                loading: () => const Center(
                  child: Padding(
                    padding: EdgeInsets.symmetric(vertical: 12),
                    child: CircularProgressIndicator(),
                  ),
                ),
                error: (e, _) => Text(
                  'Could not load leases: ${e is ApiException ? e.message : e}',
                  style: TextStyle(color: cs.error, fontSize: 13),
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

              TextFormField(
                controller: _amountCtrl,
                keyboardType: const TextInputType.numberWithOptions(
                  decimal: true,
                ),
                decoration: const InputDecoration(
                  labelText: 'Amount (optional)',
                  prefixText: '\$',
                ),
              ),
              const SizedBox(height: 12),

              TextFormField(
                controller: _notesCtrl,
                maxLines: 2,
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
                    color: cs.errorContainer,
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Text(
                    _error!,
                    style: TextStyle(color: cs.onErrorContainer, fontSize: 13),
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
                    : const Text('Save Deposit'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

// ── Empty / Error ─────────────────────────────────────────────────────────────

class _EmptyBody extends StatelessWidget {
  const _EmptyBody();

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.shield_outlined, size: 48, color: cs.onSurfaceVariant),
          const SizedBox(height: 12),
          Text(
            'No deposits yet',
            style: Theme.of(
              context,
            ).textTheme.titleMedium?.copyWith(color: cs.onSurfaceVariant),
          ),
          const SizedBox(height: 4),
          Text(
            'Tap + to record a security deposit.',
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
