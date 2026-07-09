import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import 'eviction_cases_repository.dart';

Future<bool> showEvictionCaseFormSheet(
  BuildContext context, {
  required int leaseId,
  EvictionCase? evictionCase,
}) async {
  final saved = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    useRootNavigator: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) =>
        _EvictionCaseFormSheet(leaseId: leaseId, evictionCase: evictionCase),
  );
  return saved == true;
}

Future<bool> showEvictionEventFormSheet(
  BuildContext context, {
  required EvictionCase evictionCase,
}) async {
  final saved = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    useRootNavigator: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) => _EvictionEventFormSheet(evictionCase: evictionCase),
  );
  return saved == true;
}

class _EvictionCaseFormSheet extends ConsumerStatefulWidget {
  const _EvictionCaseFormSheet({required this.leaseId, this.evictionCase});

  final int leaseId;
  final EvictionCase? evictionCase;

  @override
  ConsumerState<_EvictionCaseFormSheet> createState() =>
      _EvictionCaseFormSheetState();
}

class _EvictionCaseFormSheetState
    extends ConsumerState<_EvictionCaseFormSheet> {
  final _filedOnCtrl = TextEditingController();
  final _hearingCtrl = TextEditingController();
  final _resolvedCtrl = TextEditingController();
  final _courtCtrl = TextEditingController();
  final _caseNumberCtrl = TextEditingController();
  final _resolutionCtrl = TextEditingController();
  final _notesCtrl = TextEditingController();

  late EvictionCaseStatus _status;
  bool _saving = false;
  String? _error;

  bool get _isEdit => widget.evictionCase != null;

  @override
  void initState() {
    super.initState();
    final evictionCase = widget.evictionCase;
    _status = evictionCase?.status ?? EvictionCaseStatus.filed;
    _filedOnCtrl.text =
        _dateInput(evictionCase?.filedOnDate) ?? _dateInput(DateTime.now())!;
    _hearingCtrl.text = _dateInput(evictionCase?.hearingDate) ?? '';
    _resolvedCtrl.text = _dateInput(evictionCase?.resolvedOnDate) ?? '';
    _courtCtrl.text = evictionCase?.courtName ?? '';
    _caseNumberCtrl.text = evictionCase?.caseNumber ?? '';
    _resolutionCtrl.text = evictionCase?.resolution ?? '';
    _notesCtrl.text = evictionCase?.notes ?? '';
  }

  @override
  void dispose() {
    _filedOnCtrl.dispose();
    _hearingCtrl.dispose();
    _resolvedCtrl.dispose();
    _courtCtrl.dispose();
    _caseNumberCtrl.dispose();
    _resolutionCtrl.dispose();
    _notesCtrl.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _error = null;
    });

    final repo = ref.read(evictionCasesRepositoryProvider);
    final payload = _payload();

    try {
      final evictionCase = widget.evictionCase;
      if (evictionCase == null) {
        await repo.createCase(leaseId: widget.leaseId, data: payload);
      } else {
        await repo.updateCase(evictionCase.id, payload);
      }
      if (mounted) Navigator.of(context).pop(true);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
      rethrow;
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Map<String, dynamic> _payload() {
    final payload = <String, dynamic>{
      'status': _status.wire,
      'filedOnDate': _optionalDate(_filedOnCtrl.text),
      'hearingDate': _optionalDate(_hearingCtrl.text),
      'courtName': _optionalText(_courtCtrl.text),
      'caseNumber': _optionalText(_caseNumberCtrl.text),
      'notes': _optionalText(_notesCtrl.text),
    };

    if (_isEdit) {
      payload['resolvedOnDate'] = _optionalDate(_resolvedCtrl.text);
      payload['resolution'] = _optionalText(_resolutionCtrl.text);
    }

    payload.removeWhere((_, value) => value == null);
    return payload;
  }

  @override
  Widget build(BuildContext context) {
    const gap = SizedBox(height: 12);
    return TabbedFormSheet(
      title: _isEdit ? 'Edit Eviction Case' : 'File Eviction Case',
      saveLabel: _isEdit ? 'Save Case' : 'File Case',
      saving: _saving,
      error: _error,
      onSave: _save,
      tabs: [
        TabbedFormStepSpec(
          label: 'Case',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              DropdownButtonFormField<EvictionCaseStatus>(
                initialValue: _status,
                decoration: const InputDecoration(labelText: 'Status'),
                items: EvictionCaseStatus.values
                    .map(
                      (status) => DropdownMenuItem(
                        value: status,
                        child: Text(status.label),
                      ),
                    )
                    .toList(),
                onChanged: (value) {
                  if (value == null) return;
                  setState(() => _status = value);
                },
              ),
              gap,
              TextFormField(
                key: const Key('eviction-case-filed-on-field'),
                controller: _filedOnCtrl,
                keyboardType: TextInputType.datetime,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(
                  labelText: 'Filed date',
                  helperText: 'YYYY-MM-DD',
                ),
                validator: (value) =>
                    _optionalDateValid(value) ? null : 'Enter YYYY-MM-DD',
              ),
              gap,
              TextFormField(
                key: const Key('eviction-case-hearing-field'),
                controller: _hearingCtrl,
                keyboardType: TextInputType.datetime,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(
                  labelText: 'Hearing date',
                  helperText: 'YYYY-MM-DD',
                ),
                validator: (value) =>
                    _optionalDateValid(value) ? null : 'Enter YYYY-MM-DD',
              ),
              if (_isEdit) ...[
                gap,
                TextFormField(
                  key: const Key('eviction-case-resolved-field'),
                  controller: _resolvedCtrl,
                  keyboardType: TextInputType.datetime,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(
                    labelText: 'Resolved date',
                    helperText: 'YYYY-MM-DD',
                  ),
                  validator: (value) =>
                      _optionalDateValid(value) ? null : 'Enter YYYY-MM-DD',
                ),
              ],
            ],
          ),
        ),
        TabbedFormStepSpec(
          label: 'Court',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextFormField(
                key: const Key('eviction-case-court-field'),
                controller: _courtCtrl,
                textInputAction: TextInputAction.next,
                textCapitalization: TextCapitalization.words,
                decoration: const InputDecoration(labelText: 'Court'),
                validator: (value) => _maxLength(value, 200, 'Court'),
              ),
              gap,
              TextFormField(
                key: const Key('eviction-case-number-field'),
                controller: _caseNumberCtrl,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(labelText: 'Case number'),
                validator: (value) => _maxLength(value, 100, 'Case number'),
              ),
              if (_isEdit) ...[
                gap,
                TextFormField(
                  key: const Key('eviction-case-resolution-field'),
                  controller: _resolutionCtrl,
                  textCapitalization: TextCapitalization.sentences,
                  decoration: const InputDecoration(labelText: 'Resolution'),
                  validator: (value) => _maxLength(value, 500, 'Resolution'),
                ),
              ],
            ],
          ),
        ),
        TabbedFormStepSpec(
          label: 'Notes',
          child: TextFormField(
            key: const Key('eviction-case-notes-field'),
            controller: _notesCtrl,
            minLines: 4,
            maxLines: 8,
            textCapitalization: TextCapitalization.sentences,
            decoration: const InputDecoration(labelText: 'Notes'),
            validator: (value) => _maxLength(value, 1000, 'Notes'),
          ),
        ),
      ],
    );
  }
}

class _EvictionEventFormSheet extends ConsumerStatefulWidget {
  const _EvictionEventFormSheet({required this.evictionCase});

  final EvictionCase evictionCase;

  @override
  ConsumerState<_EvictionEventFormSheet> createState() =>
      _EvictionEventFormSheetState();
}

class _EvictionEventFormSheetState
    extends ConsumerState<_EvictionEventFormSheet> {
  final _eventDateCtrl = TextEditingController();
  final _notesCtrl = TextEditingController();
  EvictionEventType _eventType = EvictionEventType.filed;
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _eventDateCtrl.text = _dateInput(DateTime.now())!;
  }

  @override
  void dispose() {
    _eventDateCtrl.dispose();
    _notesCtrl.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      await ref
          .read(evictionCasesRepositoryProvider)
          .addEvent(
            widget.evictionCase.id,
            data: {
              'eventType': _eventType.wire,
              'eventDate': _eventDateCtrl.text.trim(),
              'notes': _optionalText(_notesCtrl.text),
            }..removeWhere((_, value) => value == null),
          );
      if (mounted) Navigator.of(context).pop(true);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
      rethrow;
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    const gap = SizedBox(height: 12);
    return TabbedFormSheet(
      title: 'Add Eviction Event',
      saveLabel: 'Add Event',
      saving: _saving,
      error: _error,
      onSave: _save,
      tabs: [
        TabbedFormStepSpec(
          label: 'Event',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              DropdownButtonFormField<EvictionEventType>(
                initialValue: _eventType,
                decoration: const InputDecoration(labelText: 'Event'),
                items: EvictionEventType.values
                    .map(
                      (type) => DropdownMenuItem(
                        value: type,
                        child: Text(type.label),
                      ),
                    )
                    .toList(),
                onChanged: (value) {
                  if (value == null) return;
                  setState(() => _eventType = value);
                },
              ),
              gap,
              TextFormField(
                key: const Key('eviction-event-date-field'),
                controller: _eventDateCtrl,
                keyboardType: TextInputType.datetime,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(
                  labelText: 'Event date',
                  helperText: 'YYYY-MM-DD',
                ),
                validator: (value) =>
                    _requiredDateValid(value) ? null : 'Enter YYYY-MM-DD',
              ),
            ],
          ),
        ),
        TabbedFormStepSpec(
          label: 'Notes',
          child: TextFormField(
            key: const Key('eviction-event-notes-field'),
            controller: _notesCtrl,
            minLines: 4,
            maxLines: 8,
            textCapitalization: TextCapitalization.sentences,
            decoration: const InputDecoration(labelText: 'Notes'),
            validator: (value) => _maxLength(value, 1000, 'Notes'),
          ),
        ),
      ],
    );
  }
}

String? _optionalText(String value) {
  final trimmed = value.trim();
  return trimmed.isEmpty ? null : trimmed;
}

String? _optionalDate(String value) {
  final trimmed = value.trim();
  return trimmed.isEmpty ? null : trimmed;
}

bool _requiredDateValid(String? value) {
  final trimmed = value?.trim() ?? '';
  if (!RegExp(r'^\d{4}-\d{2}-\d{2}$').hasMatch(trimmed)) return false;
  return DateTime.tryParse(trimmed) != null;
}

bool _optionalDateValid(String? value) {
  final trimmed = value?.trim() ?? '';
  if (trimmed.isEmpty) return true;
  return _requiredDateValid(trimmed);
}

String? _maxLength(String? value, int max, String label) {
  final trimmed = value?.trim() ?? '';
  if (trimmed.length > max) return '$label must be $max characters or fewer';
  return null;
}

String? _dateInput(DateTime? value) {
  if (value == null) return null;
  final local = value.toLocal();
  return '${local.year.toString().padLeft(4, '0')}-'
      '${local.month.toString().padLeft(2, '0')}-'
      '${local.day.toString().padLeft(2, '0')}';
}
