import 'package:flutter/material.dart';

// ── Constants ─────────────────────────────────────────────────────────────────

const appointmentTypes = [
  'Showing',
  'MoveIn',
  'MoveOut',
  'Inspection',
  'MaintenanceVisit',
  'OwnerMeeting',
];

const appointmentStatuses = [
  'Scheduled',
  'Confirmed',
  'Completed',
  'Cancelled',
  'NoShow',
];

// ── Formatting helpers ────────────────────────────────────────────────────────

const _monthNames = [
  '',
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec',
];

String formatAppointmentDate(DateTime d) =>
    '${_monthNames[d.month]} ${d.day}, ${d.year}';

String _padTwo(int n) => n.toString().padLeft(2, '0');

String formatAppointmentTime(DateTime d) {
  final hour = d.hour % 12 == 0 ? 12 : d.hour % 12;
  final period = d.hour < 12 ? 'AM' : 'PM';
  return '$hour:${_padTwo(d.minute)} $period';
}

String formatAppointmentDateTime(DateTime d) =>
    '${formatAppointmentDate(d)}  ${formatAppointmentTime(d)}';

String friendlyAppointmentType(String type) {
  switch (type) {
    case 'Showing':
      return 'Showing';
    case 'MoveIn':
      return 'Move In';
    case 'MoveOut':
      return 'Move Out';
    case 'Inspection':
      return 'Inspection';
    case 'MaintenanceVisit':
      return 'Maintenance';
    case 'OwnerMeeting':
      return 'Owner Meeting';
    default:
      return type;
  }
}

// ── Status chip ───────────────────────────────────────────────────────────────

class AppointmentStatusChip extends StatelessWidget {
  const AppointmentStatusChip({super.key, required this.status});

  final String status;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    Color bg;
    Color fg;
    switch (status) {
      case 'Scheduled':
        bg = colorScheme.secondaryContainer;
        fg = colorScheme.onSecondaryContainer;
        break;
      case 'Confirmed':
        bg = colorScheme.primaryContainer;
        fg = colorScheme.onPrimaryContainer;
        break;
      case 'Completed':
        bg = colorScheme.tertiaryContainer;
        fg = colorScheme.onTertiaryContainer;
        break;
      case 'Cancelled':
      case 'NoShow':
        bg = colorScheme.errorContainer;
        fg = colorScheme.onErrorContainer;
        break;
      default:
        bg = colorScheme.surfaceContainerHighest;
        fg = colorScheme.onSurfaceVariant;
    }
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        status == 'NoShow' ? 'No Show' : status,
        style: TextStyle(fontSize: 11, fontWeight: FontWeight.w600, color: fg),
      ),
    );
  }
}
