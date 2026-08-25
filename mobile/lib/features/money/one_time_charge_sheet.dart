import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/time/app_clock.dart';
import '../accounting/accounting_book_models.dart';
import '../accounting/accounting_books_repository.dart';
import '../accounting/accounting_help.dart';
import '../accounting/accounting_help_tip.dart';
import '../home/mobile_quick_action_fab.dart';
import '../../core/presentation/formatting.dart';
import 'tenant_ledger_models.dart';
import 'tenant_ledger_repository.dart';

const tenantChargeTypeOptions = <String>[
  'Rent',
  'Late fee',
  'Utility',
  'Damage',
  'Pet',
  'Parking',
  'Storage',
  'Cleaning',
  'Returned payment',
  'Other',
];

String? tenantChargeTypeSystemKey(String chargeType) => switch (chargeType) {
  'Rent' => 'rental-income',
  'Late fee' => 'late-fee-income',
  'Utility' => 'utility-reimbursement-income',
  'Damage' => 'other-rental-income',
  'Pet' => 'pet-income',
  'Parking' => 'parking-income',
  'Storage' => 'other-rental-income',
  'Cleaning' => 'other-rental-income',
  'Returned payment' => 'late-fee-income',
  _ => null,
};

final tenantIncomeAccountsProvider =
    FutureProvider.autoDispose<List<ChartOfAccountsRow>>((ref) async {
      final page = await ref
          .read(accountingBooksRepositoryProvider)
          .chartOfAccounts(
            query: const ChartOfAccountsQuery(take: 200, activeOnly: true),
          );
      return page.items
          .where((account) => account.accountType == AccountType.income)
          .toList(growable: false);
    });

Future<TenantChargeMutationResult?> showOneTimeChargeSheet(
  BuildContext context,
  WidgetRef ref, {
  required int tenantAccountId,
  String? initialDescription,
}) => showMobileQuickActionHiddenModalBottomSheet<TenantChargeMutationResult>(
  context: context,
  isScrollControlled: true,
  showDragHandle: true,
  useSafeArea: true,
  builder: (_) => _OneTimeChargeSheet(
    tenantAccountId: tenantAccountId,
    initialDescription: initialDescription,
  ),
);

class _OneTimeChargeSheet extends ConsumerStatefulWidget {
  const _OneTimeChargeSheet({
    required this.tenantAccountId,
    this.initialDescription,
  });

  final int tenantAccountId;
  final String? initialDescription;

  @override
  ConsumerState<_OneTimeChargeSheet> createState() =>
      _OneTimeChargeSheetState();
}

class _OneTimeChargeSheetState extends ConsumerState<_OneTimeChargeSheet> {
  final _formKey = GlobalKey<FormState>();
  final _amount = TextEditingController();
  final _description = TextEditingController();
  String _chargeType = 'Other';
  int? _categoryAccountId;
  DateTime _effectiveOn = DateUtils.dateOnly(DateTime.now());
  DateTime _dueOn = DateUtils.dateOnly(DateTime.now());
  DateTime? _servicePeriodStartOn;
  DateTime? _servicePeriodEndOn;
  bool _datesLoaded = false;
  bool _saving = false;
  String? _error;
  String? _documentLabel;
  late final String _operationKey =
      'mobile-one-time-charge-${widget.tenantAccountId}-${DateTime.now().microsecondsSinceEpoch}';

  @override
  void initState() {
    super.initState();
    _description.text = widget.initialDescription ?? '';
    Future.microtask(_loadBusinessDate);
  }

  @override
  void dispose() {
    _amount.dispose();
    _description.dispose();
    super.dispose();
  }

  Future<void> _loadBusinessDate() async {
    try {
      final now = DateUtils.dateOnly(await ref.read(appNowProvider.future));
      if (!mounted) return;
      setState(() {
        _effectiveOn = now;
        _dueOn = now;
        _datesLoaded = true;
      });
    } catch (_) {
      if (mounted) setState(() => _datesLoaded = true);
    }
  }

  Future<void> _pickDate({required bool dueDate}) async {
    final current = dueDate ? _dueOn : _effectiveOn;
    final picked = await showDatePicker(
      context: context,
      initialDate: current,
      firstDate: DateTime(current.year - 5),
      lastDate: DateTime(current.year + 5),
    );
    if (picked == null) return;
    setState(() {
      if (dueDate) {
        _dueOn = DateUtils.dateOnly(picked);
      } else {
        _effectiveOn = DateUtils.dateOnly(picked);
      }
    });
  }

  Future<void> _pickServiceDate({required bool endDate}) async {
    final current = endDate
        ? (_servicePeriodEndOn ?? _servicePeriodStartOn ?? _effectiveOn)
        : (_servicePeriodStartOn ?? _effectiveOn);
    final picked = await showDatePicker(
      context: context,
      initialDate: current,
      firstDate: DateTime(current.year - 5),
      lastDate: DateTime(current.year + 5),
    );
    if (picked == null) return;
    setState(() {
      if (endDate) {
        _servicePeriodEndOn = DateUtils.dateOnly(picked);
      } else {
        _servicePeriodStartOn = DateUtils.dateOnly(picked);
      }
    });
  }

  ChartOfAccountsRow? _resolvedAccount(List<ChartOfAccountsRow> accounts) {
    final systemKey = tenantChargeTypeSystemKey(_chargeType);
    if (systemKey == null) {
      for (final account in accounts) {
        if (account.id == _categoryAccountId) return account;
      }
      return null;
    }
    for (final account in accounts) {
      if (account.systemKey == systemKey) return account;
    }
    return null;
  }

  Future<void> _submit() async {
    final accounts = ref.read(tenantIncomeAccountsProvider).value ?? const [];
    final resolvedAccount = _resolvedAccount(accounts);
    var valid = _formKey.currentState?.validate() ?? false;
    if ((_servicePeriodStartOn == null) != (_servicePeriodEndOn == null)) {
      valid = false;
      _error = 'Enter both service-period dates or leave both empty.';
    } else if (_servicePeriodStartOn != null &&
        _servicePeriodEndOn!.isBefore(_servicePeriodStartOn!)) {
      valid = false;
      _error = 'Service-period end must be on or after the start.';
    } else if (_chargeType != 'Other' &&
        (ref.read(tenantIncomeAccountsProvider).hasError ||
            resolvedAccount == null)) {
      valid = false;
      _error = 'The matching income category is unavailable.';
    }
    if (!valid) {
      setState(() {});
      return;
    }
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final result = await ref
          .read(tenantLedgerRepositoryProvider)
          .postCharge(
            widget.tenantAccountId,
            PostTenantChargeInput(
              amount: double.parse(_amount.text.trim()),
              effectiveOn: _effectiveOn,
              dueOn: _dueOn,
              description: _description.text.trim(),
              incomeLedgerAccountId: resolvedAccount?.id,
              servicePeriodStartOn: _servicePeriodStartOn,
              servicePeriodEndOn: _servicePeriodEndOn,
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
    final accountsAsync = ref.watch(tenantIncomeAccountsProvider);
    final resolvedAccount = _resolvedAccount(accountsAsync.value ?? const []);
    final bottom = MediaQuery.viewInsetsOf(context).bottom;
    return MobileQuickActionHider(
      child: SingleChildScrollView(
        padding: EdgeInsets.fromLTRB(20, 4, 20, 24 + bottom),
        child: Form(
          key: _formKey,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'Add one-time charge',
                      style: Theme.of(context).textTheme.titleLarge,
                    ),
                  ),
                  const AccountingHelpTipButton(
                    topic: AccountingHelpTopic.oneTimeCharge,
                  ),
                ],
              ),
              const SizedBox(height: 4),
              const Text('Create a real charge on this tenant account.'),
              const SizedBox(height: 16),
              DropdownButtonFormField<String>(
                key: const Key('one-time-charge-type'),
                initialValue: _chargeType,
                decoration: const InputDecoration(
                  labelText: 'What is this charge for?',
                ),
                items: [
                  for (final type in tenantChargeTypeOptions)
                    DropdownMenuItem(value: type, child: Text(type)),
                ],
                onChanged: _saving
                    ? null
                    : (value) {
                        if (value != null) {
                          setState(() {
                            _chargeType = value;
                            if (value != 'Other') _categoryAccountId = null;
                          });
                        }
                      },
              ),
              const SizedBox(height: 12),
              TextFormField(
                key: const Key('one-time-charge-amount'),
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
              _DateField(
                label: 'Effective date',
                value: _effectiveOn,
                datesLoaded: _datesLoaded,
                onTap: () => _pickDate(dueDate: false),
              ),
              _DateField(
                label: 'Due date',
                value: _dueOn,
                datesLoaded: _datesLoaded,
                onTap: () => _pickDate(dueDate: true),
              ),
              const SizedBox(height: 8),
              Text(
                'Service period (optional)',
                style: Theme.of(context).textTheme.titleSmall,
              ),
              Row(
                children: [
                  Expanded(
                    child: _OptionalDateField(
                      label: 'Start',
                      value: _servicePeriodStartOn,
                      onTap: () => _pickServiceDate(endDate: false),
                      onClear: () =>
                          setState(() => _servicePeriodStartOn = null),
                    ),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: _OptionalDateField(
                      label: 'End',
                      value: _servicePeriodEndOn,
                      onTap: () => _pickServiceDate(endDate: true),
                      onClear: () => setState(() => _servicePeriodEndOn = null),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 12),
              TextFormField(
                key: const Key('one-time-charge-description'),
                controller: _description,
                decoration: const InputDecoration(
                  labelText: 'Description',
                  hintText: 'January water bill',
                ),
                validator: (value) => value == null || value.trim().isEmpty
                    ? 'Describe this charge.'
                    : null,
              ),
              const SizedBox(height: 12),
              if (_chargeType == 'Other')
                _IncomeAccountDropdown(
                  accounts: accountsAsync,
                  selectedAccount: resolvedAccount,
                  label: 'Category',
                  onChanged: (value) =>
                      setState(() => _categoryAccountId = value),
                )
              else
                Text(
                  accountsAsync.isLoading
                      ? 'The matching income category is loading.'
                      : resolvedAccount == null
                      ? 'The matching income category is unavailable.'
                      : 'This charge uses the matching income category automatically.',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              const SizedBox(height: 12),
              OutlinedButton.icon(
                icon: const Icon(Icons.attach_file_outlined),
                onPressed: _saving
                    ? null
                    : () => setState(
                        () => _documentLabel = 'Supporting document selected',
                      ),
                label: Text(_documentLabel ?? 'Supporting document (optional)'),
              ),
              if (_error != null) ...[
                const SizedBox(height: 12),
                Text(
                  _error!,
                  key: const Key('one-time-charge-error'),
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
              ],
              const SizedBox(height: 16),
              FilledButton(
                key: const Key('one-time-charge-submit'),
                onPressed: _saving ? null : _submit,
                child: Text(_saving ? 'Adding…' : 'Add charge'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _IncomeAccountDropdown extends StatefulWidget {
  const _IncomeAccountDropdown({
    required this.accounts,
    required this.selectedAccount,
    required this.label,
    required this.onChanged,
  });

  final AsyncValue<List<ChartOfAccountsRow>> accounts;
  final ChartOfAccountsRow? selectedAccount;
  final String label;
  final ValueChanged<int?> onChanged;

  @override
  State<_IncomeAccountDropdown> createState() => _IncomeAccountDropdownState();
}

class _IncomeAccountDropdownState extends State<_IncomeAccountDropdown> {
  int? _selectedId;

  @override
  Widget build(BuildContext context) => widget.accounts.when(
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
    data: (accounts) => DropdownButtonFormField<int>(
      key: const Key('one-time-charge-category'),
      initialValue: _selectedId ?? widget.selectedAccount?.id,
      decoration: InputDecoration(labelText: widget.label),
      items: [
        for (final account in accounts)
          DropdownMenuItem(
            value: account.id,
            child: Text('${account.code} · ${account.name}'),
          ),
      ],
      onChanged: (value) {
        setState(() => _selectedId = value);
        widget.onChanged(value);
      },
      validator: (value) => value == null ? 'Choose an income category.' : null,
    ),
  );
}

class _DateField extends StatelessWidget {
  const _DateField({
    required this.label,
    required this.value,
    required this.datesLoaded,
    required this.onTap,
  });

  final String label;
  final DateTime value;
  final bool datesLoaded;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => ListTile(
    contentPadding: EdgeInsets.zero,
    title: Text(label),
    subtitle: Text(datesLoaded ? dateFmt(value) : 'Loading…'),
    trailing: const Icon(Icons.calendar_today_outlined),
    onTap: onTap,
  );
}

class _OptionalDateField extends StatelessWidget {
  const _OptionalDateField({
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
