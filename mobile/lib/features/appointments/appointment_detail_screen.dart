import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import 'appointments_repository.dart';
import 'appointments_screen.dart' show AppointmentFormSheet;
import 'appointments_shared.dart';

// ── Detail screen ─────────────────────────────────────────────────────────────

/// Detail view for a single appointment.
///
/// Shows all fields, provides edit (via form sheet) and quick
/// "Mark complete / cancel" actions based on current status.
class AppointmentDetailScreen extends ConsumerStatefulWidget {
  const AppointmentDetailScreen({super.key, required this.appointment});

  final Appointment appointment;

  @override
  ConsumerState<AppointmentDetailScreen> createState() =>
      _AppointmentDetailScreenState();
}

class _AppointmentDetailScreenState
    extends ConsumerState<AppointmentDetailScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref
          .read(appointmentDetailProvider(widget.appointment.id).notifier)
          .load(),
    );
  }

  Future<void> _refresh() => ref
      .read(appointmentDetailProvider(widget.appointment.id).notifier)
      .refresh();

  void _showEditSheet(Appointment current) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => AppointmentFormSheet(
        existing: current,
        onSaved: _refresh,
      ),
    );
  }

  Future<void> _updateStatus(int id, String newStatus) async {
    try {
      await ref
          .read(appointmentsRepositoryProvider)
          .updateStatus(id, newStatus);
      await _refresh();
    } on ApiException catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(e.message)),
      );
    }
  }

  Future<void> _confirmDelete(int id) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (_) => AlertDialog(
        title: const Text('Delete appointment?'),
        content: const Text('This cannot be undone.'),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );

    if (confirmed != true || !mounted) return;

    try {
      await ref
          .read(appointmentsRepositoryProvider)
          .deleteAppointment(id);
      if (!mounted) return;
      Navigator.of(context).pop();
    } on ApiException catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(e.message)),
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final detailAsync =
        ref.watch(appointmentDetailProvider(widget.appointment.id));
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Appointment'),
        actions: [
          detailAsync.whenOrNull(
                data: (appt) => Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    IconButton(
                      icon: const Icon(Icons.edit_outlined),
                      tooltip: 'Edit',
                      onPressed: () => _showEditSheet(appt),
                    ),
                    IconButton(
                      icon: const Icon(Icons.delete_outline),
                      tooltip: 'Delete',
                      onPressed: () => _confirmDelete(appt.id),
                    ),
                  ],
                ),
              ) ??
              const SizedBox.shrink(),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: detailAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(
            message: e is ApiException ? e.message : e.toString(),
            onRetry: _refresh,
          ),
          data: (appt) => _DetailBody(
            appointment: appt,
            theme: theme,
            onUpdateStatus: (newStatus) => _updateStatus(appt.id, newStatus),
          ),
        ),
      ),
    );
  }
}

// ── Detail body ───────────────────────────────────────────────────────────────

class _DetailBody extends StatelessWidget {
  const _DetailBody({
    required this.appointment,
    required this.theme,
    required this.onUpdateStatus,
  });

  final Appointment appointment;
  final ThemeData theme;
  final Future<void> Function(String) onUpdateStatus;

  @override
  Widget build(BuildContext context) {
    final appt = appointment;
    final colorScheme = theme.colorScheme;
    final actions = _quickActions(appt.status);

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        // Header card
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(
                      child: Text(
                        appt.title,
                        style: theme.textTheme.titleLarge
                            ?.copyWith(fontWeight: FontWeight.w700),
                      ),
                    ),
                    const SizedBox(width: 8),
                    AppointmentStatusChip(status: appt.status),
                  ],
                ),
                const SizedBox(height: 8),
                _LabeledRow(
                  icon: Icons.category_outlined,
                  label: friendlyAppointmentType(appt.type),
                  colorScheme: colorScheme,
                ),
                const SizedBox(height: 4),
                _LabeledRow(
                  icon: Icons.schedule_outlined,
                  label: formatAppointmentDateTime(appt.scheduledStart),
                  colorScheme: colorScheme,
                ),
                if (appt.scheduledEnd != null) ...[
                  const SizedBox(height: 4),
                  _LabeledRow(
                    icon: Icons.schedule_outlined,
                    label:
                        'Ends ${formatAppointmentDateTime(appt.scheduledEnd!)}',
                    colorScheme: colorScheme,
                  ),
                ],
              ],
            ),
          ),
        ),
        const SizedBox(height: 12),

        // People / property card
        if (appt.propertyName != null ||
            appt.tenantName != null ||
            appt.prospectName != null ||
            appt.assignedTo != null) ...[
          Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    'Details',
                    style: theme.textTheme.titleSmall
                        ?.copyWith(fontWeight: FontWeight.w700),
                  ),
                  const SizedBox(height: 12),
                  if (appt.propertyName != null)
                    _DetailRow(label: 'Property', value: appt.propertyName!),
                  if (appt.unitNumber != null)
                    _DetailRow(label: 'Unit', value: appt.unitNumber!),
                  if (appt.tenantName != null)
                    _DetailRow(label: 'Tenant', value: appt.tenantName!),
                  if (appt.prospectName != null)
                    _DetailRow(
                        label: 'Prospect', value: appt.prospectName!),
                  if (appt.prospectEmail != null)
                    _DetailRow(
                        label: 'Email', value: appt.prospectEmail!),
                  if (appt.assignedTo != null)
                    _DetailRow(
                        label: 'Assigned to', value: appt.assignedTo!),
                ],
              ),
            ),
          ),
          const SizedBox(height: 12),
        ],

        // Notes card
        if (appt.notes != null && appt.notes!.isNotEmpty) ...[
          Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    'Notes',
                    style: theme.textTheme.titleSmall
                        ?.copyWith(fontWeight: FontWeight.w700),
                  ),
                  const SizedBox(height: 8),
                  Text(appt.notes!, style: theme.textTheme.bodyMedium),
                ],
              ),
            ),
          ),
          const SizedBox(height: 12),
        ],

        // Quick action buttons
        if (actions.isNotEmpty) ...[
          Text(
            'Quick actions',
            style: theme.textTheme.titleSmall
                ?.copyWith(fontWeight: FontWeight.w700),
          ),
          const SizedBox(height: 8),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: actions
                .map(
                  (a) => _ActionButton(
                    label: a.label,
                    icon: a.icon,
                    isPrimary: a.isPrimary,
                    onTap: () => onUpdateStatus(a.newStatus),
                  ),
                )
                .toList(),
          ),
          const SizedBox(height: 16),
        ],

        // Timestamps
        Text(
          'Created ${formatAppointmentDateTime(appt.createdAt)}',
          style: theme.textTheme.bodySmall
              ?.copyWith(color: colorScheme.onSurfaceVariant),
        ),
        const SizedBox(height: 2),
        Text(
          'Last updated ${formatAppointmentDateTime(appt.updatedAt)}',
          style: theme.textTheme.bodySmall
              ?.copyWith(color: colorScheme.onSurfaceVariant),
        ),
        const SizedBox(height: 24),
      ],
    );
  }
}

// ── Quick action model ────────────────────────────────────────────────────────

class _QuickAction {
  const _QuickAction({
    required this.label,
    required this.icon,
    required this.newStatus,
    this.isPrimary = false,
  });

  final String label;
  final IconData icon;
  final String newStatus;
  final bool isPrimary;
}

List<_QuickAction> _quickActions(String currentStatus) {
  switch (currentStatus) {
    case 'Scheduled':
      return const [
        _QuickAction(
          label: 'Confirm',
          icon: Icons.check_circle_outline,
          newStatus: 'Confirmed',
          isPrimary: true,
        ),
        _QuickAction(
          label: 'Mark complete',
          icon: Icons.task_alt_outlined,
          newStatus: 'Completed',
        ),
        _QuickAction(
          label: 'Cancel',
          icon: Icons.cancel_outlined,
          newStatus: 'Cancelled',
        ),
      ];
    case 'Confirmed':
      return const [
        _QuickAction(
          label: 'Mark complete',
          icon: Icons.task_alt_outlined,
          newStatus: 'Completed',
          isPrimary: true,
        ),
        _QuickAction(
          label: 'No show',
          icon: Icons.person_off_outlined,
          newStatus: 'NoShow',
        ),
        _QuickAction(
          label: 'Cancel',
          icon: Icons.cancel_outlined,
          newStatus: 'Cancelled',
        ),
      ];
    case 'Completed':
      return const [
        _QuickAction(
          label: 'Re-open',
          icon: Icons.refresh_outlined,
          newStatus: 'Scheduled',
        ),
      ];
    case 'Cancelled':
    case 'NoShow':
      return const [
        _QuickAction(
          label: 'Re-schedule',
          icon: Icons.event_repeat_outlined,
          newStatus: 'Scheduled',
          isPrimary: true,
        ),
      ];
    default:
      return const [];
  }
}

// ── Small reusable widgets ────────────────────────────────────────────────────

class _LabeledRow extends StatelessWidget {
  const _LabeledRow({
    required this.icon,
    required this.label,
    required this.colorScheme,
  });

  final IconData icon;
  final String label;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Icon(icon, size: 15, color: colorScheme.onSurfaceVariant),
        const SizedBox(width: 6),
        Expanded(
          child: Text(
            label,
            style: TextStyle(
                fontSize: 13, color: colorScheme.onSurfaceVariant),
          ),
        ),
      ],
    );
  }
}

class _DetailRow extends StatelessWidget {
  const _DetailRow({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 90,
            child: Text(
              label,
              style: TextStyle(
                fontSize: 12,
                color: colorScheme.onSurfaceVariant,
                fontWeight: FontWeight.w500,
              ),
            ),
          ),
          Expanded(
            child: Text(
              value,
              style: theme.textTheme.bodySmall
                  ?.copyWith(fontWeight: FontWeight.w600),
            ),
          ),
        ],
      ),
    );
  }
}

class _ActionButton extends StatelessWidget {
  const _ActionButton({
    required this.label,
    required this.icon,
    required this.onTap,
    this.isPrimary = false,
  });

  final String label;
  final IconData icon;
  final VoidCallback onTap;
  final bool isPrimary;

  @override
  Widget build(BuildContext context) {
    if (isPrimary) {
      return FilledButton.icon(
        onPressed: onTap,
        icon: Icon(icon, size: 16),
        label: Text(label),
      );
    }
    return OutlinedButton.icon(
      onPressed: onTap,
      icon: Icon(icon, size: 16),
      label: Text(label),
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
