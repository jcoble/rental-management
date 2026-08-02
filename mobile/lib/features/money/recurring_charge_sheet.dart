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

Future<RecurringTenantChargeRow?> showRecurringChargeSheet(
  BuildContext context,
  WidgetRef ref, {
  required int tenantAccountId,
  int? leaseAgreementId,
  int? propertyId,
  int? unitId,
}) => showMobileQuickActionHiddenModalBottomSheet<RecurringTenantChargeRow>(
  context: context,
  isScrollControlled: true,
  showDragHandle: true,
  useSafeArea: true,
  builder: (_) => _RecurringChargeSheet(
    tenantAccountId: tenantAccountId,
    leaseAgreementId: leaseAgreementId,
    propertyId: propertyId,
    unitId: unitId,
  ),
);

class _RecurringChargeSheet extends ConsumerStatefulWidget {
  const _RecurringChargeSheet({
    required this.tenantAccountId,
    this.leaseAgreementId,
    this.propertyId,
    this.unitId,
  });

  final int tenantAccountId;
  final int? leaseAgreementId;
  final int? propertyId;
  final int? unitId;

  @override
  ConsumerState<_RecurringChargeSheet> createState() =>
      _RecurringChargeSheetState();
}

class _RecurringChargeSheetState extends ConsumerState<_RecurringChargeSheet> {
  final _formKey = GlobalKey<FormState>();
  final _name = TextEditingController();
  final _amount = TextEditingController();
  DateTime _startOn = DateUtils.dateOnly(DateTime.now());
  DateTime? _endOn;
  int _dueDay = 1;
  int? _accountId;
  bool _datesLoaded = false;
  bool _saving = false;
  String? _error;
  late final String _operationKey =
      'mobile-recurring-charge-${widget.tenantAccountId}-${DateTime.now().microsecondsSinceEpoch}';

  @override
  void initState() {
    super.initState();
    Future.microtask(_loadBusinessDate);
  }

  @override
  void dispose() {
    _name.dispose();
    _amount.dispose();
    super.dispose();
  }

  Future<void> _loadBusinessDate() async {
    try {
      final now = DateUtils.dateOnly(await ref.read(appNowProvider.future));
      if (!mounted) return;
      setState(() {
        _startOn = now;
        _datesLoaded = true;
      });
    } catch (_) {
      if (mounted) setState(() => _datesLoaded = true);
    }
  }

  Future<void> _pickStart() async {
    final picked = await showDatePicker(
      context: context,
      initialDate: _startOn,
      firstDate: DateTime(_startOn.year - 1),
      lastDate: DateTime(_startOn.year + 10),
    );
    if (picked != null) setState(() => _startOn = DateUtils.dateOnly(picked));
  }

  Future<void> _pickEnd() async {
    final picked = await showDatePicker(
      context: context,
      initialDate: _endOn ?? _startOn,
      firstDate: _startOn,
      lastDate: DateTime(_startOn.year + 10),
    );
    if (picked != null) setState(() => _endOn = DateUtils.dateOnly(picked));
  }

  Future<void> _submit() async {
    final accounts = ref.read(tenantIncomeAccountsProvider).value ?? const [];
    final valid = _formKey.currentState?.validate() ?? false;
    if (!valid || _accountId == null) {
      setState(() {
        _error = _accountId == null ? 'Choose an income category.' : null;
      });
      return;
    }
    final accountExists = accounts.any((account) => account.id == _accountId);
    if (!accountExists) {
      setState(() => _error = 'The selected income category is unavailable.');
      return;
    }
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final result = await ref
          .read(tenantLedgerRepositoryProvider)
          .createRecurringCharge(
            widget.tenantAccountId,
            CreateRecurringTenantChargeInput(
              displayName: _name.text.trim(),
              amount: double.parse(_amount.text.trim()),
              ledgerAccountId: _accountId!,
              leaseAgreementId: widget.leaseAgreementId,
              effectiveStartOn: _startOn,
              effectiveEndOn: _endOn,
              monthlyDueDay: _dueDay,
              nextRunDate: _startOn,
              propertyId: widget.propertyId,
              unitId: widget.unitId,
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
    final accounts = ref.watch(tenantIncomeAccountsProvider);
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
                'Recurring charge',
                style: Theme.of(context).textTheme.titleLarge,
              ),
              const SizedBox(height: 4),
              const Text('Set up a charge for future periods.'),
              const SizedBox(height: 16),
              TextFormField(
                key: const Key('recurring-charge-name'),
                controller: _name,
                decoration: const InputDecoration(
                  labelText: 'Name',
                  hintText: 'Monthly rent',
                ),
                validator: (value) => value == null || value.trim().isEmpty
                    ? 'Name this recurring charge.'
                    : null,
              ),
              const SizedBox(height: 12),
              TextFormField(
                key: const Key('recurring-charge-amount'),
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
              _RecurringIncomeAccountDropdown(
                accounts: accounts,
                selectedId: _accountId,
                onChanged: (value) => setState(() => _accountId = value),
              ),
              _RecurringDateField(
                label: 'Starts',
                value: _startOn,
                loaded: _datesLoaded,
                onTap: _pickStart,
              ),
              _RecurringOptionalDateField(
                label: 'Ends (optional)',
                value: _endOn,
                onTap: _pickEnd,
                onClear: () => setState(() => _endOn = null),
              ),
              const SizedBox(height: 8),
              DropdownButtonFormField<int>(
                key: const Key('recurring-charge-day-due'),
                initialValue: _dueDay,
                decoration: const InputDecoration(labelText: 'Day due'),
                items: [
                  for (var day = 1; day <= 31; day++)
                    DropdownMenuItem(value: day, child: Text(_ordinal(day))),
                ],
                onChanged: _saving
                    ? null
                    : (value) {
                        if (value != null) setState(() => _dueDay = value);
                      },
              ),
              const SizedBox(height: 12),
              Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: Theme.of(context).colorScheme.surfaceContainerHighest,
                  borderRadius: BorderRadius.circular(12),
                ),
                child: const Text(
                  'Changes apply to future charges only. Already-posted charges stay as-is.',
                ),
              ),
              if (_error != null) ...[
                const SizedBox(height: 12),
                Text(
                  _error!,
                  key: const Key('recurring-charge-error'),
                  style: TextStyle(color: Colors.red),
                ),
              ],
              const SizedBox(height: 16),
              FilledButton(
                key: const Key('recurring-charge-submit'),
                onPressed: _saving ? null : _submit,
                child: Text(_saving ? 'Saving…' : 'Save'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _RecurringIncomeAccountDropdown extends StatelessWidget {
  const _RecurringIncomeAccountDropdown({
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
      key: const Key('recurring-charge-category'),
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

class _RecurringDateField extends StatelessWidget {
  const _RecurringDateField({
    required this.label,
    required this.value,
    required this.loaded,
    required this.onTap,
  });

  final String label;
  final DateTime value;
  final bool loaded;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => ListTile(
    contentPadding: EdgeInsets.zero,
    title: Text(label),
    subtitle: Text(loaded ? dateFmt(value) : 'Loading…'),
    trailing: const Icon(Icons.calendar_today_outlined),
    onTap: onTap,
  );
}

class _RecurringOptionalDateField extends StatelessWidget {
  const _RecurringOptionalDateField({
    required this.label,
    required this.value,
    required this.onTap,
    required this.onClear,
  });

  final String label;
  final DateTime? value;
  final VoidCallback onTap;
  final VoidCallback onClear;

  @override
  Widget build(BuildContext context) => InputDecorator(
    decoration: InputDecoration(
      labelText: label,
      suffixIcon: value == null
          ? null
          : IconButton(icon: const Icon(Icons.clear), onPressed: onClear),
    ),
    child: InkWell(
      onTap: onTap,
      child: Text(value == null ? 'Optional' : dateFmt(value!)),
    ),
  );
}

String _ordinal(int value) {
  final suffix = value % 10 == 1 && value != 11
      ? 'st'
      : value % 10 == 2 && value != 12
      ? 'nd'
      : value % 10 == 3 && value != 13
      ? 'rd'
      : 'th';
  return '$value$suffix';
}
