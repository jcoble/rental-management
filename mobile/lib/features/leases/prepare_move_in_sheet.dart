import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import '../applications/applications_models.dart';
import '../applications/applications_repository.dart';
import 'leases_repository.dart';

Future<PrepareMoveInResult?> showPrepareMoveInSheet(
  BuildContext context, {
  required WidgetRef ref,
  RentalApplication? application,
}) async {
  final businessDate = await ref
      .read(leaseManagementsRepositoryProvider)
      .prepareMoveInBusinessDate();
  if (!context.mounted) return null;
  return showModalBottomSheet<PrepareMoveInResult>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    builder: (_) => _PrepareMoveInSheet(
      initialApplication: application,
      businessDate: businessDate,
    ),
  );
}

class _PrepareMoveInSheet extends ConsumerStatefulWidget {
  const _PrepareMoveInSheet({
    required this.businessDate,
    this.initialApplication,
  });
  final RentalApplication? initialApplication;
  final DateTime businessDate;

  @override
  ConsumerState<_PrepareMoveInSheet> createState() =>
      _PrepareMoveInSheetState();
}

class _PrepareMoveInSheetState extends ConsumerState<_PrepareMoveInSheet> {
  static const _pageSize = 20;
  final _applicationSearch = TextEditingController();
  final _templateSearch = TextEditingController();
  final _rent = TextEditingController();
  final _dueDay = TextEditingController(text: '1');
  final _deposit = TextEditingController(text: '0');
  final _lateFee = TextEditingController(text: '0');
  final _grace = TextEditingController(text: '5');
  final _openingBalance = TextEditingController();
  final _openingNote = TextEditingController();

  RentalApplication? _application;
  LeaseTemplateOption? _template;
  int _applicationSkip = 0;
  int _templateSkip = 0;
  String? _applicationQuery;
  String? _templateQuery;
  String _role = 'PrimaryTenant';
  String _termType = 'FixedTerm';
  bool _createDepositAccount = true;
  bool _includeOpeningBalance = false;
  late DateTime _partyEffectiveFrom;
  late DateTime _termStart;
  DateTime? _termEnd;
  DateTime? _plannedPossession;
  DateTime? _openingEffectiveOn;
  bool _saving = false;
  String? _error;
  String? _operationKey;

  @override
  void initState() {
    super.initState();
    _partyEffectiveFrom = DateUtils.dateOnly(widget.businessDate);
    _termStart = DateUtils.dateOnly(widget.businessDate);
    _application = widget.initialApplication;
    final desired = widget.initialApplication?.desiredMoveInDate;
    if (desired != null) {
      _plannedPossession = desired;
      _partyEffectiveFrom = DateUtils.dateOnly(desired);
      _termStart = DateUtils.dateOnly(desired);
    }
  }

  @override
  void dispose() {
    for (final controller in [
      _applicationSearch,
      _templateSearch,
      _rent,
      _dueDay,
      _deposit,
      _lateFee,
      _grace,
      _openingBalance,
      _openingNote,
    ]) {
      controller.dispose();
    }
    super.dispose();
  }

  bool get _applicationReady =>
      _application?.status == 'Approved' &&
      (_application?.approvedTenantId ?? 0) > 0 &&
      (_application?.unitId ?? 0) > 0;

  Future<void> _pickDate({
    required DateTime initial,
    required ValueChanged<DateTime> onPicked,
  }) async {
    final value = await showDatePicker(
      context: context,
      initialDate: initial,
      firstDate: DateTime(2000),
      lastDate: DateTime(2100),
    );
    if (value != null && mounted) setState(() => onPicked(value));
  }

  Future<void> _save() async {
    final app = _application;
    final tenantId = app?.approvedTenantId;
    final unitId = app?.unitId;
    final template = _template;
    if (app == null || tenantId == null || unitId == null || template == null) {
      setState(
        () => _error =
            'Choose an approved application and an active lease template.',
      );
      return;
    }
    final opening = _includeOpeningBalance
        ? double.tryParse(_openingBalance.text.trim())
        : null;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final result = await ref
          .read(leaseManagementsRepositoryProvider)
          .prepareMoveIn(
            PrepareMoveInInput(
              applicationId: app.id,
              unitId: unitId,
              plannedPossessionAtUtc: _plannedPossession,
              partyEffectiveFrom: _partyEffectiveFrom,
              parties: [
                PrepareMoveInPartyInput(
                  tenantId: tenantId,
                  role: _role,
                  isAgreementSigner: true,
                  signingOrder: 1,
                ),
              ],
              documentTemplateId: template.id,
              termType: _termType,
              termStartOn: _termStart,
              termEndOn: _termType == 'FixedTerm' ? _termEnd : null,
              baseRentAmount: double.parse(_rent.text.trim()),
              rentDueDay: int.parse(_dueDay.text.trim()),
              securityDepositObligation: double.parse(_deposit.text.trim()),
              lateFeeAmount: double.parse(_lateFee.text.trim()),
              gracePeriodDays: int.parse(_grace.text.trim()),
              createSecurityDepositAccount: _createDepositAccount,
              openingBalanceAmount: opening,
              openingBalanceEffectiveOn: _includeOpeningBalance
                  ? _openingEffectiveOn
                  : null,
              openingBalanceNote:
                  _includeOpeningBalance && _openingNote.text.trim().isNotEmpty
                  ? _openingNote.text.trim()
                  : null,
            ),
            operationKey: _operationKey ??=
                LeaseManagementsRepository.newOperationKey(),
          );
      ref.invalidate(leaseManagementsPageProvider);
      if (mounted) Navigator.of(context).pop(result);
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
      rethrow;
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return TabbedFormSheet(
      title: 'Prepare move-in',
      saveLabel: 'Prepare move-in',
      saving: _saving,
      error: _error,
      onSave: _save,
      tabs: [
        TabbedFormStepSpec(
          label: 'Application',
          isComplete: () => _applicationReady,
          validate: () => _applicationReady,
          child: _applicationStep(),
        ),
        TabbedFormStepSpec(
          label: 'Household',
          isComplete: () => _applicationReady && _role == 'PrimaryTenant',
          validate: () => _applicationReady && _role == 'PrimaryTenant',
          child: _householdStep(),
        ),
        TabbedFormStepSpec(
          label: 'Agreement',
          validate: () =>
              _template != null &&
              (_termType != 'FixedTerm' || _termEnd != null),
          child: _agreementStep(),
        ),
        TabbedFormStepSpec(
          label: 'Money',
          validate: () =>
              !_includeOpeningBalance || _openingEffectiveOn != null,
          child: _moneyStep(),
        ),
      ],
    );
  }

  Widget _applicationStep() {
    if (widget.initialApplication != null)
      return _applicationSummary(widget.initialApplication!);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const Text(
          'Choose an approved application. Results stay server-filtered and paged.',
        ),
        const SizedBox(height: 12),
        TextField(
          controller: _applicationSearch,
          decoration: InputDecoration(
            labelText: 'Search approved applications',
            suffixIcon: IconButton(
              icon: const Icon(Icons.search),
              onPressed: () => setState(() {
                _applicationQuery = _applicationSearch.text.trim();
                _applicationSkip = 0;
              }),
            ),
          ),
          onSubmitted: (value) => setState(() {
            _applicationQuery = value.trim();
            _applicationSkip = 0;
          }),
        ),
        const SizedBox(height: 12),
        FutureBuilder<ApplicationListPage>(
          future: ref
              .read(applicationsRepositoryProvider)
              .listPage(
                ApplicationListQuery(
                  status: 'Approved',
                  search: _applicationQuery,
                  skip: _applicationSkip,
                  take: _pageSize,
                ),
              ),
          builder: (context, snapshot) {
            if (snapshot.hasError) {
              return const Text('Unable to load approved applications.');
            }
            if (!snapshot.hasData)
              return const Center(child: CircularProgressIndicator());
            final page = snapshot.data!;
            return Column(
              children: [
                for (final app in page.items)
                  RadioListTile<int>(
                    value: app.id,
                    groupValue: _application?.id,
                    title: Text(app.fullName),
                    subtitle: Text(
                      'Application #${app.id} · Unit #${app.unitId ?? 0}',
                    ),
                    onChanged:
                        app.approvedTenantId == null || app.unitId == null
                        ? null
                        : (_) => setState(() => _application = app),
                  ),
                Row(
                  mainAxisAlignment: MainAxisAlignment.spaceBetween,
                  children: [
                    TextButton(
                      onPressed: page.hasPrevious
                          ? () => setState(
                              () => _applicationSkip =
                                  (_applicationSkip - _pageSize).clamp(
                                    0,
                                    _applicationSkip,
                                  ),
                            )
                          : null,
                      child: const Text('Previous'),
                    ),
                    Text(
                      '${page.skip + 1}-${page.skip + page.items.length} of ${page.totalCount}',
                    ),
                    TextButton(
                      onPressed: page.hasNext
                          ? () => setState(() => _applicationSkip += _pageSize)
                          : null,
                      child: const Text('Next'),
                    ),
                  ],
                ),
              ],
            );
          },
        ),
        if (_application != null) _applicationSummary(_application!),
      ],
    );
  }

  Widget _applicationSummary(RentalApplication app) => Card(
    child: ListTile(
      leading: const Icon(Icons.verified_user_outlined),
      title: Text(app.fullName),
      subtitle: Text(
        'Application #${app.id} · Approved tenant #${app.approvedTenantId ?? 0} · Unit #${app.unitId ?? 0}',
      ),
    ),
  );

  Widget _householdStep() {
    final application = _application;
    if (application == null) {
      return const Text('Choose an approved application first.');
    }

    return Column(
      children: [
        _applicationSummary(application),
        DropdownButtonFormField<String>(
          value: _role,
          decoration: const InputDecoration(labelText: 'Household role'),
          items: const [
            DropdownMenuItem(
              value: 'PrimaryTenant',
              child: Text('Primary tenant'),
            ),
          ],
          onChanged: (value) =>
              setState(() => _role = value ?? 'PrimaryTenant'),
        ),
        const ListTile(
          contentPadding: EdgeInsets.zero,
          title: Text('Agreement signer'),
          subtitle: Text(
            'The approved primary tenant is a required signer. Non-signing occupants are added separately as household parties.',
          ),
          trailing: Icon(Icons.verified_outlined),
        ),
        ListTile(
          title: const Text('Unit'),
          subtitle: Text(
            'Unit #${application.unitId ?? 0} (from approved application)',
          ),
        ),
      ],
    );
  }

  Widget _agreementStep() => Column(
    children: [
      TextField(
        controller: _templateSearch,
        decoration: InputDecoration(
          labelText: 'Search active lease templates',
          suffixIcon: IconButton(
            icon: const Icon(Icons.search),
            onPressed: () => setState(() {
              _templateQuery = _templateSearch.text.trim();
              _templateSkip = 0;
            }),
          ),
        ),
        onSubmitted: (value) => setState(() {
          _templateQuery = value.trim();
          _templateSkip = 0;
        }),
      ),
      FutureBuilder<LeaseTemplateOptionPage>(
        future: ref
            .read(leaseManagementsRepositoryProvider)
            .leaseTemplatesPage(
              skip: _templateSkip,
              take: _pageSize,
              search: _templateQuery,
              propertyId: _application?.propertyId,
            ),
        builder: (context, snapshot) {
          if (snapshot.hasError) {
            return const Text('Unable to load active lease templates.');
          }
          if (!snapshot.hasData)
            return const Padding(
              padding: EdgeInsets.all(16),
              child: CircularProgressIndicator(),
            );
          final page = snapshot.data!;
          return Column(
            children: [
              DropdownButtonFormField<int>(
                value: page.items.any((item) => item.id == _template?.id)
                    ? _template?.id
                    : null,
                decoration: const InputDecoration(labelText: 'Lease template'),
                items: [
                  for (final item in page.items)
                    DropdownMenuItem(value: item.id, child: Text(item.name)),
                ],
                onChanged: (id) {
                  LeaseTemplateOption? selected;
                  for (final item in page.items) {
                    if (item.id == id) {
                      selected = item;
                      break;
                    }
                  }
                  setState(() => _template = selected);
                },
              ),
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  TextButton(
                    onPressed: page.hasPrevious
                        ? () => setState(
                            () => _templateSkip = (_templateSkip - _pageSize)
                                .clamp(0, _templateSkip),
                          )
                        : null,
                    child: const Text('Previous'),
                  ),
                  Text(
                    '${page.skip + 1}-${page.skip + page.items.length} of ${page.totalCount}',
                  ),
                  TextButton(
                    onPressed: page.hasNext
                        ? () => setState(() => _templateSkip += _pageSize)
                        : null,
                    child: const Text('Next'),
                  ),
                ],
              ),
            ],
          );
        },
      ),
      DropdownButtonFormField<String>(
        value: _termType,
        decoration: const InputDecoration(labelText: 'Term type'),
        items: const [
          DropdownMenuItem(value: 'FixedTerm', child: Text('Fixed term')),
          DropdownMenuItem(
            value: 'MonthToMonth',
            child: Text('Month to month'),
          ),
        ],
        onChanged: (value) => setState(() {
          _termType = value ?? 'FixedTerm';
          if (_termType == 'MonthToMonth') _termEnd = null;
        }),
      ),
      _dateTile(
        'Planned possession',
        _plannedPossession,
        (value) => _plannedPossession = value,
        optional: true,
      ),
      _dateTile(
        'Party effective date',
        _partyEffectiveFrom,
        (value) => _partyEffectiveFrom = value,
      ),
      _dateTile('Term start', _termStart, (value) => _termStart = value),
      if (_termType == 'FixedTerm')
        _dateTile('Term end', _termEnd, (value) => _termEnd = value),
    ],
  );

  Widget _moneyStep() => Column(
    children: [
      _number(_rent, 'Monthly rent', min: 0),
      _number(_dueDay, 'Rent due day', min: 1, max: 31, integer: true),
      _number(_deposit, 'Security deposit obligation', min: 0),
      _number(_lateFee, 'Late fee', min: 0),
      _number(_grace, 'Grace period days', min: 0, max: 31, integer: true),
      SwitchListTile(
        title: const Text('Create security deposit account'),
        value: _createDepositAccount,
        onChanged: (value) => setState(() => _createDepositAccount = value),
      ),
      SwitchListTile(
        title: const Text('Add opening balance'),
        value: _includeOpeningBalance,
        onChanged: (value) => setState(() => _includeOpeningBalance = value),
      ),
      if (_includeOpeningBalance) ...[
        _number(
          _openingBalance,
          'Opening balance (positive owed, negative credit)',
          nonZero: true,
        ),
        _dateTile(
          'Opening balance effective date',
          _openingEffectiveOn,
          (value) => _openingEffectiveOn = value,
        ),
        TextFormField(
          controller: _openingNote,
          decoration: const InputDecoration(
            labelText: 'Opening balance note (optional)',
          ),
          maxLength: 500,
        ),
      ],
    ],
  );

  Widget _number(
    TextEditingController controller,
    String label, {
    double? min,
    double? max,
    bool integer = false,
    bool nonZero = false,
  }) => Padding(
    padding: const EdgeInsets.only(bottom: 10),
    child: TextFormField(
      controller: controller,
      decoration: InputDecoration(labelText: label),
      keyboardType: const TextInputType.numberWithOptions(
        decimal: true,
        signed: true,
      ),
      validator: (value) {
        final number = integer
            ? int.tryParse(value ?? '')
            : double.tryParse(value ?? '');
        if (number == null ||
            (min != null && number < min) ||
            (max != null && number > max) ||
            (nonZero && number == 0))
          return 'Enter a valid value';
        return null;
      },
    ),
  );

  Widget _dateTile(
    String label,
    DateTime? value,
    ValueChanged<DateTime> onPicked, {
    bool optional = false,
  }) => ListTile(
    contentPadding: EdgeInsets.zero,
    title: Text(label),
    subtitle: Text(
      value == null
          ? (optional ? 'Not set' : 'Required')
          : value.toIso8601String().split('T').first,
    ),
    trailing: const Icon(Icons.calendar_today_outlined),
    onTap: () => _pickDate(
      initial: value ?? DateUtils.dateOnly(widget.businessDate),
      onPicked: onPicked,
    ),
  );
}
