import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/lease.dart';
import '../../core/models/tenant.dart';
import '../tenants/tenants_repository.dart';
import 'leases_repository.dart';

enum HouseholdAction { add, changeRole, end, grantAccess, revokeAccess }

Future<bool> showHouseholdManagementSheet(
  BuildContext context, {
  required HouseholdAction action,
  required LeaseManagementDetail management,
  required List<LeaseManagementParty> currentParties,
  required List<LeaseAgreementHistory> agreements,
  LeaseManagementParty? party,
  ActiveTenantUserAccess? access,
}) async =>
    await showModalBottomSheet<bool>(
      context: context,
      useSafeArea: true,
      isScrollControlled: true,
      builder: (_) => _HouseholdManagementSheet(
        action: action,
        management: management,
        currentParties: currentParties,
        agreements: agreements,
        party: party,
        access: access,
      ),
    ) ??
    false;

class _HouseholdManagementSheet extends ConsumerStatefulWidget {
  const _HouseholdManagementSheet({
    required this.action,
    required this.management,
    required this.currentParties,
    required this.agreements,
    this.party,
    this.access,
  });

  final HouseholdAction action;
  final LeaseManagementDetail management;
  final List<LeaseManagementParty> currentParties;
  final List<LeaseAgreementHistory> agreements;
  final LeaseManagementParty? party;
  final ActiveTenantUserAccess? access;

  @override
  ConsumerState<_HouseholdManagementSheet> createState() =>
      _HouseholdManagementSheetState();
}

class _HouseholdManagementSheetState
    extends ConsumerState<_HouseholdManagementSheet> {
  final _search = TextEditingController();
  final _reason = TextEditingController();
  int _step = 0;
  bool _busy = false;
  List<Tenant> _candidates = const [];
  int? _tenantId;
  int? _agreementId;
  int? _companionPartyId;
  String _role = 'Occupant';
  late DateTime _effectiveDate;

  @override
  void initState() {
    super.initState();
    _role = widget.party?.role ?? 'Occupant';
    final businessDate = widget.management.summary.businessDate;
    _effectiveDate = widget.action == HouseholdAction.end
        ? DateUtils.addDaysToDate(businessDate, -1)
        : businessDate;
    if (widget.action == HouseholdAction.add) _searchCandidates();
  }

  @override
  void dispose() {
    _search.dispose();
    _reason.dispose();
    super.dispose();
  }

  bool get _isResponsible => _role != 'Occupant';
  bool get _needsLegalBasis =>
      _isResponsible &&
      widget.action != HouseholdAction.grantAccess &&
      widget.action != HouseholdAction.revokeAccess;

  Future<void> _searchCandidates() async {
    setState(() => _busy = true);
    try {
      final page = await ref
          .read(tenantsRepositoryProvider)
          .listPage(
            TenantListQuery(
              take: 20,
              sort: 'name',
              search: _search.text.trim(),
              availableForLease: true,
              includeLeaseManagementId: widget.management.summary.id,
            ),
          );
      if (mounted) setState(() => _candidates = page.items);
    } on ApiException catch (error) {
      if (mounted) _error(error.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  bool _validate() {
    if (widget.action == HouseholdAction.add && _tenantId == null) {
      _error('Choose an existing person.');
      return false;
    }
    if (_reason.text.trim().isEmpty) {
      _error('Explain why this change is being made.');
      return false;
    }
    if (_needsLegalBasis && _agreementId == null) {
      _error(
        'Choose the signed correction or restatement that authorizes this change.',
      );
      return false;
    }
    final primaryHandoff =
        (widget.action == HouseholdAction.changeRole &&
            (_role == 'PrimaryTenant' ||
                widget.party?.role == 'PrimaryTenant')) ||
        (widget.action == HouseholdAction.end &&
            widget.party?.role == 'PrimaryTenant');
    if (primaryHandoff && _companionPartyId == null) {
      _error('Choose the other person in the primary handoff.');
      return false;
    }
    return true;
  }

  Future<void> _submit() async {
    if (!_validate()) return;
    setState(() => _busy = true);
    final repository = ref.read(leaseManagementsRepositoryProvider);
    final operationKey = LeaseManagementsRepository.newOperationKey();
    final managementId = widget.management.summary.id;
    final reason = _reason.text.trim();
    final legalBasis = {
      'sameRelationshipConfirmed': true,
      'agreementId': _needsLegalBasis ? _agreementId : null,
      'addendumId': null,
    };
    try {
      switch (widget.action) {
        case HouseholdAction.add:
          await repository.addParty(
            leaseManagementId: managementId,
            operationKey: operationKey,
            request: {
              'tenantId': _tenantId,
              'role': _role,
              'effectiveFrom': _date(_effectiveDate),
              'guarantorLegalNoticeEligible': false,
              'changeReason': reason,
              'legalBasis': legalBasis,
            },
          );
          break;
        case HouseholdAction.changeRole:
          final promoting = _role == 'PrimaryTenant';
          await repository.changePartyRole(
            leaseManagementId: managementId,
            partyId: widget.party!.id,
            operationKey: operationKey,
            request: {
              'newRole': _role,
              'effectiveOn': _date(_effectiveDate),
              'guarantorLegalNoticeEligible': false,
              'accessDisposition': 'RevokeImmediately',
              'companionPrimaryPartyId': _companionPartyId,
              'companionNewRole': _companionPartyId == null
                  ? null
                  : promoting
                  ? 'CoTenant'
                  : 'PrimaryTenant',
              'companionGuarantorLegalNoticeEligible': false,
              'changeReason': reason,
              'legalBasis': legalBasis,
            },
          );
          break;
        case HouseholdAction.end:
          await repository.endParty(
            leaseManagementId: managementId,
            partyId: widget.party!.id,
            operationKey: operationKey,
            request: {
              'effectiveThrough': _date(_effectiveDate),
              'accessDisposition': 'RevokeImmediately',
              'primarySuccessorPartyId': _companionPartyId,
              'changeReason': reason,
              'legalBasis': legalBasis,
            },
          );
          break;
        case HouseholdAction.grantAccess:
          await repository.grantPartyAccess(
            leaseManagementId: managementId,
            partyId: widget.party!.id,
            reason: reason,
            operationKey: operationKey,
          );
          break;
        case HouseholdAction.revokeAccess:
          await repository.revokePartyAccess(
            leaseManagementId: managementId,
            partyId: widget.party!.id,
            tenantUserAccessId: widget.access!.id,
            reason: reason,
            operationKey: operationKey,
          );
          break;
      }
      if (mounted) Navigator.of(context).pop(true);
    } on ApiException catch (error) {
      if (mounted) _error(error.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _error(String message) => ScaffoldMessenger.of(context)
    ..hideCurrentSnackBar()
    ..showSnackBar(SnackBar(content: Text(message)));

  @override
  Widget build(BuildContext context) {
    final simpleAccess =
        widget.action == HouseholdAction.grantAccess ||
        widget.action == HouseholdAction.revokeAccess;
    return DraggableScrollableSheet(
      expand: false,
      initialChildSize: .88,
      minChildSize: .55,
      builder: (context, scrollController) => Column(
        children: [
          ListTile(
            title: Text(_title, style: Theme.of(context).textTheme.titleLarge),
            subtitle: const Text(
              'Household membership can change over time. Signed agreement PDFs remain immutable.',
            ),
            trailing: IconButton(
              onPressed: _busy ? null : () => Navigator.of(context).pop(),
              icon: const Icon(Icons.close),
            ),
          ),
          Expanded(
            child: Stepper(
              currentStep: _step,
              controlsBuilder: (context, details) => Padding(
                padding: const EdgeInsets.only(top: 16),
                child: Row(
                  children: [
                    FilledButton(
                      onPressed: _busy
                          ? null
                          : _step == (simpleAccess ? 1 : 2)
                          ? _submit
                          : details.onStepContinue,
                      child: Text(
                        _step == (simpleAccess ? 1 : 2)
                            ? 'Confirm'
                            : 'Continue',
                      ),
                    ),
                    if (_step > 0)
                      TextButton(
                        onPressed: _busy ? null : details.onStepCancel,
                        child: const Text('Back'),
                      ),
                  ],
                ),
              ),
              onStepContinue: () => setState(() => _step += 1),
              onStepCancel: () => setState(() => _step -= 1),
              steps: [
                Step(
                  title: const Text('Person and change'),
                  content: _personStep(),
                ),
                Step(
                  title: const Text('Reason and access'),
                  content: _reasonStep(),
                ),
                if (!simpleAccess)
                  Step(
                    title: const Text('Review'),
                    content: const Text(
                      'The server will recheck the date, signed legal basis, primary handoff, property scope, current session, and capabilities in one atomic command.',
                    ),
                  ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _personStep() => Column(
    children: [
      if (widget.action == HouseholdAction.add) ...[
        TextField(
          controller: _search,
          decoration: InputDecoration(
            labelText: 'Search existing people',
            suffixIcon: IconButton(
              onPressed: _busy ? null : _searchCandidates,
              icon: const Icon(Icons.search),
            ),
          ),
          onSubmitted: (_) => _searchCandidates(),
        ),
        DropdownButtonFormField<int>(
          initialValue: _tenantId,
          decoration: const InputDecoration(labelText: 'Person'),
          items: _candidates
              .map(
                (tenant) => DropdownMenuItem(
                  value: tenant.id,
                  child: Text('${tenant.firstName} ${tenant.lastName}'),
                ),
              )
              .toList(),
          onChanged: (value) => setState(() => _tenantId = value),
        ),
      ],
      if (widget.action == HouseholdAction.add ||
          widget.action == HouseholdAction.changeRole)
        DropdownButtonFormField<String>(
          initialValue: _role,
          decoration: const InputDecoration(labelText: 'Household role'),
          items: const [
            DropdownMenuItem(
              value: 'PrimaryTenant',
              child: Text('Primary signer'),
            ),
            DropdownMenuItem(value: 'CoTenant', child: Text('Co-tenant')),
            DropdownMenuItem(value: 'Guarantor', child: Text('Guarantor')),
            DropdownMenuItem(value: 'Occupant', child: Text('Occupant')),
          ],
          onChanged: (value) => setState(() => _role = value!),
        ),
      if (widget.action != HouseholdAction.grantAccess &&
          widget.action != HouseholdAction.revokeAccess)
        ListTile(
          contentPadding: EdgeInsets.zero,
          title: Text(
            widget.action == HouseholdAction.end
                ? 'Effective through'
                : 'Effective on',
          ),
          subtitle: Text(_date(_effectiveDate)),
          trailing: const Icon(Icons.calendar_today_outlined),
          onTap: () async {
            final selected = await showDatePicker(
              context: context,
              initialDate: _effectiveDate,
              firstDate: DateTime(2000),
              lastDate: DateTime(2100),
            );
            if (selected != null && mounted) {
              setState(() => _effectiveDate = selected);
            }
          },
        ),
      if (_needsLegalBasis)
        DropdownButtonFormField<int>(
          initialValue: _agreementId,
          decoration: const InputDecoration(labelText: 'Signed legal basis'),
          items: widget.agreements
              .where(
                (agreement) =>
                    (agreement.changeType == 'Correction' ||
                        agreement.changeType == 'Restatement') &&
                    agreement.executedArtifact != null,
              )
              .map(
                (agreement) => DropdownMenuItem(
                  value: agreement.id,
                  child: Text(
                    '${agreement.agreementNumber} · v${agreement.versionNumber}',
                  ),
                ),
              )
              .toList(),
          onChanged: (value) => setState(() => _agreementId = value),
        ),
      if ((widget.action == HouseholdAction.changeRole &&
              (_role == 'PrimaryTenant' ||
                  widget.party?.role == 'PrimaryTenant')) ||
          (widget.action == HouseholdAction.end &&
              widget.party?.role == 'PrimaryTenant'))
        DropdownButtonFormField<int>(
          initialValue: _companionPartyId,
          decoration: const InputDecoration(labelText: 'Primary handoff'),
          items: widget.currentParties
              .where((candidate) => candidate.id != widget.party?.id)
              .map(
                (candidate) => DropdownMenuItem(
                  value: candidate.id,
                  child: Text('${candidate.tenantName} · ${candidate.role}'),
                ),
              )
              .toList(),
          onChanged: (value) => setState(() => _companionPartyId = value),
        ),
    ],
  );

  Widget _reasonStep() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      if (widget.action == HouseholdAction.grantAccess)
        const Padding(
          padding: EdgeInsets.only(bottom: 12),
          child: Text(
            'A login is created from this member’s email if needed. It grants access only through this relationship and queues the invitation in the same transaction.',
          ),
        ),
      TextField(
        controller: _reason,
        minLines: 2,
        maxLines: 4,
        maxLength: 500,
        decoration: const InputDecoration(labelText: 'Reason'),
      ),
    ],
  );

  String get _title => switch (widget.action) {
    HouseholdAction.add => 'Add household member',
    HouseholdAction.changeRole => 'Change household role',
    HouseholdAction.end => 'End household membership',
    HouseholdAction.grantAccess => 'Create resident login',
    HouseholdAction.revokeAccess => 'Revoke resident login',
  };

  static String _date(DateTime value) =>
      value.toIso8601String().split('T').first;
}
