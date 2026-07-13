import 'auth_models.dart';

const rentalReadCapabilityKeys = <String>['rentals.read', 'rentals.manage'];

const applicationCapabilityKeys = <String>['leasing.applications.manage'];

const ownerDirectoryCapabilityKeys = <String>[
  'rentals.manage',
  'money.owner-reports.read',
];

const moneyOverviewCapabilityKeys = <String>['money.balances.read'];

const moneyLedgerCapabilityKeys = <String>[
  'money.balances.read',
  'money.charges.manage',
  'money.payments.manage',
  'money.expenses.manage',
];

const depositCapabilityKeys = <String>[
  'money.deposits.manage',
  'leasing.deposits.read',
];

const bankingCapabilityKeys = <String>[
  'money.reconciliation.operate',
  'bank-connections.manage',
];

const reportsCapabilityKeys = <String>[
  'reports.read',
  'money.owner-reports.read',
];

const workOrderCapabilityKeys = <String>[
  'work.read',
  'work.manage',
  'maintenance.assigned-work.read',
  'maintenance.assigned-work.update',
];

const inboxCapabilityKeys = <String>[
  'rentals.read',
  'rentals.manage',
  'work.read',
  'work.manage',
  'leasing.listings.manage',
  'leasing.applications.manage',
  'leasing.showings.manage',
  'leasing.agreements.prepare',
  'leasing.onboarding.manage',
  'maintenance.assigned-work.read',
  'maintenance.assigned-work.update',
  'maintenance.assigned-work.converse',
];

const scanCapabilityKeys = <String>[
  'rentals.manage',
  'work.manage',
  'money.payments.manage',
  'money.expenses.manage',
  'leasing.applications.manage',
  'leasing.agreements.prepare',
];

const notificationManagementCapability = 'notifications.manage';

bool hasAnyMobileCapability(Set<String> capabilities, Iterable<String> keys) =>
    keys.any(capabilities.contains);

bool canUseGlobalScan(Set<String> capabilities) =>
    hasAnyMobileCapability(capabilities, scanCapabilityKeys);

bool canUseVoiceRecord(Set<String> capabilities) =>
    capabilities.contains('money.expenses.manage');

bool canUseAssistant(Set<String> capabilities) =>
    capabilities.contains('reports.read');

bool canManageOwnMobileAlerts(WorkspaceExperience experience) =>
    experience != WorkspaceExperience.tenant;

bool canManageMobileNotificationFoundation(Set<String> capabilities) =>
    capabilities.contains(notificationManagementCapability);

bool canUseManagementOnboarding({
  required WorkspaceExperience experience,
  required Set<String> capabilities,
}) =>
    experience == WorkspaceExperience.management &&
    capabilities.contains('rentals.manage');

bool canOpenRentalsHub(Set<String> capabilities) => hasAnyMobileCapability(
  capabilities,
  [...rentalReadCapabilityKeys, ...applicationCapabilityKeys],
);

bool canOpenRentalsHubForExperience({
  required WorkspaceExperience experience,
  required Set<String> capabilities,
}) =>
    canOpenRentalsHub(capabilities) ||
    (experience == WorkspaceExperience.leasing &&
        capabilities.contains('leasing.deposits.read'));

bool canOpenMoneyHub(Set<String> capabilities) =>
    hasAnyMobileCapability(capabilities, [
      ...moneyOverviewCapabilityKeys,
      ...moneyLedgerCapabilityKeys,
      ...depositCapabilityKeys,
      ...bankingCapabilityKeys,
      ...reportsCapabilityKeys,
    ]);

bool canOpenInboxHub(Set<String> capabilities) =>
    hasAnyMobileCapability(capabilities, inboxCapabilityKeys);

bool canOpenMobilePath({
  required WorkspaceExperience experience,
  required Set<String> capabilities,
  required String path,
}) {
  if (path == '/' ||
      path == '/login' ||
      path == '/register' ||
      path == '/forgot-password' ||
      path == '/reset-password' ||
      path == '/verify-email' ||
      path == '/access-denied') {
    return true;
  }

  if (path == '/choose-setup' ||
      path == '/setting-up' ||
      path == '/live-setup') {
    return canUseManagementOnboarding(
      experience: experience,
      capabilities: capabilities,
    );
  }

  if (experience == WorkspaceExperience.tenant) {
    return path == '/notifications' || path.startsWith('/messages/');
  }

  if (path == '/settings' || path == '/settings/notifications/my-alerts') {
    return canManageOwnMobileAlerts(experience);
  }
  if (path == '/settings/notifications/team-routing' ||
      path == '/settings/notifications/tenant-notices') {
    return canManageOwnMobileAlerts(experience) &&
        canManageMobileNotificationFoundation(capabilities);
  }

  // Owner has a dedicated data-free landing until owner-safe projections are
  // available. Never infer management access from overlapping capabilities.
  if (experience == WorkspaceExperience.owner) return false;

  if (path == '/rentals') {
    return canOpenRentalsHubForExperience(
      experience: experience,
      capabilities: capabilities,
    );
  }
  if (path == '/owners') {
    return experience == WorkspaceExperience.management &&
        hasAnyMobileCapability(capabilities, ownerDirectoryCapabilityKeys);
  }
  if (path.startsWith('/units/')) {
    return hasAnyMobileCapability(capabilities, rentalReadCapabilityKeys);
  }
  if (path == '/work') {
    return hasAnyMobileCapability(capabilities, [
      ...workOrderCapabilityKeys,
      'leasing.showings.manage',
    ]);
  }
  if (path.startsWith('/work-orders/')) {
    return hasAnyMobileCapability(capabilities, workOrderCapabilityKeys);
  }
  if (path == '/money') {
    return experience == WorkspaceExperience.management &&
        canOpenMoneyHub(capabilities);
  }
  if (path.startsWith('/tenant-accounts/')) {
    return experience == WorkspaceExperience.management &&
        hasAnyMobileCapability(capabilities, [
          ...moneyLedgerCapabilityKeys,
          ...depositCapabilityKeys,
        ]);
  }
  if (path.startsWith('/expenses/')) {
    return experience == WorkspaceExperience.management &&
        hasAnyMobileCapability(capabilities, moneyLedgerCapabilityKeys);
  }
  if (path == '/inbox' ||
      path == '/notifications' ||
      path.startsWith('/messages/')) {
    return canOpenInboxHub(capabilities);
  }
  if (path.startsWith('/scan/')) {
    return canUseGlobalScan(capabilities);
  }
  return false;
}
