import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../leases/leases_repository.dart';
import '../properties/properties_repository.dart';
import '../settings/notification_settings_repository.dart';
import '../tenants/tenants_repository.dart';
import 'getting_started_tasks.dart';

/// Derives the getting-started [GettingStartedSignals] from data the app already
/// fetches elsewhere — the property / tenant / lease lists plus notification
/// settings. There are NO per-task or per-row calls: "has a unit" comes from
/// `sum(property.unitCount)` on the property list, not a units fetch per
/// property, exactly like the web hook.
///
/// `autoDispose` mirrors the home-tab `FutureProvider.autoDispose` style: the
/// dashboard card and the checklist screen are the only watchers, so the queries
/// drop the moment both are gone. The four list/settings calls run in parallel.
final gettingStartedSignalsProvider =
    FutureProvider.autoDispose<GettingStartedSignals>((ref) async {
  final properties = ref.watch(propertiesRepositoryProvider);
  final tenants = ref.watch(tenantsRepositoryProvider);
  final leases = ref.watch(leasesRepositoryProvider);
  final settingsRepo = ref.watch(notificationSettingsRepositoryProvider);

  final results = await Future.wait([
    properties.listProperties(),
    tenants.listTenants(),
    leases.listLeases(),
    settingsRepo.get(),
  ]);

  final propertyList = results[0] as List;
  final tenantList = results[1] as List;
  final leaseList = results[2] as List;
  final settings = results[3] as NotificationSettings;

  final unitCount = propertyList.fold<int>(
    0,
    (sum, p) => sum + ((p.unitCount as int?) ?? 0),
  );

  // Email delivery is "set up" when any notification type routes to email, or a
  // daily-briefing email recipient is configured. Mirrors the web "set where
  // alerts go" task (mobile has no dedicated notification-email endpoint, so we
  // read it off the channel matrix the settings screen already loads).
  final hasEmail = settings.channelPreferences.any((p) => p.enableEmail) ||
      settings.dailyBriefingEmailRecipients.isNotEmpty;

  return GettingStartedSignals(
    propertyCount: propertyList.length,
    unitCount: unitCount,
    tenantCount: tenantList.length,
    leaseCount: leaseList.length,
    hasNotificationEmail: hasEmail,
    // Only the two toggles that default OFF count as a deliberate setup; matches
    // the web predicate exactly (lease-expiry reminders default ON server-side).
    hasAutomations: settings.enableRentCharges || settings.enableLateFees,
    hasTexting: settings.signalWireConfigured,
  );
});

/// Convenience: the progress rollup over the live signals. Returns null while
/// the signals are still loading or errored, so callers can hide their surface
/// (no flash of an "all to-do" state) until data settles.
final gettingStartedProgressProvider =
    Provider.autoDispose<GettingStartedProgress?>((ref) {
  final signals = ref.watch(gettingStartedSignalsProvider);
  return signals.maybeWhen(
    data: computeGettingStartedProgress,
    orElse: () => null,
  );
});
