import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/lease.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import 'leases_repository.dart';

Future<LeaseAddendumDraftMutationResult?> showCreateAddendumDraftSheet(
  BuildContext context, {
  required LeaseManagementSummary management,
}) => showModalBottomSheet<LeaseAddendumDraftMutationResult>(
  context: context,
  isScrollControlled: true,
  useSafeArea: true,
  builder: (_) => _CreateAddendumDraftSheet(management: management),
);

Future<LeaseAddendumDraftMutationResult?> showEditAddendumDraftSheet(
  BuildContext context, {
  required LeaseAddendumDraftDetail draft,
  required String propertyName,
  required String unitNumber,
}) => showModalBottomSheet<LeaseAddendumDraftMutationResult>(
  context: context,
  isScrollControlled: true,
  useSafeArea: true,
  builder: (_) => _EditAddendumDraftSheet(
    draft: draft,
    propertyName: propertyName,
    unitNumber: unitNumber,
  ),
);

Future<LeaseAddendumDraftMutationResult?> showCorrectAddendumSheet(
  BuildContext context, {
  required int leaseManagementId,
  required LeaseAddendumHistory source,
  required DateTime businessDate,
}) => showModalBottomSheet<LeaseAddendumDraftMutationResult>(
  context: context,
  isScrollControlled: true,
  useSafeArea: true,
  builder: (_) => _CorrectAddendumSheet(
    leaseManagementId: leaseManagementId,
    source: source,
    businessDate: businessDate,
  ),
);

Future<IssueAddendumResult?> showIssueAddendumSheet(
  BuildContext context, {
  required LeaseAddendumDraftDetail draft,
  required String propertyName,
  required String unitNumber,
}) => showModalBottomSheet<IssueAddendumResult>(
  context: context,
  isScrollControlled: true,
  useSafeArea: true,
  builder: (_) => _IssueAddendumSheet(
    draft: draft,
    propertyName: propertyName,
    unitNumber: unitNumber,
  ),
);

class _CreateAddendumDraftSheet extends ConsumerStatefulWidget {
  const _CreateAddendumDraftSheet({required this.management});

  final LeaseManagementSummary management;

  @override
  ConsumerState<_CreateAddendumDraftSheet> createState() =>
      _CreateAddendumDraftSheetState();
}

class _CreateAddendumDraftSheetState
    extends ConsumerState<_CreateAddendumDraftSheet> {
  static const _pageSize = 20;
  final _number = TextEditingController();
  final Set<int> _selectedPartyIds = <int>{};
  final List<LeaseAddendumFinancialEffect> _financialEffects = [];
  final String _operationKey = LeaseManagementsRepository.newOperationKey();
  LeaseAddendumEligibleBaseAgreementPage? _agreements;
  LeaseTemplateOptionPage? _templates;
  List<LeaseManagementParty>? _candidates;
  int _agreementSkip = 0;
  int _templateSkip = 0;
  int? _baseAgreementId;
  int? _templateId;
  String _purpose = 'Other';
  late DateTime _effectiveFrom;
  DateTime? _effectiveThrough;
  bool _loading = true;
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _effectiveFrom = DateUtils.dateOnly(widget.management.businessDate);
    _loadInitial();
  }

  @override
  void dispose() {
    _number.dispose();
    super.dispose();
  }

  Future<void> _loadInitial() async {
    try {
      final repository = ref.read(leaseManagementsRepositoryProvider);
      final results = await Future.wait<Object>([
        repository.addendumEligibleBaseAgreementsPage(
          leaseManagementId: widget.management.id,
          take: _pageSize,
        ),
        repository.addendumSignerCandidates(widget.management.id),
        repository.leaseTemplatesPage(
          take: _pageSize,
          propertyId: widget.management.propertyId,
        ),
      ]);
      if (!mounted) return;
      final agreements = results[0] as LeaseAddendumEligibleBaseAgreementPage;
      final candidates = results[1] as List<LeaseManagementParty>;
      final templates = results[2] as LeaseTemplateOptionPage;
      setState(() {
        _agreements = agreements;
        _candidates = candidates;
        _templates = templates;
        _baseAgreementId = agreements.items.isEmpty
            ? null
            : agreements.items.first.id;
        _templateId = templates.items.isEmpty ? null : templates.items.first.id;
        _loading = false;
      });
    } catch (error) {
      if (mounted) {
        setState(() {
          _loading = false;
          _error = error is ApiException ? error.message : error.toString();
        });
      }
    }
  }

  Future<void> _loadAgreementPage(int skip) async {
    setState(() => _loading = true);
    try {
      final page = await ref
          .read(leaseManagementsRepositoryProvider)
          .addendumEligibleBaseAgreementsPage(
            leaseManagementId: widget.management.id,
            skip: skip,
            take: _pageSize,
          );
      if (!mounted) return;
      setState(() {
        _agreementSkip = page.skip;
        _agreements = page;
        _baseAgreementId = page.items.isEmpty ? null : page.items.first.id;
        _loading = false;
      });
    } catch (error) {
      if (mounted) setState(() => _loading = false);
      rethrow;
    }
  }

  Future<void> _loadTemplatePage(int skip) async {
    setState(() => _loading = true);
    try {
      final page = await ref
          .read(leaseManagementsRepositoryProvider)
          .leaseTemplatesPage(
            skip: skip,
            take: _pageSize,
            propertyId: widget.management.propertyId,
          );
      if (!mounted) return;
      setState(() {
        _templateSkip = page.skip;
        _templates = page;
        _templateId = page.items.isEmpty ? null : page.items.first.id;
        _loading = false;
      });
    } catch (error) {
      if (mounted) setState(() => _loading = false);
      rethrow;
    }
  }

  List<LeaseAddendumDraftSigner> _signers() {
    final signers = <LeaseAddendumDraftSigner>[];
    var order = 1;
    for (final candidate in _candidates ?? const <LeaseManagementParty>[]) {
      if (!_selectedPartyIds.contains(candidate.id)) continue;
      signers.add(
        LeaseAddendumDraftSigner(
          id: 0,
          leaseManagementPartyId: candidate.id,
          tenantId: candidate.tenantId,
          signerRole: _signerRole(candidate.role),
          nameSnapshot: candidate.tenantName,
          emailSnapshot: candidate.email?.trim() ?? '',
          signingOrder: order++,
          isRequired: true,
        ),
      );
    }
    return signers;
  }

  bool get _datesValid =>
      _effectiveThrough == null || !_effectiveThrough!.isBefore(_effectiveFrom);

  bool get _selectionValid {
    final signers = _signers();
    var hasTenantSigner = false;
    for (final signer in signers) {
      if (!signer.isComplete) return false;
      if (signer.signerRole == 'PrimaryTenant' ||
          signer.signerRole == 'CoTenant') {
        hasTenantSigner = true;
      }
    }
    return _baseAgreementId != null &&
        _templateId != null &&
        signers.isNotEmpty &&
        hasTenantSigner;
  }

  String get _baseAgreementCurrency {
    for (final agreement
        in _agreements?.items ?? const <LeaseAddendumEligibleBaseAgreement>[]) {
      if (agreement.id == _baseAgreementId) return agreement.currency;
    }
    return '';
  }

  void _setBaseAgreement(int? value) {
    setState(() {
      _baseAgreementId = value;
      final currency = _baseAgreementCurrency;
      if (currency.isNotEmpty) {
        for (var index = 0; index < _financialEffects.length; index++) {
          _financialEffects[index] = _effectWithCurrency(
            _financialEffects[index],
            currency,
          );
        }
      }
    });
  }

  Future<void> _addFinancialEffect() async {
    final currency = _baseAgreementCurrency;
    if (currency.isEmpty) {
      setState(
        () => _error =
            'Choose a base agreement before adding a financial effect.',
      );
      return;
    }
    final effect = await _showFinancialEffectSheet(
      context,
      currency: currency,
      defaultEffectiveFrom: _effectiveFrom,
    );
    if (effect != null && mounted) {
      setState(() {
        _financialEffects.add(effect);
        _error = null;
      });
    }
  }

  Future<void> _editFinancialEffect(int index) async {
    final effect = await _showFinancialEffectSheet(
      context,
      currency: _baseAgreementCurrency,
      defaultEffectiveFrom: _effectiveFrom,
      initial: _financialEffects[index],
    );
    if (effect != null && mounted) {
      setState(() => _financialEffects[index] = effect);
    }
  }

  Future<void> _save() async {
    if (!_selectionValid || !_datesValid || _number.text.trim().isEmpty) {
      setState(() => _error = 'Complete the legal draft selections first.');
      return;
    }
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final result = await ref
          .read(leaseManagementsRepositoryProvider)
          .createAddendumDraft(
            leaseManagementId: widget.management.id,
            operationKey: _operationKey,
            input: LeaseAddendumDraftInput(
              baseAgreementId: _baseAgreementId!,
              addendumNumber: _number.text.trim(),
              purpose: _purpose,
              effectiveFromOn: _effectiveFrom,
              effectiveThroughOn: _effectiveThrough,
              termsSchemaVersion: 1,
              termsPayload: <String, dynamic>{},
              documentTemplateId: _templateId!,
              signers: _signers(),
              financialEffects: List<LeaseAddendumFinancialEffect>.from(
                _financialEffects,
              ),
            ),
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
    if (_loading) {
      return const SafeArea(
        child: SizedBox(
          height: 300,
          child: Center(child: CircularProgressIndicator()),
        ),
      );
    }
    return TabbedFormSheet(
      title: 'Create addendum draft',
      saveLabel: 'Create draft',
      saving: _saving,
      error: _error,
      onSave: _save,
      tabs: [
        TabbedFormStepSpec(
          label: 'Document',
          validate: () =>
              _baseAgreementId != null &&
              _templateId != null &&
              _number.text.trim().isNotEmpty &&
              _datesValid,
          child: _documentStep(),
        ),
        TabbedFormStepSpec(
          label: 'Signers',
          validate: () => _selectionValid,
          child: _signerStep(),
        ),
        TabbedFormStepSpec(
          label: 'Effects',
          child: _FinancialEffectsEditor(
            effects: _financialEffects,
            onAdd: _addFinancialEffect,
            onEdit: _editFinancialEffect,
            onRemove: (index) =>
                setState(() => _financialEffects.removeAt(index)),
          ),
        ),
        TabbedFormStepSpec(
          label: 'Review',
          validate: () => _selectionValid && _datesValid,
          child: _reviewStep(),
        ),
      ],
    );
  }

  Widget _documentStep() {
    final agreements = _agreements!;
    final templates = _templates!;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _unit(widget.management.propertyName, widget.management.unitNumber),
        DropdownButtonFormField<int>(
          initialValue: _baseAgreementId,
          decoration: const InputDecoration(
            labelText: 'Executed base agreement',
          ),
          items: [
            for (final agreement in agreements.items)
              DropdownMenuItem(
                value: agreement.id,
                child: Text(
                  '${agreement.agreementNumber} · v${agreement.versionNumber}',
                ),
              ),
          ],
          onChanged: _setBaseAgreement,
        ),
        _Pager(
          label:
              '${agreements.skip + 1}–${agreements.skip + agreements.items.length} of ${agreements.totalCount}',
          previous: agreements.hasPrevious
              ? () => _loadAgreementPage(
                  _agreementSkip > _pageSize ? _agreementSkip - _pageSize : 0,
                )
              : null,
          next: agreements.hasNext
              ? () => _loadAgreementPage(_agreementSkip + _pageSize)
              : null,
        ),
        DropdownButtonFormField<int>(
          initialValue: _templateId,
          decoration: const InputDecoration(labelText: 'Active lease template'),
          items: [
            for (final template in templates.items)
              DropdownMenuItem(value: template.id, child: Text(template.name)),
          ],
          onChanged: (value) => setState(() => _templateId = value),
        ),
        _Pager(
          label:
              '${templates.skip + 1}–${templates.skip + templates.items.length} of ${templates.totalCount}',
          previous: templates.hasPrevious
              ? () => _loadTemplatePage(
                  _templateSkip > _pageSize ? _templateSkip - _pageSize : 0,
                )
              : null,
          next: templates.hasNext
              ? () => _loadTemplatePage(_templateSkip + _pageSize)
              : null,
        ),
        TextFormField(
          controller: _number,
          decoration: const InputDecoration(labelText: 'Addendum number'),
          maxLength: 100,
          validator: (value) => value == null || value.trim().isEmpty
              ? 'Addendum number is required'
              : null,
        ),
        DropdownButtonFormField<String>(
          initialValue: _purpose,
          decoration: const InputDecoration(labelText: 'Purpose'),
          items: const [
            DropdownMenuItem(value: 'Financial', child: Text('Financial')),
            DropdownMenuItem(value: 'Pet', child: Text('Pet')),
            DropdownMenuItem(value: 'Occupancy', child: Text('Occupancy')),
            DropdownMenuItem(value: 'Rules', child: Text('Rules')),
            DropdownMenuItem(value: 'Other', child: Text('Other')),
          ],
          onChanged: (value) => setState(() => _purpose = value ?? _purpose),
        ),
        _DateTile(
          label: 'Effective from',
          value: _effectiveFrom,
          onChanged: (value) => setState(() => _effectiveFrom = value),
        ),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('Has an effective-through date'),
          value: _effectiveThrough != null,
          onChanged: (enabled) => setState(() {
            _effectiveThrough = enabled ? _effectiveFrom : null;
          }),
        ),
        if (_effectiveThrough != null)
          _DateTile(
            label: 'Effective through',
            value: _effectiveThrough!,
            onChanged: (value) => setState(() => _effectiveThrough = value),
          ),
      ],
    );
  }

  Widget _signerStep() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      const Text(
        'Current household signer candidates',
        style: TextStyle(fontWeight: FontWeight.w700),
      ),
      const SizedBox(height: 8),
      for (final party in _candidates ?? const <LeaseManagementParty>[])
        CheckboxListTile(
          contentPadding: EdgeInsets.zero,
          value: _selectedPartyIds.contains(party.id),
          title: Text(party.tenantName),
          subtitle: Text(
            '${party.role} · ${party.email?.trim().isNotEmpty == true ? party.email : 'No email'}',
          ),
          onChanged: party.email?.trim().isNotEmpty == true
              ? (selected) => setState(() {
                  if (selected == true) {
                    _selectedPartyIds.add(party.id);
                  } else {
                    _selectedPartyIds.remove(party.id);
                  }
                })
              : null,
        ),
      const Text(
        'Every packet signer is required. Non-signing occupants remain household parties and are not added to the signature packet.',
      ),
    ],
  );

  Widget _reviewStep() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      _unit(widget.management.propertyName, widget.management.unitNumber),
      _Fact(label: 'Addendum', value: _number.text.trim()),
      _Fact(label: 'Purpose', value: _purpose),
      _Fact(label: 'Effective', value: _date(_effectiveFrom)),
      _Fact(label: 'Required signers', value: '${_signers().length}'),
      _Fact(label: 'Financial effects', value: '${_financialEffects.length}'),
      const SizedBox(height: 12),
      const Text(
        'Eligible agreements, current signer candidates, and property-scoped active templates are loaded from canonical server-filtered selectors.',
      ),
    ],
  );
}

class _EditAddendumDraftSheet extends ConsumerStatefulWidget {
  const _EditAddendumDraftSheet({
    required this.draft,
    required this.propertyName,
    required this.unitNumber,
  });

  final LeaseAddendumDraftDetail draft;
  final String propertyName;
  final String unitNumber;

  @override
  ConsumerState<_EditAddendumDraftSheet> createState() =>
      _EditAddendumDraftSheetState();
}

class _EditAddendumDraftSheetState
    extends ConsumerState<_EditAddendumDraftSheet> {
  late final TextEditingController _number;
  late String _purpose;
  late DateTime _effectiveFrom;
  DateTime? _effectiveThrough;
  late final List<LeaseAddendumFinancialEffect> _financialEffects;
  final String _operationKey = LeaseManagementsRepository.newOperationKey();
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _number = TextEditingController(text: widget.draft.addendumNumber);
    _purpose = widget.draft.purpose;
    _effectiveFrom = widget.draft.effectiveFromOn;
    _effectiveThrough = widget.draft.effectiveThroughOn;
    _financialEffects = List<LeaseAddendumFinancialEffect>.from(
      widget.draft.financialEffects,
    );
  }

  @override
  void dispose() {
    _number.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    final templateId = widget.draft.documentTemplateId;
    if (templateId == null ||
        !widget.draft.hasExactOrderedSigners ||
        _number.text.trim().isEmpty ||
        (_effectiveThrough != null &&
            _effectiveThrough!.isBefore(_effectiveFrom))) {
      setState(() => _error = 'The exact editable draft state is incomplete.');
      return;
    }
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final result = await ref
          .read(leaseManagementsRepositoryProvider)
          .editAddendumDraft(
            leaseManagementId: widget.draft.leaseManagementId,
            leaseAddendumId: widget.draft.leaseAddendumId,
            operationKey: _operationKey,
            input: LeaseAddendumDraftInput(
              draftRevision: widget.draft.draftRevision,
              baseAgreementId: widget.draft.baseAgreementId,
              addendumNumber: _number.text.trim(),
              purpose: _purpose,
              effectiveFromOn: _effectiveFrom,
              effectiveThroughOn: _effectiveThrough,
              termsSchemaVersion: widget.draft.termsSchemaVersion,
              termsPayload: Map<String, dynamic>.from(
                widget.draft.termsPayload,
              ),
              documentTemplateId: templateId,
              signers: List<LeaseAddendumDraftSigner>.from(
                widget.draft.signers,
              ),
              financialEffects: List<LeaseAddendumFinancialEffect>.from(
                _financialEffects,
              ),
            ),
          );
      if (mounted) Navigator.of(context).pop(result);
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) => TabbedFormSheet(
    title: 'Edit addendum draft',
    saveLabel: 'Save draft',
    saving: _saving,
    error: _error,
    onSave: _save,
    tabs: [
      TabbedFormStepSpec(label: 'Draft', child: _draftStep()),
      TabbedFormStepSpec(
        label: 'Signers',
        validate: () => widget.draft.hasExactOrderedSigners,
        child: _signers(widget.draft.signers),
      ),
      TabbedFormStepSpec(label: 'Effects', child: _effects()),
    ],
  );

  Widget _draftStep() => Column(
    children: [
      _unit(widget.propertyName, widget.unitNumber),
      _Fact(label: 'Base agreement', value: widget.draft.baseAgreementNumber),
      TextFormField(
        controller: _number,
        decoration: const InputDecoration(labelText: 'Addendum number'),
      ),
      DropdownButtonFormField<String>(
        initialValue: _purpose,
        decoration: const InputDecoration(labelText: 'Purpose'),
        items: const [
          DropdownMenuItem(value: 'Financial', child: Text('Financial')),
          DropdownMenuItem(value: 'Pet', child: Text('Pet')),
          DropdownMenuItem(value: 'Occupancy', child: Text('Occupancy')),
          DropdownMenuItem(value: 'Rules', child: Text('Rules')),
          DropdownMenuItem(value: 'Other', child: Text('Other')),
        ],
        onChanged: (value) => setState(() => _purpose = value ?? _purpose),
      ),
      _DateTile(
        label: 'Effective from',
        value: _effectiveFrom,
        onChanged: (value) => setState(() => _effectiveFrom = value),
      ),
      if (_effectiveThrough != null)
        _DateTile(
          label: 'Effective through',
          value: _effectiveThrough!,
          onChanged: (value) => setState(() => _effectiveThrough = value),
        ),
    ],
  );

  Future<void> _addFinancialEffect() async {
    final effect = await _showFinancialEffectSheet(
      context,
      currency: widget.draft.baseAgreementCurrency,
      defaultEffectiveFrom: _effectiveFrom,
    );
    if (effect != null && mounted) {
      setState(() => _financialEffects.add(effect));
    }
  }

  Future<void> _editFinancialEffect(int index) async {
    final effect = await _showFinancialEffectSheet(
      context,
      currency: widget.draft.baseAgreementCurrency,
      defaultEffectiveFrom: _effectiveFrom,
      initial: _financialEffects[index],
    );
    if (effect != null && mounted) {
      setState(() => _financialEffects[index] = effect);
    }
  }

  Widget _effects() => _FinancialEffectsEditor(
    effects: _financialEffects,
    onAdd: _addFinancialEffect,
    onEdit: _editFinancialEffect,
    onRemove: (index) => setState(() => _financialEffects.removeAt(index)),
  );
}

Future<LeaseAddendumFinancialEffect?> _showFinancialEffectSheet(
  BuildContext context, {
  required String currency,
  required DateTime defaultEffectiveFrom,
  LeaseAddendumFinancialEffect? initial,
}) => showModalBottomSheet<LeaseAddendumFinancialEffect>(
  context: context,
  isScrollControlled: true,
  useSafeArea: true,
  builder: (_) => _FinancialEffectSheet(
    currency: currency,
    defaultEffectiveFrom: defaultEffectiveFrom,
    initial: initial,
  ),
);

class _FinancialEffectsEditor extends StatelessWidget {
  const _FinancialEffectsEditor({
    required this.effects,
    required this.onAdd,
    required this.onEdit,
    required this.onRemove,
  });

  final List<LeaseAddendumFinancialEffect> effects;
  final VoidCallback onAdd;
  final ValueChanged<int> onEdit;
  final ValueChanged<int> onRemove;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      const Text(
        'Structured financial effects',
        style: TextStyle(fontWeight: FontWeight.w700),
      ),
      const SizedBox(height: 4),
      const Text(
        'Optional. Billing reads these structured changes directly; nonfinancial addendums can leave this empty.',
      ),
      const SizedBox(height: 12),
      if (effects.isEmpty)
        const Padding(
          padding: EdgeInsets.only(bottom: 12),
          child: Text('No financial effects.'),
        )
      else
        for (var index = 0; index < effects.length; index++)
          Card(
            margin: const EdgeInsets.only(bottom: 10),
            child: ListTile(
              title: Text(effects[index].description),
              subtitle: Text(
                '${leaseEffectTypeLabel(effects[index].effectType)} · '
                '${effects[index].currency} ${effects[index].amount.toStringAsFixed(2)}\n'
                '${effects[index].chargeCode}',
              ),
              isThreeLine: true,
              onTap: () => onEdit(index),
              trailing: IconButton(
                tooltip: 'Remove financial effect',
                onPressed: () => onRemove(index),
                icon: const Icon(Icons.delete_outline),
              ),
            ),
          ),
      FilledButton.tonalIcon(
        onPressed: onAdd,
        icon: const Icon(Icons.add),
        label: const Text('Add financial effect'),
      ),
    ],
  );
}

class _FinancialEffectSheet extends StatefulWidget {
  const _FinancialEffectSheet({
    required this.currency,
    required this.defaultEffectiveFrom,
    this.initial,
  });

  final String currency;
  final DateTime defaultEffectiveFrom;
  final LeaseAddendumFinancialEffect? initial;

  @override
  State<_FinancialEffectSheet> createState() => _FinancialEffectSheetState();
}

class _FinancialEffectSheetState extends State<_FinancialEffectSheet> {
  static const _types = <String>[
    'RecurringRentDelta',
    'OneTimeCharge',
    'DepositObligationDelta',
  ];

  late final TextEditingController _amount;
  late final TextEditingController _chargeCode;
  late final TextEditingController _description;
  late String _type;
  DateTime? _effectiveFrom;
  DateTime? _effectiveThrough;
  DateTime? _dueOn;
  String? _error;

  @override
  void initState() {
    super.initState();
    final initial = widget.initial;
    _type = initial?.effectType ?? 'RecurringRentDelta';
    _amount = TextEditingController(
      text: initial == null ? '' : initial.amount.toString(),
    );
    _chargeCode = TextEditingController(text: initial?.chargeCode ?? '');
    _description = TextEditingController(text: initial?.description ?? '');
    _effectiveFrom =
        initial?.effectiveFromOn ??
        (_type == 'OneTimeCharge' ? null : widget.defaultEffectiveFrom);
    _effectiveThrough = initial?.effectiveThroughOn;
    _dueOn =
        initial?.dueOn ??
        (_type == 'OneTimeCharge' ? widget.defaultEffectiveFrom : null);
  }

  @override
  void dispose() {
    _amount.dispose();
    _chargeCode.dispose();
    _description.dispose();
    super.dispose();
  }

  bool get _isOneTime => _type == 'OneTimeCharge';

  String? _validationError() {
    final amount = double.tryParse(_amount.text.trim());
    if (amount == null || (_isOneTime ? amount <= 0 : amount == 0)) {
      return _isOneTime
          ? 'Enter a positive one-time charge amount.'
          : 'Enter a nonzero change amount.';
    }
    if (_chargeCode.text.trim().isEmpty) {
      return 'Charge code is required.';
    }
    if (_description.text.trim().isEmpty) {
      return 'Description is required.';
    }
    if (_isOneTime && _dueOn == null) {
      return 'A one-time charge needs a due date.';
    }
    if (!_isOneTime && _effectiveFrom == null) {
      return 'This change needs an effective date.';
    }
    if (_effectiveThrough != null &&
        (_effectiveFrom == null ||
            _effectiveThrough!.isBefore(_effectiveFrom!))) {
      return 'The effect cannot end before it begins.';
    }
    return null;
  }

  void _changeType(String value) {
    setState(() {
      _type = value;
      _error = null;
      if (_isOneTime) {
        _dueOn ??= widget.defaultEffectiveFrom;
        _effectiveFrom = null;
        _effectiveThrough = null;
      } else {
        _effectiveFrom ??= widget.defaultEffectiveFrom;
        _dueOn = null;
      }
    });
  }

  void _save() {
    final error = _validationError();
    if (error != null) {
      setState(() => _error = error);
      return;
    }
    Navigator.of(context).pop(
      LeaseAddendumFinancialEffect(
        id: widget.initial?.id ?? 0,
        effectType: _type,
        amount: double.parse(_amount.text.trim()),
        currency: widget.currency,
        chargeCode: _chargeCode.text.trim(),
        effectiveFromOn: _isOneTime ? null : _effectiveFrom,
        effectiveThroughOn: _isOneTime ? null : _effectiveThrough,
        dueOn: _isOneTime ? _dueOn : null,
        description: _description.text.trim(),
      ),
    );
  }

  @override
  Widget build(BuildContext context) => TabbedFormSheet(
    title: widget.initial == null
        ? 'Add financial effect'
        : 'Edit financial effect',
    saveLabel: widget.initial == null ? 'Add effect' : 'Save effect',
    saving: false,
    error: _error,
    onSave: _save,
    tabs: [
      TabbedFormStepSpec(
        label: 'Effect',
        validate: () {
          final amount = double.tryParse(_amount.text.trim());
          return amount != null &&
              (_isOneTime ? amount > 0 : amount != 0) &&
              _chargeCode.text.trim().isNotEmpty &&
              _description.text.trim().isNotEmpty;
        },
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            DropdownButtonFormField<String>(
              initialValue: _type,
              decoration: const InputDecoration(labelText: 'Effect type'),
              items: [
                for (final type in _types)
                  DropdownMenuItem(
                    value: type,
                    child: Text(leaseEffectTypeLabel(type)),
                  ),
              ],
              onChanged: (value) {
                if (value != null) _changeType(value);
              },
            ),
            TextFormField(
              controller: _amount,
              keyboardType: const TextInputType.numberWithOptions(
                decimal: true,
                signed: true,
              ),
              decoration: const InputDecoration(
                labelText: 'Amount',
                helperText:
                    'Use a negative amount for a rent or deposit reduction.',
              ),
            ),
            TextFormField(
              initialValue: widget.currency,
              readOnly: true,
              decoration: const InputDecoration(
                labelText: 'Currency',
                helperText: 'Matches the base agreement.',
              ),
            ),
            TextFormField(
              controller: _chargeCode,
              maxLength: 50,
              decoration: const InputDecoration(labelText: 'Charge code'),
            ),
            TextFormField(
              controller: _description,
              maxLength: 500,
              decoration: const InputDecoration(labelText: 'Description'),
            ),
          ],
        ),
      ),
      TabbedFormStepSpec(
        label: 'Timing',
        validate: () => _validationError() == null,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (_isOneTime)
              _DateTile(
                label: 'Due on',
                value: _dueOn ?? widget.defaultEffectiveFrom,
                onChanged: (value) => setState(() => _dueOn = value),
              )
            else ...[
              _DateTile(
                label: 'Effective from',
                value: _effectiveFrom ?? widget.defaultEffectiveFrom,
                onChanged: (value) => setState(() => _effectiveFrom = value),
              ),
              SwitchListTile(
                contentPadding: EdgeInsets.zero,
                title: const Text('Has an effective-through date'),
                value: _effectiveThrough != null,
                onChanged: (enabled) => setState(() {
                  _effectiveThrough = enabled
                      ? (_effectiveFrom ?? widget.defaultEffectiveFrom)
                      : null;
                }),
              ),
              if (_effectiveThrough != null)
                _DateTile(
                  label: 'Effective through',
                  value: _effectiveThrough!,
                  onChanged: (value) =>
                      setState(() => _effectiveThrough = value),
                ),
            ],
          ],
        ),
      ),
    ],
  );
}

String leaseEffectTypeLabel(String value) => switch (value) {
  'RecurringRentDelta' => 'Recurring rent change',
  'OneTimeCharge' => 'One-time charge',
  'DepositObligationDelta' => 'Deposit obligation change',
  _ => value,
};

LeaseAddendumFinancialEffect _effectWithCurrency(
  LeaseAddendumFinancialEffect effect,
  String currency,
) => LeaseAddendumFinancialEffect(
  id: effect.id,
  effectType: effect.effectType,
  amount: effect.amount,
  currency: currency,
  chargeCode: effect.chargeCode,
  effectiveFromOn: effect.effectiveFromOn,
  effectiveThroughOn: effect.effectiveThroughOn,
  dueOn: effect.dueOn,
  description: effect.description,
);

class _CorrectAddendumSheet extends ConsumerStatefulWidget {
  const _CorrectAddendumSheet({
    required this.leaseManagementId,
    required this.source,
    required this.businessDate,
  });

  final int leaseManagementId;
  final LeaseAddendumHistory source;
  final DateTime businessDate;

  @override
  ConsumerState<_CorrectAddendumSheet> createState() =>
      _CorrectAddendumSheetState();
}

class _CorrectAddendumSheetState extends ConsumerState<_CorrectAddendumSheet> {
  late DateTime _effectiveOn;
  final String _operationKey = LeaseManagementsRepository.newOperationKey();
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    final firstEligible = DateUtils.addDaysToDate(
      widget.source.effectiveFromOn,
      1,
    );
    _effectiveOn = widget.businessDate.isAfter(firstEligible)
        ? DateUtils.dateOnly(widget.businessDate)
        : firstEligible;
  }

  bool get _valid =>
      _effectiveOn.isAfter(widget.source.effectiveFromOn) &&
      (widget.source.effectiveThroughOn == null ||
          !_effectiveOn.isAfter(widget.source.effectiveThroughOn!));

  Future<void> _save() async {
    if (!_valid) {
      setState(
        () => _error = 'Choose a date inside the source effective range.',
      );
      return;
    }
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final result = await ref
          .read(leaseManagementsRepositoryProvider)
          .correctAddendumDraft(
            leaseManagementId: widget.leaseManagementId,
            sourceAddendumId: widget.source.id,
            supersessionEffectiveOn: _effectiveOn,
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
  Widget build(BuildContext context) => TabbedFormSheet(
    title: 'Correct addendum',
    saveLabel: 'Create correction draft',
    saving: _saving,
    error: _error,
    onSave: _save,
    tabs: [
      TabbedFormStepSpec(
        label: 'Correction',
        validate: () => _valid,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            _Fact(
              label: 'Source',
              value:
                  '${widget.source.addendumNumber} · v${widget.source.versionNumber}',
            ),
            _DateTile(
              label: 'Correction becomes effective',
              value: _effectiveOn,
              onChanged: (value) => setState(() => _effectiveOn = value),
            ),
            const Text(
              'The executed source remains immutable. The server creates a new editable version in the same series.',
            ),
          ],
        ),
      ),
    ],
  );
}

class _IssueAddendumSheet extends ConsumerStatefulWidget {
  const _IssueAddendumSheet({
    required this.draft,
    required this.propertyName,
    required this.unitNumber,
  });

  final LeaseAddendumDraftDetail draft;
  final String propertyName;
  final String unitNumber;

  @override
  ConsumerState<_IssueAddendumSheet> createState() =>
      _IssueAddendumSheetState();
}

class _IssueAddendumSheetState extends ConsumerState<_IssueAddendumSheet> {
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
      text: '${widget.draft.addendumNumber} for signature',
    );
  }

  @override
  void dispose() {
    _subject.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    if (!widget.draft.hasExactOrderedSigners || _subject.text.trim().isEmpty) {
      setState(() => _error = 'The exact issuance state is incomplete.');
      return;
    }
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final repository = ref.read(leaseManagementsRepositoryProvider);
      final preparation = _preparation ??= await repository
          .prepareAddendumIssuance(
            leaseManagementId: widget.draft.leaseManagementId,
            leaseAddendumId: widget.draft.leaseAddendumId,
            draftRevision: widget.draft.draftRevision,
            operationKey: _prepareKey,
          );
      final result = await repository.issueAddendum(
        leaseManagementId: widget.draft.leaseManagementId,
        leaseAddendumId: widget.draft.leaseAddendumId,
        preparation: preparation,
        subject: _subject.text.trim(),
        operationKey: _issueKey,
      );
      if (mounted) Navigator.of(context).pop(result);
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) => TabbedFormSheet(
    title: 'Issue addendum',
    saveLabel: 'Prepare and issue',
    saving: _saving,
    error: _error,
    onSave: _save,
    tabs: [
      TabbedFormStepSpec(
        label: 'Draft',
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            _unit(widget.propertyName, widget.unitNumber),
            _Fact(
              label: 'Addendum',
              value:
                  '${widget.draft.addendumNumber} · v${widget.draft.versionNumber}',
            ),
            _Fact(
              label: 'Draft revision',
              value: '${widget.draft.draftRevision}',
            ),
            _signers(widget.draft.signers),
          ],
        ),
      ),
      TabbedFormStepSpec(
        label: 'Issue',
        validate: () => _subject.text.trim().isNotEmpty,
        child: TextFormField(
          controller: _subject,
          decoration: const InputDecoration(
            labelText: 'Signature email subject',
          ),
        ),
      ),
    ],
  );
}

Widget _signers(List<LeaseAddendumDraftSigner> signers) => Column(
  crossAxisAlignment: CrossAxisAlignment.start,
  children: [
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

class _Pager extends StatelessWidget {
  const _Pager({required this.label, this.previous, this.next});
  final String label;
  final VoidCallback? previous;
  final VoidCallback? next;

  @override
  Widget build(BuildContext context) => Row(
    children: [
      IconButton(
        onPressed: previous,
        icon: const Icon(Icons.chevron_left),
        tooltip: 'Previous page',
      ),
      Expanded(child: Text(label, textAlign: TextAlign.center)),
      IconButton(
        onPressed: next,
        icon: const Icon(Icons.chevron_right),
        tooltip: 'Next page',
      ),
    ],
  );
}

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
    trailing: const Icon(Icons.calendar_today_outlined),
    onTap: () async {
      final picked = await showDatePicker(
        context: context,
        initialDate: value,
        firstDate: DateTime(1900),
        lastDate: DateTime(2200),
      );
      if (picked != null) onChanged(picked);
    },
  );
}

class _Fact extends StatelessWidget {
  const _Fact({required this.label, required this.value});
  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 6),
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

Widget _unit(String propertyName, String unitNumber) => Padding(
  padding: const EdgeInsets.only(bottom: 12),
  child: Text('$propertyName · Unit $unitNumber'),
);

String _signerRole(String partyRole) => switch (partyRole) {
  'PrimaryTenant' => 'PrimaryTenant',
  'CoTenant' => 'CoTenant',
  'Guarantor' => 'Guarantor',
  _ => 'Other',
};

String _date(DateTime value) => '${value.month}/${value.day}/${value.year}';
