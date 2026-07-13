import 'package:flutter/material.dart';

import '../../core/models/lease.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import 'leases_repository.dart';

enum LeaseSuccessorOperation { correction, restatement, renewal, monthToMonth }

extension LeaseSuccessorOperationDetails on LeaseSuccessorOperation {
  String get title => switch (this) {
    LeaseSuccessorOperation.correction => 'Create correction',
    LeaseSuccessorOperation.restatement => 'Create restatement',
    LeaseSuccessorOperation.renewal => 'Create renewal',
    LeaseSuccessorOperation.monthToMonth => 'Create month-to-month agreement',
  };

  String get changeLabel => switch (this) {
    LeaseSuccessorOperation.correction => 'Correction',
    LeaseSuccessorOperation.restatement => 'Restatement',
    LeaseSuccessorOperation.renewal => 'Renewal',
    LeaseSuccessorOperation.monthToMonth => 'Month-to-month',
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
    this.operationKey = '',
    this.addendumDecisions = const [],
  });

  final DateTime termStart;
  final DateTime? termEnd;
  final DateTime governingFrom;
  final String operationKey;
  final List<LeaseRenewalAddendumDecisionInput> addendumDecisions;
}

LeaseSuccessorDates initialLeaseSuccessorDates(
  LeaseAgreementHistory source,
  LeaseSuccessorOperation operation, {
  required DateTime businessDate,
}) {
  final businessToday = DateUtils.dateOnly(businessDate);
  final dayAfterSourceStart = source.governingFromOn.add(
    const Duration(days: 1),
  );
  final dayAfterTerm = source.termEndOn?.add(const Duration(days: 1));
  final nextStart =
      dayAfterTerm ??
      (businessToday.isAfter(dayAfterSourceStart)
          ? businessToday.add(const Duration(days: 1))
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
  final termEnd = sourceEnd == null
      ? DateTime(
          nextStart.year + 1,
          nextStart.month,
          nextStart.day,
        ).subtract(const Duration(days: 1))
      : nextStart.add(sourceEnd.difference(source.termStartOn));
  return LeaseSuccessorDates(
    termStart: nextStart,
    termEnd: termEnd,
    governingFrom: nextStart,
  );
}

Future<LeaseSuccessorDates?> showSuccessorAgreementSheet(
  BuildContext context, {
  required LeaseAgreementHistory source,
  required LeaseSuccessorOperation operation,
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
    operation: operation,
    propertyName: propertyName,
    unitNumber: unitNumber,
    businessDate: businessDate,
    effectiveAddendumSeries: effectiveAddendumSeries,
  ),
);

class _SuccessorAgreementSheet extends StatefulWidget {
  const _SuccessorAgreementSheet({
    required this.source,
    required this.operation,
    required this.propertyName,
    required this.unitNumber,
    required this.businessDate,
    required this.effectiveAddendumSeries,
  });

  final LeaseAgreementHistory source;
  final LeaseSuccessorOperation operation;
  final String propertyName;
  final String unitNumber;
  final DateTime businessDate;
  final LeaseAgreementEffectiveAddendumSeries? effectiveAddendumSeries;

  @override
  State<_SuccessorAgreementSheet> createState() =>
      _SuccessorAgreementSheetState();
}

class _SuccessorAgreementSheetState extends State<_SuccessorAgreementSheet> {
  late DateTime _start;
  DateTime? _end;
  late DateTime _governingFrom;
  late final String _operationKey;
  final Map<String, String> _addendumDecisions = <String, String>{};

  @override
  void initState() {
    super.initState();
    final initial = initialLeaseSuccessorDates(
      widget.source,
      widget.operation,
      businessDate: widget.businessDate,
    );
    _operationKey = LeaseManagementsRepository.newOperationKey();
    _start = initial.termStart;
    _end = initial.termEnd;
    _governingFrom = initial.governingFrom;
  }

  bool get _datesValid {
    final source = widget.source;
    switch (widget.operation) {
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
    if (!widget.operation.requiresEffectiveAddendumDecisions) {
      return widget.effectiveAddendumSeries == null &&
          _addendumDecisions.isEmpty;
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
    if (!widget.operation.requiresEffectiveAddendumDecisions) return const [];
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
    title: widget.operation.title,
    saveLabel: 'Create draft',
    saving: false,
    onSave: () async {
      if (!_datesValid || !_addendumDecisionsComplete) return;
      Navigator.of(context).pop(
        LeaseSuccessorDates(
          termStart: _start,
          termEnd: _end,
          governingFrom: _governingFrom,
          operationKey: _operationKey,
          addendumDecisions: _exactAddendumDecisions(),
        ),
      );
    },
    tabs: [
      TabbedFormStepSpec(
        label: 'Dates',
        validate: () => _datesValid,
        child: _datesStep(),
      ),
      if (widget.operation.requiresEffectiveAddendumDecisions)
        TabbedFormStepSpec(
          label: 'Addenda',
          isComplete: () => _addendumDecisionsComplete,
          validate: () => _addendumDecisionsComplete,
          child: _addendaStep(),
        ),
      TabbedFormStepSpec(
        label: 'Review',
        validate: () => _datesValid && _addendumDecisionsComplete,
        child: _reviewStep(),
      ),
    ],
  );

  Widget _datesStep() {
    final replacement =
        widget.operation == LeaseSuccessorOperation.correction ||
        widget.operation == LeaseSuccessorOperation.restatement;
    final monthToMonth =
        widget.operation == LeaseSuccessorOperation.monthToMonth;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('${widget.propertyName} · Unit ${widget.unitNumber}'),
        const SizedBox(height: 8),
        Text(
          replacement
              ? 'A correction or restatement preserves the signed term dates. Choose when the replacement version becomes governing.'
              : 'This creates a successor draft. The signed agreement remains immutable.',
        ),
        const SizedBox(height: 12),
        _DateTile(
          label: 'Term starts',
          value: _start,
          enabled: !replacement,
          onChanged: (value) => setState(() {
            _start = value;
            if (!replacement) _governingFrom = value;
          }),
        ),
        if (!monthToMonth && _end != null)
          _DateTile(
            label: 'Term ends',
            value: _end!,
            enabled: !replacement,
            onChanged: (value) => setState(() => _end = value),
          ),
        _DateTile(
          label: 'Becomes governing',
          value: _governingFrom,
          enabled: replacement,
          onChanged: (value) => setState(() => _governingFrom = value),
        ),
        if (!_datesValid)
          Text(
            replacement
                ? 'The governing date must be after the source governing date and within its term.'
                : 'The successor must begin after the current term and use valid term dates.',
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
                        '${_effectTypeLabel(effect.effectType)} · '
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
                        labelText: 'Successor treatment',
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
                    const Text('No successor decision is required.'),
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
      _ReviewFact(label: 'Change', value: widget.operation.changeLabel),
      _ReviewFact(label: 'Term starts', value: _date(_start)),
      _ReviewFact(
        label: 'Term ends',
        value: _end == null ? 'Month-to-month' : _date(_end!),
      ),
      _ReviewFact(label: 'Becomes governing', value: _date(_governingFrom)),
      if (widget.operation.requiresEffectiveAddendumDecisions) ...[
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
        'The new version remains a draft. Review its copied terms and signer snapshot before issuance.',
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

String _effectTypeLabel(String effectType) => switch (effectType) {
  'RecurringRentDelta' => 'Recurring rent change',
  'OneTimeCharge' => 'One-time charge',
  'DepositObligationDelta' => 'Deposit obligation change',
  _ => effectType,
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
  'End' => 'No addendum terms or effects carry forward to the successor.',
  'IncorporateIntoBase' =>
    'The terms are folded into the new base agreement; no replacement addendum is created.',
  'ReissueAsAddendum' =>
    'A new editable addendum draft copies the terms, signers, and financial effects.',
  _ => '',
};
