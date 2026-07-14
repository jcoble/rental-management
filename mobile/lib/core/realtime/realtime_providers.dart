import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../auth/auth_controller.dart';
import '../auth/auth_repository.dart';
import '../auth/token_store.dart';
import '../../features/accounting/accounting_repository.dart';
import '../../features/analytics/analytics_repository.dart';
import '../../features/appointments/appointments_repository.dart';
import '../../features/applications/applications_repository.dart';
import '../../features/activity/activity_repository.dart';
import '../../features/banking/banking_repository.dart';
import '../../features/deposits/deposits_repository.dart';
import '../../features/home/home_access_providers.dart';
import '../../features/inspections/inspections_repository.dart';
import '../../features/leases/leases_repository.dart';
import '../../features/maintenance/work_orders_repository.dart';
import '../../features/messages/messages_repository.dart';
import '../../features/money/money_repository.dart';
import '../../features/money/transactions_controller.dart';
import '../../features/notices/notices_repository.dart';
import '../../features/notifications/notifications_repository.dart';
import '../../features/onboarding/getting_started_provider.dart';
import '../../features/onboarding/onboarding_repository.dart';
import '../../features/owner_reports/owner_reports_repository.dart';
import '../../features/owners/owners_repository.dart';
import '../../features/payments/payment_detail_screen.dart';
import '../../features/payments/payments_repository.dart';
import '../../features/portal/tenant_portal_repository.dart';
import '../../features/properties/capital_assets_repository.dart';
import '../../features/properties/property_dispositions_repository.dart';
import '../../features/properties/property_loans_repository.dart';
import '../../features/properties/properties_repository.dart';
import '../../features/recurring_maintenance/recurring_maintenance_repository.dart';
import '../../features/scan/scan_repository.dart';
import '../../features/settings/notification_foundation_repository.dart';
import '../../features/team/team_repository.dart';
import '../../features/tenants/tenants_repository.dart';
import '../../features/units/units_repository.dart';
import '../../features/vendors/vendors_repository.dart';
import '../../features/voice/voice_intake_controller.dart';
import 'signalr_service.dart';

// ---------------------------------------------------------------------------
// Service provider
// ---------------------------------------------------------------------------

final signalrServiceProvider = Provider<SignalrService>((ref) {
  final store = ref.watch(tokenStoreProvider);
  final service = SignalrService(store);
  ref.onDispose(service.dispose);
  return service;
});

// ---------------------------------------------------------------------------
// Realtime watcher
// ---------------------------------------------------------------------------

/// Keep this provider alive for the lifetime of the app by watching it from
/// [HomeShell].  It manages the SignalR lifecycle and invalidates feature
/// providers when the server pushes entity-change events.
///
/// Connect on [AuthStateAuthenticated] → disconnect on [AuthStateUnauthenticated]
/// → do nothing during [AuthStateUnknown] (startup splash).
final realtimeWatcherProvider = Provider<void>((ref) {
  final authState = ref.watch(authControllerProvider);
  final service = ref.watch(signalrServiceProvider);

  if (authState is AuthStateAuthenticated) {
    service.connect();

    final sub = service.events.listen(
      (event) => _invalidateForEntity(ref, event.entityType),
    );
    ref.onDispose(sub.cancel);
  } else if (authState is AuthStateUnauthenticated) {
    service.disconnect();
  }
});

/// Clears every access-scoped cache and reconnects SignalR with the token that
/// carries the replacement context/revision. Called only after the auth
/// controller has installed a different canonical access boundary.
Future<void> resetAccessScopedClient(WidgetRef ref) async {
  ref.invalidate(accessContextsProvider);
  ref.invalidate(sandboxStateProvider);
  ref.invalidate(homeBriefingProvider);
  ref.invalidate(homeLatestMessagesProvider);
  ref.invalidate(homeFieldQueueProvider);
  ref.invalidate(activityHistoryScopedProvider);
  ref.invalidate(activityHistoryProvider);
  ref.invalidate(analyticsOverviewProvider);
  ref.invalidate(moneySnapshotProvider);
  ref.invalidate(pastDueProvider);
  ref.invalidate(tenantLedgerEntriesPageProvider);
  ref.invalidate(paymentDetailProvider);
  ref.invalidate(accountingSummaryProvider);
  ref.invalidate(transactionsProvider);
  ref.invalidate(expensesPageProvider);
  ref.invalidate(expensesListProvider);
  ref.invalidate(unitExpensesProvider);
  ref.invalidate(expenseDetailProvider);
  ref.invalidate(expenseReceiptProvider);
  ref.invalidate(bankingSummaryProvider);
  ref.invalidate(bankingTransactionsProvider);
  ref.invalidate(bankingReviewQueueProvider);
  ref.invalidate(depositsProvider);
  ref.invalidate(leaseManagementsPageProvider);
  ref.invalidate(propertyLeaseManagementsProvider);
  ref.invalidate(tenantLeaseManagementsProvider);
  ref.invalidate(leaseManagementDetailProvider);
  ref.invalidate(leaseAgreementHistoryProvider);
  ref.invalidate(leaseAddendumHistoryProvider);
  ref.invalidate(leaseLedgerProvider);
  ref.invalidate(propertiesProvider);
  ref.invalidate(propertiesPageProvider);
  ref.invalidate(availableForLeasePropertiesProvider);
  ref.invalidate(propertyDetailProvider);
  ref.invalidate(unitsProvider);
  ref.invalidate(availableForLeaseUnitsProvider);
  ref.invalidate(unitHealthPageProvider);
  ref.invalidate(unitDashboardProvider);
  ref.invalidate(unitListingWorkspaceProvider);
  ref.invalidate(tenantsProvider);
  ref.invalidate(availableForLeaseTenantsProvider);
  ref.invalidate(tenantsPageProvider);
  ref.invalidate(tenantDetailProvider);
  ref.invalidate(workOrdersProvider);
  ref.invalidate(workOrdersPageProvider);
  ref.invalidate(workOrderDetailProvider);
  ref.invalidate(workOrderDocumentsProvider);
  ref.invalidate(documentBytesProvider);
  ref.invalidate(propertiesForWoProvider);
  ref.invalidate(applicationsProvider);
  ref.invalidate(applicationsPageProvider);
  ref.invalidate(applicationDetailProvider);
  ref.invalidate(applicationScreeningProvider);
  ref.invalidate(inspectionsProvider);
  ref.invalidate(inspectionsPageProvider);
  ref.invalidate(inspectionDetailProvider);
  ref.invalidate(inspectionTemplatesProvider);
  ref.invalidate(inspectionPropertiesProvider);
  ref.invalidate(inspectionUnitsProvider);
  ref.invalidate(inspectionPhotoBytesProvider);
  ref.invalidate(ownersPageProvider);
  ref.invalidate(ownerDetailProvider);
  ref.invalidate(ownerSummariesProvider);
  ref.invalidate(ownerStatementProvider);
  ref.invalidate(ownerDistributionsProvider);
  ref.invalidate(propertyLoansProvider);
  ref.invalidate(loanPaymentsProvider);
  ref.invalidate(propertyCapitalAssetsProvider);
  ref.invalidate(propertyDispositionsProvider);
  ref.invalidate(vendorsProvider);
  ref.invalidate(vendorsBySortProvider);
  ref.invalidate(vendorsPageProvider);
  ref.invalidate(vendorScorecardProvider);
  ref.invalidate(appointmentsProvider);
  ref.invalidate(appointmentDetailProvider);
  ref.invalidate(scanListFamilyProvider);
  ref.invalidate(conversationsProvider);
  ref.invalidate(conversationsPageProvider);
  ref.invalidate(conversationProvider);
  ref.invalidate(unreadCountProvider);
  ref.invalidate(inboxProvider);
  ref.invalidate(gettingStartedSignalsProvider);
  ref.invalidate(gettingStartedProgressProvider);
  ref.invalidate(noticeDraftsProvider);
  ref.invalidate(recurringMaintenanceProvider);
  ref.invalidate(recurringPropertiesProvider);
  ref.invalidate(recurringUnitsProvider);
  ref.invalidate(recurringVendorsProvider);
  ref.invalidate(myAlertsProvider);
  ref.invalidate(teamRoleProfilesProvider);
  ref.invalidate(teamProvider);
  ref.invalidate(tenantPortalSnapshotProvider);
  ref.invalidate(tenantPortalAccountProvider);
  ref.invalidate(tenantPortalChargesPageProvider);
  ref.invalidate(tenantPortalEntriesPageProvider);
  ref.invalidate(tenantWorkOrderDetailProvider);
  ref.invalidate(tenantAutopayStatusProvider);
  ref.invalidate(voiceConversationProvider);

  final service = ref.read(signalrServiceProvider);
  await service.disconnect();
  await service.connect();
}

// ---------------------------------------------------------------------------
// Entity → provider invalidation map
// ---------------------------------------------------------------------------

/// Maps the server-side [entityType] string to the Riverpod providers that
/// should be refreshed.  Unknown types are silently ignored so new server-side
/// entity types don't crash the client.
///
/// List providers that follow the `Notifier` pattern are refreshed via their
/// `refresh()` method (guarded by [ref.exists]).  Family providers and
/// autoDispose FutureProviders are invalidated via [ref.invalidate], which is
/// always safe — a no-op when no instances are live.
void _invalidateForEntity(Ref ref, String entityType) {
  _refreshIfAlive(ref, activityHistoryProvider);

  switch (entityType) {
    case 'Payment':
    case 'TenantLedgerEntry':
      ref.invalidate(tenantLedgerEntriesPageProvider);
      ref.invalidate(moneySnapshotProvider);
      _refreshIfAlive(ref, pastDueProvider);
      _refreshIfAlive(ref, accountingSummaryProvider);

    case 'Expense':
      ref.invalidate(expensesPageProvider);
      ref.invalidate(expensesListProvider);
      _refreshIfAlive(ref, accountingSummaryProvider);

    case 'LeaseManagement':
    case 'LeaseAgreement':
      ref.invalidate(gettingStartedSignalsProvider);
      ref.invalidate(leaseManagementsPageProvider);
      ref.invalidate(propertyLeaseManagementsProvider);
      ref.invalidate(tenantLeaseManagementsProvider);
      ref.invalidate(leaseManagementDetailProvider);
      ref.invalidate(leaseAgreementHistoryProvider);
      ref.invalidate(leaseLedgerProvider);

    case 'Property':
      _refreshIfAlive(ref, propertiesProvider);
      ref.invalidate(propertiesPageProvider);
      ref.invalidate(gettingStartedSignalsProvider);

    case 'Unit':
      ref.invalidate(unitsProvider);
      ref.invalidate(gettingStartedSignalsProvider);

    case 'Tenant':
      _refreshIfAlive(ref, tenantsProvider);
      ref.invalidate(tenantsPageProvider);
      ref.invalidate(tenantDetailProvider);
      ref.invalidate(gettingStartedSignalsProvider);

    case 'WorkOrder':
      _refreshIfAlive(ref, workOrdersProvider);
      ref.invalidate(workOrdersPageProvider);
      ref.invalidate(workOrderDetailProvider);

    case 'RentalApplication':
      ref.invalidate(applicationsProvider);
      ref.invalidate(applicationsPageProvider);
      ref.invalidate(applicationDetailProvider);

    case 'Inspection':
      ref.invalidate(inspectionsProvider);
      ref.invalidate(inspectionsPageProvider);
      ref.invalidate(inspectionDetailProvider);

    case 'OwnerEntity':
      ref.invalidate(ownersPageProvider);
      ref.invalidate(ownerDetailProvider);
      ref.invalidate(gettingStartedSignalsProvider);

    case 'OwnerDistribution':
      _refreshIfAlive(ref, ownerSummariesProvider);
      _refreshIfAlive(ref, ownerStatementProvider);
      ref.invalidate(ownerDistributionsProvider);
      _refreshIfAlive(ref, accountingSummaryProvider);

    case 'Loan':
      ref.invalidate(propertyLoansProvider);

    case 'CapitalAsset':
      ref.invalidate(propertyCapitalAssetsProvider);
      _refreshIfAlive(ref, accountingSummaryProvider);

    case 'PropertyDisposition':
      ref.invalidate(propertyDispositionsProvider);
      _refreshIfAlive(ref, propertiesProvider);
      ref.invalidate(propertiesPageProvider);
      ref.invalidate(propertyDetailProvider);
      ref.invalidate(unitsProvider);
      ref.invalidate(propertyLeaseManagementsProvider);
      ref.invalidate(leaseManagementDetailProvider);
      ref.invalidate(leaseManagementsPageProvider);
      ref.invalidate(propertyCapitalAssetsProvider);
      _refreshIfAlive(ref, accountingSummaryProvider);

    case 'Portfolio':
      ref.invalidate(gettingStartedSignalsProvider);

    case 'Vendor':
      ref.invalidate(vendorsProvider);
      ref.invalidate(vendorsPageProvider);

    case 'Appointment':
      _refreshIfAlive(ref, appointmentsProvider);
      ref.invalidate(appointmentDetailProvider);

    case 'ScanDraft':
      // autoDispose FutureProvider.family — invalidating re-fetches every
      // currently-mounted variant (e.g. ScanListScreen with filter = null).
      ref.invalidate(scanListFamilyProvider);

    case 'Conversation':
      // Refresh the thread list (unread counts + ordering) and invalidate the
      // open-thread family so a message sent from web/portal appears live.
      _refreshIfAlive(ref, conversationsProvider);
      ref.invalidate(conversationsPageProvider);
      ref.invalidate(conversationProvider);

    case 'Notification':
      // In-app Notification rows are broadcast over THIS (updates) hub by the
      // API/Engine (RentChargeService, ConversationService, LateFeeService,
      // LeaseExpiryReminderService, the SMS-inbound services, ...). Refresh the
      // unread badge and, if the inbox screen is open, its list — so a new
      // notification appears live without a manual reload.
      _refreshIfAlive(ref, unreadCountProvider);
      _refreshIfAlive(ref, inboxProvider);

    default:
      // Unknown entity type — defensive no-op.
      break;
  }
}

/// Calls [refresh()] on a [NotifierProvider]'s notifier only when the
/// provider is currently alive in the Riverpod container.
///
/// Avoids force-creating providers just to trigger a no-op refresh on a
/// screen the user hasn't visited yet.
void _refreshIfAlive<N extends Notifier<dynamic>>(
  Ref ref,
  NotifierProvider<N, dynamic> provider,
) {
  if (!ref.exists(provider)) return;
  // ignore: avoid_dynamic_calls
  (ref.read(provider.notifier) as dynamic).refresh();
}
