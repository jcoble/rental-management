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
import '../recurring_maintenance/recurring_maintenance_list_screen.dart';
import '../money/expenses_list_screen.dart';
import '../notices/notices_screen.dart';
import '../owner_reports/owner_reports_screen.dart';
import '../payments/payments_screen.dart';
import '../properties/properties_tab.dart';
import '../settings/settings_screen.dart';
import '../team/team_screen.dart';
import '../tenants/tenants_list_screen.dart';
import '../vendors/vendors_list_screen.dart';

/// One browsable destination.
class _BrowseItem {
  const _BrowseItem({
    required this.icon,
    required this.label,
    required this.subtitle,
    required this.builder,
  });

  final IconData icon;
  final String label;
  final String subtitle;
  final WidgetBuilder builder;
}

class _BrowseGroup {
  const _BrowseGroup({required this.title, required this.items});

  final String title;
  final List<_BrowseItem> items;
}

/// Grouped, searchable "Browse" screen — the replacement for the flat 17-tile
/// More list. The Rentals group name is frozen across web + mobile (web nav
/// `AppShell.svelte` calls the same Properties/Tenants/Leases/Applications group
/// "Rentals"); see `Docs/label-glossary.md` for the canonical cross-surface
/// terms. Daily destinations (Today, Money tab, Work tab, Capture) live in the
/// shell; everything else is discoverable here.
class MoreTab extends StatefulWidget {
  const MoreTab({super.key});

  @override
  State<MoreTab> createState() => _MoreTabState();
}

class _MoreTabState extends State<MoreTab> {
  final _searchCtrl = TextEditingController();
  String _query = '';

  static final _groups = <_BrowseGroup>[
    _BrowseGroup(
      title: 'Rentals',
      items: [
        _BrowseItem(
          icon: Symbols.apartment_rounded,
          label: 'Properties',
          subtitle: 'Buildings, units and details',
          builder: (_) => const PropertiesTab(),
        ),
        _BrowseItem(
          icon: Symbols.group_rounded,
          label: 'Tenants',
          subtitle: 'People and contacts',
          builder: (_) => const TenantsListScreen(),
        ),
        _BrowseItem(
          icon: Symbols.description_rounded,
          label: 'Leases',
          subtitle: 'Agreements and terms',
          builder: (_) => const LeasesListScreen(),
        ),
        _BrowseItem(
          icon: Symbols.assignment_ind_rounded,
          label: 'Applications',
          subtitle: 'Review and approve applicants',
          builder: (_) => const ApplicationsListScreen(),
        ),
      ],
    ),
    _BrowseGroup(
      title: 'Operations',
      items: [
        _BrowseItem(
          icon: Symbols.event_repeat_rounded,
          label: 'Recurring maintenance',
          subtitle: 'Scheduled tasks that auto-create work orders',
          builder: (_) => const RecurringMaintenanceListScreen(),
        ),
        _BrowseItem(
          icon: Symbols.fact_check_rounded,
          label: 'Inspections',
          subtitle: 'Walk units with smart checklists',
          builder: (_) => const InspectionsListScreen(),
        ),
        _BrowseItem(
          icon: Symbols.handyman_rounded,
          label: 'Vendors',
          subtitle: 'Text jobs, ratings and scorecards',
          builder: (_) => const VendorsListScreen(),
        ),
        _BrowseItem(
          icon: Symbols.event_rounded,
          label: 'Appointments',
          subtitle: 'Showings and visits',
          builder: (_) => const AppointmentsScreen(),
        ),
        _BrowseItem(
          icon: Symbols.mark_email_unread_rounded,
          label: 'Notices',
          subtitle: 'Renewal, late rent and move-out drafts',
          builder: (_) => const NoticesScreen(),
        ),
      ],
    ),
    _BrowseGroup(
      title: 'Money',
      items: [
        _BrowseItem(
          icon: Symbols.receipt_long_rounded,
          label: 'Payments',
          subtitle: 'Track rent and fees',
          builder: (_) => const PaymentsScreen(),
        ),
        _BrowseItem(
          icon: Symbols.shopping_bag_rounded,
          label: 'Expenses',
          subtitle: 'Receipts, bills and deductions',
          builder: (_) => const ExpensesListScreen(),
        ),
        _BrowseItem(
          icon: Symbols.shield_rounded,
          label: 'Security deposits',
          subtitle: 'Holdings, deductions and returns',
          builder: (_) => const DepositsScreen(),
        ),
        _BrowseItem(
          icon: Symbols.account_balance_rounded,
          label: 'Banking',
          subtitle: 'Reconciliation and matches',
          builder: (_) => const BankingScreen(),
        ),
        _BrowseItem(
          icon: Symbols.bar_chart_rounded,
          label: 'Owner reports',
          subtitle: 'Annual statements by owner',
          builder: (_) => const OwnerReportsScreen(),
        ),
        _BrowseItem(
          icon: Symbols.insights_rounded,
          label: 'Insights',
          subtitle: 'Occupancy, collections and trends',
          builder: (_) => const InsightsScreen(),
        ),
      ],
    ),
    _BrowseGroup(
      title: 'AI',
      items: [
        _BrowseItem(
          icon: Symbols.auto_awesome_rounded,
          label: 'Assistant',
          subtitle: 'Daily briefing and questions',
          builder: (_) => const AiTab(),
        ),
      ],
    ),
    _BrowseGroup(
      title: 'Admin',
      items: [
        _BrowseItem(
          icon: Symbols.groups_rounded,
          label: 'Team',
          subtitle: 'Members, roles and access',
          builder: (_) => const TeamScreen(),
        ),
        _BrowseItem(
          icon: Symbols.settings_rounded,
          label: 'Settings',
          subtitle: 'Notifications and reminders',
          builder: (_) => const SettingsScreen(),
        ),
      ],
    ),
  ];

  @override
  void dispose() {
    _searchCtrl.dispose();
    super.dispose();
  }

  bool _matches(_BrowseItem item) {
    if (_query.isEmpty) return true;
    final q = _query.toLowerCase();
    return item.label.toLowerCase().contains(q) ||
        item.subtitle.toLowerCase().contains(q);
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    // Filter groups to those with at least one matching item.
    final groups = <_BrowseGroup>[];
    for (final g in _groups) {
      final items = g.items.where(_matches).toList();
      if (items.isNotEmpty) {
        groups.add(_BrowseGroup(title: g.title, items: items));
      }
    }

    return Scaffold(
      appBar: AppBar(title: const Text('Browse')),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
            child: SearchBar(
              controller: _searchCtrl,
              hintText: 'Search',
              leading: const Icon(Icons.search),
              trailing: [
                if (_query.isNotEmpty)
                  IconButton(
                    icon: const Icon(Icons.close),
                    onPressed: () {
                      _searchCtrl.clear();
                      setState(() => _query = '');
                    },
                  ),
              ],
              onChanged: (v) => setState(() => _query = v),
            ),
          ),
          Expanded(
            child: groups.isEmpty
                ? Center(
                    child: Text(
                      'No matches for "$_query".',
                      style: theme.textTheme.bodyMedium
                          ?.copyWith(color: cs.onSurfaceVariant),
                    ),
                  )
                : ListView(
                    padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
                    children: [
                      for (final group in groups) ...[
                        Padding(
                          padding: const EdgeInsets.fromLTRB(4, 16, 4, 8),
                          child: Text(
                            group.title.toUpperCase(),
                            style: theme.textTheme.labelMedium?.copyWith(
                              color: cs.primary,
                              fontWeight: FontWeight.w700,
                              letterSpacing: 0.8,
                            ),
                          ),
                        ),
                        for (final item in group.items) ...[
                          _MenuTile(
                            icon: item.icon,
                            label: item.label,
                            subtitle: item.subtitle,
                            color: cs.primary,
                            onTap: () => Navigator.of(context).push<void>(
                              MaterialPageRoute<void>(builder: item.builder),
                            ),
                          ),
                          const SizedBox(height: 10),
                        ],
                      ],
                    ],
                  ),
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
                    Text(label, style: theme.textTheme.titleSmall),
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
