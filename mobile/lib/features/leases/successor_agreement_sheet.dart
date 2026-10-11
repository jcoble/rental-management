import 'package:flutter/material.dart';

import '../../core/models/lease.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import 'leases_repository.dart';
import 'addendum_action_sheets.dart';

enum LeaseSuccessorOperation { correction, restatement, renewal, monthToMonth }

extension LeaseSuccessorOperationDetails on LeaseSuccessorOperation {
  /// The plain words the landlord picks from. Never the internal change name.
  String get changeLabel => switch (this) {
    LeaseSuccessorOperation.correction => 'Fix a mistake',
    LeaseSuccessorOperation.restatement => 'Update the terms',
    LeaseSuccessorOperation.renewal => 'Renew for another term',
    LeaseSuccessorOperation.monthToMonth => 'Switch to month-to-month',
  };

  /// What actually happens on the server: every kind writes a new draft
  /// version, copies the signers, and leaves the current lease in charge until
  /// the new one is fully signed
  /// (RentalCommand.Data/Leasing/LeaseAgreementDraftRules.cs).
  String get outcomeLine => switch (this) {
    LeaseSuccessorOperation.correction ||
    LeaseSuccessorOperation.restatement =>
      'Creates a corrected version of the lease. Everyone signs it again, and '
          'the lease you have now keeps going until they do.',
    LeaseSuccessorOperation.renewal ||
    LeaseSuccessorOperation.monthToMonth =>
      'Creates a new version of the lease and sends it out for signature.',
  };

  String get apiChangeType => switch (this) {
    LeaseSuccessorOperation.correction => 'Correction',
    LeaseSuccessorOperation.restatement => 'Restatement',
    LeaseSuccessorOperation.renewal => 'Renewal',
    LeaseSuccessorOperation.monthToMonth => 'MonthToMonth',
  };

  bool get requiresEffectiveAddendumDecisions =>
      this == LeaseSuccessorOperation.renewal ||
      this == LeaseSuccessorOperation.monthToMonth;
}

class LeaseSuccessorDates {
  const LeaseSuccessorDates({
    required this.termStart,
    required this.termEnd,
    required this.governingFrom,
    this.operation = LeaseSuccessorOperation.correction,
    this.operationKey = '',
    this.addendumDecisions = const [],
    this.correctionReason,
  });

  final DateTime termStart;
  final DateTime? termEnd;
  final DateTime governingFrom;
  final LeaseSuccessorOperation operation;
  final String operationKey;
  final List<LeaseRenewalAddendumDecisionInput> addendumDecisions;
  final String? correctionReason;
}

LeaseSuccessorDates initialLeaseSuccessorDates(
  LeaseAgreementHistory source,
  LeaseSuccessorOperation operation, {
  required DateTime businessDate,
}) {
  final businessToday = DateUtils.dateOnly(businessDate);
  final dayAfterSourceStart = DateUtils.addDaysToDate(
    source.governingFromOn,
    1,
  );
  final dayAfterTerm = source.termEndOn == null
      ? null
      : DateUtils.addDaysToDate(source.termEndOn!, 1);
  final nextStart =
      dayAfterTerm ??
      (businessToday.isAfter(dayAfterSourceStart)
          ? DateUtils.addDaysToDate(businessToday, 1)
          : dayAfterSourceStart);

  if (operation == LeaseSuccessorOperation.correction ||
      operation == LeaseSuccessorOperation.restatement) {
    final governingFrom = businessToday.isAfter(source.governingFromOn)
        ? businessToday
        : dayAfterSourceStart;
    return LeaseSuccessorDates(
      termStart: source.termStartOn,
      termEnd: source.termEndOn,
      governingFrom: governingFrom,
    );
  }

  if (operation == LeaseSuccessorOperation.monthToMonth) {
    return LeaseSuccessorDates(
      termStart: nextStart,
      termEnd: null,
      governingFrom: nextStart,
    );
  }

  final sourceEnd = source.termEndOn;
  final sourceTermDays = sourceEnd == null
      ? null
      : DateTime.utc(sourceEnd.year, sourceEnd.month, sourceEnd.day)
            .difference(
              DateTime.utc(
                source.termStartOn.year,
                source.termStartOn.month,
                source.termStartOn.day,
              ),
            )
            .inDays;
  final termEnd = sourceEnd == null
      ? DateUtils.addDaysToDate(
          DateTime(nextStart.year + 1, nextStart.month, nextStart.day),
          -1,
        )
      : DateUtils.addDaysToDate(nextStart, sourceTermDays!);
  return LeaseSuccessorDates(
    termStart: nextStart,
    termEnd: termEnd,
    governingFrom: nextStart,
  );
}

/// The one inline "Change the lease" form. [initialOperation] preselects the
/// kind of change; the landlord can still switch it inside the sheet.
Future<LeaseSuccessorDates?> showSuccessorAgreementSheet(
  BuildContext context, {
  required LeaseAgreementHistory source,
  required LeaseSuccessorOperation? initialOperation,
  required String propertyName,
  required String unitNumber,
  required DateTime businessDate,
  LeaseAgreementEffectiveAddendumSeries? effectiveAddendumSeries,
}) => showModalBottomSheet<LeaseSuccessorDates>(
  context: context,
  isScrollControlled: true,
  useSafeArea: true,
  builder: (_) => _SuccessorAgreementSheet(
    source: source,
    initialOperation: initialOperation,
    propertyName: propertyName,
    unitNumber: unitNumber,
    businessDate: businessDate,
    effectiveAddendumSeries: effectiveAddendumSeries,
  ),
);

class _SuccessorAgreementSheet extends StatefulWidget {
  const _SuccessorAgreementSheet({
    required this.source,
    required this.initialOperation,
    required this.propertyName,
    required this.unitNumber,
    required this.businessDate,
    required this.effectiveAddendumSeries,
  });

  final LeaseAgreementHistory source;
  final LeaseSuccessorOperation? initialOperation;
  final String propertyName;
  final String unitNumber;
  final DateTime businessDate;
  final LeaseAgreementEffectiveAddendumSeries? effectiveAddendumSeries;

  @override
  State<_SuccessorAgreementSheet> createState() =>
      _SuccessorAgreementSheetState();
}

class _SuccessorAgreementSheetState extends State<_SuccessorAgreementSheet> {
  late LeaseSuccessorOperation _operation;
  late DateTime _start;
  DateTime? _end;
  late DateTime _governingFrom;
  late final String _operationKey;
  late final TextEditingController _correctionReason;
  final Map<String, String> _addendumDecisions = <String, String>{};

  @override
  void initState() {
    super.initState();
    _operation =
        widget.initialOperation ?? LeaseSuccessorOperation.correction;
    _operationKey = LeaseManagementsRepository.newOperationKey();
    _correctionReason = TextEditingController();
    _applyDefaultDates();
  }

  void _applyDefaultDates() {
    final initial = initialLeaseSuccessorDates(
      widget.source,
      _operation,
      businessDate: widget.businessDate,
    );
    _start = initial.termStart;
    _end = initial.termEnd;
    _governingFrom = initial.governingFrom;
  }

  void _chooseOperation(LeaseSuccessorOperation operation) {
    if (operation == _operation) return;
    setState(() {
      _operation = operation;
      _addendumDecisions.clear();
      if (operation != LeaseSuccessorOperation.correction) {
        _correctionReason.clear();
      }
      _applyDefaultDates();
    });
  }

  @override
  void dispose() {
    _correctionReason.dispose();
    super.dispose();
  }

  bool get _correctionReasonValid =>
      _operation != LeaseSuccessorOperation.correction ||
      (_correctionReason.text.trim().isNotEmpty &&
          _correctionReason.text.trim().length <= 1000);

  bool get _datesValid {
    final source = widget.source;
    switch (_operation) {
      case LeaseSuccessorOperation.correction:
      case LeaseSuccessorOperation.restatement:
        return DateUtils.isSameDay(_start, source.termStartOn) &&
            _sameOptionalDay(_end, source.termEndOn) &&
            _governingFrom.isAfter(source.governingFromOn) &&
            (_end == null || !_governingFrom.isAfter(_end!));
      case LeaseSuccessorOperation.renewal:
        return _end != null &&
            !_end!.isBefore(_start) &&
            DateUtils.isSameDay(_governingFrom, _start) &&
            (source.termEndOn == null || _start.isAfter(source.termEndOn!));
      case LeaseSuccessorOperation.monthToMonth:
        return _end == null &&
            DateUtils.isSameDay(_governingFrom, _start) &&
            (source.termEndOn == null || _start.isAfter(source.termEndOn!));
    }
  }

  bool get _addendumDecisionsComplete {
    if (!_operation.requiresEffectiveAddendumDecisions) {
      return _addendumDecisions.isEmpty;
    }
    final effectiveSeries = widget.effectiveAddendumSeries;
    if (effectiveSeries == null) return false;
    if (effectiveSeries.requiredDecisionCount > 0 &&
        effectiveSeries.series.isEmpty) {
      return false;
    }
    for (final series in effectiveSeries.series) {
      if (!series.decisionRequired) continue;
      if (!_addendumDecisions.containsKey(series.seriesPublicId)) {
        return false;
      }
    }
    return _addendumDecisions.length == effectiveSeries.requiredDecisionCount;
  }

  List<LeaseRenewalAddendumDecisionInput> _exactAddendumDecisions() {
    if (!_operation.requiresEffectiveAddendumDecisions) return const [];
    final decisions = <LeaseRenewalAddendumDecisionInput>[];
    for (final series in widget.effectiveAddendumSeries!.series) {
      if (!series.decisionRequired) continue;
      decisions.add(
        LeaseRenewalAddendumDecisionInput(
          sourceAddendumSeriesPublicId: series.seriesPublicId,
          decision: _addendumDecisions[series.seriesPublicId]!,
        ),
      );
    }
    return decisions;
  }

  @override
  Widget build(BuildContext context) => TabbedFormSheet(
    title: 'Change the lease',
    saveLabel: 'Save changes',
    saving: false,
    onSave: () async {
      if (!_datesValid ||
          !_correctionReasonValid ||
          !_addendumDecisionsComplete) {
        return;
      }
      Navigator.of(context).pop(
        LeaseSuccessorDates(
          termStart: _start,
          termEnd: _end,
          governingFrom: _governingFrom,
          operation: _operation,
          operationKey: _operationKey,
          addendumDecisions: _exactAddendumDecisions(),
          correctionReason: _operation == LeaseSuccessorOperation.correction
              ? _correctionReason.text.trim()
              : null,
        ),
      );
    },
    tabs: [
      TabbedFormStepSpec(
        label: 'Change',
        validate: () => _datesValid && _correctionReasonValid,
        child: _datesStep(),
      ),
      if (_operation.requiresEffectiveAddendumDecisions)
        TabbedFormStepSpec(
          label: 'Addenda',
          isComplete: () => _addendumDecisionsComplete,
          validate: () => _addendumDecisionsComplete,
          child: _addendaStep(),
        ),
      TabbedFormStepSpec(
        label: 'Review',
        validate: () =>
            _datesValid && _correctionReasonValid && _addendumDecisionsComplete,
        child: _reviewStep(),
      ),
    ],
  );

  Widget _datesStep() {
    final replacement =
        _operation == LeaseSuccessorOperation.correction ||
        _operation == LeaseSuccessorOperation.restatement;
    final monthToMonth = _operation == LeaseSuccessorOperation.monthToMonth;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('${widget.propertyName} · Unit ${widget.unitNumber}'),
        const SizedBox(height: 12),
        DropdownButtonFormField<LeaseSuccessorOperation>(
          key: const Key('change-kind'),
          isExpanded: true,
          initialValue: _operation,
          decoration: const InputDecoration(
            labelText: 'What kind of change?',
          ),
          items: [
            for (final option in LeaseSuccessorOperation.values)
              DropdownMenuItem(
                value: option,
                child: Text(option.changeLabel),
              ),
          ],
          onChanged: (value) {
            if (value != null) _chooseOperation(value);
          },
        ),
        const SizedBox(height: 8),
        Text(
          _operation.outcomeLine,
          style: TextStyle(
            color: Theme.of(context).colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: 16),
        if (_operation == LeaseSuccessorOperation.correction) ...[
          TextFormField(
            controller: _correctionReason,
            decoration: const InputDecoration(
              labelText: 'What was wrong?',
              helperText: 'Saved with the lease so you can look it up later',
            ),
            minLines: 2,
            maxLines: 4,
            maxLength: 1000,
            validator: (value) => value == null || value.trim().isEmpty
                ? 'Say what the new version fixes'
                : null,
          ),
          const SizedBox(height: 8),
        ],
        if (replacement) ...[
          Text(
            'Lease dates stay the same: ${_date(_start)} – '
            '${_end == null ? 'month to month' : _date(_end!)}.',
          ),
          _DateTile(
            label: 'Effective from',
            value: _governingFrom,
            enabled: true,
            onChanged: (value) => setState(() => _governingFrom = value),
          ),
        ] else ...[
          _DateTile(
            label: monthToMonth ? 'Month-to-month starts' : 'New lease starts',
            value: _start,
            enabled: true,
            onChanged: (value) => setState(() {
              _start = value;
              _governingFrom = value;
            }),
          ),
          if (!monthToMonth && _end != null)
            _DateTile(
              label: 'New lease ends',
              value: _end!,
              enabled: true,
              onChanged: (value) => setState(() => _end = value),
            ),
        ],
        if (!_datesValid)
          Text(
            replacement
                ? 'Pick a date after the lease you have now started, and on or '
                      'before it ends.'
                : 'Pick a start date after the lease you have now ends.',
            style: TextStyle(color: Theme.of(context).colorScheme.error),
          ),
      ],
    );
  }

  Widget _addendaStep() {
    final effectiveSeries = widget.effectiveAddendumSeries;
    if (effectiveSeries == null) {
      return const Text(
        'The authoritative effective addendum series could not be loaded.',
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('${widget.propertyName} · Unit ${widget.unitNumber}'),
        const SizedBox(height: 8),
        Text(
          '${effectiveSeries.requiredDecisionCount} effective addendum '
          '${effectiveSeries.requiredDecisionCount == 1 ? 'series requires' : 'series require'} a decision as of ${_date(effectiveSeries.businessDate)}.',
        ),
        const SizedBox(height: 16),
        if (!effectiveSeries.decisionRequired)
          const Text('No effective addendum series requires a decision.'),
        for (final series in effectiveSeries.series) ...[
          Card(
            margin: const EdgeInsets.only(bottom: 12),
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    series.title,
                    style: Theme.of(context).textTheme.titleMedium,
                  ),
                  const SizedBox(height: 4),
                  Text(
                    '${_purposeLabel(series.purpose)} · v${series.currentVersionNumber} · '
                    'Base ${series.baseAgreementNumber}',
                  ),
                  Text(
                    'Effective ${_date(series.effectiveFromOn)} – '
                    '${series.effectiveThroughOn == null ? 'Open-ended' : _date(series.effectiveThroughOn!)}',
                  ),
                  const SizedBox(height: 12),
                  Text(
                    'Financial effects (${series.financialEffectCount})',
                    style: Theme.of(context).textTheme.titleSmall,
                  ),
                  if (series.financialEffectCount == 0)
                    const Padding(
                      padding: EdgeInsets.only(top: 4),
                      child: Text('No financial effects.'),
                    ),
                  for (final effect in series.financialEffects)
                    ListTile(
                      contentPadding: EdgeInsets.zero,
                      dense: true,
                      title: Text(
                        '${leaseEffectTypeLabel(effect.effectType)} · '
                        '${effect.currency} ${effect.amount.toStringAsFixed(2)}',
                      ),
                      subtitle: Text(
                        '${effect.chargeCode} · ${effect.description}\n'
                        '${_effectDates(effect)}',
                      ),
                      isThreeLine: true,
                    ),
                  const SizedBox(height: 8),
                  if (series.decisionRequired) ...[
                    DropdownButtonFormField<String>(
                      key: ValueKey('addendum-series-${series.seriesPublicId}'),
                      value: _addendumDecisions[series.seriesPublicId],
                      decoration: const InputDecoration(
                        labelText: 'What to do with this add-on',
                      ),
                      items: const [
                        DropdownMenuItem(
                          value: 'End',
                          child: Text('End with source agreement'),
                        ),
                        DropdownMenuItem(
                          value: 'IncorporateIntoBase',
                          child: Text('Incorporate into base agreement'),
                        ),
                        DropdownMenuItem(
                          value: 'ReissueAsAddendum',
                          child: Text('Reissue as addendum'),
                        ),
                      ],
                      validator: (value) => value == null
                          ? 'Choose a decision for this addendum series'
                          : null,
                      onChanged: (value) => setState(() {
                        if (value == null) {
                          _addendumDecisions.remove(series.seriesPublicId);
                        } else {
                          _addendumDecisions[series.seriesPublicId] = value;
                        }
                      }),
                    ),
                    if (_addendumDecisions[series.seriesPublicId]
                        case final decision?)
                      Padding(
                        padding: const EdgeInsets.only(top: 8),
                        child: Text(
                          _decisionConsequence(decision),
                          style: TextStyle(
                            color: Theme.of(
                              context,
                            ).colorScheme.onSurfaceVariant,
                          ),
                        ),
                      ),
                  ] else
                    const Text('No decision is needed for this add-on.'),
                ],
              ),
            ),
          ),
        ],
      ],
    );
  }

  Widget _reviewStep() => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      ListTile(
        contentPadding: EdgeInsets.zero,
        leading: const Icon(Icons.home_work_outlined),
        title: Text('${widget.propertyName} · Unit ${widget.unitNumber}'),
        subtitle: Text(
          'Source ${widget.source.agreementNumber} · v${widget.source.versionNumber}',
        ),
      ),
      const Divider(),
      _ReviewFact(label: 'Change', value: _operation.changeLabel),
      if (_operation == LeaseSuccessorOperation.correction)
        _ReviewFact(label: 'What was wrong', value: _correctionReason.text.trim()),
      _ReviewFact(label: 'Lease starts', value: _date(_start)),
      _ReviewFact(
        label: 'Lease ends',
        value: _end == null ? 'Runs month to month' : _date(_end!),
      ),
      _ReviewFact(label: 'Effective from', value: _date(_governingFrom)),
      if (_operation.requiresEffectiveAddendumDecisions) ...[
        const SizedBox(height: 12),
        Text(
          'Effective addenda',
          style: Theme.of(context).textTheme.titleSmall,
        ),
        for (final series in widget.effectiveAddendumSeries!.series)
          ListTile(
            contentPadding: EdgeInsets.zero,
            title: Text(series.title),
            subtitle: Text(
              _decisionLabel(_addendumDecisions[series.seriesPublicId]),
            ),
          ),
      ],
      const SizedBox(height: 12),
      const Text(
        'The lease you have now stays in place until the new version is fully signed.',
      ),
    ],
  );
}

class _DateTile extends StatelessWidget {
  const _DateTile({
    required this.label,
    required this.value,
    required this.enabled,
    required this.onChanged,
  });

  final String label;
  final DateTime value;
  final bool enabled;
  final ValueChanged<DateTime> onChanged;

  @override
  Widget build(BuildContext context) => ListTile(
    contentPadding: EdgeInsets.zero,
    title: Text(label),
    subtitle: Text(_date(value)),
    trailing: Icon(
      enabled ? Icons.calendar_month_outlined : Icons.lock_outline,
    ),
    onTap: !enabled
        ? null
        : () async {
            final selected = await showDatePicker(
              context: context,
              firstDate: DateTime(2000),
              lastDate: DateTime(2100),
              initialDate: value,
            );
            if (selected != null) onChanged(selected);
          },
  );
}

class _ReviewFact extends StatelessWidget {
  const _ReviewFact({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 4),
    child: Row(
      children: [
        SizedBox(width: 132, child: Text(label)),
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

bool _sameOptionalDay(DateTime? left, DateTime? right) =>
    left == null && right == null ||
    left != null && right != null && DateUtils.isSameDay(left, right);

String _date(DateTime value) => value.toIso8601String().split('T').first;

String _purposeLabel(String purpose) => switch (purpose) {
  'Financial' => 'Financial',
  'Pet' => 'Pet',
  'Occupancy' => 'Occupancy',
  'Rules' => 'Rules',
  'Other' => 'Other',
  _ => purpose,
};

String _effectDates(LeaseAgreementRenewalFinancialEffectSummary effect) {
  if (effect.dueOn != null) return 'Due ${_date(effect.dueOn!)}';
  if (effect.effectiveFromOn == null) return 'No separate effect date';
  return 'Effective ${_date(effect.effectiveFromOn!)} – '
      '${effect.effectiveThroughOn == null ? 'Open-ended' : _date(effect.effectiveThroughOn!)}';
}

String _decisionLabel(String? decision) => switch (decision) {
  'End' => 'End with source agreement',
  'IncorporateIntoBase' => 'Incorporate into base agreement',
  'ReissueAsAddendum' => 'Reissue as addendum',
  _ => 'Not selected',
};

String _decisionConsequence(String decision) => switch (decision) {
  'End' => 'No addendum terms or effects carry forward to the new lease.',
  'IncorporateIntoBase' =>
    'The terms are folded into the new base agreement; no replacement addendum is created.',
  'ReissueAsAddendum' =>
    'A new editable addendum draft copies the terms, signers, and financial effects.',
  _ => '',
};
