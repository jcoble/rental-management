import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/models/models.dart';
import '../properties/properties_repository.dart';
import 'appointments_repository.dart';
import 'appointments_shared.dart';
import 'appointment_detail_screen.dart';

// ── Group helper ──────────────────────────────────────────────────────────────

/// Groups appointments into "Upcoming" (now or future / not yet done) and
/// "Past" buckets, sorted upcoming-first / past-newest-first.
Map<String, List<Appointment>> _groupAppointments(List<Appointment> all) {
  final now = DateTime.now();
  final upcoming = <Appointment>[];
  final past = <Appointment>[];

  final sorted = List<Appointment>.from(all)
    ..sort((a, b) => a.scheduledStart.compareTo(b.scheduledStart));

  for (final appt in sorted) {
    final inFuture = !appt.scheduledStart.isBefore(now);
    final isActive =
        appt.status == 'Scheduled' || appt.status == 'Confirmed';
    if (inFuture || isActive) {
      upcoming.add(appt);
    } else {
      past.add(appt);
    }
  }
  past.sort((a, b) => b.scheduledStart.compareTo(a.scheduledStart));

  return {
    if (upcoming.isNotEmpty) 'Upcoming': upcoming,
    if (past.isNotEmpty) 'Past': past,
  };
}

// ── Main screen ───────────────────────────────────────────────────────────────

/// Appointments list — upcoming first, then past, with pull-to-refresh.
class AppointmentsScreen extends ConsumerStatefulWidget {
  const AppointmentsScreen({super.key});

  @override
  ConsumerState<AppointmentsScreen> createState() =>
      _AppointmentsScreenState();
}

class _AppointmentsScreenState extends ConsumerState<AppointmentsScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(appointmentsProvider.notifier).load(),
    );
  }

  Future<void> _refresh() =>
      ref.read(appointmentsProvider.notifier).refresh();

  void _openDetail(BuildContext context, Appointment appt) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => AppointmentDetailScreen(appointment: appt),
      ),
    );
  }

  void _showCreateSheet(BuildContext context) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => AppointmentFormSheet(
        onSaved: () => ref.read(appointmentsProvider.notifier).refresh(),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final appointmentsAsync = ref.watch(appointmentsProvider);
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(title: const Text('Appointments')),
      floatingActionButton: FloatingActionButton(
        onPressed: () => _showCreateSheet(context),
        tooltip: 'New appointment',
        child: const Icon(Icons.add),
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: appointmentsAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(
            message: e is ApiException ? e.message : e.toString(),
            onRetry: _refresh,
          ),
          data: (list) {
            if (list.isEmpty) return const _EmptyBody();

            final groups = _groupAppointments(list);
            final sections = groups.entries.toList();

            return ListView.builder(
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 88),
              itemCount: sections.fold<int>(
                  0, (sum, e) => sum + 1 + e.value.length),
              itemBuilder: (context, index) {
                var offset = 0;
                for (final section in sections) {
                  if (index == offset) {
                    return _SectionHeader(label: section.key, theme: theme);
                  }
                  offset++;
                  final itemIndex = index - offset;
                  if (itemIndex < section.value.length) {
                    final appt = section.value[itemIndex];
                    return _AppointmentCard(
                      appointment: appt,
                      onTap: () => _openDetail(context, appt),
                    );
                  }
                  offset += section.value.length;
                }
                return const SizedBox.shrink();
              },
            );
          },
        ),
      ),
    );
  }
}

// ── Section header ────────────────────────────────────────────────────────────

class _SectionHeader extends StatelessWidget {
  const _SectionHeader({required this.label, required this.theme});

  final String label;
  final ThemeData theme;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 8, top: 4),
      child: Text(
        label,
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w700,
          color: theme.colorScheme.onSurfaceVariant,
        ),
      ),
    );
  }
}

// ── Appointment card ──────────────────────────────────────────────────────────

class _AppointmentCard extends StatelessWidget {
  const _AppointmentCard({
    required this.appointment,
    required this.onTap,
  });

  final Appointment appointment;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final appt = appointment;

    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(14),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: Text(
                      appt.title,
                      style: theme.textTheme.bodyLarge
                          ?.copyWith(fontWeight: FontWeight.w600),
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  const SizedBox(width: 8),
                  AppointmentStatusChip(status: appt.status),
                ],
              ),
              const SizedBox(height: 6),
              Row(
                children: [
                  Icon(Icons.event_outlined,
                      size: 14, color: colorScheme.primary),
                  const SizedBox(width: 4),
                  Text(
                    friendlyAppointmentType(appt.type),
                    style: TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.w500,
                        color: colorScheme.primary),
                  ),
                  const SizedBox(width: 12),
                  Icon(Icons.schedule_outlined,
                      size: 14, color: colorScheme.onSurfaceVariant),
                  const SizedBox(width: 4),
                  Expanded(
                    child: Text(
                      formatAppointmentDateTime(appt.scheduledStart),
                      style: TextStyle(
                          fontSize: 12,
                          color: colorScheme.onSurfaceVariant),
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                ],
              ),
              if (appt.propertyName != null || appt.tenantName != null) ...[
                const SizedBox(height: 4),
                Row(
                  children: [
                    if (appt.propertyName != null) ...[
                      Icon(Icons.apartment_outlined,
                          size: 13, color: colorScheme.onSurfaceVariant),
                      const SizedBox(width: 4),
                      Flexible(
                        child: Text(
                          appt.propertyName!,
                          style: TextStyle(
                              fontSize: 12,
                              color: colorScheme.onSurfaceVariant),
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                    ],
                    if (appt.propertyName != null && appt.tenantName != null)
                      const SizedBox(width: 12),
                    if (appt.tenantName != null) ...[
                      Icon(Icons.person_outline,
                          size: 13, color: colorScheme.onSurfaceVariant),
                      const SizedBox(width: 4),
                      Flexible(
                        child: Text(
                          appt.tenantName!,
                          style: TextStyle(
                              fontSize: 12,
                              color: colorScheme.onSurfaceVariant),
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                    ],
                  ],
                ),
              ],
              if (appt.tenantName == null && appt.prospectName != null) ...[
                const SizedBox(height: 4),
                Row(
                  children: [
                    Icon(Icons.person_search_outlined,
                        size: 13, color: colorScheme.onSurfaceVariant),
                    const SizedBox(width: 4),
                    Flexible(
                      child: Text(
                        appt.prospectName!,
                        style: TextStyle(
                            fontSize: 12,
                            color: colorScheme.onSurfaceVariant),
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                  ],
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

// ── Empty / Error ─────────────────────────────────────────────────────────────

class _EmptyBody extends StatelessWidget {
  const _EmptyBody();

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.event_outlined,
              size: 48, color: colorScheme.onSurfaceVariant),
          const SizedBox(height: 12),
          Text(
            'No appointments yet',
            style: Theme.of(context).textTheme.titleMedium?.copyWith(
                  color: colorScheme.onSurfaceVariant,
                ),
          ),
          const SizedBox(height: 4),
          Text(
            'Tap + to schedule your first appointment.',
            style: TextStyle(color: colorScheme.onSurfaceVariant),
          ),
        ],
      ),
    );
  }
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.error_outline, size: 40, color: colorScheme.error),
            const SizedBox(height: 12),
            Text(
              message,
              textAlign: TextAlign.center,
              style: TextStyle(color: colorScheme.error),
            ),
            const SizedBox(height: 16),
            FilledButton.tonal(
              onPressed: onRetry,
              child: const Text('Retry'),
            ),
          ],
        ),
      ),
    );
  }
}

// ── Appointment form sheet (create + edit) ────────────────────────────────────

/// Bottom-sheet form for creating or editing an appointment.
///
/// Pass [existing] to pre-fill fields for editing; omit for create.
class AppointmentFormSheet extends ConsumerStatefulWidget {
  const AppointmentFormSheet({super.key, this.existing, required this.onSaved});

  final Appointment? existing;
  final VoidCallback onSaved;

  @override
  ConsumerState<AppointmentFormSheet> createState() =>
      _AppointmentFormSheetState();
}

class _AppointmentFormSheetState extends ConsumerState<AppointmentFormSheet> {
  final _formKey = GlobalKey<FormState>();

  late final TextEditingController _titleCtrl;
  late final TextEditingController _prospectNameCtrl;
  late final TextEditingController _prospectEmailCtrl;
  late final TextEditingController _assignedToCtrl;
  late final TextEditingController _notesCtrl;

  late String _selectedType;
  late String _selectedStatus;

  DateTime? _startDate;
  TimeOfDay? _startTime;
  DateTime? _endDate;
  TimeOfDay? _endTime;

  int? _selectedPropertyId;
  int? _selectedTenantId;

  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    final e = widget.existing;
    _titleCtrl = TextEditingController(text: e?.title ?? '');
    _prospectNameCtrl =
        TextEditingController(text: e?.prospectName ?? '');
    _prospectEmailCtrl =
        TextEditingController(text: e?.prospectEmail ?? '');
    _assignedToCtrl = TextEditingController(text: e?.assignedTo ?? '');
    _notesCtrl = TextEditingController(text: e?.notes ?? '');
    _selectedType = e?.type ?? 'Showing';
    _selectedStatus = e?.status ?? 'Scheduled';
    _selectedPropertyId = e?.propertyId;
    _selectedTenantId = e?.tenantId;

    if (e != null) {
      _startDate = e.scheduledStart;
      _startTime = TimeOfDay.fromDateTime(e.scheduledStart);
      if (e.scheduledEnd != null) {
        _endDate = e.scheduledEnd;
        _endTime = TimeOfDay.fromDateTime(e.scheduledEnd!);
      }
    }

    Future.microtask(() {
      ref.read(propertiesProvider.notifier).load();
      ref.read(_tenantsProvider.notifier).load();
    });
  }

  @override
  void dispose() {
    _titleCtrl.dispose();
    _prospectNameCtrl.dispose();
    _prospectEmailCtrl.dispose();
    _assignedToCtrl.dispose();
    _notesCtrl.dispose();
    super.dispose();
  }

  Future<void> _pickStartDateTime() async {
    final date = await showDatePicker(
      context: context,
      initialDate: _startDate ?? DateTime.now(),
      firstDate: DateTime(2000),
      lastDate: DateTime(2100),
    );
    if (date == null || !mounted) return;
    final time = await showTimePicker(
      context: context,
      initialTime: _startTime ?? TimeOfDay.now(),
    );
    if (time == null || !mounted) return;
    setState(() {
      _startDate = date;
      _startTime = time;
    });
  }

  Future<void> _pickEndDateTime() async {
    final date = await showDatePicker(
      context: context,
      initialDate: _endDate ?? _startDate ?? DateTime.now(),
      firstDate: DateTime(2000),
      lastDate: DateTime(2100),
    );
    if (date == null || !mounted) return;
    final time = await showTimePicker(
      context: context,
      initialTime: _endTime ?? TimeOfDay.now(),
    );
    if (time == null || !mounted) return;
    setState(() {
      _endDate = date;
      _endTime = time;
    });
  }

  DateTime? _combineDateAndTime(DateTime? date, TimeOfDay? time) {
    if (date == null || time == null) return null;
    return DateTime(
        date.year, date.month, date.day, time.hour, time.minute);
  }

  String? _startDisplayText() {
    final dt = _combineDateAndTime(_startDate, _startTime);
    return dt != null ? formatAppointmentDateTime(dt) : null;
  }

  String? _endDisplayText() {
    final dt = _combineDateAndTime(_endDate, _endTime);
    return dt != null ? formatAppointmentDateTime(dt) : null;
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;

    final start = _combineDateAndTime(_startDate, _startTime);
    if (start == null) {
      setState(() => _error = 'Please select a start date and time.');
      return;
    }

    final end = _combineDateAndTime(_endDate, _endTime);

    final body = <String, dynamic>{
      'title': _titleCtrl.text.trim(),
      'type': _selectedType,
      'status': _selectedStatus,
      'scheduledStart': start.toIso8601String(),
      if (end != null) 'scheduledEnd': end.toIso8601String(),
      if (_selectedPropertyId != null) 'propertyId': _selectedPropertyId,
      if (_selectedTenantId != null) 'tenantId': _selectedTenantId,
      if (_prospectNameCtrl.text.trim().isNotEmpty)
        'prospectName': _prospectNameCtrl.text.trim(),
      if (_prospectEmailCtrl.text.trim().isNotEmpty)
        'prospectEmail': _prospectEmailCtrl.text.trim(),
      if (_assignedToCtrl.text.trim().isNotEmpty)
        'assignedTo': _assignedToCtrl.text.trim(),
      if (_notesCtrl.text.trim().isNotEmpty)
        'notes': _notesCtrl.text.trim(),
    };

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final repo = ref.read(appointmentsRepositoryProvider);
      if (widget.existing != null) {
        await repo.updateAppointment(widget.existing!.id, body);
      } else {
        await repo.createAppointment(body);
      }
      widget.onSaved();
      if (mounted) Navigator.of(context).pop();
    } on ApiException catch (e) {
      setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;
    final propertiesAsync = ref.watch(propertiesProvider);
    final tenantsAsync = ref.watch(_tenantsProvider);
    final isEdit = widget.existing != null;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottomPadding),
      child: Form(
        key: _formKey,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // Header
              Row(
                children: [
                  Expanded(
                    child: Text(
                      isEdit ? 'Edit Appointment' : 'New Appointment',
                      style: theme.textTheme.titleLarge
                          ?.copyWith(fontWeight: FontWeight.w700),
                    ),
                  ),
                  IconButton(
                    icon: const Icon(Icons.close),
                    onPressed: () => Navigator.of(context).pop(),
                  ),
                ],
              ),
              const SizedBox(height: 16),

              // Title
              TextFormField(
                controller: _titleCtrl,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(labelText: 'Title'),
                validator: (v) =>
                    (v == null || v.trim().isEmpty)
                        ? 'Title is required'
                        : null,
              ),
              const SizedBox(height: 12),

              // Type
              DropdownButtonFormField<String>(
                initialValue: _selectedType,
                decoration: const InputDecoration(labelText: 'Type'),
                items: appointmentTypes
                    .map((t) => DropdownMenuItem(
                          value: t,
                          child: Text(friendlyAppointmentType(t)),
                        ))
                    .toList(),
                onChanged: (v) {
                  if (v != null) setState(() => _selectedType = v);
                },
              ),
              const SizedBox(height: 12),

              // Status
              DropdownButtonFormField<String>(
                initialValue: _selectedStatus,
                decoration: const InputDecoration(labelText: 'Status'),
                items: appointmentStatuses
                    .map((s) => DropdownMenuItem(
                          value: s,
                          child: Text(s == 'NoShow' ? 'No Show' : s),
                        ))
                    .toList(),
                onChanged: (v) {
                  if (v != null) setState(() => _selectedStatus = v);
                },
              ),
              const SizedBox(height: 12),

              // Start date/time
              _DateTimePickerField(
                label: 'Start date & time',
                displayText: _startDisplayText(),
                onTap: _pickStartDateTime,
              ),
              const SizedBox(height: 12),

              // End date/time (optional)
              _DateTimePickerField(
                label: 'End date & time (optional)',
                displayText: _endDisplayText(),
                onTap: _pickEndDateTime,
                trailing: _endDate != null
                    ? IconButton(
                        icon: const Icon(Icons.clear, size: 18),
                        onPressed: () => setState(() {
                          _endDate = null;
                          _endTime = null;
                        }),
                      )
                    : null,
              ),
              const SizedBox(height: 12),

              // Property dropdown
              propertiesAsync.when(
                loading: () => const LinearProgressIndicator(),
                error: (e, st) => const SizedBox.shrink(),
                data: (properties) => DropdownButtonFormField<int?>(
                  initialValue: _selectedPropertyId,
                  decoration:
                      const InputDecoration(labelText: 'Property (optional)'),
                  items: [
                    const DropdownMenuItem<int?>(
                        value: null, child: Text('None')),
                    ...properties.map(
                      (p) => DropdownMenuItem<int?>(
                          value: p.id, child: Text(p.name)),
                    ),
                  ],
                  onChanged: (v) =>
                      setState(() => _selectedPropertyId = v),
                ),
              ),
              const SizedBox(height: 12),

              // Tenant dropdown
              tenantsAsync.when(
                loading: () => const LinearProgressIndicator(),
                error: (e, st) => const SizedBox.shrink(),
                data: (tenants) => DropdownButtonFormField<int?>(
                  initialValue: _selectedTenantId,
                  decoration:
                      const InputDecoration(labelText: 'Tenant (optional)'),
                  items: [
                    const DropdownMenuItem<int?>(
                        value: null, child: Text('None')),
                    ...tenants.map(
                      (t) => DropdownMenuItem<int?>(
                        value: t.id,
                        child: Text(
                            t.fullName ?? '${t.firstName} ${t.lastName}'),
                      ),
                    ),
                  ],
                  onChanged: (v) =>
                      setState(() => _selectedTenantId = v),
                ),
              ),
              const SizedBox(height: 12),

              // Prospect name
              TextFormField(
                controller: _prospectNameCtrl,
                textInputAction: TextInputAction.next,
                decoration:
                    const InputDecoration(labelText: 'Prospect name (optional)'),
              ),
              const SizedBox(height: 12),

              // Prospect email
              TextFormField(
                controller: _prospectEmailCtrl,
                keyboardType: TextInputType.emailAddress,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(
                    labelText: 'Prospect email (optional)'),
                validator: (v) {
                  if (v == null || v.trim().isEmpty) return null;
                  final emailRe = RegExp(r'^[^@]+@[^@]+\.[^@]+$');
                  return emailRe.hasMatch(v.trim())
                      ? null
                      : 'Enter a valid email';
                },
              ),
              const SizedBox(height: 12),

              // Assigned to
              TextFormField(
                controller: _assignedToCtrl,
                textInputAction: TextInputAction.next,
                decoration:
                    const InputDecoration(labelText: 'Assigned to (optional)'),
              ),
              const SizedBox(height: 12),

              // Notes
              TextFormField(
                controller: _notesCtrl,
                textInputAction: TextInputAction.done,
                maxLines: 3,
                decoration:
                    const InputDecoration(labelText: 'Notes (optional)'),
              ),

              if (_error != null) ...[
                const SizedBox(height: 12),
                Container(
                  padding: const EdgeInsets.symmetric(
                      horizontal: 12, vertical: 10),
                  decoration: BoxDecoration(
                    color: colorScheme.errorContainer,
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Text(
                    _error!,
                    style: TextStyle(
                        color: colorScheme.onErrorContainer, fontSize: 13),
                  ),
                ),
              ],

              const SizedBox(height: 20),

              FilledButton(
                onPressed: _saving ? null : _submit,
                child: _saving
                    ? const SizedBox(
                        height: 20,
                        width: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : Text(isEdit ? 'Save Changes' : 'Create Appointment'),
              ),
              const SizedBox(height: 8),
            ],
          ),
        ),
      ),
    );
  }
}

// ── Date/time picker field ────────────────────────────────────────────────────

class _DateTimePickerField extends StatelessWidget {
  const _DateTimePickerField({
    required this.label,
    required this.displayText,
    required this.onTap,
    this.trailing,
  });

  final String label;
  final String? displayText;
  final VoidCallback onTap;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(8),
      child: InputDecorator(
        decoration: InputDecoration(
          labelText: label,
          suffixIcon: trailing ??
              const Icon(Icons.calendar_today_outlined, size: 18),
        ),
        child: Text(
          displayText ?? 'Tap to select',
          style: TextStyle(
            color: displayText != null
                ? colorScheme.onSurface
                : colorScheme.onSurfaceVariant,
          ),
        ),
      ),
    );
  }
}

// ── Tenants notifier (local, used only by the form) ───────────────────────────

class _TenantsNotifier extends Notifier<AsyncValue<List<Tenant>>> {
  @override
  AsyncValue<List<Tenant>> build() => const AsyncValue.loading();

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final dio = ref.read(dioProvider);
      final response = await dio.get<List<dynamic>>('/tenants');
      final data = response.data ?? [];
      final tenants = data
          .whereType<Map<String, dynamic>>()
          .map(Tenant.fromJson)
          .toList();
      state = AsyncValue.data(tenants);
    } on Exception catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }
}

final _tenantsProvider =
    NotifierProvider<_TenantsNotifier, AsyncValue<List<Tenant>>>(
  _TenantsNotifier.new,
);
