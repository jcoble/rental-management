import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/time/app_clock.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import '../../core/presentation/formatting.dart';
import 'payment_detail_screen.dart';
import 'payments_repository.dart';

Future<RecordTenantReceiptResult?> showRecordTenantReceiptSheet(
  BuildContext context,
  WidgetRef ref, {
  required int tenantAccountId,
  required int leaseManagementId,
  String? tenantName,
  String? rentalLabel,
  double? initialAmount,
  int? targetChargeEntryId,
  bool initialLeaveUnapplied = false,
}) {
  return showModalBottomSheet<RecordTenantReceiptResult>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    useSafeArea: true,
    builder: (_) => _RecordTenantReceiptSheet(
      tenantAccountId: tenantAccountId,
      leaseManagementId: leaseManagementId,
      tenantName: tenantName,
      rentalLabel: rentalLabel,
      initialAmount: initialAmount,
      targetChargeEntryId: targetChargeEntryId,
      initialLeaveUnapplied: initialLeaveUnapplied,
    ),
  );
}

class PaymentsScreen extends ConsumerStatefulWidget {
  const PaymentsScreen({super.key});

  @override
  ConsumerState<PaymentsScreen> createState() => _PaymentsScreenState();
}

class _PaymentsScreenState extends ConsumerState<PaymentsScreen> {
  final _searchController = TextEditingController();
  int _skip = 0;
  String? _search;

  TenantLedgerEntryListQuery get _query => TenantLedgerEntryListQuery(
    skip: _skip,
    take: 20,
    search: _search,
    sort: '-postedAtUtc',
  );

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  void _submitSearch(String value) {
    final normalized = value.trim();
    setState(() {
      _search = normalized.isEmpty ? null : normalized;
      _skip = 0;
    });
  }

  @override
  Widget build(BuildContext context) {
    final pageAsync = ref.watch(tenantLedgerEntriesPageProvider(_query));
    final body = RefreshIndicator(
      onRefresh: () async =>
          ref.invalidate(tenantLedgerEntriesPageProvider(_query)),
      child: ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.fromLTRB(16, 12, 16, 96),
        children: [
          TextField(
            controller: _searchController,
            textInputAction: TextInputAction.search,
            onSubmitted: _submitSearch,
            decoration: InputDecoration(
              labelText: 'Search receipts',
              hintText: 'Tenant, account, property, or unit',
              prefixIcon: const Icon(Icons.search),
              suffixIcon: _search == null
                  ? null
                  : IconButton(
                      tooltip: 'Clear search',
                      onPressed: () {
                        _searchController.clear();
                        _submitSearch('');
                      },
                      icon: const Icon(Icons.close),
                    ),
            ),
          ),
          const SizedBox(height: 16),
          pageAsync.when(
            loading: () => const Padding(
              padding: EdgeInsets.all(32),
              child: Center(child: CircularProgressIndicator()),
            ),
            error: (error, _) => _ErrorCard(
              message: error is ApiException ? error.message : error.toString(),
              onRetry: () =>
                  ref.invalidate(tenantLedgerEntriesPageProvider(_query)),
            ),
            data: (page) => Column(
              children: [
                if (page.items.isEmpty)
                  const Padding(
                    padding: EdgeInsets.symmetric(vertical: 48),
                    child: Text('No receipts found.'),
                  )
                else
                  for (final receipt in page.items)
                    _ReceiptCard(receipt: receipt),
                if (page.hasPrevious || page.hasNext) ...[
                  const SizedBox(height: 12),
                  Row(
                    mainAxisAlignment: MainAxisAlignment.spaceBetween,
                    children: [
                      OutlinedButton.icon(
                        onPressed: page.hasPrevious
                            ? () => setState(
                                () => _skip = _skip >= page.take
                                    ? _skip - page.take
                                    : 0,
                              )
                            : null,
                        icon: const Icon(Icons.chevron_left),
                        label: const Text('Previous'),
                      ),
                      Text('${page.totalCount} receipts'),
                      OutlinedButton.icon(
                        onPressed: page.hasNext
                            ? () => setState(() => _skip += page.take)
                            : null,
                        iconAlignment: IconAlignment.end,
                        icon: const Icon(Icons.chevron_right),
                        label: const Text('Next'),
                      ),
                    ],
                  ),
                ],
              ],
            ),
          ),
        ],
      ),
    );
    return Scaffold(
      appBar: AppBar(title: const Text('Receipts')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'receipts-fab',
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: body,
    );
  }
}

class _ReceiptCard extends StatelessWidget {
  const _ReceiptCard({required this.receipt});

  final StaffTenantLedgerEntry receipt;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final home = [
      receipt.propertyName,
      if (receipt.unitNumber.trim().isNotEmpty) 'Unit ${receipt.unitNumber}',
    ].where((value) => value.trim().isNotEmpty).join(' · ');
    return Card(
      margin: const EdgeInsets.only(bottom: 10),
      child: ListTile(
        leading: const Icon(Icons.receipt_long_outlined),
        title: Text(
          receipt.primaryTenantName?.trim().isNotEmpty == true
              ? receipt.primaryTenantName!
              : receipt.description,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
        subtitle: Text(
          [if (home.isNotEmpty) home, dateFmt(receipt.effectiveOn)].join(' · '),
          maxLines: 2,
          overflow: TextOverflow.ellipsis,
        ),
        trailing: Text(
          moneyFmt(receipt.amount),
          style: theme.textTheme.titleSmall?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        onTap: () => Navigator.of(context).push<void>(
          MaterialPageRoute<void>(
            builder: (_) => PaymentDetailScreen(
              tenantAccountId: receipt.tenantAccountId,
              tenantLedgerEntryId: receipt.tenantLedgerEntryId,
            ),
          ),
        ),
      ),
    );
  }
}

class _RecordTenantReceiptSheet extends ConsumerStatefulWidget {
  const _RecordTenantReceiptSheet({
    required this.tenantAccountId,
    required this.leaseManagementId,
    this.tenantName,
    this.rentalLabel,
    this.initialAmount,
    this.targetChargeEntryId,
    this.initialLeaveUnapplied = false,
  });

  final int tenantAccountId;
  final int leaseManagementId;
  final String? tenantName;
  final String? rentalLabel;
  final double? initialAmount;
  final int? targetChargeEntryId;
  final bool initialLeaveUnapplied;

  @override
  ConsumerState<_RecordTenantReceiptSheet> createState() =>
      _RecordTenantReceiptSheetState();
}

class _RecordTenantReceiptSheetState
    extends ConsumerState<_RecordTenantReceiptSheet> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _amount = TextEditingController(
    text: widget.initialAmount == null
        ? ''
        : widget.initialAmount!.toStringAsFixed(2),
  );
  final _description = TextEditingController(text: 'Rent payment');
  final _method = TextEditingController();
  final _reference = TextEditingController();
  final _payer = TextEditingController();
  late final String _operationKey =
      'mobile-receipt-${widget.tenantAccountId}-${DateTime.now().microsecondsSinceEpoch}';
  DateTime _receivedOn = DateTime.now();
  late bool _leaveUnapplied =
      widget.targetChargeEntryId == null && widget.initialLeaveUnapplied;
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _loadBusinessDate();
  }

  Future<void> _loadBusinessDate() async {
    try {
      final now = await ref.read(appNowProvider.future);
      if (!mounted) return;
      setState(() => _receivedOn = DateUtils.dateOnly(now));
    } catch (_) {
      // Keep the device clock fallback when the simulation clock is unavailable.
    }
  }

  @override
  void dispose() {
    _amount.dispose();
    _description.dispose();
    _method.dispose();
    _reference.dispose();
    _payer.dispose();
    super.dispose();
  }

  Future<void> _pickDate() async {
    final picked = await showDatePicker(
      context: context,
      initialDate: _receivedOn,
      firstDate: DateTime(_receivedOn.year - 3),
      lastDate: DateTime(_receivedOn.year + 1),
    );
    if (picked != null) setState(() => _receivedOn = picked);
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final result = await ref
          .read(paymentsRepositoryProvider)
          .recordReceipt(
            widget.tenantAccountId,
            RecordTenantReceiptInput(
              amount: double.parse(_amount.text.trim()),
              effectiveOn: _receivedOn,
              description: _description.text.trim().isEmpty
                  ? 'Rent payment'
                  : _description.text.trim(),
              paymentMethodSummary: _method.text.trim(),
              externalReference: _reference.text.trim().isEmpty
                  ? null
                  : _reference.text.trim(),
              payerName: _payer.text.trim().isEmpty
                  ? null
                  : _payer.text.trim(),
              targetChargeEntryId: widget.targetChargeEntryId,
              allocateOldestCharges: !_leaveUnapplied,
            ),
            operationKey: _operationKey,
          );
      ref.invalidate(tenantLedgerEntriesPageProvider);
      if (mounted) Navigator.of(context).pop(result);
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final bottom = MediaQuery.viewInsetsOf(context).bottom;
    final contextLabel = [
      widget.rentalLabel,
      widget.tenantName,
    ].whereType<String>().where((value) => value.trim().isNotEmpty).join(' · ');
    return MobileQuickActionHider(
      child: SingleChildScrollView(
        padding: EdgeInsets.fromLTRB(20, 4, 20, 24 + bottom),
        child: Form(
          key: _formKey,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                'Record receipt',
                style: Theme.of(context).textTheme.titleLarge,
              ),
              if (contextLabel.isNotEmpty) ...[
                const SizedBox(height: 4),
                Text(contextLabel),
              ],
              const SizedBox(height: 16),
              TextFormField(
                key: const Key('tenant-receipt-amount'),
                controller: _amount,
                keyboardType: const TextInputType.numberWithOptions(
                  decimal: true,
                ),
                decoration: const InputDecoration(
                  labelText: 'Amount',
                  prefixText: '\$',
                ),
                validator: (value) {
                  final amount = double.tryParse(value?.trim() ?? '');
                  return amount == null || amount <= 0
                      ? 'Enter an amount greater than zero'
                      : null;
                },
              ),
              const SizedBox(height: 12),
              TextFormField(
                key: const Key('tenant-receipt-method'),
                controller: _method,
                decoration: const InputDecoration(
                  labelText: 'How they paid',
                  hintText: 'Cash, check, bank transfer…',
                ),
                validator: (value) => value == null || value.trim().isEmpty
                    ? 'Say how they paid'
                    : null,
              ),
              const SizedBox(height: 12),
              ListTile(
                contentPadding: EdgeInsets.zero,
                title: const Text('Received on'),
                subtitle: Text(dateFmt(_receivedOn)),
                trailing: const Icon(Icons.calendar_today_outlined),
                onTap: _pickDate,
              ),
              const SizedBox(height: 4),
              _MoreReceiptDetails(
                targetChargeEntryId: widget.targetChargeEntryId,
                leaveUnapplied: _leaveUnapplied,
                onLeaveUnappliedChanged: _saving
                    ? null
                    : (value) => setState(() => _leaveUnapplied = value),
                payer: _payer,
                reference: _reference,
                notes: _description,
              ),
              if (_error != null) ...[
                const SizedBox(height: 12),
                Text(
                  _error!,
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
              ],
              const SizedBox(height: 20),
              FilledButton(
                key: const Key('tenant-receipt-submit'),
                onPressed: _saving ? null : _submit,
                child: Text(_saving ? 'Saving…' : 'Record receipt'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Everything past the three essentials (amount, how they paid, date) lives
/// behind one collapsed disclosure so the common case stays a three-field form.
class _MoreReceiptDetails extends StatelessWidget {
  const _MoreReceiptDetails({
    required this.targetChargeEntryId,
    required this.leaveUnapplied,
    required this.onLeaveUnappliedChanged,
    required this.payer,
    required this.reference,
    required this.notes,
  });

  final int? targetChargeEntryId;
  final bool leaveUnapplied;
  final ValueChanged<bool>? onLeaveUnappliedChanged;
  final TextEditingController payer;
  final TextEditingController reference;
  final TextEditingController notes;

  @override
  Widget build(BuildContext context) => ExpansionTile(
    key: const Key('tenant-receipt-more-details'),
    tilePadding: EdgeInsets.zero,
    childrenPadding: const EdgeInsets.only(bottom: 8),
    expandedCrossAxisAlignment: CrossAxisAlignment.stretch,
    title: const Text('More details'),
    children: [
      if (targetChargeEntryId == null)
        CheckboxListTile(
          contentPadding: EdgeInsets.zero,
          value: leaveUnapplied,
          onChanged: onLeaveUnappliedChanged == null
              ? null
              : (value) => onLeaveUnappliedChanged!(value ?? false),
          title: const Text('Leave this payment unapplied'),
          subtitle: const Text(
            'Hold it as credit instead of paying off the oldest rent owed.',
          ),
          controlAffinity: ListTileControlAffinity.leading,
        )
      else
        ListTile(
          contentPadding: EdgeInsets.zero,
          leading: const Icon(Icons.receipt_long_outlined),
          title: const Text('Goes toward the charge you picked'),
          subtitle: Text('Charge #$targetChargeEntryId'),
        ),
      const SizedBox(height: 8),
      TextFormField(
        controller: payer,
        decoration: const InputDecoration(labelText: 'Payer name (optional)'),
      ),
      const SizedBox(height: 12),
      TextFormField(
        controller: reference,
        decoration: const InputDecoration(
          labelText: 'Reference (optional)',
          hintText: 'Check number or confirmation code',
        ),
      ),
      const SizedBox(height: 12),
      TextFormField(
        controller: notes,
        decoration: const InputDecoration(labelText: 'Notes'),
      ),
    ],
  );
}

class _ErrorCard extends StatelessWidget {
  const _ErrorCard({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        children: [
          Text(message, textAlign: TextAlign.center),
          const SizedBox(height: 8),
          TextButton(onPressed: onRetry, child: const Text('Retry')),
        ],
      ),
    ),
  );
}
