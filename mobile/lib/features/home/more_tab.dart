import 'package:flutter/material.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../ai/ai_tab.dart';
import '../analytics/insights_screen.dart';
import '../applications/applications_list_screen.dart';
import '../appointments/appointments_screen.dart';
import '../banking/banking_screen.dart';
import '../deposits/deposits_screen.dart';
import '../inspections/inspections_list_screen.dart';
import '../leases/leases_list_screen.dart';
import '../maintenance/work_orders_screen.dart';
import '../notices/notices_screen.dart';
import '../owner_reports/owner_reports_screen.dart';
import '../payments/payments_screen.dart';
import '../recurring_maintenance/recurring_maintenance_list_screen.dart';
import '../settings/settings_screen.dart';
import '../team/team_screen.dart';
import '../tenants/tenants_list_screen.dart';
import '../vendors/vendors_list_screen.dart';

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
            icon: Symbols.receipt_long_rounded,
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
            icon: Symbols.build_rounded,
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
            icon: Symbols.event_repeat_rounded,
            label: 'Recurring Maintenance',
            subtitle: 'Scheduled tasks that auto-create work orders',
            color: colorScheme.tertiary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(
                  builder: (_) => const RecurringMaintenanceListScreen(),
                ),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Symbols.fact_check_rounded,
            label: 'Inspections',
            subtitle: 'Walk units with smart checklists',
            color: colorScheme.tertiary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(
                  builder: (_) => const InspectionsListScreen(),
                ),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Symbols.handyman_rounded,
            label: 'Vendors',
            subtitle: 'Text jobs, ratings and scorecards',
            color: colorScheme.tertiary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(
                  builder: (_) => const VendorsListScreen(),
                ),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Symbols.auto_awesome_rounded,
            label: 'Assistant',
            subtitle: 'Daily briefing & questions',
            color: colorScheme.secondary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(
                  builder: (_) => const AiTab(),
                ),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Symbols.group_rounded,
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
            icon: Symbols.description_rounded,
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
            icon: Symbols.assignment_ind_rounded,
            label: 'Applications',
            subtitle: 'Review and approve applicants',
            color: colorScheme.tertiary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(
                    builder: (_) => const ApplicationsListScreen()),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Symbols.mark_email_unread_rounded,
            label: 'Notices',
            subtitle: 'Renewal, late rent and move-out drafts',
            color: colorScheme.secondary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(builder: (_) => const NoticesScreen()),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Symbols.event_rounded,
            label: 'Appointments',
            subtitle: 'Showings and visits',
            color: colorScheme.tertiary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(builder: (_) => const AppointmentsScreen()),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Symbols.insights_rounded,
            label: 'Insights',
            subtitle: 'Occupancy, collections and trends',
            color: colorScheme.primary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(builder: (_) => const InsightsScreen()),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Symbols.shield_rounded,
            label: 'Security Deposits',
            subtitle: 'Holdings, deductions and returns',
            color: colorScheme.secondary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(builder: (_) => const DepositsScreen()),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Symbols.account_balance_rounded,
            label: 'Banking',
            subtitle: 'Read-only reconciliation',
            color: colorScheme.primary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(builder: (_) => const BankingScreen()),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Symbols.bar_chart_rounded,
            label: 'Owner Reports',
            subtitle: 'Annual statements by owner',
            color: colorScheme.tertiary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(
                    builder: (_) => const OwnerReportsScreen()),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Symbols.groups_rounded,
            label: 'Team',
            subtitle: 'Members, roles and access',
            color: colorScheme.primary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(builder: (_) => const TeamScreen()),
              );
            },
          ),
          const SizedBox(height: 12),
          _MenuTile(
            icon: Symbols.settings_rounded,
            label: 'Settings',
            subtitle: 'Notifications and reminders',
            color: colorScheme.secondary,
            onTap: () {
              Navigator.of(context).push<void>(
                MaterialPageRoute<void>(builder: (_) => const SettingsScreen()),
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
    required this.label,
    required this.subtitle,
    required this.color,
    required this.onTap,
  });

  /// A `Symbols.*_rounded` glyph, rendered filled inside a tinted icon chip
  /// (the EdiPlatform tonal icon-chip pattern).
  final IconData icon;
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
        borderRadius: const BorderRadius.all(Radius.circular(28)),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: color.withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(12),
                ),
                child: Icon(icon, color: color, size: 24, fill: 1),
              ),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      label,
                      style: theme.textTheme.titleSmall,
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
              Icon(Symbols.chevron_right_rounded,
                  color: colorScheme.onSurfaceVariant),
            ],
          ),
        ),
      ),
    );
  }
}
