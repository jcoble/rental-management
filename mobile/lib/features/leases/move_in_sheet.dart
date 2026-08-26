import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/lease.dart';
import '../../core/presentation/formatting.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import 'leases_repository.dart';

/// What the move-in sheet hands back: the recorded move-in, plus whether the
/// landlord also asked to walk the unit with a checklist.
typedef MoveInOutcome = ({ConfirmMoveInResult result, bool startInspection});

/// One-question move-in sheet: the deposit is the only thing the landlord has
/// to answer, and everything else is already known from the lease.
Future<MoveInOutcome?> showMoveInSheet(
  BuildContext context, {
  required LeaseManagementSummary summary,
}) => showModalBottomSheet<MoveInOutcome>(
  context: context,
  isScrollControlled: true,
  useSafeArea: true,
  useRootNavigator: true,
  builder: (_) => _MoveInSheet(summary: summary),
);

/// Turns the API's wording for a blocked move-in into one plain sentence.
String plainMoveInProblem(ApiException error) {
  final text = error.message.toLowerCase();
  if (text.contains('conflicting possession') ||
      text.contains('operational period')) {
    return "That unit isn't available yet — check the previous tenant's "
        'move-out.';
  }
  if (text.contains('executed governing agreement')) {
    return "The lease isn't signed yet.";
  }
  if (text.contains('tenant account must be open')) {
    return "The rent ledger isn't open yet.";
  }
  if (text.contains('deposit')) {
    return 'Check the deposit details.';
  }
  if (text.contains('not eligible') || text.contains('current resident')) {
    return "This tenant can't be moved in yet.";
  }
  return error.message;
}

class _MoveInSheet extends ConsumerStatefulWidget {
  const _MoveInSheet({required this.summary});

  final LeaseManagementSummary summary;

  @override
  ConsumerState<_MoveInSheet> createState() => _MoveInSheetState();
}

class _MoveInSheetState extends ConsumerState<_MoveInSheet> {
  final _method = TextEditingController();
  late DateTime _depositReceivedOn = widget.summary.businessDate;
  bool _depositReceived = false;
  bool _startInspection = false;
  bool _saving = false;
  String? _error;
  String? _operationKey;

  @override
  void dispose() {
    _method.dispose();
    super.dispose();
  }

  Future<void> _pickDepositDate() async {
    final picked = await showDatePicker(
      context: context,
      initialDate: _depositReceivedOn,
      firstDate: DateTime(_depositReceivedOn.year - 2),
      lastDate: DateTime(_depositReceivedOn.year + 2),
    );
    if (picked == null || !mounted) return;
    setState(() => _depositReceivedOn = picked);
  }

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final result = await ref
          .read(leaseManagementsRepositoryProvider)
          .confirmMoveIn(
            leaseManagementId: widget.summary.id,
            unitId: widget.summary.unitId,
            depositEffectiveOn: _depositReceived ? _depositReceivedOn : null,
            depositPaymentMethodSummary: _depositReceived
                ? _method.text
                : null,
            operationKey: _operationKey ??=
                LeaseManagementsRepository.newOperationKey(),
          );
      if (mounted) {
        Navigator.of(context).pop((
          result: result,
          startInspection: _startInspection,
        ));
      }
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = plainMoveInProblem(error));
      rethrow;
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) => TabbedFormSheet(
    title: 'Tenant has moved in',
    saveLabel: 'Record move-in',
    saving: _saving,
    error: _error,
    onSave: _save,
    tabs: [
      TabbedFormStepSpec(
        label: 'Move-in',
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Card(
              margin: const EdgeInsets.only(bottom: 16),
              child: ListTile(
                leading: const Icon(Icons.key_outlined),
                title: Text(widget.summary.propertyName),
                subtitle: Text('Unit ${widget.summary.unitNumber}'),
              ),
            ),
            InputDecorator(
              decoration: const InputDecoration(labelText: 'Move-in date'),
              child: Text(
                'Recorded as of ${dateFmt(widget.summary.businessDate)}',
              ),
            ),
            const SizedBox(height: 12),
            SwitchListTile(
              key: const Key('move-in-deposit-received'),
              contentPadding: EdgeInsets.zero,
              title: const Text('Deposit received?'),
              value: _depositReceived,
              onChanged: (value) => setState(() {
                _depositReceived = value;
                _error = null;
              }),
            ),
            if (_depositReceived) ...[
              TextFormField(
                key: const Key('move-in-deposit-method'),
                controller: _method,
                maxLength: 200,
                decoration: const InputDecoration(
                  labelText: 'How was it paid?',
                  hintText: 'Check, cash, card, or bank transfer',
                ),
                validator: (value) => (value == null || value.trim().isEmpty)
                    ? 'Tell us how it was paid'
                    : null,
              ),
              const SizedBox(height: 12),
              InkWell(
                key: const Key('move-in-deposit-date'),
                onTap: _pickDepositDate,
                borderRadius: BorderRadius.circular(4),
                child: InputDecorator(
                  decoration: const InputDecoration(
                    labelText: 'Date received',
                    suffixIcon: Icon(Icons.calendar_today_outlined, size: 18),
                  ),
                  child: Text(dateFmt(_depositReceivedOn)),
                ),
              ),
              const SizedBox(height: 12),
            ],
            SwitchListTile(
              key: const Key('move-in-do-inspection'),
              contentPadding: EdgeInsets.zero,
              title: const Text('Do a move-in inspection'),
              subtitle: const Text('Walk the unit with a checklist next.'),
              value: _startInspection,
              onChanged: (value) => setState(() => _startInspection = value),
            ),
          ],
        ),
      ),
    ],
  );
}
