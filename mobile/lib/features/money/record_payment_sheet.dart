import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/time/app_clock.dart';
import '../home/mobile_quick_action_fab.dart';
import '../../core/presentation/formatting.dart';
import 'tenant_ledger_models.dart';
import 'tenant_ledger_repository.dart';

const tenantPaymentMethods = <String>[
  'Cash',
  'Check',
  'Card',
  'Bank transfer',
  'Zelle',
  'Venmo',
  'Money order',
  'Online portal',
  'Other',
];

final tenantOpenChargesProvider = FutureProvider.autoDispose
    .family<List<TenantLedgerRow>, int>((ref, tenantAccountId) async {
      final page = await ref
          .read(tenantLedgerRepositoryProvider)
          .ledger(
            tenantAccountId,
            query: const TenantLedgerQuery(
              take: 100,
              sort: 'effectiveOn',
              openOnly: true,
            ),
          );
      return page.items;
    });

Future<RecordTenantReceiptResult?> showRecordPaymentSheet(
  BuildContext context,
  WidgetRef ref, {
  required int tenantAccountId,
  String? tenantName,
  String? rentalLabel,
  double? initialAmount,
  int? targetChargeEntryId,
}) => showMobileQuickActionHiddenModalBottomSheet<RecordTenantReceiptResult>(
  context: context,
  isScrollControlled: true,
  showDragHandle: true,
  useSafeArea: true,
  builder: (_) => _RecordPaymentSheet(
    tenantAccountId: tenantAccountId,
    tenantName: tenantName,
    rentalLabel: rentalLabel,
    initialAmount: initialAmount,
    targetChargeEntryId: targetChargeEntryId,
  ),
);

class _RecordPaymentSheet extends ConsumerStatefulWidget {
  const _RecordPaymentSheet({
    required this.tenantAccountId,
    this.tenantName,
    this.rentalLabel,
    this.initialAmount,
    this.targetChargeEntryId,
  });

  final int tenantAccountId;
  final String? tenantName;
  final String? rentalLabel;
  final double? initialAmount;
  final int? targetChargeEntryId;

  @override
  ConsumerState<_RecordPaymentSheet> createState() =>
      _RecordPaymentSheetState();
}

class _RecordPaymentSheetState extends ConsumerState<_RecordPaymentSheet> {
  final _formKey = GlobalKey<FormState>();
  late final _amount = TextEditingController(
    text: widget.initialAmount?.toStringAsFixed(2) ?? '',
  );
  final _reference = TextEditingController();
  late final _payer = TextEditingController(text: widget.tenantName ?? '');
  final _note = TextEditingController(text: 'Tenant payment');
  DateTime _receivedOn = DateUtils.dateOnly(DateTime.now());
  String? _method;
  late bool _applyOldest = widget.targetChargeEntryId == null;
  int? _targetChargeEntryId;
  bool _saving = false;
  bool _datesLoaded = false;
  String? _error;
  String? _documentLabel;
  late final String _operationKey =
      'mobile-record-payment-${widget.tenantAccountId}-${DateTime.now().microsecondsSinceEpoch}';

  @override
  void initState() {
    super.initState();
    _targetChargeEntryId = widget.targetChargeEntryId;
    Future.microtask(_loadBusinessDate);
  }

  @override
  void dispose() {
    _amount.dispose();
    _reference.dispose();
    _payer.dispose();
    _note.dispose();
    super.dispose();
  }

  Future<void> _loadBusinessDate() async {
    try {
      final now = await ref.read(appNowProvider.future);
      if (!mounted) return;
      setState(() {
        _receivedOn = DateUtils.dateOnly(now);
        _datesLoaded = true;
      });
    } catch (_) {
      if (mounted) setState(() => _datesLoaded = true);
    }
  }

  Future<void> _pickDate() async {
    final picked = await showDatePicker(
      context: context,
      initialDate: _receivedOn,
      firstDate: DateTime(_receivedOn.year - 5),
      lastDate: DateTime(_receivedOn.year + 1),
    );
    if (picked != null) {
      setState(() => _receivedOn = DateUtils.dateOnly(picked));
    }
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    if (!_applyOldest && _targetChargeEntryId == null) {
      setState(() => _error = 'Choose an open charge.');
      return;
    }
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final result = await ref
          .read(tenantLedgerRepositoryProvider)
          .recordReceipt(
            widget.tenantAccountId,
            RecordTenantReceiptInput(
              amount: double.parse(_amount.text.trim()),
              effectiveOn: _receivedOn,
              description: _note.text.trim().isEmpty
                  ? 'Tenant payment'
                  : _note.text.trim(),
              paymentMethodSummary: _method!,
              externalReference: _reference.text.trim().isEmpty
                  ? null
                  : _reference.text.trim(),
              payerName: _payer.text.trim().isEmpty ? null : _payer.text.trim(),
              targetChargeEntryId: _applyOldest ? null : _targetChargeEntryId,
              allocateOldestCharges: _applyOldest,
            ),
            operationKey: _operationKey,
          );
      if (mounted) Navigator.of(context).pop(result);
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final openCharges = ref.watch(
      tenantOpenChargesProvider(widget.tenantAccountId),
    );
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
                'Record payment',
                style: Theme.of(context).textTheme.titleLarge,
              ),
              const SizedBox(height: 4),
              const Text('Save the receipt without changing posted history.'),
              if (contextLabel.isNotEmpty) ...[
                const SizedBox(height: 4),
                Text(contextLabel),
              ],
              const SizedBox(height: 16),
              TextFormField(
                key: const Key('record-payment-amount'),
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
                      ? 'Enter an amount greater than zero.'
                      : null;
                },
              ),
              const SizedBox(height: 12),
              ListTile(
                contentPadding: EdgeInsets.zero,
                title: const Text('Date received'),
                subtitle: Text(
                  _datesLoaded ? dateFmt(_receivedOn) : 'Loading…',
                ),
                trailing: const Icon(Icons.calendar_today_outlined),
                onTap: _saving ? null : _pickDate,
              ),
              DropdownButtonFormField<String>(
                key: const Key('record-payment-method'),
                initialValue: _method,
                decoration: const InputDecoration(labelText: 'Payment method'),
                items: [
                  for (final method in tenantPaymentMethods)
                    DropdownMenuItem(value: method, child: Text(method)),
                ],
                onChanged: _saving
                    ? null
                    : (value) => setState(() => _method = value),
                validator: (value) => value == null || value.isEmpty
                    ? 'Choose a payment method.'
                    : null,
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _reference,
                decoration: const InputDecoration(
                  labelText: 'Reference / check #',
                  hintText: 'Optional',
                ),
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _payer,
                decoration: const InputDecoration(
                  labelText: 'Payer',
                  hintText: 'Tenant name',
                ),
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _note,
                decoration: const InputDecoration(
                  labelText: 'Note',
                  hintText: 'Optional note',
                ),
              ),
              const SizedBox(height: 16),
              _PaymentAllocationField(
                applyOldest: _applyOldest,
                targetChargeEntryId: _targetChargeEntryId,
                openCharges: openCharges,
                onModeChanged: (value) => setState(() {
                  _applyOldest = value;
                  if (value) _targetChargeEntryId = null;
                }),
                onTargetChanged: (value) =>
                    setState(() => _targetChargeEntryId = value),
              ),
              const SizedBox(height: 12),
              OutlinedButton.icon(
                icon: const Icon(Icons.attach_file_outlined),
                onPressed: _saving
                    ? null
                    : () => setState(
                        () => _documentLabel = 'Receipt document selected',
                      ),
                label: Text(_documentLabel ?? 'Receipt document (optional)'),
              ),
              if (_error != null) ...[
                const SizedBox(height: 12),
                Text(
                  _error!,
                  key: const Key('record-payment-error'),
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
              ],
              const SizedBox(height: 16),
              FilledButton(
                key: const Key('record-payment-submit'),
                onPressed: _saving ? null : _submit,
                child: Text(_saving ? 'Recording…' : 'Record payment'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _PaymentAllocationField extends StatelessWidget {
  const _PaymentAllocationField({
    required this.applyOldest,
    required this.targetChargeEntryId,
    required this.openCharges,
    required this.onModeChanged,
    required this.onTargetChanged,
  });

  final bool applyOldest;
  final int? targetChargeEntryId;
  final AsyncValue<List<TenantLedgerRow>> openCharges;
  final ValueChanged<bool> onModeChanged;
  final ValueChanged<int?> onTargetChanged;

  @override
  Widget build(BuildContext context) => Card(
    margin: EdgeInsets.zero,
    child: Padding(
      padding: const EdgeInsets.all(12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text('Apply to', style: Theme.of(context).textTheme.titleSmall),
          RadioGroup<bool>(
            groupValue: applyOldest,
            onChanged: (value) {
              if (value != null) onModeChanged(value);
            },
            child: Column(
              children: [
                RadioListTile<bool>(
                  contentPadding: EdgeInsets.zero,
                  value: true,
                  title: const Text('Oldest open charges first'),
                ),
                RadioListTile<bool>(
                  contentPadding: EdgeInsets.zero,
                  value: false,
                  title: const Text('A specific charge'),
                ),
              ],
            ),
          ),
          if (!applyOldest)
            openCharges.when(
              loading: () => const Text('Loading open charges…'),
              error: (error, _) => Text(
                error is ApiException
                    ? error.message
                    : 'Open charges unavailable.',
              ),
              data: (charges) => DropdownButtonFormField<int>(
                key: const Key('record-payment-target'),
                initialValue: targetChargeEntryId,
                decoration: const InputDecoration(labelText: 'Open charge'),
                items: [
                  for (final charge in charges)
                    DropdownMenuItem(
                      value: charge.tenantLedgerEntryId,
                      child: Text(
                        '${charge.description} · ${moneyFmt(charge.openAmount)} · ${dateFmt(charge.effectiveOn)}',
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                ],
                onChanged: onTargetChanged,
                validator: (value) =>
                    value == null ? 'Choose an open charge.' : null,
              ),
            ),
        ],
      ),
    ),
  );
}
