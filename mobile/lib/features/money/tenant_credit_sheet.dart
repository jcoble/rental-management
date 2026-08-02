import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/time/app_clock.dart';
import '../accounting/accounting_book_models.dart';
import '../home/mobile_quick_action_fab.dart';
import 'money_format.dart';
import 'one_time_charge_sheet.dart';
import 'tenant_ledger_models.dart';
import 'tenant_ledger_repository.dart';

const targetedTenantCreditError =
    "This credit is larger than what's left of the original charge. Enter it as a standalone credit instead.";

final tenantCreditTargetRowsProvider = FutureProvider.autoDispose
    .family<List<TenantLedgerRow>, int>((ref, tenantAccountId) async {
      final page = await ref
          .read(tenantLedgerRepositoryProvider)
          .ledger(
            tenantAccountId,
            query: const TenantLedgerQuery(take: 200, sort: '-effectiveOn'),
          );
      return page.items;
    });

bool creditEligibleTenantLedgerRow(TenantLedgerRow row) =>
    row.chargeAmount > 0 &&
    row.type != TenantLedgerEntryType.depositCharge &&
    row.type != TenantLedgerEntryType.paymentReceipt &&
    row.type != TenantLedgerEntryType.reversal;

Future<TenantLedgerMutationResult?> showTenantCreditSheet(
  BuildContext context,
  WidgetRef ref, {
  required int tenantAccountId,
  TenantLedgerRow? initialTarget,
}) => showMobileQuickActionHiddenModalBottomSheet<TenantLedgerMutationResult>(
  context: context,
  isScrollControlled: true,
  showDragHandle: true,
  useSafeArea: true,
  builder: (_) => _TenantCreditSheet(
    tenantAccountId: tenantAccountId,
    initialTarget: initialTarget,
  ),
);

class _TenantCreditSheet extends ConsumerStatefulWidget {
  const _TenantCreditSheet({
    required this.tenantAccountId,
    required this.initialTarget,
  });

  final int tenantAccountId;
  final TenantLedgerRow? initialTarget;

  @override
  ConsumerState<_TenantCreditSheet> createState() => _TenantCreditSheetState();
}

class _TenantCreditSheetState extends ConsumerState<_TenantCreditSheet> {
  final _formKey = GlobalKey<FormState>();
  final _amount = TextEditingController();
  final _reason = TextEditingController();
  DateTime _effectiveOn = DateUtils.dateOnly(DateTime.now());
  bool _applyToCharge = false;
  int? _targetChargeEntryId;
  int? _categoryAccountId;
  bool _datesLoaded = false;
  bool _saving = false;
  String? _error;
  String? _documentLabel;
  late final String _operationKey =
      'mobile-tenant-credit-${widget.tenantAccountId}-${DateTime.now().microsecondsSinceEpoch}';

  @override
  void initState() {
    super.initState();
    final target = widget.initialTarget;
    if (target != null && creditEligibleTenantLedgerRow(target)) {
      _applyToCharge = true;
      _targetChargeEntryId = target.tenantLedgerEntryId;
    }
    Future.microtask(_loadBusinessDate);
  }

  @override
  void dispose() {
    _amount.dispose();
    _reason.dispose();
    super.dispose();
  }

  Future<void> _loadBusinessDate() async {
    try {
      final now = DateUtils.dateOnly(await ref.read(appNowProvider.future));
      if (!mounted) return;
      setState(() {
        _effectiveOn = now;
        _datesLoaded = true;
      });
    } catch (_) {
      if (mounted) setState(() => _datesLoaded = true);
    }
  }

  Future<void> _pickDate() async {
    final picked = await showDatePicker(
      context: context,
      initialDate: _effectiveOn,
      firstDate: DateTime(_effectiveOn.year - 5),
      lastDate: DateTime(_effectiveOn.year + 5),
    );
    if (picked != null) {
      setState(() => _effectiveOn = DateUtils.dateOnly(picked));
    }
  }

  Future<void> _submit() async {
    final valid = _formKey.currentState?.validate() ?? false;
    if (!valid) return;
    if (_applyToCharge && _targetChargeEntryId == null) {
      setState(
        () => _error = 'Choose the original charge or turn off the checkbox.',
      );
      return;
    }
    if (!_applyToCharge && _categoryAccountId == null) {
      setState(() => _error = 'Choose an income category.');
      return;
    }
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final result = await ref
          .read(tenantLedgerRepositoryProvider)
          .postCredit(
            widget.tenantAccountId,
            PostTenantCreditInput(
              amount: double.parse(_amount.text.trim()),
              effectiveOn: _effectiveOn,
              description: _reason.text.trim(),
              allocateOldestCharges: false,
              targetChargeEntryId: _applyToCharge ? _targetChargeEntryId : null,
              incomeLedgerAccountId: _applyToCharge ? null : _categoryAccountId,
            ),
            operationKey: _operationKey,
          );
      if (result.error?.trim().isNotEmpty == true || !result.applied) {
        if (mounted) {
          setState(
            () => _error = result.error ?? 'The credit was not recorded.',
          );
        }
        return;
      }
      if (mounted) Navigator.of(context).pop(result);
    } on ApiException catch (error) {
      if (mounted) {
        setState(
          () => _error = _applyToCharge && error.statusCode == 400
              ? targetedTenantCreditError
              : error.message,
        );
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  TenantLedgerRow? _selectedTarget(List<TenantLedgerRow> rows) {
    for (final row in rows) {
      if (row.tenantLedgerEntryId == _targetChargeEntryId &&
          creditEligibleTenantLedgerRow(row)) {
        return row;
      }
    }
    final initial = widget.initialTarget;
    return initial != null &&
            initial.tenantLedgerEntryId == _targetChargeEntryId &&
            creditEligibleTenantLedgerRow(initial)
        ? initial
        : null;
  }

  @override
  Widget build(BuildContext context) {
    final targetsAsync = ref.watch(
      tenantCreditTargetRowsProvider(widget.tenantAccountId),
    );
    final accountsAsync = ref.watch(tenantIncomeAccountsProvider);
    final targets =
        targetsAsync.value
            ?.where(creditEligibleTenantLedgerRow)
            .toList(growable: false) ??
        const <TenantLedgerRow>[];
    final selectedTarget = _selectedTarget(targets);
    final bottom = MediaQuery.viewInsetsOf(context).bottom;
    return MobileQuickActionHider(
      child: SingleChildScrollView(
        padding: EdgeInsets.fromLTRB(20, 4, 20, 24 + bottom),
        child: Form(
          key: _formKey,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                'Give credit',
                style: Theme.of(context).textTheme.titleLarge,
              ),
              const SizedBox(height: 4),
              const Text(
                'Credits keep the original charge and payment history intact.',
              ),
              const SizedBox(height: 16),
              TextFormField(
                key: const Key('tenant-credit-amount'),
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
                title: const Text('Effective date'),
                subtitle: Text(
                  _datesLoaded ? dateFmt(_effectiveOn) : 'Loading…',
                ),
                trailing: const Icon(Icons.calendar_today_outlined),
                onTap: _saving ? null : _pickDate,
              ),
              TextFormField(
                key: const Key('tenant-credit-reason'),
                controller: _reason,
                decoration: const InputDecoration(
                  labelText: 'Reason',
                  hintText: 'Overcharged water bill',
                ),
                validator: (value) => value == null || value.trim().isEmpty
                    ? 'Explain why this credit is being given.'
                    : null,
              ),
              const SizedBox(height: 16),
              Card(
                margin: EdgeInsets.zero,
                child: Padding(
                  padding: const EdgeInsets.all(12),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      CheckboxListTile(
                        contentPadding: EdgeInsets.zero,
                        value: _applyToCharge,
                        onChanged: _saving
                            ? null
                            : (value) => setState(() {
                                _applyToCharge = value ?? false;
                                if (!_applyToCharge) {
                                  _targetChargeEntryId = null;
                                }
                              }),
                        title: const Text('Apply to this charge'),
                        controlAffinity: ListTileControlAffinity.leading,
                      ),
                      if (_applyToCharge)
                        targetsAsync.when(
                          loading: () =>
                              const Text('Loading eligible charges…'),
                          error: (error, _) => Text(
                            error is ApiException
                                ? error.message
                                : 'Eligible charges unavailable.',
                          ),
                          data: (_) => DropdownButtonFormField<int>(
                            key: const Key('tenant-credit-target'),
                            initialValue: _targetChargeEntryId,
                            decoration: const InputDecoration(
                              labelText: 'Original charge',
                            ),
                            items: [
                              for (final row in targets)
                                DropdownMenuItem(
                                  value: row.tenantLedgerEntryId,
                                  child: Text(
                                    '${row.description} · ${moneyFmt(row.chargeAmount)} · ${dateFmt(row.effectiveOn)}',
                                    overflow: TextOverflow.ellipsis,
                                  ),
                                ),
                            ],
                            onChanged: (value) =>
                                setState(() => _targetChargeEntryId = value),
                            validator: (value) => value == null
                                ? 'Choose the original charge.'
                                : null,
                          ),
                        )
                      else
                        _CreditIncomeAccountDropdown(
                          accounts: accountsAsync,
                          selectedId: _categoryAccountId,
                          onChanged: (value) =>
                              setState(() => _categoryAccountId = value),
                        ),
                    ],
                  ),
                ),
              ),
              if (selectedTarget != null && selectedTarget.openAmount > 0)
                _CreditPreview(
                  note:
                      'The remaining balance is re-read from the server after the credit is saved.',
                  children: [
                    _PreviewValue(
                      label: 'Original charge',
                      value: moneyFmt(selectedTarget.chargeAmount),
                    ),
                    _PreviewValue(label: 'Credit', value: _enteredMoney),
                    _PreviewValue(
                      label: 'Remaining charge (server)',
                      value: moneyFmt(selectedTarget.openAmount),
                    ),
                  ],
                )
              else if (selectedTarget != null)
                _CreditPreview(
                  note:
                      'This charge is already fully paid, so the server will keep the credit unapplied.',
                  children: [
                    _PreviewValue(label: 'Credit', value: _enteredMoney),
                    _PreviewValue(
                      label: 'Unapplied credit after save',
                      value: _enteredMoney,
                    ),
                  ],
                )
              else if (!_applyToCharge)
                _CreditPreview(
                  note:
                      'This will remain as an unapplied tenant credit until it is used.',
                  children: const [],
                ),
              const SizedBox(height: 12),
              OutlinedButton.icon(
                icon: const Icon(Icons.attach_file_outlined),
                onPressed: _saving
                    ? null
                    : () => setState(
                        () => _documentLabel = 'Supporting document selected',
                      ),
                label: Text(_documentLabel ?? 'Document (optional)'),
              ),
              if (_error != null) ...[
                const SizedBox(height: 12),
                Text(
                  _error!,
                  key: const Key('tenant-credit-error'),
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
              ],
              const SizedBox(height: 16),
              FilledButton(
                key: const Key('tenant-credit-submit'),
                onPressed: _saving ? null : _submit,
                child: Text(_saving ? 'Saving…' : 'Give credit'),
              ),
            ],
          ),
        ),
      ),
    );
  }

  String get _enteredMoney {
    final amount = double.tryParse(_amount.text.trim());
    return amount == null ? '—' : moneyFmt(amount);
  }
}

class _CreditIncomeAccountDropdown extends StatelessWidget {
  const _CreditIncomeAccountDropdown({
    required this.accounts,
    required this.selectedId,
    required this.onChanged,
  });

  final AsyncValue<List<ChartOfAccountsRow>> accounts;
  final int? selectedId;
  final ValueChanged<int?> onChanged;

  @override
  Widget build(BuildContext context) => accounts.when(
    loading: () => const InputDecorator(
      decoration: InputDecoration(labelText: 'Category'),
      child: Text('Loading income categories…'),
    ),
    error: (error, _) => InputDecorator(
      decoration: const InputDecoration(labelText: 'Category'),
      child: Text(
        error is ApiException
            ? error.message
            : 'Income categories unavailable.',
      ),
    ),
    data: (items) => DropdownButtonFormField<int>(
      key: const Key('tenant-credit-category'),
      initialValue: selectedId,
      decoration: const InputDecoration(labelText: 'Category'),
      items: [
        for (final account in items)
          DropdownMenuItem(
            value: account.id,
            child: Text('${account.code} · ${account.name}'),
          ),
      ],
      onChanged: onChanged,
      validator: (value) => value == null ? 'Choose an income category.' : null,
    ),
  );
}

class _CreditPreview extends StatelessWidget {
  const _CreditPreview({required this.note, required this.children});

  final List<Widget> children;
  final String note;

  @override
  Widget build(BuildContext context) => Container(
    margin: const EdgeInsets.only(top: 12),
    padding: const EdgeInsets.all(12),
    decoration: BoxDecoration(
      color: Theme.of(context).colorScheme.surfaceContainerHighest,
      borderRadius: BorderRadius.circular(12),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text('Preview', style: Theme.of(context).textTheme.titleSmall),
        if (children.isNotEmpty) ...[
          const SizedBox(height: 10),
          Wrap(spacing: 16, runSpacing: 8, children: children),
        ],
        const SizedBox(height: 8),
        Text(note, style: Theme.of(context).textTheme.bodySmall),
      ],
    ),
  );
}

class _PreviewValue extends StatelessWidget {
  const _PreviewValue({required this.label, required this.value});

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
