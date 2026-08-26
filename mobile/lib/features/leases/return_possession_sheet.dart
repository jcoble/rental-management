import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/lease.dart';
import '../../core/presentation/formatting.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import 'leases_repository.dart';

/// Sent when the landlord does not type a reason of their own.
const _defaultTurnoverReason = 'Tenant moved out';

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
  void initState() {
    super.initState();
    for (final party in widget.returnContext.parties) {
      _partyDispositions[party.id] = 'EndMembership';
    }
    for (final access in widget.returnContext.activeTenantUserAccesses) {
      _accessDispositions[access.id] = 'RevokeNow';
    }
  }

  @override
  void dispose() {
    _turnoverReason.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    final parties = [
      for (final party in widget.returnContext.parties)
        ReturnPossessionPartyInput(
          leaseManagementPartyId: party.id,
          disposition: _partyDispositions[party.id]!,
        ),
    ];
    final accesses = [
      for (final access in widget.returnContext.activeTenantUserAccesses)
        ReturnPossessionAccessInput(
          tenantUserAccessId: access.id,
          disposition: _accessDispositions[access.id]!,
        ),
    ];
    final typedReason = _turnoverReason.text.trim();

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
            turnoverReason: typedReason.isEmpty
                ? _defaultTurnoverReason
                : typedReason,
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
    saveLabel: 'Record move-out',
    saving: _saving,
    error: _error,
    onSave: _save,
    tabs: [
      TabbedFormStepSpec(
        label: 'Move-out',
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Card(
              margin: const EdgeInsets.only(bottom: 16),
              child: ListTile(
                leading: const Icon(Icons.key_off_outlined),
                title: Text(widget.summary.propertyName),
                subtitle: Text('Unit ${widget.summary.unitNumber}'),
              ),
            ),
            InputDecorator(
              decoration: const InputDecoration(labelText: 'Move-out date'),
              child: Text(
                'Recorded as of ${dateFmt(widget.summary.businessDate)}',
              ),
            ),
            const SizedBox(height: 16),
            const Text(
              'Everyone on this lease moves out and their app access ends.',
            ),
            const SizedBox(height: 8),
            _accessDisclosure(context),
            const SizedBox(height: 12),
            TextFormField(
              key: const ValueKey('return-turnover-reason'),
              controller: _turnoverReason,
              minLines: 2,
              maxLines: 4,
              maxLength: 1000,
              decoration: const InputDecoration(
                labelText: 'Why are they moving out? (optional)',
                hintText: 'Keys returned after the final walkthrough.',
              ),
              onChanged: (_) => setState(() => _error = null),
            ),
          ],
        ),
      ),
    ],
  );

  Widget _accessDisclosure(BuildContext context) {
    final theme = Theme.of(context);
    return Theme(
      data: theme.copyWith(dividerColor: Colors.transparent),
      child: ExpansionTile(
        key: const Key('return-access-disclosure'),
        tilePadding: EdgeInsets.zero,
        childrenPadding: const EdgeInsets.only(top: 4, bottom: 8),
        expandedCrossAxisAlignment: CrossAxisAlignment.stretch,
        title: Text(
          'Change what happens to their access',
          style: theme.textTheme.titleSmall?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        children: [
          for (final party in widget.returnContext.parties) ...[
            DropdownButtonFormField<String>(
              key: ValueKey('return-party-${party.id}'),
              isExpanded: true,
              value: _partyDispositions[party.id],
              decoration: InputDecoration(
                labelText: '${party.tenantName} · ${_roleLabel(party.role)}',
              ),
              items: [
                const DropdownMenuItem(
                  value: 'EndMembership',
                  child: Text('Moves out'),
                ),
                if (party.role == 'Guarantor')
                  const DropdownMenuItem(
                    value: 'RetainGuarantor',
                    child: Text('Stays a guarantor'),
                  ),
              ],
              onChanged: (value) => setState(() {
                if (value != null) _partyDispositions[party.id] = value;
              }),
            ),
            const SizedBox(height: 12),
          ],
          if (widget.returnContext.activeTenantUserAccesses.isEmpty)
            const Text('Nobody on this lease uses the tenant app.'),
          for (final access in widget.returnContext.activeTenantUserAccesses)
            Padding(
              padding: const EdgeInsets.only(bottom: 12),
              child: DropdownButtonFormField<String>(
                key: ValueKey('return-access-${access.id}'),
                isExpanded: true,
                value: _accessDispositions[access.id],
                decoration: InputDecoration(
                  labelText: access.userDisplayName.trim().isEmpty
                      ? access.userEmail
                      : access.userDisplayName,
                  helperText: 'App sign-in · ${access.userEmail}',
                ),
                items: const [
                  DropdownMenuItem(
                    value: 'RevokeNow',
                    child: Text('App access ends now'),
                  ),
                  DropdownMenuItem(
                    value: 'RetainHistorical',
                    child: Text('Can still see old records'),
                  ),
                ],
                onChanged: (value) => setState(() {
                  if (value != null) _accessDispositions[access.id] = value;
                }),
              ),
            ),
        ],
      ),
    );
  }
}

String _roleLabel(String role) => switch (role) {
  'PrimaryTenant' => 'Primary tenant',
  'CoTenant' => 'Co-tenant',
  'Occupant' => 'Occupant',
  'Guarantor' => 'Guarantor',
  _ => role,
};
