import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../auth/auth_controller.dart';
import '../auth/token_store.dart';
import '../../features/appointments/appointments_repository.dart';
import '../../features/leases/leases_repository.dart';
import '../../features/maintenance/work_orders_repository.dart';
import '../../features/messages/messages_repository.dart';
import '../../features/payments/payments_repository.dart';
import '../../features/properties/properties_repository.dart';
import '../../features/scan/scan_repository.dart';
import '../../features/tenants/tenants_repository.dart';
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
  switch (entityType) {
    case 'Payment':
      _refreshIfAlive(ref, paymentsProvider);
      _refreshIfAlive(ref, accountingSummaryProvider);
      _refreshIfAlive(ref, leasesForPaymentProvider);

    case 'Expense':
      _refreshIfAlive(ref, accountingSummaryProvider);

    case 'Lease':
      _refreshIfAlive(ref, leasesProvider);
      _refreshIfAlive(ref, leasesForPaymentProvider);
      // Family providers: invalidate all live instances.
      ref.invalidate(propertyLeasesProvider);
      ref.invalidate(tenantLeasesProvider);
      ref.invalidate(leaseDetailProvider);

    case 'Property':
      _refreshIfAlive(ref, propertiesProvider);

    case 'Unit':
      ref.invalidate(unitsProvider);

    case 'Tenant':
      _refreshIfAlive(ref, tenantsProvider);
      ref.invalidate(tenantDetailProvider);

    case 'WorkOrder':
      _refreshIfAlive(ref, workOrdersProvider);
      ref.invalidate(workOrderDetailProvider);

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
      ref.invalidate(conversationProvider);

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
