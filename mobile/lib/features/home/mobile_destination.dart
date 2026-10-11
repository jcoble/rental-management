import 'package:flutter/material.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/auth/auth_models.dart';
import '../../core/auth/mobile_access_policy.dart';
import '../activity/activity_history_screen.dart';
import '../ai/ai_tab.dart';
import '../analytics/insights_screen.dart';
import '../applications/applications_list_screen.dart';
import '../appointments/appointments_screen.dart';
import '../banking/banking_screen.dart';
import '../deposits/deposits_screen.dart';
import '../inspections/inspections_list_screen.dart';
import '../leases/leases_list_screen.dart';
import '../maintenance/work_orders_screen.dart';
import '../messages/messages_list_screen.dart';
import '../money/expenses_list_screen.dart';
import '../money/money_screen.dart';
import '../notices/notices_screen.dart';
import '../notifications/notifications_inbox_screen.dart';
import '../onboarding/getting_started_screen.dart';
import '../owner_reports/owner_reports_screen.dart';
import '../owners/owners_list_screen.dart';
import '../payments/payments_screen.dart';
import '../properties/properties_tab.dart';
import '../recurring_maintenance/recurring_maintenance_list_screen.dart';
import '../settings/settings_screen.dart';
import '../team/team_screen.dart';
import '../tenants/tenants_list_screen.dart';
import '../units/units_list_screen.dart';
import '../vendors/vendors_list_screen.dart';
import 'mobile_domain_navigation.dart';

class MobileDestination {
  const MobileDestination({
    required this.id,
    required this.icon,
    required this.label,
    required this.subtitle,
    required this.builder,
    this.capabilityKeys = const [],
  });

  final MobileDestinationId id;
  final IconData icon;
  final String label;
  final String subtitle;
  final WidgetBuilder builder;
  final List<String> capabilityKeys;

  bool isVisibleFor(Set<String> capabilities) =>
      capabilityKeys.isEmpty || capabilityKeys.any(capabilities.contains);

  MobileDestination copyWith({String? label, String? subtitle}) =>
      MobileDestination(
        id: id,
        icon: icon,
        label: label ?? this.label,
        subtitle: subtitle ?? this.subtitle,
        builder: builder,
        capabilityKeys: capabilityKeys,
      );

  void open(BuildContext context) {
    final target = _shellTargetFor(id);
    final shellNavigator = mobileShellNavigatorOf(context);
    if (target != null && shellNavigator != null) {
      shellNavigator.openTab(target.$1, destination: target.$2);
      revealMobileShellIfDetached(context);
      return;
    }

    Navigator.of(context).push<void>(MaterialPageRoute<void>(builder: builder));
  }
}

(MobileShellTabId, MobileDestinationId)? _shellTargetFor(
  MobileDestinationId id,
) {
  return switch (id) {
    MobileDestinationId.properties => (
      MobileShellTabId.rentals,
      MobileDestinationId.properties,
    ),
    MobileDestinationId.owners => (
      MobileShellTabId.rentals,
      MobileDestinationId.owners,
    ),
    MobileDestinationId.units => (
      MobileShellTabId.rentals,
      MobileDestinationId.units,
    ),
    MobileDestinationId.tenants => (
      MobileShellTabId.rentals,
      MobileDestinationId.tenants,
    ),
    MobileDestinationId.leases => (
      MobileShellTabId.rentals,
      MobileDestinationId.leases,
    ),
    MobileDestinationId.applications => (
      MobileShellTabId.rentals,
      MobileDestinationId.applications,
    ),
    MobileDestinationId.moneyOverview => (
      MobileShellTabId.money,
      MobileDestinationId.moneyOverview,
    ),
    MobileDestinationId.moneyLedger ||
    MobileDestinationId.payments ||
    MobileDestinationId.expenses => (
      MobileShellTabId.money,
      MobileDestinationId.moneyLedger,
    ),
    MobileDestinationId.deposits => (
      MobileShellTabId.money,
      MobileDestinationId.deposits,
    ),
    MobileDestinationId.banking => (
      MobileShellTabId.money,
      MobileDestinationId.banking,
    ),
    MobileDestinationId.insights => (
      MobileShellTabId.money,
      MobileDestinationId.insights,
    ),
    MobileDestinationId.reports => (
      MobileShellTabId.money,
      MobileDestinationId.reports,
    ),
    MobileDestinationId.workOrders => (
      MobileShellTabId.work,
      MobileDestinationId.workOrders,
    ),
    MobileDestinationId.calendar => (
      MobileShellTabId.work,
      MobileDestinationId.calendar,
    ),
    MobileDestinationId.inspections => (
      MobileShellTabId.work,
      MobileDestinationId.inspections,
    ),
    MobileDestinationId.vendors => (
      MobileShellTabId.work,
      MobileDestinationId.vendors,
    ),
    MobileDestinationId.automations => (
      MobileShellTabId.work,
      MobileDestinationId.automations,
    ),
    MobileDestinationId.notices => (
      MobileShellTabId.work,
      MobileDestinationId.notices,
    ),
    MobileDestinationId.messages => (
      MobileShellTabId.inbox,
      MobileDestinationId.messages,
    ),
    MobileDestinationId.notifications => (
      MobileShellTabId.inbox,
      MobileDestinationId.notifications,
    ),
    MobileDestinationId.activityHistory => (
      MobileShellTabId.inbox,
      MobileDestinationId.activityHistory,
    ),
    _ => null,
  };
}

class MobileDestinationGroup {
  const MobileDestinationGroup({
    required this.title,
    required this.destinations,
  });

  final String title;
  final List<MobileDestination> destinations;
}

enum TenantShellDestinationId {
  home,
  accountAndLease,
  maintenance,
  messages,
  profile,
}

class TenantShellDestination {
  const TenantShellDestination({
    required this.id,
    required this.label,
    required this.bottomNavigationLabel,
    required this.icon,
  });

  final TenantShellDestinationId id;
  final String label;
  final String bottomNavigationLabel;
  final IconData icon;
}

const tenantShellDestinations = <TenantShellDestination>[
  TenantShellDestination(
    id: TenantShellDestinationId.home,
    label: 'Home',
    bottomNavigationLabel: 'Home',
    icon: Symbols.home_rounded,
  ),
  TenantShellDestination(
    id: TenantShellDestinationId.accountAndLease,
    label: 'Account & lease',
    bottomNavigationLabel: 'Lease',
    icon: Symbols.description_rounded,
  ),
  TenantShellDestination(
    id: TenantShellDestinationId.maintenance,
    label: 'Maintenance',
    bottomNavigationLabel: 'Repairs',
    icon: Symbols.build_rounded,
  ),
  TenantShellDestination(
    id: TenantShellDestinationId.messages,
    label: 'Messages',
    bottomNavigationLabel: 'Messages',
    icon: Symbols.forum_rounded,
  ),
  TenantShellDestination(
    id: TenantShellDestinationId.profile,
    label: 'Profile',
    bottomNavigationLabel: 'Profile',
    icon: Symbols.account_circle_rounded,
  ),
];

const gettingStartedDestination = MobileDestination(
  id: MobileDestinationId.gettingStarted,
  icon: Symbols.rocket_launch_rounded,
  label: 'Getting started',
  subtitle: 'Set-up checklist for your rentals',
  builder: _gettingStartedBuilder,
);

const teamDestination = MobileDestination(
  id: MobileDestinationId.team,
  icon: Symbols.groups_rounded,
  label: 'Team',
  subtitle: 'Members, roles and access',
  builder: _teamBuilder,
);

const settingsDestination = MobileDestination(
  id: MobileDestinationId.settings,
  icon: Symbols.settings_rounded,
  label: 'Settings',
  subtitle: 'Notifications and reminders',
  builder: _settingsBuilder,
);

const rentalDestinations = <MobileDestination>[
  MobileDestination(
    id: MobileDestinationId.properties,
    icon: Symbols.apartment_rounded,
    label: 'Properties',
    subtitle: 'Buildings, units and details',
    builder: _propertiesBuilder,
    capabilityKeys: rentalReadCapabilityKeys,
  ),
  MobileDestination(
    id: MobileDestinationId.owners,
    icon: Symbols.account_balance_rounded,
    label: 'Owners',
    subtitle: 'Entities, contacts and property assignments',
    builder: _ownersBuilder,
    capabilityKeys: ownerDirectoryCapabilityKeys,
  ),
  MobileDestination(
    id: MobileDestinationId.units,
    icon: Symbols.home_work_rounded,
    label: 'Units',
    subtitle: "Today's summary for each rental",
    builder: _unitsBuilder,
    capabilityKeys: rentalReadCapabilityKeys,
  ),
  MobileDestination(
    id: MobileDestinationId.tenants,
    icon: Symbols.group_rounded,
    label: 'Tenants',
    subtitle: 'People and contacts',
    builder: _tenantsBuilder,
    capabilityKeys: rentalReadCapabilityKeys,
  ),
  MobileDestination(
    id: MobileDestinationId.leases,
    icon: Symbols.description_rounded,
    label: 'Leases',
    subtitle: 'Agreements and terms',
    builder: _leasesBuilder,
    capabilityKeys: rentalReadCapabilityKeys,
  ),
  MobileDestination(
    id: MobileDestinationId.applications,
    icon: Symbols.assignment_ind_rounded,
    label: 'Applications',
    subtitle: 'Review and approve applicants',
    builder: _applicationsBuilder,
    capabilityKeys: applicationCapabilityKeys,
  ),
];

const depositsDestination = MobileDestination(
  id: MobileDestinationId.deposits,
  icon: Symbols.shield_rounded,
  label: 'Deposits',
  subtitle: 'Holdings, deductions and returns',
  builder: _depositsBuilder,
  capabilityKeys: depositCapabilityKeys,
);

List<MobileDestination> rentalHubDestinationsFor({
  required WorkspaceExperience experience,
  required Set<String> capabilities,
}) => [
  ...visibleMobileDestinations(rentalDestinations, capabilities),
  if (experience == WorkspaceExperience.leasing &&
      depositsDestination.isVisibleFor(capabilities))
    depositsDestination,
];

const moneyHubDestinations = <MobileDestination>[
  MobileDestination(
    id: MobileDestinationId.insights,
    icon: Symbols.insights_rounded,
    label: 'Your rentals',
    subtitle: 'Occupancy, collections and trends',
    builder: _insightsBuilder,
    capabilityKeys: reportsCapabilityKeys,
  ),
  MobileDestination(
    id: MobileDestinationId.moneyOverview,
    icon: Symbols.insights_rounded,
    label: 'Insights',
    subtitle: 'Collection health and cash movement',
    builder: _moneyInsightsBuilder,
    capabilityKeys: moneyOverviewCapabilityKeys,
  ),
  MobileDestination(
    id: MobileDestinationId.moneyLedger,
    icon: Symbols.receipt_long_rounded,
    label: 'Ledger',
    subtitle: 'Payments and expenses in one feed',
    builder: _moneyLedgerBuilder,
    capabilityKeys: moneyLedgerCapabilityKeys,
  ),
  depositsDestination,
  MobileDestination(
    id: MobileDestinationId.banking,
    icon: Symbols.account_balance_rounded,
    label: 'Banking',
    subtitle: 'Reconciliation and matches',
    builder: _bankingBuilder,
    capabilityKeys: bankingCapabilityKeys,
  ),
  MobileDestination(
    id: MobileDestinationId.reports,
    icon: Symbols.bar_chart_rounded,
    label: 'Reports',
    subtitle: 'Annual owner statements',
    builder: _ownerReportsBuilder,
    capabilityKeys: reportsCapabilityKeys,
  ),
];

const workHubDestinations = <MobileDestination>[
  MobileDestination(
    id: MobileDestinationId.workOrders,
    icon: Symbols.build_rounded,
    label: 'Repairs',
    subtitle: 'Open repairs and maintenance requests',
    builder: _workOrdersBuilder,
    capabilityKeys: workOrderCapabilityKeys,
  ),
  MobileDestination(
    id: MobileDestinationId.calendar,
    icon: Symbols.event_rounded,
    label: 'Calendar',
    subtitle: 'Showings and visits',
    builder: _appointmentsBuilder,
    capabilityKeys: ['work.read', 'leasing.showings.manage'],
  ),
  MobileDestination(
    id: MobileDestinationId.inspections,
    icon: Symbols.fact_check_rounded,
    label: 'Inspections',
    subtitle: 'Walk units with smart checklists',
    builder: _inspectionsBuilder,
    capabilityKeys: ['work.read', 'work.manage'],
  ),
  MobileDestination(
    id: MobileDestinationId.vendors,
    icon: Symbols.handyman_rounded,
    label: 'Vendors',
    subtitle: 'Text jobs, ratings and scorecards',
    builder: _vendorsBuilder,
    capabilityKeys: ['work.manage'],
  ),
  MobileDestination(
    id: MobileDestinationId.automations,
    icon: Symbols.event_repeat_rounded,
    label: 'Automations',
    subtitle: 'Recurring maintenance templates',
    builder: _recurringMaintenanceBuilder,
    capabilityKeys: ['work.manage'],
  ),
  MobileDestination(
    id: MobileDestinationId.notices,
    icon: Symbols.mark_email_unread_rounded,
    label: 'Notices',
    subtitle: 'Renewal, late-rent and move-out drafts',
    builder: _noticesBuilder,
    capabilityKeys: [tenantNoticeDraftCapability],
  ),
];

bool canOpenWorkHub(Set<String> capabilities) => workHubDestinations.any(
  (destination) => destination.isVisibleFor(capabilities),
);

bool canOpenWorkOrders(Set<String> capabilities) => workHubDestinations
    .firstWhere(
      (destination) => destination.id == MobileDestinationId.workOrders,
    )
    .isVisibleFor(capabilities);

List<MobileDestination> workDestinationsFor(
  Set<String> capabilities, {
  required bool assignedWorkExperience,
}) => [
  for (final destination in workHubDestinations)
    if (destination.isVisibleFor(capabilities))
      assignedWorkExperience && destination.id == MobileDestinationId.workOrders
          ? destination.copyWith(
              label: 'My work',
              subtitle: 'Repairs and maintenance assigned to you',
            )
          : destination,
];

const inboxHubDestinations = <MobileDestination>[
  MobileDestination(
    id: MobileDestinationId.messages,
    icon: Symbols.forum_rounded,
    label: 'Messages',
    subtitle: 'Tenant and vendor conversations',
    builder: _messagesBuilder,
    capabilityKeys: inboxCapabilityKeys,
  ),
  MobileDestination(
    id: MobileDestinationId.notifications,
    icon: Symbols.notifications_rounded,
    label: 'Notifications',
    subtitle: 'Unread alerts and system updates',
    builder: _notificationsBuilder,
    capabilityKeys: inboxCapabilityKeys,
  ),
  MobileDestination(
    id: MobileDestinationId.activityHistory,
    icon: Symbols.history_rounded,
    label: 'Activity history',
    subtitle: 'Mobile audit trail and record changes',
    builder: _activityHistoryBuilder,
    capabilityKeys: reportsCapabilityKeys,
  ),
];

List<MobileDestination> visibleMobileDestinations(
  Iterable<MobileDestination> destinations,
  Set<String> capabilities,
) => destinations
    .where((destination) => destination.isVisibleFor(capabilities))
    .toList(growable: false);

const browseDestinationGroups = <MobileDestinationGroup>[
  MobileDestinationGroup(
    title: 'Rentals',
    destinations: [gettingStartedDestination, ...rentalDestinations],
  ),
  MobileDestinationGroup(title: 'Work', destinations: workHubDestinations),
  MobileDestinationGroup(
    title: 'Money',
    destinations: [
      MobileDestination(
        id: MobileDestinationId.payments,
        icon: Symbols.receipt_long_rounded,
        label: 'Payments',
        subtitle: 'Track rent and fees',
        builder: _paymentsBuilder,
      ),
      MobileDestination(
        id: MobileDestinationId.expenses,
        icon: Symbols.shopping_bag_rounded,
        label: 'Expenses',
        subtitle: 'Receipts, bills and deductions',
        builder: _expensesBuilder,
      ),
      ...moneyHubDestinations,
    ],
  ),
  MobileDestinationGroup(title: 'Inbox', destinations: inboxHubDestinations),
  MobileDestinationGroup(
    title: 'AI',
    destinations: [
      MobileDestination(
        id: MobileDestinationId.assistant,
        icon: Symbols.auto_awesome_rounded,
        label: 'Assistant',
        subtitle: 'Daily briefing and questions',
        builder: _aiBuilder,
      ),
    ],
  ),
  MobileDestinationGroup(
    title: 'Admin',
    destinations: [teamDestination, settingsDestination],
  ),
];

Widget _aiBuilder(BuildContext context) => const AiTab();
Widget _activityHistoryBuilder(BuildContext context) =>
    const ActivityHistoryScreen();
Widget _applicationsBuilder(BuildContext context) =>
    const ApplicationsListScreen();
Widget _appointmentsBuilder(BuildContext context) => const AppointmentsScreen();
Widget _bankingBuilder(BuildContext context) => const BankingScreen();
Widget _depositsBuilder(BuildContext context) => const DepositsScreen();
Widget _expensesBuilder(BuildContext context) => const ExpensesListScreen();
Widget _gettingStartedBuilder(BuildContext context) =>
    const GettingStartedScreen();
Widget _insightsBuilder(BuildContext context) => const InsightsScreen();
Widget _inspectionsBuilder(BuildContext context) =>
    const InspectionsListScreen();
Widget _leasesBuilder(BuildContext context) => const LeasesListScreen();
Widget _messagesBuilder(BuildContext context) => const MessagesListScreen();
Widget _moneyInsightsBuilder(BuildContext context) => const MoneyScreen();
Widget _moneyLedgerBuilder(BuildContext context) => const MoneyScreen(
  initialView: MoneyScreenView.payments,
  showTransactionSelector: true,
);
Widget _noticesBuilder(BuildContext context) => const NoticesScreen();
Widget _notificationsBuilder(BuildContext context) =>
    const NotificationsInboxScreen();
Widget _ownerReportsBuilder(BuildContext context) => const OwnerReportsScreen();
Widget _ownersBuilder(BuildContext context) => const OwnersListScreen();
Widget _paymentsBuilder(BuildContext context) => const PaymentsScreen();
Widget _propertiesBuilder(BuildContext context) => const PropertiesTab();
Widget _recurringMaintenanceBuilder(BuildContext context) =>
    const RecurringMaintenanceListScreen();
Widget _settingsBuilder(BuildContext context) => const SettingsScreen();
Widget _teamBuilder(BuildContext context) => const TeamScreen();
Widget _tenantsBuilder(BuildContext context) => const TenantsListScreen();
Widget _unitsBuilder(BuildContext context) => const UnitsListScreen();
Widget _vendorsBuilder(BuildContext context) => const VendorsListScreen();
Widget _workOrdersBuilder(BuildContext context) => const WorkOrdersScreen();
