import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/lease.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import 'leases_repository.dart';

Future<ReturnPossessionResult?> showReturnPossessionSheet(
  BuildContext context, {
  required LeaseManagementSummary summary,
  required ReturnPossessionContext returnContext,
}) => showModalBottomSheet<ReturnPossessionResult>(
  context: context,
  isScrollControlled: true,
  useSafeArea: true,
  builder: (_) =>
      _ReturnPossessionSheet(summary: summary, returnContext: returnContext),
);

class _ReturnPossessionSheet extends ConsumerStatefulWidget {
  const _ReturnPossessionSheet({
    required this.summary,
    required this.returnContext,
  });

  final LeaseManagementSummary summary;
  final ReturnPossessionContext returnContext;

  @override
  ConsumerState<_ReturnPossessionSheet> createState() =>
      _ReturnPossessionSheetState();
}

class _ReturnPossessionSheetState
    extends ConsumerState<_ReturnPossessionSheet> {
  final _turnoverReason = TextEditingController();
  final Map<int, String> _partyDispositions = <int, String>{};
  final Map<int, String> _accessDispositions = <int, String>{};
  bool _saving = false;
  String? _error;
  String? _operationKey;

  @override
  void dispose() {
    _turnoverReason.dispose();
    super.dispose();
  }

  bool get _householdComplete {
    for (final party in widget.returnContext.parties) {
      if (!_partyDispositions.containsKey(party.id)) return false;
    }
    return widget.returnContext.parties.isNotEmpty;
  }

  bool get _accessComplete {
    for (final access in widget.returnContext.activeTenantUserAccesses) {
      if (!_accessDispositions.containsKey(access.id)) return false;
    }
    return true;
  }

  bool get _reviewComplete =>
      _householdComplete &&
      _accessComplete &&
      _turnoverReason.text.trim().isNotEmpty &&
      _turnoverReason.text.trim().length <= 1000;

  Future<void> _save() async {
    if (!_reviewComplete) {
      setState(
        () => _error = 'Complete each choice and provide a reason.',
      );
      return;
    }

    final parties = <ReturnPossessionPartyInput>[];
    final accesses = <ReturnPossessionAccessInput>[];
    for (final party in widget.returnContext.parties) {
      parties.add(
        ReturnPossessionPartyInput(
          leaseManagementPartyId: party.id,
          disposition: _partyDispositions[party.id]!,
        ),
      );
    }
    for (final access in widget.returnContext.activeTenantUserAccesses) {
      accesses.add(
        ReturnPossessionAccessInput(
          tenantUserAccessId: access.id,
          disposition: _accessDispositions[access.id]!,
        ),
      );
    }

    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final result = await ref
          .read(leaseManagementsRepositoryProvider)
          .returnPossession(
            leaseManagementId: widget.summary.id,
            unitId: widget.summary.unitId,
            parties: parties,
            accesses: accesses,
            turnoverReason: _turnoverReason.text.trim(),
            operationKey: _operationKey ??=
                LeaseManagementsRepository.newOperationKey(),
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
    title: 'Move-out',
    saveLabel: 'Move-out',
    saving: _saving,
    error: _error,
    onSave: _save,
    tabs: [
      TabbedFormStepSpec(
        label: 'Household',
        isComplete: () => _householdComplete,
        validate: () => _householdComplete,
        child: _householdStep(),
      ),
      TabbedFormStepSpec(
        label: 'Access',
        isComplete: () => _accessComplete,
        validate: () => _accessComplete,
        child: _accessStep(),
      ),
      TabbedFormStepSpec(
        label: 'Review',
        validate: () => _reviewComplete,
        child: _reviewStep(),
      ),
    ],
  );

  Widget _householdStep() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      _unitContext(),
      const Text(
        'Choose what happens to each current household member. '
        'The current portfolio date determines who is listed.',
      ),
      const SizedBox(height: 16),
      for (final party in widget.returnContext.parties) ...[
        DropdownButtonFormField<String>(
          key: ValueKey('return-party-${party.id}'),
          value: _partyDispositions[party.id],
          decoration: InputDecoration(
            labelText: '${party.tenantName} · ${_roleLabel(party.role)}',
          ),
          items: [
            const DropdownMenuItem(
              value: 'EndMembership',
              child: Text('End household membership'),
            ),
            if (party.role == 'Guarantor')
              const DropdownMenuItem(
                value: 'RetainGuarantor',
                child: Text('Retain as guarantor'),
              ),
          ],
          validator: (value) =>
              value == null ? 'Choose what happens to them' : null,
          onChanged: (value) => setState(() {
            if (value == null) {
              _partyDispositions.remove(party.id);
            } else {
              _partyDispositions[party.id] = value;
            }
          }),
        ),
        const SizedBox(height: 12),
      ],
    ],
  );

  Widget _accessStep() {
    final hasAccess = widget.returnContext.activeTenantUserAccesses.isNotEmpty;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _unitContext(),
        const Text('Choose an option for each tenant app access record.'),
        const SizedBox(height: 16),
        if (!hasAccess)
          const Text(
            'There is no tenant app access to update.',
          ),
        for (final access in widget.returnContext.activeTenantUserAccesses) ...[
          DropdownButtonFormField<String>(
            key: ValueKey('return-access-${access.id}'),
            value: _accessDispositions[access.id],
            decoration: InputDecoration(
              labelText: access.userDisplayName.trim().isEmpty
                  ? access.userEmail
                  : access.userDisplayName,
              helperText: '${access.tenantName} · ${access.userEmail}',
            ),
            items: const [
              DropdownMenuItem(
                value: 'RevokeNow',
                child: Text('Revoke access now'),
              ),
              DropdownMenuItem(
                value: 'RetainHistorical',
                child: Text('Retain historical access'),
              ),
            ],
            validator: (value) =>
                value == null ? 'Choose their login option' : null,
            onChanged: (value) => setState(() {
              if (value == null) {
                _accessDispositions.remove(access.id);
              } else {
                _accessDispositions[access.id] = value;
              }
            }),
          ),
          const SizedBox(height: 12),
        ],
      ],
    );
  }

  Widget _reviewStep() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      _unitContext(),
      const Text(
        'This records the move-out now and starts getting it ready. The current portfolio date is used; no phone date is submitted.',
      ),
      const SizedBox(height: 16),
      TextFormField(
        key: const ValueKey('return-turnover-reason'),
        controller: _turnoverReason,
        minLines: 3,
        maxLines: 5,
        maxLength: 1000,
        decoration: const InputDecoration(
          labelText: 'Reason for getting it ready',
          hintText: 'Keys returned after final move-out inspection.',
        ),
        validator: (value) => value == null || value.trim().isEmpty
            ? 'A reason is required'
            : null,
        onChanged: (_) => setState(() => _error = null),
      ),
      const SizedBox(height: 12),
      Text('Household', style: Theme.of(context).textTheme.titleSmall),
      for (final party in widget.returnContext.parties)
        ListTile(
          contentPadding: EdgeInsets.zero,
          title: Text(party.tenantName),
          subtitle: Text(_partyDispositionLabel(_partyDispositions[party.id])),
        ),
      Text('Tenant app access', style: Theme.of(context).textTheme.titleSmall),
      for (final access in widget.returnContext.activeTenantUserAccesses)
        ListTile(
          contentPadding: EdgeInsets.zero,
          title: Text(
            access.userDisplayName.trim().isEmpty
                ? access.userEmail
                : access.userDisplayName,
          ),
          subtitle: Text(
            _accessDispositionLabel(_accessDispositions[access.id]),
          ),
        ),
    ],
  );

  Widget _unitContext() => Card(
    margin: const EdgeInsets.only(bottom: 16),
    child: ListTile(
      leading: const Icon(Icons.key_off_outlined),
      title: Text(widget.summary.propertyName),
      subtitle: Text('Unit ${widget.summary.unitNumber}'),
    ),
  );
}

String _roleLabel(String role) => switch (role) {
  'PrimaryTenant' => 'Primary tenant',
  'CoTenant' => 'Co-tenant',
  'Occupant' => 'Occupant',
  'Guarantor' => 'Guarantor',
  _ => role,
};

String _partyDispositionLabel(String? disposition) => switch (disposition) {
  'EndMembership' => 'End household membership',
  'RetainGuarantor' => 'Retain as guarantor',
  _ => 'Not selected',
};

String _accessDispositionLabel(String? disposition) => switch (disposition) {
  'RevokeNow' => 'Revoke access now',
  'RetainHistorical' => 'Retain historical access',
  _ => 'Not selected',
};
