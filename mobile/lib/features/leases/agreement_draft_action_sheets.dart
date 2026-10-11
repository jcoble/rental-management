import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/lease.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import 'leases_repository.dart';

Future<LeaseSuccessorDraftResult?> showEditAgreementDraftSheet(
  BuildContext context, {
  required LeaseAgreementDraftDetail draft,
  required String propertyName,
  required String unitNumber,
  LeaseAgreementHistory? source,
}) => showModalBottomSheet<LeaseSuccessorDraftResult>(
  context: context,
  isScrollControlled: true,
  useSafeArea: true,
  builder: (_) => _EditAgreementDraftSheet(
    draft: draft,
    propertyName: propertyName,
    unitNumber: unitNumber,
    source: source,
  ),
);

Future<IssueAgreementResult?> showIssueAgreementSheet(
  BuildContext context, {
  required LeaseAgreementDraftDetail draft,
  required String propertyName,
  required String unitNumber,
}) => showModalBottomSheet<IssueAgreementResult>(
  context: context,
  isScrollControlled: true,
  useSafeArea: true,
  builder: (_) => _IssueAgreementSheet(
    draft: draft,
    propertyName: propertyName,
    unitNumber: unitNumber,
  ),
);

class _EditAgreementDraftSheet extends ConsumerStatefulWidget {
  const _EditAgreementDraftSheet({
    required this.draft,
    required this.propertyName,
    required this.unitNumber,
    required this.source,
  });

  final LeaseAgreementDraftDetail draft;
  final String propertyName;
  final String unitNumber;
  final LeaseAgreementHistory? source;

  @override
  ConsumerState<_EditAgreementDraftSheet> createState() =>
      _EditAgreementDraftSheetState();
}

class _EditAgreementDraftSheetState
    extends ConsumerState<_EditAgreementDraftSheet> {
  late final TextEditingController _agreementNumber;
  late final TextEditingController _rent;
  late final TextEditingController _dueDay;
  late final TextEditingController _deposit;
  late final TextEditingController _lateFee;
  late final TextEditingController _grace;
  late String _termType;
  late DateTime _termStart;
  DateTime? _termEnd;
  late DateTime _governingFrom;
  bool _saving = false;
  String? _error;
  String? _operationKey;

  @override
  void initState() {
    super.initState();
    final draft = widget.draft;
    _agreementNumber = TextEditingController(text: draft.agreementNumber);
    _rent = TextEditingController(text: _numberText(draft.baseRentAmount));
    _dueDay = TextEditingController(text: '${draft.rentDueDay}');
    _deposit = TextEditingController(
      text: _numberText(draft.securityDepositObligation),
    );
    _lateFee = TextEditingController(text: _numberText(draft.lateFeeAmount));
    _grace = TextEditingController(text: '${draft.gracePeriodDays}');
    for (final controller in [
      _agreementNumber,
      _rent,
      _dueDay,
      _deposit,
      _lateFee,
      _grace,
    ]) {
      controller.addListener(_draftChanged);
    }
    _termType = draft.termType;
    _termStart = draft.termStartOn;
    _termEnd = draft.termEndOn;
    _governingFrom = draft.governingFromOn;
  }

  void _draftChanged() {
    if (mounted) setState(() {});
  }

  @override
  void dispose() {
    for (final controller in [
      _agreementNumber,
      _rent,
      _dueDay,
      _deposit,
      _lateFee,
      _grace,
    ]) {
      controller.dispose();
    }
    super.dispose();
  }

  bool get _datesValid =>
      !_governingFrom.isBefore(_termStart) &&
      (_termType == 'MonthToMonth'
          ? _termEnd == null
          : _termEnd != null &&
                !_termEnd!.isBefore(_termStart) &&
                !_governingFrom.isAfter(_termEnd!));

  bool get _draftShapeValid =>
      widget.draft.draftRevision > 0 &&
      widget.draft.documentTemplateId != null &&
      widget.draft.termsSchemaVersion > 0 &&
      widget.draft.hasExactOrderedSigners;

  Future<void> _save() async {
    final templateId = widget.draft.documentTemplateId;
    if (templateId == null || !_draftShapeValid || !_datesValid) {
      setState(() => _error = 'The draft is missing exact editable state.');
      return;
    }
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final result = await ref
          .read(leaseManagementsRepositoryProvider)
          .editAgreementDraft(
            leaseManagementId: widget.draft.leaseManagementId,
            leaseAgreementId: widget.draft.leaseAgreementId,
            operationKey: _operationKey ??=
                LeaseManagementsRepository.newOperationKey(),
            input: EditAgreementDraftInput(
              draftRevision: widget.draft.draftRevision,
              agreementNumber: _agreementNumber.text.trim(),
              termType: _termType,
              termStartOn: _termStart,
              termEndOn: _termEnd,
              governingFromOn: _governingFrom,
              baseRentAmount: double.parse(_rent.text.trim()),
              rentDueDay: int.parse(_dueDay.text.trim()),
              securityDepositObligation: double.parse(_deposit.text.trim()),
              lateFeeAmount: double.parse(_lateFee.text.trim()),
              gracePeriodDays: int.parse(_grace.text.trim()),
              termsSchemaVersion: widget.draft.termsSchemaVersion,
              termsPayload: Map<String, dynamic>.from(
                widget.draft.termsPayload,
              ),
              documentTemplateId: templateId,
              signers: List<LeaseAgreementDraftSigner>.from(
                widget.draft.signers,
              ),
            ),
          );
      if (mounted) Navigator.of(context).pop(result);
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
      rethrow;
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) => TabbedFormSheet(
    title: widget.draft.changeType == 'Correction'
        ? 'Review correction draft'
        : 'Edit agreement draft',
    saveLabel: 'Save draft',
    saving: _saving,
    error: _error,
    onSave: _save,
    tabs: [
      TabbedFormStepSpec(
        label: 'Agreement',
        validate: () => _datesValid,
        child: _agreementStep(),
      ),
      TabbedFormStepSpec(label: 'Money', child: _moneyStep()),
      TabbedFormStepSpec(
        label: 'Signers',
        validate: () => widget.draft.hasExactOrderedSigners,
        child: _signersStep(widget.draft.signers),
      ),
      TabbedFormStepSpec(
        label: 'Review',
        validate: () => _datesValid && _draftShapeValid,
        child: _editReviewStep(),
      ),
    ],
  );

  Widget _agreementStep() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      _unitContext(widget.propertyName, widget.unitNumber),
      if (widget.draft.changeType == 'Correction') ...[
        Card(
          color: Theme.of(context).colorScheme.primaryContainer,
          child: Padding(
            padding: const EdgeInsets.all(12),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Text(
                  'The old agreement still governs',
                  style: TextStyle(fontWeight: FontWeight.w700),
                ),
                const SizedBox(height: 4),
                const Text(
                  'This replacement stays a draft until it is fully signed and executed.',
                ),
                if (widget.draft.correctionReason case final reason?) ...[
                  const SizedBox(height: 8),
                  Text('Reason: $reason'),
                ],
              ],
            ),
          ),
        ),
        const SizedBox(height: 12),
      ],
      TextFormField(
        controller: _agreementNumber,
        decoration: const InputDecoration(labelText: 'Agreement number'),
        maxLength: 100,
        validator: (value) => value == null || value.trim().isEmpty
            ? 'Agreement number is required'
            : null,
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
          _termType = value ?? _termType;
          if (_termType == 'MonthToMonth') _termEnd = null;
          if (_termType == 'FixedTerm' && _termEnd == null) {
            _termEnd = _termStart;
          }
        }),
      ),
      _DateTile(
        label: 'Term starts',
        value: _termStart,
        onChanged: (value) => setState(() => _termStart = value),
      ),
      if (_termType == 'FixedTerm')
        _DateTile(
          label: 'Term ends',
          value: _termEnd ?? _termStart,
          onChanged: (value) => setState(() => _termEnd = value),
        ),
      _DateTile(
        label: 'Lease start date',
        value: _governingFrom,
        onChanged: (value) => setState(() => _governingFrom = value),
      ),
      if (!_datesValid)
        Text(
          'Lease and term dates must stay within the selected term.',
          style: TextStyle(color: Theme.of(context).colorScheme.error),
        ),
    ],
  );

  Widget _moneyStep() => Column(
    children: [
      _number(_rent, 'Monthly rent', min: 0),
      _number(_dueDay, 'Rent due day', min: 1, max: 31, integer: true),
      _number(_deposit, 'Security deposit obligation', min: 0),
      _number(_lateFee, 'Late fee', min: 0),
      _number(_grace, 'Grace period days', min: 0, max: 31, integer: true),
    ],
  );

  Widget _editReviewStep() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      _unitContext(widget.propertyName, widget.unitNumber),
      _ReviewFact(
        label: 'Draft',
        value:
            '${widget.draft.agreementNumber} · v${widget.draft.versionNumber} · revision ${widget.draft.draftRevision}',
      ),
      _ReviewFact(label: 'Term', value: _termType),
      _ReviewFact(label: 'Rent', value: _rent.text),
      _ReviewFact(label: 'Signers', value: '${widget.draft.signers.length}'),
      if (widget.draft.changeType == 'Correction' && widget.source != null) ...[
        const SizedBox(height: 16),
        Text(
          'Old agreement vs correction',
          style: Theme.of(context).textTheme.titleSmall,
        ),
        const SizedBox(height: 4),
        const Text(
          'Changed fields stay visible. Unchanged copied content is collapsed.',
        ),
        const SizedBox(height: 8),
        ..._correctionComparison(context),
      ],
      const SizedBox(height: 12),
      const Text(
        'The template, structured terms, and who signs, in order, are preserved from the current draft.',
      ),
    ],
  );

  List<Widget> _correctionComparison(BuildContext context) {
    final source = widget.source!;
    final rows = <({String label, String oldValue, String newValue})>[
      (
        label: 'Agreement number',
        oldValue: source.agreementNumber,
        newValue: _agreementNumber.text,
      ),
      (label: 'Term type', oldValue: source.termType, newValue: _termType),
      (
        label: 'Term starts',
        oldValue: _date(source.termStartOn),
        newValue: _date(_termStart),
      ),
      (
        label: 'Term ends',
        oldValue: source.termEndOn == null
            ? 'Month-to-month'
            : _date(source.termEndOn!),
        newValue: _termEnd == null ? 'Month-to-month' : _date(_termEnd!),
      ),
      (
        label: 'Governs from',
        oldValue: _date(source.governingFromOn),
        newValue: _date(_governingFrom),
      ),
      (
        label: 'Monthly rent',
        oldValue: _numberText(source.baseRentAmount),
        newValue: _rent.text,
      ),
    ];
    final changed = rows.where((row) => row.oldValue != row.newValue).toList();
    final unchanged = rows
        .where((row) => row.oldValue == row.newValue)
        .toList();
    return [
      if (changed.isEmpty)
        const ListTile(
          contentPadding: EdgeInsets.zero,
          title: Text('No tracked agreement fields have changed yet.'),
        ),
      for (final row in changed)
        Card(
          color: Theme.of(context).colorScheme.primaryContainer,
          child: ListTile(
            title: Text(row.label),
            subtitle: Text('Old: ${row.oldValue}\nNew: ${row.newValue}'),
            isThreeLine: true,
          ),
        ),
      if (unchanged.isNotEmpty)
        ExpansionTile(
          tilePadding: EdgeInsets.zero,
          title: Text(
            '${unchanged.length} unchanged copied field${unchanged.length == 1 ? '' : 's'}',
          ),
          children: [
            for (final row in unchanged)
              ListTile(title: Text(row.label), subtitle: Text(row.newValue)),
          ],
        ),
    ];
  }
}

class _IssueAgreementSheet extends ConsumerStatefulWidget {
  const _IssueAgreementSheet({
    required this.draft,
    required this.propertyName,
    required this.unitNumber,
  });

  final LeaseAgreementDraftDetail draft;
  final String propertyName;
  final String unitNumber;

  @override
  ConsumerState<_IssueAgreementSheet> createState() =>
      _IssueAgreementSheetState();
}

class _IssueAgreementSheetState extends ConsumerState<_IssueAgreementSheet> {
  late final TextEditingController _subject;
  final String _prepareKey = LeaseManagementsRepository.newOperationKey();
  final String _issueKey = LeaseManagementsRepository.newOperationKey();
  AgreementIssuancePreparation? _preparation;
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _subject = TextEditingController(
      text: '${widget.draft.agreementNumber} for signature',
    );
  }

  @override
  void dispose() {
    _subject.dispose();
    super.dispose();
  }

  bool get _canIssue =>
      widget.draft.draftRevision > 0 &&
      widget.draft.documentSourceVersionId > 0 &&
      widget.draft.hasExactOrderedSigners &&
      _subject.text.trim().isNotEmpty;

  Future<void> _issue() async {
    if (!_canIssue) {
      setState(() => _error = 'The draft is missing exact issuance state.');
      return;
    }
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final repository = ref.read(leaseManagementsRepositoryProvider);
      final preparation = _preparation ??= await repository
          .prepareAgreementIssuance(
            leaseManagementId: widget.draft.leaseManagementId,
            leaseAgreementId: widget.draft.leaseAgreementId,
            draftRevision: widget.draft.draftRevision,
            operationKey: _prepareKey,
          );
      final result = await repository.issueAgreement(
        leaseManagementId: widget.draft.leaseManagementId,
        leaseAgreementId: widget.draft.leaseAgreementId,
        preparation: preparation,
        subject: _subject.text.trim(),
        operationKey: _issueKey,
      );
      if (mounted) Navigator.of(context).pop(result);
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
      rethrow;
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) => TabbedFormSheet(
    title: 'Issue for signature',
    saveLabel: 'Prepare and issue',
    saving: _saving,
    error: _error,
    onSave: _issue,
    tabs: [
      TabbedFormStepSpec(label: 'Draft', child: _draftStep()),
      TabbedFormStepSpec(
        label: 'Signers',
        validate: () => widget.draft.hasExactOrderedSigners,
        child: _signersStep(widget.draft.signers),
      ),
      TabbedFormStepSpec(
        label: 'Issue',
        validate: () => _canIssue,
        child: _issueStep(),
      ),
    ],
  );

  Widget _draftStep() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      _unitContext(widget.propertyName, widget.unitNumber),
      _ReviewFact(
        label: 'Agreement',
        value:
            '${widget.draft.agreementNumber} · v${widget.draft.versionNumber}',
      ),
      _ReviewFact(label: 'Revision', value: '${widget.draft.draftRevision}'),
      _ReviewFact(
        label: 'Source version',
        value: '${widget.draft.documentSourceVersionId}',
      ),
      const SizedBox(height: 12),
      const Text(
        'Preparation renders and fingerprints the current draft. Issuance then creates the management-side signature request.',
      ),
    ],
  );

  Widget _issueStep() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      _unitContext(widget.propertyName, widget.unitNumber),
      TextFormField(
        controller: _subject,
        decoration: const InputDecoration(labelText: 'Signature email subject'),
        maxLength: 200,
        validator: (value) => value == null || value.trim().isEmpty
            ? 'Subject is required'
            : null,
      ),
      const Text(
        'Signing happens through each recipient’s secure public token link. This management action does not sign on a tenant’s behalf.',
      ),
    ],
  );
}

Widget _signersStep(List<LeaseAgreementDraftSigner> signers) => Column(
  crossAxisAlignment: CrossAxisAlignment.start,
  children: [
    const Text(
      'Who signs, in order',
      style: TextStyle(fontWeight: FontWeight.w700),
    ),
    const SizedBox(height: 8),
    for (final signer in signers)
      ListTile(
        contentPadding: EdgeInsets.zero,
        leading: CircleAvatar(child: Text('${signer.signingOrder}')),
        title: Text(signer.nameSnapshot),
        subtitle: Text('${signer.signerRole} · ${signer.emailSnapshot}'),
        trailing: const Chip(label: Text('Required')),
      ),
  ],
);

Widget _unitContext(String propertyName, String unitNumber) => Padding(
  padding: const EdgeInsets.only(bottom: 12),
  child: Row(
    children: [
      const Icon(Icons.home_work_outlined),
      const SizedBox(width: 8),
      Expanded(child: Text('$propertyName · Unit $unitNumber')),
    ],
  ),
);

class _DateTile extends StatelessWidget {
  const _DateTile({
    required this.label,
    required this.value,
    required this.onChanged,
  });
  final String label;
  final DateTime value;
  final ValueChanged<DateTime> onChanged;

  @override
  Widget build(BuildContext context) => ListTile(
    contentPadding: EdgeInsets.zero,
    title: Text(label),
    subtitle: Text(_date(value)),
    trailing: const Icon(Icons.calendar_month_outlined),
    onTap: () async {
      final selected = await showDatePicker(
        context: context,
        initialDate: value,
        firstDate: DateTime(2000),
        lastDate: DateTime(2100),
      );
      if (selected != null) onChanged(selected);
    },
  );
}

Widget _number(
  TextEditingController controller,
  String label, {
  required double min,
  double? max,
  bool integer = false,
}) => Padding(
  padding: const EdgeInsets.only(bottom: 10),
  child: TextFormField(
    controller: controller,
    decoration: InputDecoration(labelText: label),
    keyboardType: const TextInputType.numberWithOptions(decimal: true),
    validator: (value) {
      final number = integer
          ? int.tryParse(value ?? '')
          : double.tryParse(value ?? '');
      if (number == null || number < min || (max != null && number > max)) {
        return 'Enter a valid value';
      }
      return null;
    },
  ),
);

class _ReviewFact extends StatelessWidget {
  const _ReviewFact({required this.label, required this.value});
  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 4),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        SizedBox(width: 120, child: Text(label)),
        Expanded(
          child: Text(
            value,
            style: const TextStyle(fontWeight: FontWeight.w600),
          ),
        ),
      ],
    ),
  );
}

String _numberText(double value) => value == value.roundToDouble()
    ? value.toInt().toString()
    : value.toString();

String _date(DateTime value) => value.toIso8601String().split('T').first;
