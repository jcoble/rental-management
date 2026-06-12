import 'package:flutter/material.dart';

/// Upcoming appointments for the signed-in tenant.
///
/// The tenant portal API (`/api/v1/portal/*`) does not yet expose an
/// appointments feed (only the landlord-side `/appointments` exists, which a
/// tenant token can't read). Until a portal appointments endpoint is added this
/// is a clean, honest empty state rather than a dead tile — no `(){}`.
class TenantAppointmentsScreen extends StatelessWidget {
  const TenantAppointmentsScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(title: const Text('Appointments')),
      body: Center(
        child: Padding(
          padding: const EdgeInsets.all(32),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(Icons.event_available_outlined,
                  size: 56, color: cs.onSurfaceVariant),
              const SizedBox(height: 16),
              Text(
                'No upcoming appointments',
                style: theme.textTheme.titleMedium,
              ),
              const SizedBox(height: 8),
              Text(
                'When your landlord schedules a showing, inspection, or visit, '
                "it'll appear here.",
                textAlign: TextAlign.center,
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
