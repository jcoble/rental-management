import 'package:flutter/material.dart';

import '../../core/models/lease.dart';
import 'leases_repository.dart';

class LeaseEndingDispositionInput {
  const LeaseEndingDispositionInput({
    required this.disposition,
    required this.decisionReason,
    required this.operationKey,
    this.noticeGivenAt,
    this.plannedMoveOutAt,
  });

  final String disposition;
  final DateTime? noticeGivenAt;
  final DateTime? plannedMoveOutAt;
  final String decisionReason;
  final String operationKey;
}

Future<LeaseEndingDispositionInput?> showEndingDispositionSheet(
  BuildContext context, {
  required LeaseManagementSummary summary,
}) => showModalBottomSheet<LeaseEndingDispositionInput>(
  context: context,
  isScrollControlled: true,
  useSafeArea: true,
  builder: (_) => _EndingDispositionSheet(summary: summary),
);

class _EndingDispositionSheet extends StatefulWidget {
  const _EndingDispositionSheet({required this.summary});

  final LeaseManagementSummary summary;

  @override
  State<_EndingDispositionSheet> createState() =>
      _EndingDispositionSheetState();
}

class _EndingDispositionSheetState extends State<_EndingDispositionSheet> {
  late String _disposition;
  DateTime? _noticeDate;
  DateTime? _moveOutDate;
  late final TextEditingController _reason;
  late final String _operationKey;

  bool get _isMoveOut => _disposition == 'NonRenewalMoveOut';

  @override
  void initState() {
    super.initState();
    _disposition = widget.summary.endingDisposition;
    _noticeDate = widget.summary.noticeGivenAt;
    _moveOutDate = widget.summary.plannedMoveOutAt;
    _reason = TextEditingController()..addListener(_changed);
    _operationKey = LeaseManagementsRepository.newOperationKey();
  }

  @override
  void dispose() {
    _reason
      ..removeListener(_changed)
      ..dispose();
    super.dispose();
  }

  void _changed() => setState(() {});

  bool get _valid =>
      _reason.text.trim().isNotEmpty &&
      _reason.text.trim().length <= 1000 &&
      (!_isMoveOut ||
          (_noticeDate != null &&
              _moveOutDate != null &&
              !_moveOutDate!.isBefore(_noticeDate!)));

  @override
  Widget build(BuildContext context) => Padding(
    padding: EdgeInsets.fromLTRB(
      20,
      20,
      20,
      20 + MediaQuery.viewInsetsOf(context).bottom,
    ),
    child: SingleChildScrollView(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            'Plan what happens when this lease ends',
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 8),
          const Text(
            'This controls tenancy notices. It does not edit or replace the current lease.',
          ),
          const SizedBox(height: 16),
          DropdownButtonFormField<String>(
            value: _disposition,
            decoration: const InputDecoration(labelText: 'Decision'),
            items: const [
              DropdownMenuItem(
                value: 'Undecided',
                child: Text('Not decided yet'),
              ),
              DropdownMenuItem(
                value: 'OfferRenewal',
                child: Text('Renew / continue with a new fixed term'),
              ),
              DropdownMenuItem(
                value: 'OfferMonthToMonth',
                child: Text('Continue month-to-month'),
              ),
              DropdownMenuItem(
                value: 'NonRenewalMoveOut',
                child: Text('Move out / end the tenancy'),
              ),
            ],
            onChanged: (value) => setState(() {
              _disposition = value ?? _disposition;
              if (!_isMoveOut) {
                _noticeDate = null;
                _moveOutDate = null;
              }
            }),
          ),
          if (_isMoveOut) ...[
            const SizedBox(height: 12),
            const Text(
              'Use this for non-renewal, notice to move out, or an early termination. Record the move-out separately when keys are handed back.',
            ),
            const SizedBox(height: 12),
            _DateButton(
              label: 'Notice date',
              value: _noticeDate,
              onChanged: (value) => setState(() => _noticeDate = value),
            ),
            const SizedBox(height: 8),
            _DateButton(
              label: 'Effective move-out date',
              value: _moveOutDate,
              firstDate: _noticeDate,
              onChanged: (value) => setState(() => _moveOutDate = value),
            ),
          ],
          const SizedBox(height: 12),
          TextField(
            controller: _reason,
            maxLength: 1000,
            minLines: 2,
            maxLines: 4,
            decoration: InputDecoration(
              labelText: 'Decision reason',
              hintText: _isMoveOut
                  ? 'Tenant notice, non-renewal, or agreed early termination'
                  : 'Why this continuation path was chosen',
            ),
          ),
          const SizedBox(height: 12),
          FilledButton.icon(
            onPressed: _valid
                ? () => Navigator.of(context).pop(
                    LeaseEndingDispositionInput(
                      disposition: _disposition,
                      noticeGivenAt: _noticeDate,
                      plannedMoveOutAt: _moveOutDate,
                      decisionReason: _reason.text.trim(),
                      operationKey: _operationKey,
                    ),
                  )
                : null,
            icon: const Icon(Icons.event_available_outlined),
            label: const Text('Record decision'),
          ),
          TextButton(
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('Cancel'),
          ),
        ],
      ),
    ),
  );
}

class _DateButton extends StatelessWidget {
  const _DateButton({
    required this.label,
    required this.value,
    required this.onChanged,
    this.firstDate,
  });

  final String label;
  final DateTime? value;
  final DateTime? firstDate;
  final ValueChanged<DateTime> onChanged;

  @override
  Widget build(BuildContext context) => OutlinedButton.icon(
    onPressed: () async {
      final today = DateUtils.dateOnly(DateTime.now());
      final minimum = DateUtils.dateOnly(firstDate ?? DateTime(2000));
      final initial = DateUtils.dateOnly(
        value ?? (today.isAfter(minimum) ? today : minimum),
      );
      final selected = await showDatePicker(
        context: context,
        initialDate: initial,
        firstDate: minimum,
        lastDate: DateTime(today.year + 10, 12, 31),
      );
      if (selected != null) onChanged(selected);
    },
    icon: const Icon(Icons.calendar_today_outlined),
    label: Text(
      value == null
          ? label
          : '$label: ${value!.month}/${value!.day}/${value!.year}',
    ),
  );
}
