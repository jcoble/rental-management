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
  final _rent = TextEditingController();
  final _dueDay = TextEditingController(text: '1');
  final _deposit = TextEditingController(text: '0');
  final _lateFee = TextEditingController(text: '0');
  final _grace = TextEditingController(text: '5');
  final _openingBalance = TextEditingController();
  final _openingNote = TextEditingController();

  RentalApplication? _application;
  LeaseTemplateOption? _template;
  bool _templatePreselected = false;
  int _applicationSkip = 0;
  String? _applicationQuery;
  String _termType = 'FixedTerm';
  String _rentTrackingStartMode = 'ForwardOnly';
  bool _createDepositAccount = true;
  bool _includeOpeningBalance = false;
  late DateTime _partyEffectiveFrom;
  late DateTime _termStart;
  DateTime? _termEnd;
  DateTime? _plannedPossession;
  DateTime? _rentTrackingStartOn;
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
    _termEnd = _oneYearFrom(_termStart);
  }

  static DateTime _oneYearFrom(DateTime start) => DateUtils.addDaysToDate(
    DateTime(start.year + 1, start.month, start.day),
    -1,
  );

  @override
  void dispose() {
    for (final controller in [
      _applicationSearch,
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
    if (app == null || tenantId == null || unitId == null) return;
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
                  role: 'PrimaryTenant',
                  isAgreementSigner: true,
                  signingOrder: 1,
                ),
              ],
              documentTemplateId: _template?.id,
              termType: _termType,
              termStartOn: _termStart,
              termEndOn: _termType == 'FixedTerm' ? _termEnd : null,
              baseRentAmount: double.parse(_rent.text.trim()),
              rentDueDay: int.parse(_dueDay.text.trim()),
              securityDepositObligation: double.parse(_deposit.text.trim()),
              lateFeeAmount: double.parse(_lateFee.text.trim()),
              gracePeriodDays: int.parse(_grace.text.trim()),
              rentTrackingStartMode: _rentTrackingStartMode,
              rentTrackingStartOn: _rentTrackingStartMode == 'CustomCutoffDate'
                  ? _rentTrackingStartOn
                  : null,
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
      saveLabel: 'Send lease to sign',
      saving: _saving,
      error: _error,
      onSave: _save,
      tabs: [
        TabbedFormStepSpec(label: 'Review', child: _reviewPage()),
      ],
    );
  }

  Widget _reviewPage() => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      _heading('Who is moving in'),
      _applicationField(),
      const SizedBox(height: 16),
      _heading('Which rental'),
      Text(
        _application?.unitId == null
            ? 'Comes from the application you pick above.'
            : 'Unit #${_application!.unitId}',
      ),
      const SizedBox(height: 16),
      _heading('Lease dates'),
      DropdownButtonFormField<String>(
        key: const Key('move-in-term-type'),
        isExpanded: true,
        initialValue: _termType,
        decoration: const InputDecoration(labelText: 'How long is the lease?'),
        items: const [
          DropdownMenuItem(
            value: 'FixedTerm',
            child: Text('Set number of months'),
          ),
          DropdownMenuItem(
            value: 'MonthToMonth',
            child: Text('Month to month'),
          ),
        ],
        onChanged: (value) => setState(() {
          _termType = value ?? 'FixedTerm';
          _termEnd = _termType == 'FixedTerm' ? _oneYearFrom(_termStart) : null;
        }),
      ),
      const SizedBox(height: 8),
      _dateField(
        label: 'Lease starts',
        value: _termStart,
        onPicked: (value) {
          _termStart = value;
          if (_termType == 'FixedTerm') _termEnd = _oneYearFrom(value);
        },
        missingMessage: 'Pick the day the lease starts',
      ),
      if (_termType == 'FixedTerm')
        _dateField(
          label: 'Lease ends',
          value: _termEnd,
          onPicked: (value) => _termEnd = value,
          missingMessage: 'Pick the day the lease ends',
        ),
      const SizedBox(height: 16),
      _heading('Rent'),
      _number(
        _rent,
        'Monthly rent',
        fieldKey: const Key('move-in-rent'),
        min: 0,
        message: 'Enter the monthly rent',
      ),
      _number(
        _dueDay,
        'Rent is due on day',
        min: 1,
        max: 31,
        integer: true,
        message: 'Enter a day from 1 to 31',
      ),
      const SizedBox(height: 8),
      _heading('Deposit'),
      _number(
        _deposit,
        'Security deposit',
        min: 0,
        message: 'Enter the deposit amount, or 0 for none',
      ),
      const SizedBox(height: 8),
      _heading('Lease form'),
      _templateField(),
      const SizedBox(height: 8),
      MoreDetailsSection(children: _moreDetails()),
      _backfillSection(),
    ],
  );

  Widget _heading(String text) => Padding(
    padding: const EdgeInsets.only(bottom: 6),
    child: Text(
      text,
      style: Theme.of(context).textTheme.titleSmall?.copyWith(
        fontWeight: FontWeight.w700,
      ),
    ),
  );

  List<Widget> _moreDetails() => [
    _number(
      _lateFee,
      'Late fee',
      min: 0,
      message: 'Enter the late fee, or 0 for none',
    ),
    _number(
      _grace,
      'Days before rent counts as late',
      min: 0,
      max: 31,
      integer: true,
      message: 'Enter a number of days from 0 to 31',
    ),
    _dateField(
      label: 'Keys handed over',
      value: _plannedPossession,
      onPicked: (value) => _plannedPossession = value,
      optional: true,
    ),
    _dateField(
      label: 'Tenant is on the lease from',
      value: _partyEffectiveFrom,
      onPicked: (value) => _partyEffectiveFrom = value,
      missingMessage: 'Pick the day the tenant joins the lease',
    ),
    SwitchListTile(
      contentPadding: EdgeInsets.zero,
      title: const Text('Hold the deposit in its own account'),
      value: _createDepositAccount,
      onChanged: (value) => setState(() => _createDepositAccount = value),
    ),
  ];

  Widget _backfillSection() => Theme(
    data: Theme.of(context).copyWith(dividerColor: Colors.transparent),
    child: ExpansionTile(
      key: const Key('move-in-backfill-section'),
      tilePadding: EdgeInsets.zero,
      childrenPadding: const EdgeInsets.only(top: 4, bottom: 8),
      expandedCrossAxisAlignment: CrossAxisAlignment.stretch,
      title: Text(
        'Moving an existing lease into Rental Command?',
        style: Theme.of(context).textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w700,
        ),
      ),
      children: [
        DropdownButtonFormField<String>(
          key: const Key('move-in-rent-tracking'),
          isExpanded: true,
          initialValue: _rentTrackingStartMode,
          decoration: const InputDecoration(labelText: 'Start charging rent'),
          items: const [
            DropdownMenuItem(
              value: 'ForwardOnly',
              child: Text('From today'),
            ),
            DropdownMenuItem(
              value: 'BackfillFromLeaseStart',
              child: Text('From the lease start, catching up'),
            ),
            DropdownMenuItem(
              value: 'CustomCutoffDate',
              child: Text('From a date I pick'),
            ),
          ],
          onChanged: (value) => setState(() {
            _rentTrackingStartMode = value ?? 'ForwardOnly';
            if (_rentTrackingStartMode != 'CustomCutoffDate') {
              _rentTrackingStartOn = null;
            }
          }),
        ),
        if (_rentTrackingStartMode == 'CustomCutoffDate')
          _dateField(
            label: 'Start charging rent on',
            value: _rentTrackingStartOn,
            onPicked: (value) => _rentTrackingStartOn = value,
            missingMessage: 'Pick the day rent starts',
          ),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('They already owe or have a credit'),
          value: _includeOpeningBalance,
          onChanged: (value) => setState(() => _includeOpeningBalance = value),
        ),
        if (_includeOpeningBalance) ...[
          _number(
            _openingBalance,
            'Amount (positive if owed, negative if a credit)',
            nonZero: true,
            message: 'Enter an amount other than zero',
          ),
          _dateField(
            label: 'As of',
            value: _openingEffectiveOn,
            onPicked: (value) => _openingEffectiveOn = value,
            missingMessage: 'Pick the day this amount is as of',
          ),
          TextFormField(
            controller: _openingNote,
            decoration: const InputDecoration(
              labelText: 'Note (optional)',
            ),
            maxLength: 500,
          ),
        ],
      ],
    ),
  );

  Widget _applicationField() => FormField<int>(
    validator: (_) => _application?.approvedTenantId == null ||
            _application?.unitId == null
        ? 'Choose an approved application'
        : null,
    builder: (state) => Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (widget.initialApplication != null)
          _applicationSummary(widget.initialApplication!)
        else
          _applicationPicker(),
        if (state.hasError)
          Padding(
            padding: const EdgeInsets.only(top: 4),
            child: Text(
              state.errorText!,
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          ),
      ],
    ),
  );

  Widget _applicationPicker() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
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
          if (!snapshot.hasData) {
            return const Center(child: CircularProgressIndicator());
          }
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
                  onChanged: app.approvedTenantId == null || app.unitId == null
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
              if (_application != null) _applicationSummary(_application!),
            ],
          );
        },
      ),
    ],
  );

  Widget _applicationSummary(RentalApplication app) => Card(
    margin: EdgeInsets.zero,
    child: ListTile(
      leading: const Icon(Icons.verified_user_outlined),
      title: Text(app.fullName),
      subtitle: Text(
        'Application #${app.id} · Unit #${app.unitId ?? 0}',
      ),
    ),
  );

  Widget _templateField() => FutureBuilder<LeaseTemplateOptionPage>(
    future: ref
        .read(leaseManagementsRepositoryProvider)
        .leaseTemplatesPage(
          take: _pageSize,
          propertyId: _application?.propertyId,
        ),
    builder: (context, snapshot) {
      if (snapshot.hasError) {
        return const Text('Unable to load your lease forms.');
      }
      if (!snapshot.hasData) {
        return const Padding(
          padding: EdgeInsets.all(16),
          child: CircularProgressIndicator(),
        );
      }
      final page = snapshot.data!;
      if (!_templatePreselected && page.items.isNotEmpty) {
        _templatePreselected = true;
        _template = page.items.first;
      }
      return DropdownButtonFormField<int?>(
        key: ValueKey('move-in-template-${_template?.id}'),
        isExpanded: true,
        initialValue: page.items.any((item) => item.id == _template?.id)
            ? _template?.id
            : null,
        decoration: const InputDecoration(
          labelText: 'Which lease form?',
          helperText: 'The standard form works if you have not made your own',
        ),
        items: [
          const DropdownMenuItem<int?>(
            value: null,
            child: Text('Standard lease form'),
          ),
          for (final item in page.items)
            DropdownMenuItem<int?>(value: item.id, child: Text(item.name)),
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
      );
    },
  );

  Widget _number(
    TextEditingController controller,
    String label, {
    Key? fieldKey,
    double? min,
    double? max,
    bool integer = false,
    bool nonZero = false,
    String message = 'Enter a valid value',
  }) => Padding(
    padding: const EdgeInsets.only(bottom: 10),
    child: TextFormField(
      key: fieldKey,
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
            (nonZero && number == 0)) {
          return message;
        }
        return null;
      },
    ),
  );

  Widget _dateField({
    required String label,
    required DateTime? value,
    required ValueChanged<DateTime> onPicked,
    bool optional = false,
    String missingMessage = 'Pick a date',
  }) => FormField<DateTime>(
    validator: (_) => optional || value != null ? null : missingMessage,
    builder: (state) => Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        ListTile(
          contentPadding: EdgeInsets.zero,
          title: Text(label),
          subtitle: Text(
            value == null
                ? (optional ? 'Not set' : 'Not picked yet')
                : value.toIso8601String().split('T').first,
          ),
          trailing: const Icon(Icons.calendar_today_outlined),
          onTap: () => _pickDate(
            initial: value ?? DateUtils.dateOnly(widget.businessDate),
            onPicked: onPicked,
          ),
        ),
        if (state.hasError)
          Text(
            state.errorText!,
            style: TextStyle(color: Theme.of(context).colorScheme.error),
          ),
      ],
    ),
  );
}
