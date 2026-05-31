import 'package:flutter/material.dart';

import '../appointments/appointments_screen.dart';
import '../leases/leases_list_screen.dart';
import '../maintenance/work_orders_screen.dart';
import '../payments/payments_screen.dart';
import '../tenants/tenants_list_screen.dart';

/// "More" tab — hosts Payments and Maintenance (work orders) sub-screens
/// inside their own local Navigator so back navigation stays within the tab.
class MoreTab extends StatelessWidget {
  const MoreTab({super.key});

  @override
  Widget build(BuildContext context) {
    return Navigator(
      onGenerateRoute: (_) => MaterialPageRoute<void>(
        builder: (_) => const _MoreMenu(),
      ),
    );
  }
}

class _MoreMenu extends StatelessWidget {
  const _MoreMenu();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(title: const Text('More')),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          _MenuTile(
            icon: Icons.receipt_long_outlined,
            activeIcon: Icons.receipt_long,
            label: 'Payments',
            subtitle: 'Track rent and fees',
            color: colorScheme.primary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(
                  builder: (_) => const PaymentsScreen(),
                ),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Icons.build_outlined,
            activeIcon: Icons.build,
            label: 'Maintenance',
            subtitle: 'Work orders and repairs',
            color: colorScheme.tertiary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(
                  builder: (_) => const WorkOrdersScreen(),
                ),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Icons.people_outline,
            activeIcon: Icons.people,
            label: 'Tenants',
            subtitle: 'People and contacts',
            color: colorScheme.secondary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(builder: (_) => const TenantsListScreen()),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Icons.description_outlined,
            activeIcon: Icons.description,
            label: 'Leases',
            subtitle: 'Agreements and terms',
            color: colorScheme.primary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(builder: (_) => const LeasesListScreen()),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Icons.event_outlined,
            activeIcon: Icons.event,
            label: 'Appointments',
            subtitle: 'Showings and visits',
            color: colorScheme.tertiary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(builder: (_) => const AppointmentsScreen()),
              );
            },
          ),
        ],
      ),
    );
  }
}

class _MenuTile extends StatelessWidget {
  const _MenuTile({
    required this.icon,
    required this.activeIcon,
    required this.label,
    required this.subtitle,
    required this.color,
    required this.onTap,
  });

  final IconData icon;
  final IconData activeIcon;
  final String label;
  final String subtitle;
  final Color color;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Card(
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: color.withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: Icon(icon, color: color, size: 24),
              ),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      label,
                      style: theme.textTheme.titleSmall
                          ?.copyWith(fontWeight: FontWeight.w600),
                    ),
                    Text(
                      subtitle,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
              ),
              Icon(Icons.chevron_right,
                  color: colorScheme.onSurfaceVariant),
            ],
          ),
        ),
      ),
    );
  }
}
