import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:material_symbols_icons/symbols.dart';
import 'package:url_launcher/url_launcher.dart';

import 'package:go_router/go_router.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/mobile_access_policy.dart';
import '../../core/theme/app_recipes.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/models/models.dart';
import '../../core/presentation/plain_english_labels.dart';
import '../../core/push/push_service.dart';
import '../../core/push/mobile_navigation_intent.dart';
import '../../core/realtime/realtime_providers.dart';
import '../../core/voice/voice_command.dart';
import '../../core/voice/voice_command_controller.dart';
import '../../core/router/mobile_access_denied_screen.dart';
import '../accounting/accounting_repository.dart';
import '../onboarding/onboarding_repository.dart';
import '../notifications/notifications_inbox_screen.dart';
import '../ai/ai_models.dart';
import '../appointments/appointments_screen.dart';
import '../appointments/tenant_appointments_screen.dart';
import '../inspections/inspections_list_screen.dart';
import '../leasing/leasing_landing_screen.dart';
import '../leases/lease_detail_screen.dart';
import '../leases/leases_list_screen.dart';
import '../maintenance/work_order_unit_aware_loader.dart';
import '../messages/message_detail_screen.dart';
import '../technician/technician_landing_screen.dart';
import '../messages/message_models.dart';
import '../messages/messages_list_screen.dart';
import '../money/expense_detail_screen.dart';
import '../money/money_snapshot_card.dart';
import '../money/overdue_screen.dart';
import '../notifications/notifications_repository.dart';
import '../onboarding/go_live_sheet.dart';
import '../onboarding/getting_started_provider.dart';
import '../onboarding/getting_started_screen.dart';
import '../onboarding/getting_started_tasks.dart';
import '../payments/payment_detail_screen.dart';
import '../payments/payments_screen.dart';
import '../portal/tenant_account_history_screen.dart';
import '../portal/tenant_maintenance_screen.dart';
import '../portal/tenant_portal_repository.dart';
import '../scan/scan_review_screen.dart';
import '../tenants/tenant_detail_screen.dart';
import '../tenants/tenants_list_screen.dart';
import '../tenants/tenant_lease_screen.dart';
import '../units/unit_command_center_screen.dart';
import '../../core/navigation/mobile_restoration_state.dart';
import 'mobile_destination.dart';
import 'home_access_providers.dart';
import 'mobile_domain_hub.dart';
import 'mobile_domain_navigation.dart';
import 'mobile_quick_action_fab.dart';
import 'mobile_quick_action_helpers.dart';
import 'mobile_role_shell.dart';
import 'mobile_shell_actions.dart';
import 'owner_landing_screen.dart';

Future<void> _openGoLiveSheetAndRefreshHome(
  BuildContext context,
  WidgetRef ref,
) async {
  final wentLive = await showGoLiveSheet(context);
  if (wentLive != true) return;
  ref.invalidate(homeBriefingProvider);
  ref.invalidate(homeLatestMessagesProvider);
  ref.invalidate(homeFieldQueueProvider);
}

// ---------------------------------------------------------------------------
// HomeShell
// ---------------------------------------------------------------------------

/// Bottom-navigation app shell.
///
/// Landlord tabs: Today · Rentals · Money · Work · Inbox.
/// Tenant tabs: Home · Account & lease · Maintenance · Messages · Profile.
class HomeShell extends ConsumerStatefulWidget {
  const HomeShell({super.key});

  @override
  ConsumerState<HomeShell> createState() => _HomeShellState();
}

class _HomeShellState extends ConsumerState<HomeShell>
    with WidgetsBindingObserver, RestorationMixin {
  final _domainNavigators = <MobileShellTabId, MobileDomainNavigator>{};
  late final List<MobileQuickActionController> _quickActionControllers =
      List.generate(_tabs.length, (_) => MobileQuickActionController());
  late final MobileShellNavigator _shellNavigator;
  int _selectedIndex = 0;
  final _restorationState = RestorableMobileRestorationState();
  bool _restoredUnitStack = false;
  String? _durableRestorationAuthorityKey;
  Future<void>? _durableRestorationLoad;
  int _durableRestorationGeneration = 0;
  bool _suppressDurableRestorationSave = false;

  @override
  String? get restorationId => 'home-shell';

  @override
  void restoreState(RestorationBucket? oldBucket, bool initialRestore) {
    registerForRestoration(_restorationState, 'unit-navigation');
  }

  static const _tabs = [
    _TabItem(label: 'Today', icon: Symbols.home_rounded),
    _TabItem(label: 'Rentals', icon: Symbols.apartment_rounded),
    _TabItem(label: 'Money', icon: Symbols.savings_rounded),
    _TabItem(label: 'Work', icon: Symbols.build_rounded),
    _TabItem(label: 'Inbox', icon: Symbols.inbox_rounded),
  ];

  static const _tabIds = [
    MobileShellTabId.today,
    MobileShellTabId.rentals,
    MobileShellTabId.money,
    MobileShellTabId.work,
    MobileShellTabId.inbox,
  ];

  static const _tenantTabs = [
    _TabItem(label: 'Home', icon: Symbols.home_rounded),
    _TabItem(label: 'Account & lease', icon: Symbols.description_rounded),
    _TabItem(label: 'Maintenance', icon: Symbols.build_rounded),
    _TabItem(label: 'Messages', icon: Symbols.forum_rounded),
    _TabItem(label: 'Profile', icon: Symbols.account_circle_rounded),
  ];

  void _openAssistant() => openMobileAssistant(context);
  void _openCapture() => openAuthorizedMobileScan(context, ref);
  void _openRecord() => openMobileRecord(context);

  void _registerDomain(MobileShellTabId tab, MobileDomainNavigator controller) {
    _domainNavigators[tab] = controller;
    if (tab == MobileShellTabId.rentals) _prepareRestorationForAuthority();
  }

  String _authorityKey(AuthStateAuthenticated auth) {
    final context = auth.access.selectedContext;
    return '${auth.user.id}:${context.accessContextId}:${context.accessRevision}';
  }

  bool _matchesCurrentAuthority(
    MobileRestorationState state,
    AuthStateAuthenticated auth,
  ) {
    final context = auth.access.selectedContext;
    return state.matchesAuthority(
      userId: auth.user.id,
      contextId: context.accessContextId,
      revision: context.accessRevision,
    );
  }

  MobileRestorationState _blankStateFor(AuthStateAuthenticated auth) {
    final context = auth.access.selectedContext;
    return const MobileRestorationState().bindAuthority(
      userId: auth.user.id,
      contextId: context.accessContextId,
      revision: context.accessRevision,
    );
  }

  void _saveDurableRestorationState() {
    if (_suppressDurableRestorationSave) return;
    final auth = ref.read(authControllerProvider);
    if (auth is! AuthStateAuthenticated) return;
    final restored = _restorationState.value;
    if (!_matchesCurrentAuthority(restored, auth)) return;
    unawaited(ref.read(mobileRestorationStateStoreProvider).save(restored));
  }

  void _setRestorationStateWithoutPersist(MobileRestorationState state) {
    _suppressDurableRestorationSave = true;
    _restorationState.value = state;
    _suppressDurableRestorationSave = false;
  }

  void _clearRestorationForSignedOutSession() {
    _durableRestorationGeneration++;
    _durableRestorationAuthorityKey = null;
    _durableRestorationLoad = null;
    _restoredUnitStack = false;
    _setRestorationStateWithoutPersist(
      _restorationState.value.clearRichState(),
    );
    unawaited(ref.read(mobileRestorationStateStoreProvider).clear());
  }

  void _clearRestorationForAuthority(AuthStateAuthenticated auth) {
    _durableRestorationGeneration++;
    _durableRestorationAuthorityKey = _authorityKey(auth);
    _durableRestorationLoad = null;
    _restoredUnitStack = false;
    _setRestorationStateWithoutPersist(_blankStateFor(auth));
    unawaited(ref.read(mobileRestorationStateStoreProvider).clear());
  }

  void _prepareRestorationForAuthority() {
    final auth = ref.read(authControllerProvider);
    if (auth is! AuthStateAuthenticated) {
      _clearRestorationForSignedOutSession();
      return;
    }
    final restored = _restorationState.value;
    if (!restored.hasAuthority) {
      _loadDurableRestorationForAuthority(auth);
      return;
    }
    if (!_matchesCurrentAuthority(restored, auth)) {
      _clearRestorationForAuthority(auth);
      return;
    }
    _saveDurableRestorationState();
    _restoreUnitStackIfAuthorized();
  }

  void _loadDurableRestorationForAuthority(AuthStateAuthenticated auth) {
    final key = _authorityKey(auth);
    if (_durableRestorationAuthorityKey == key &&
        _durableRestorationLoad != null) {
      return;
    }
    _durableRestorationAuthorityKey = key;
    final generation = _durableRestorationGeneration;
    _durableRestorationLoad = _loadDurableRestorationForAuthorityKey(
      key,
      generation,
    );
  }

  AuthStateAuthenticated? _currentRestorationAuthority(
    String key,
    int generation,
  ) {
    if (!mounted ||
        _durableRestorationAuthorityKey != key ||
        _durableRestorationGeneration != generation) {
      return null;
    }
    final auth = ref.read(authControllerProvider);
    if (auth is! AuthStateAuthenticated || _authorityKey(auth) != key) {
      return null;
    }
    return auth;
  }

  bool _isCurrentRestorationAuthority(String key, int generation) {
    if (!mounted || _durableRestorationGeneration != generation) {
      return false;
    }
    final auth = ref.read(authControllerProvider);
    return auth is AuthStateAuthenticated && _authorityKey(auth) == key;
  }

  Future<void> _loadDurableRestorationForAuthorityKey(
    String key,
    int generation,
  ) async {
    final store = ref.read(mobileRestorationStateStoreProvider);
    final loadResult = await store.load();
    final auth = _currentRestorationAuthority(key, generation);
    if (auth == null) return;

    if (loadResult case MobileRestorationStateLoadSuccess(:final state)) {
      if (_matchesCurrentAuthority(state, auth)) {
        _setRestorationStateWithoutPersist(state);
        _restoreUnitStackIfAuthorized();
        return;
      }
    }

    if (loadResult is! MobileRestorationStateLoadMissing) {
      await store.clear();
    }
    final currentAuth = _currentRestorationAuthority(key, generation);
    if (currentAuth == null) return;
    _setRestorationStateWithoutPersist(_blankStateFor(currentAuth));
  }

  void _restoreUnitStackIfAuthorized() {
    if (_restoredUnitStack) return;
    final auth = ref.read(authControllerProvider);
    final restored = _restorationState.value;
    final context = auth is AuthStateAuthenticated
        ? auth.access.selectedContext
        : null;
    if (auth is! AuthStateAuthenticated ||
        context == null ||
        !_matchesCurrentAuthority(restored, auth) ||
        restored.unitId == null ||
        _domainNavigators[MobileShellTabId.rentals] == null) {
      return;
    }
    final tab = UnitCommandCenterTab.values.firstWhere(
      (value) => value.name == restored.destination,
      orElse: () => UnitCommandCenterTab.summary,
    );
    final view = UnitCommandCenterView.values
        .where((value) => value.name == restored.anchor)
        .firstOrNull;
    final restorationAuthorityKey = _authorityKey(auth);
    final restorationGeneration = _durableRestorationGeneration;
    bool isCurrentRestorationNavigation() => _isCurrentRestorationAuthority(
      restorationAuthorityKey,
      restorationGeneration,
    );
    _restoredUnitStack = true;
    _openShellTab(
      MobileShellTabId.rentals,
      destination: MobileDestinationId.units,
      detailBuilder: (_) => UnitCommandCenterLoaderScreen(
        unitId: restored.unitId!,
        initialTab: tab,
        initialView: view,
      ),
      canNavigate: isCurrentRestorationNavigation,
    );
  }

  void _unregisterDomain(
    MobileShellTabId tab,
    MobileDomainNavigator controller,
  ) {
    if (identical(_domainNavigators[tab], controller)) {
      _domainNavigators.remove(tab);
    }
  }

  void _setTabQuickActionsHidden(
    MobileShellTabId tab,
    Object owner,
    bool hidden,
  ) {
    final index = _tabIds.indexOf(tab);
    if (index >= _quickActionControllers.length) return;
    _quickActionControllers[index].setHidden(owner, hidden);
  }

  Widget _quickActionScope(int index, Widget child) {
    return MobileQuickActionScope(
      controller: _quickActionControllers[index],
      child: child,
    );
  }

  List<MobileShellTabId> _availableLandlordTabs(AuthStateAuthenticated auth) {
    final capabilities = auth.capabilities;
    final tabs = <MobileShellTabId>[
      if (auth.activeExperience == WorkspaceExperience.management)
        MobileShellTabId.today,
      if (canOpenRentalsHubForExperience(
        experience: auth.activeExperience,
        capabilities: capabilities,
      ))
        MobileShellTabId.rentals,
      if (auth.activeExperience == WorkspaceExperience.management &&
          canOpenMoneyHub(capabilities))
        MobileShellTabId.money,
      if (canOpenWorkHub(capabilities)) MobileShellTabId.work,
      if (canOpenInboxHub(capabilities)) MobileShellTabId.inbox,
    ];
    if (tabs.isEmpty &&
        auth.activeExperience == WorkspaceExperience.management) {
      return const [MobileShellTabId.today];
    }
    return tabs;
  }

  void _handleBottomNavigationSelected(
    int index, {
    required bool tenantMode,
    required List<MobileShellTabId> landlordTabs,
  }) {
    if (_selectedIndex != index) {
      setState(() => _selectedIndex = index);
      return;
    }

    if (tenantMode) return;
    if (index >= landlordTabs.length) return;
    final tab = landlordTabs[index];
    _domainNavigators[tab]?.popToCurrentRoot();
  }

  void _openShellTab(
    MobileShellTabId tab, {
    MobileDestinationId? destination,
    MobileDetailBuilder? detailBuilder,
    MobileNavigationGuard? canNavigate,
  }) {
    final authState = ref.read(authControllerProvider);
    final tenantMode =
        authState is AuthStateAuthenticated && authState.isTenantExperience;
    if (tenantMode) {
      if (detailBuilder != null) {
        Navigator.of(
          context,
        ).push<void>(MaterialPageRoute<void>(builder: detailBuilder));
      }
      return;
    }
    if (authState is! AuthStateAuthenticated) return;

    final availableTabs = _availableLandlordTabs(authState);
    final index = availableTabs.indexOf(tab);
    if (index < 0) return;
    if (_selectedIndex != index) {
      setState(() => _selectedIndex = index);
    }

    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      _prepareRestorationForAuthority();
      if (canNavigate?.call() == false) return;

      final domainNavigator = _domainNavigators[tab];
      if (destination != null && domainNavigator != null) {
        domainNavigator.openDestination(
          destination,
          detailBuilder: detailBuilder,
          canNavigate: canNavigate,
        );
        return;
      }

      if (detailBuilder != null) {
        if (canNavigate?.call() == false) return;
        Navigator.of(
          context,
        ).push<void>(MaterialPageRoute<void>(builder: detailBuilder));
      }
    });
  }

  bool _openShellRoute(String route) {
    final uri = Uri.tryParse(route);
    if (uri == null) return false;

    final path = uri.path;
    final auth = ref.read(authControllerProvider);
    if (auth is! AuthStateAuthenticated) return false;
    if (!canOpenMobilePath(
      experience: auth.activeExperience,
      capabilities: auth.capabilities,
      path: path,
    )) {
      _showAccessDenied();
      return true;
    }
    // Dedicated Leasing, Maintenance, Owner and Tenant shells own independent
    // tab stacks. Their typed routes must be pushed by go_router instead of
    // being translated into the Management domain hubs below.
    if (auth.activeExperience != WorkspaceExperience.management) return false;
    final segments = uri.pathSegments;
    final id = segments.length >= 2 ? int.tryParse(segments[1]) : null;

    switch (path) {
      case '/rentals':
        _openShellTab(MobileShellTabId.rentals);
        return true;
      case '/owners':
        _openShellTab(
          MobileShellTabId.rentals,
          destination: MobileDestinationId.owners,
        );
        return true;
      case '/units':
        _openShellTab(
          MobileShellTabId.rentals,
          destination: MobileDestinationId.units,
        );
        return true;
      case '/work':
        _openShellTab(MobileShellTabId.work);
        return true;
      case '/money':
        _openShellTab(MobileShellTabId.money);
        return true;
      case '/inbox':
        _openShellTab(MobileShellTabId.inbox);
        return true;
      case '/notifications':
        final auth = ref.read(authControllerProvider);
        if (auth is AuthStateAuthenticated && auth.isTenantExperience) {
          Navigator.of(context).push<void>(
            MaterialPageRoute<void>(
              builder: (_) => const NotificationsInboxScreen(),
            ),
          );
          return true;
        }
        _openShellTab(
          MobileShellTabId.inbox,
          destination: MobileDestinationId.notifications,
        );
        return true;
    }

    final unitTarget = parseUnitCommandCenterRoute(route);
    if (unitTarget != null) {
      _openShellTab(
        MobileShellTabId.rentals,
        destination: MobileDestinationId.units,
        detailBuilder: (_) => UnitCommandCenterLoaderScreen(
          unitId: unitTarget.unitId,
          initialTab: unitTarget.initialTab,
          initialView: unitTarget.initialView,
        ),
      );
      return true;
    }

    if (segments.length == 4 &&
        segments[0] == 'tenant-accounts' &&
        segments[2] == 'entries') {
      final tenantAccountId = int.tryParse(segments[1]);
      final tenantLedgerEntryId = int.tryParse(segments[3]);
      if (tenantAccountId == null || tenantLedgerEntryId == null) return false;
      _openShellTab(
        MobileShellTabId.money,
        destination: MobileDestinationId.moneyLedger,
        detailBuilder: (_) => PaymentDetailScreen(
          tenantAccountId: tenantAccountId,
          tenantLedgerEntryId: tenantLedgerEntryId,
        ),
      );
      return true;
    }

    if (segments.length != 2 || id == null) return false;

    switch (segments.first) {
      case 'work-orders':
        _openShellTab(
          MobileShellTabId.work,
          destination: MobileDestinationId.workOrders,
          detailBuilder: (_) =>
              WorkOrderShellTargetLoaderScreen(workOrderId: id),
        );
        return true;
      case 'expenses':
        _openShellTab(
          MobileShellTabId.money,
          destination: MobileDestinationId.moneyLedger,
          detailBuilder: (_) => ExpenseDetailScreen(expenseId: id),
        );
        return true;
      case 'messages':
        _openShellTab(
          MobileShellTabId.inbox,
          destination: MobileDestinationId.messages,
          detailBuilder: (_) => MessageDetailScreen(conversationId: id),
        );
        return true;
      case 'scan':
        Navigator.of(context).push<void>(
          MaterialPageRoute<void>(
            builder: (_) => ScanReviewScreen(draftId: id),
          ),
        );
        return true;
    }

    return false;
  }

  void _showAccessDenied() {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (deniedContext) => MobileAccessDeniedScreen(
          onReturn: () => Navigator.of(deniedContext).pop(),
        ),
      ),
    );
  }

  @override
  void initState() {
    super.initState();
    _shellNavigator = MobileShellNavigator(
      openTab: _openShellTab,
      openRoute: _openShellRoute,
      setTabQuickActionsHidden: _setTabQuickActionsHidden,
    );
    MobileShellNavigationRegistry.attach(_shellNavigator);
    WidgetsBinding.instance.addObserver(this);
    _restorationState.addListener(_saveDurableRestorationState);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      // I7: if the first-login gate couldn't be determined at login (the
      // sandbox-state lookup hiccupped), re-resolve it now that the shell is up,
      // mirroring the web's per-navigation re-check. No-op once resolved, so a
      // returning user pays nothing; a genuinely-new account gets gated here
      // instead of slipping past the Sandbox/Live choice.
      ref
          .read(authControllerProvider.notifier)
          .reresolveOnboardingIfUnresolved();
      // Initialise the realtime watcher so it stays alive for the shell. The
      // updates hub also carries in-app Notification events (see
      // realtime_providers' `_invalidateForEntity` 'Notification' case).
      ref.read(realtimeWatcherProvider);
      // A voice command may have cold-started the app (Assistant launched us)
      // before this shell built — pick up anything already waiting in the bus.
      final pending = ref.read(pendingVoiceCommandProvider);
      if (pending != null) _handleVoiceCommand(pending);
      // Likewise drain a notification-tap deep link that cold-started the app
      // before authentication completed.
      final pendingLink = ref.read(pendingPushLinkProvider);
      if (pendingLink != null) _handlePushLink(pendingLink);
      _prepareRestorationForAuthority();
    });
  }

  @override
  void dispose() {
    MobileShellNavigationRegistry.detach(_shellNavigator);
    WidgetsBinding.instance.removeObserver(this);
    for (final controller in _quickActionControllers) {
      controller.dispose();
    }
    _restorationState.removeListener(_saveDurableRestorationState);
    _restorationState.dispose();
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) {
      // Refresh the unread badge whenever the app comes back to the foreground
      // (a push may have been read on another device, or arrived while backgrounded).
      if (ref.read(authControllerProvider) is AuthStateAuthenticated) {
        ref.read(unreadCountProvider.notifier).refresh();
      }
    }
  }

  /// Navigates to a notification-tap deep link once the shell is mounted and
  /// the user is authenticated, then clears the one-slot bus.
  void _handlePushLink(MobileNavigationIntent intent) {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      final authority = ref.read(authControllerProvider);
      if (authority is AuthStateAuthenticated) {
        final route = intent.resolveFor(
          authority,
          nowUtc: DateTime.now().toUtc(),
        );
        // A14: PUSH the deep-linked target on top of the shell (not `go`,
        // which REPLACES the stack) so a detail screen opened from a
        // notification tap keeps a working back button to the dashboard
        // instead of stranding the user with no way back.
        if (!_shellNavigator.openRoute(route)) {
          context.push(route);
        }
      }
      ref.read(pendingPushLinkProvider.notifier).consume();
    });
  }

  /// Lands the landlord in the right place for a parsed voice command and shows
  /// a plain-language confirmation of what was understood. Scheduled post-frame
  /// so it can navigate / setState safely even when invoked from a build-time
  /// listener.
  void _handleVoiceCommand(VoiceCommand command) {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;

      final messenger = ScaffoldMessenger.of(context);
      void toast(String message) => messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(message)));

      // Voice commands are landlord-facing for now (matches on-device testing).
      final authState = ref.read(authControllerProvider);
      if (authState is! AuthStateAuthenticated ||
          authState.isTenantExperience) {
        toast("Voice commands aren't available for tenant accounts yet.");
        ref.read(pendingVoiceCommandProvider.notifier).consume();
        return;
      }

      final capabilities = authState.capabilities;
      final allowed = switch (command.action) {
        VoiceAction.scanDocument => canUseGlobalScan(capabilities),
        VoiceAction.logExpense => canUseVoiceRecord(capabilities),
        VoiceAction.showOverdueRent => hasAnyMobileCapability(
          capabilities,
          moneyOverviewCapabilityKeys,
        ),
        VoiceAction.openWorkOrders => canOpenWorkOrders(capabilities),
      };
      if (!allowed) {
        _showAccessDenied();
        ref.read(pendingVoiceCommandProvider.notifier).consume();
        return;
      }

      final navigator = Navigator.of(context);
      switch (command.action) {
        case VoiceAction.scanDocument:
        case VoiceAction.logExpense:
          // Voice and tap converge on the capture flow (the flagship intake).
          navigator.popUntil((route) => route.isFirst);
          _openCapture();
        case VoiceAction.showOverdueRent:
          _openShellTab(
            MobileShellTabId.money,
            destination: MobileDestinationId.moneyOverview,
            detailBuilder: (_) => const OverdueScreen(),
          );
        case VoiceAction.openWorkOrders:
          _openShellTab(
            MobileShellTabId.work,
            destination: MobileDestinationId.workOrders,
          );
      }

      toast(command.understoodSummary);
      ref.read(pendingVoiceCommandProvider.notifier).consume();
    });
  }

  _TabItem _tabItemFor(MobileShellTabId tab, WorkspaceExperience experience) {
    if (tab == MobileShellTabId.work) {
      if (experience == WorkspaceExperience.leasing) {
        return const _TabItem(label: 'Calendar', icon: Symbols.event_rounded);
      }
      if (experience == WorkspaceExperience.maintenance) {
        return const _TabItem(label: 'My work', icon: Symbols.build_rounded);
      }
    }
    return _tabs[_tabIds.indexOf(tab)];
  }

  Widget _buildLandlordTab(
    MobileShellTabId tab,
    AuthUser? user,
    List<MobileShellTabId> availableTabs,
  ) {
    final staticIndex = _tabIds.indexOf(tab);
    return _quickActionScope(staticIndex, switch (tab) {
      MobileShellTabId.today => _HomeTab(
        user: user,
        onOpenCapture: _openCapture,
        onOpenOverdue: () => _openShellTab(
          MobileShellTabId.money,
          destination: MobileDestinationId.moneyOverview,
          detailBuilder: (_) => const OverdueScreen(),
        ),
        onSwitchToTab: (requestedStaticIndex) {
          if (requestedStaticIndex < 0 ||
              requestedStaticIndex >= _tabIds.length) {
            return;
          }
          final visibleIndex = availableTabs.indexOf(
            _tabIds[requestedStaticIndex],
          );
          if (visibleIndex >= 0) {
            setState(() => _selectedIndex = visibleIndex);
          }
        },
        onOpenAssistant: _openAssistant,
      ),
      MobileShellTabId.rentals => RentalsHubScreen(
        onControllerReady: (controller) =>
            _registerDomain(MobileShellTabId.rentals, controller),
        onControllerDisposed: (controller) =>
            _unregisterDomain(MobileShellTabId.rentals, controller),
      ),
      MobileShellTabId.money => MoneyHubScreen(
        onControllerReady: (controller) =>
            _registerDomain(MobileShellTabId.money, controller),
        onControllerDisposed: (controller) =>
            _unregisterDomain(MobileShellTabId.money, controller),
      ),
      MobileShellTabId.work => WorkHubScreen(
        onControllerReady: (controller) =>
            _registerDomain(MobileShellTabId.work, controller),
        onControllerDisposed: (controller) =>
            _unregisterDomain(MobileShellTabId.work, controller),
      ),
      MobileShellTabId.inbox => InboxHubScreen(
        onControllerReady: (controller) =>
            _registerDomain(MobileShellTabId.inbox, controller),
        onControllerDisposed: (controller) =>
            _unregisterDomain(MobileShellTabId.inbox, controller),
      ),
    });
  }

  @override
  Widget build(BuildContext context) {
    // Keep the realtime watcher alive while the shell is in the tree. It also
    // refreshes the notification badge/inbox on inbound Notification events.
    ref.watch(realtimeWatcherProvider);

    // React to voice commands that arrive while the shell is already running.
    ref.listen<VoiceCommand?>(pendingVoiceCommandProvider, (_, next) {
      if (next != null) _handleVoiceCommand(next);
    });

    // React to notification taps (warm app) that stashed a deep link.
    ref.listen<MobileNavigationIntent?>(pendingPushLinkProvider, (_, next) {
      if (next != null) _handlePushLink(next);
    });

    ref.listen<AuthState>(authControllerProvider, (previous, next) {
      if (next is! AuthStateAuthenticated) {
        if (previous is AuthStateAuthenticated) {
          _selectedIndex = 0;
          _clearRestorationForSignedOutSession();
        }
        return;
      }

      if (previous is! AuthStateAuthenticated) {
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (!mounted) return;
          _prepareRestorationForAuthority();
        });
        return;
      }

      if (!accessAuthorityChanged(previous, next)) {
        return;
      }
      _selectedIndex = 0;
      _clearRestorationForAuthority(next);
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (!mounted) return;
        for (final navigator in _domainNavigators.values) {
          navigator.popToCurrentRoot();
        }
        _prepareRestorationForAuthority();
      });
    });

    final authState = ref.watch(authControllerProvider);
    if (authState is! AuthStateAuthenticated) {
      return const SizedBox.shrink();
    }

    if (authState.activeExperience == WorkspaceExperience.owner) {
      return MobileShellNavigation(
        controller: _shellNavigator,
        child: const OwnerLandingScreen(),
      );
    }

    if (authState.activeExperience == WorkspaceExperience.leasing) {
      return MobileShellNavigation(
        controller: _shellNavigator,
        child: const LeasingLandingScreen(),
      );
    }

    if (authState.activeExperience == WorkspaceExperience.maintenance) {
      return MobileShellNavigation(
        controller: _shellNavigator,
        child: const TechnicianLandingScreen(),
      );
    }

    if (authState.activeExperience == WorkspaceExperience.tenant) {
      final user = authState.user;
      return MobileShellNavigation(
        controller: _shellNavigator,
        child: MobileRoleShell(
          actions: const [MobileNotificationBell(), MobileAccountMenu()],
          destinations: [
            MobileRoleDestination(
              label: tenantShellDestinations[0].label,
              bottomNavigationLabel:
                  tenantShellDestinations[0].bottomNavigationLabel,
              icon: tenantShellDestinations[0].icon,
              ownsScaffold: true,
              builder: (_) => _TenantHomeTab(user: user),
            ),
            MobileRoleDestination(
              label: tenantShellDestinations[1].label,
              bottomNavigationLabel:
                  tenantShellDestinations[1].bottomNavigationLabel,
              icon: tenantShellDestinations[1].icon,
              ownsScaffold: true,
              builder: (_) => const _TenantAccountLeaseTab(),
            ),
            MobileRoleDestination(
              label: tenantShellDestinations[2].label,
              bottomNavigationLabel:
                  tenantShellDestinations[2].bottomNavigationLabel,
              icon: tenantShellDestinations[2].icon,
              ownsScaffold: true,
              builder: (_) => const TenantMaintenanceScreen(),
            ),
            MobileRoleDestination(
              label: tenantShellDestinations[3].label,
              bottomNavigationLabel:
                  tenantShellDestinations[3].bottomNavigationLabel,
              icon: tenantShellDestinations[3].icon,
              ownsScaffold: true,
              builder: (_) => const MessagesListScreen(),
            ),
            MobileRoleDestination(
              label: tenantShellDestinations[4].label,
              bottomNavigationLabel:
                  tenantShellDestinations[4].bottomNavigationLabel,
              icon: tenantShellDestinations[4].icon,
              ownsScaffold: true,
              builder: (_) => const _TenantProfileTab(),
            ),
          ],
        ),
      );
    }

    final user = authState.user;
    final tenantMode = authState.isTenantExperience;
    final landlordTabs = tenantMode
        ? const <MobileShellTabId>[]
        : _availableLandlordTabs(authState);
    if (!tenantMode && landlordTabs.isEmpty) {
      return MobileShellNavigation(
        controller: _shellNavigator,
        child: MobileAccessDeniedScreen(
          returnLabel: 'Refresh access',
          onReturn: () => unawaited(
            ref.read(authControllerProvider.notifier).restoreSession(),
          ),
        ),
      );
    }
    final tabs = tenantMode
        ? _tenantTabs
        : landlordTabs
              .map((tab) => _tabItemFor(tab, authState.activeExperience))
              .toList(growable: false);
    final selectedIndex = _selectedIndex >= tabs.length
        ? tabs.length - 1
        : _selectedIndex;
    final quickActionController = tenantMode
        ? null
        : _quickActionControllers[_tabIds.indexOf(landlordTabs[selectedIndex])];

    return MobileRestorationScope(
      controller: _restorationState,
      child: MobileShellNavigation(
        controller: _shellNavigator,
        child: Scaffold(
          body: Column(
            children: [
              // App-wide "Sandbox mode" indicator: a slim bar above the tabs, shown only while the
              // account is a seeded demo sandbox. Inert (zero-height) once the account is Live.
              const _SandboxIndicator(),
              Expanded(
                child: IndexedStack(
                  index: selectedIndex,
                  children: tenantMode
                      ? [
                          _TenantHomeTab(user: user),
                          const _TenantAccountLeaseTab(),
                          const TenantMaintenanceScreen(),
                          const MessagesListScreen(),
                          const _TenantProfileTab(),
                        ]
                      : landlordTabs
                            .map(
                              (tab) =>
                                  _buildLandlordTab(tab, user, landlordTabs),
                            )
                            .toList(growable: false),
                ),
              ),
            ],
          ),
          floatingActionButton: quickActionController == null
              ? null
              : AnimatedBuilder(
                  animation: quickActionController,
                  builder: (context, _) {
                    if (quickActionController.hidden) {
                      return const SizedBox.shrink();
                    }

                    return MobileQuickActionFab(
                      heroTag: 'home-quick-action-fab-$selectedIndex',
                      primaryActions: quickActionController.primaryActions,
                      useNearestScope: false,
                      onChat: _openAssistant,
                      onRecord: _openRecord,
                      onScan: quickActionController.scanAction ?? _openCapture,
                    );
                  },
                ),
          floatingActionButtonLocation: quickActionController == null
              ? null
              : FloatingActionButtonLocation.endFloat,
          bottomNavigationBar: _MorphNavBar(
            tabs: tabs,
            selectedIndex: selectedIndex,
            centerGap: false,
            onSelected: (index) => _handleBottomNavigationSelected(
              index,
              tenantMode: tenantMode,
              landlordTabs: landlordTabs,
            ),
          ),
        ),
      ),
    );
  }
}

/// App-wide "Example data" indicator — a slim tinted bar shown at the top of the
/// shell while the account is a seeded demo sandbox. Renders nothing (zero
/// height) once the account is Live or while the state is still loading, so it
/// never causes layout jank for real accounts.
///
/// A11: softer, plainer wording ("Example data" instead of dev-term "Sandbox
/// mode") and — unlike the old informational-only bar — it is now ACTIONABLE:
/// tapping it opens the guarded go-live flow so the user can start fresh with
/// their own rentals. The Sandbox feature itself is unchanged.
class _SandboxIndicator extends ConsumerWidget {
  const _SandboxIndicator();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final state = ref.watch(sandboxStateProvider);
    final isSandbox = state.maybeWhen(
      data: (s) => s.isSandbox,
      orElse: () => false,
    );
    if (!isSandbox) return const SizedBox.shrink();

    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    return Material(
      color: scheme.tertiaryContainer,
      child: InkWell(
        onTap: () => _openGoLiveSheetAndRefreshHome(context, ref),
        child: Container(
          key: const Key('sandbox-indicator'),
          width: double.infinity,
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
          child: SafeArea(
            bottom: false,
            child: Row(
              children: [
                Icon(
                  Icons.science_outlined,
                  size: 15,
                  color: scheme.onTertiaryContainer,
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    'Example data — tap to start fresh with your own.',
                    style: theme.textTheme.labelMedium?.copyWith(
                      color: scheme.onTertiaryContainer,
                      fontWeight: FontWeight.w600,
                    ),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                const SizedBox(width: 8),
                Icon(
                  Icons.arrow_forward_rounded,
                  size: 15,
                  color: scheme.onTertiaryContainer,
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// Bottom navigation built from [M3MorphNavItem]s: a flat surface with a top
/// hairline, where the selected destination morphs its corner shape (pill ↔
/// rounded-rect) and its Material Symbol's FILL axis 0→1. Replaces the stock
/// [NavigationBar] so the M3-Expressive morph is visible (§3.2 / §7.5).
class _MorphNavBar extends StatelessWidget {
  const _MorphNavBar({
    required this.tabs,
    required this.selectedIndex,
    required this.onSelected,
    this.centerGap = false,
  });

  final List<_TabItem> tabs;
  final int selectedIndex;
  final ValueChanged<int> onSelected;

  /// When true, a spacer is inserted in the middle of the row to clear the
  /// center-docked Capture FAB. Assumes an even number of destinations split
  /// evenly around the gap (4 tabs → 2 left, FAB, 2 right).
  final bool centerGap;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final mid = tabs.length ~/ 2;
    if (!centerGap && tabs.length >= 5) {
      return Container(
        decoration: BoxDecoration(
          color: scheme.surfaceContainerLow,
          border: Border(
            top: BorderSide(
              color: scheme.outlineVariant.withValues(alpha: 0.4),
            ),
          ),
        ),
        child: SafeArea(
          top: false,
          child: SizedBox(
            height: 68,
            child: Row(
              children: [
                for (var i = 0; i < tabs.length; i++)
                  Expanded(
                    child: _CompactNavItem(
                      icon: tabs[i].icon,
                      label: tabs[i].label,
                      selected: i == selectedIndex,
                      onTap: () => onSelected(i),
                    ),
                  ),
              ],
            ),
          ),
        ),
      );
    }

    return Container(
      decoration: BoxDecoration(
        color: scheme.surfaceContainerLow,
        border: Border(
          top: BorderSide(color: scheme.outlineVariant.withValues(alpha: 0.4)),
        ),
      ),
      child: SafeArea(
        top: false,
        child: SizedBox(
          height: 64,
          child: Row(
            mainAxisAlignment: MainAxisAlignment.spaceEvenly,
            children: [
              for (var i = 0; i < tabs.length; i++) ...[
                if (centerGap && i == mid) const SizedBox(width: 64),
                M3MorphNavItem(
                  icon: tabs[i].icon,
                  label: i == selectedIndex ? tabs[i].label : null,
                  selected: i == selectedIndex,
                  onTap: () => onSelected(i),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

class _CompactNavItem extends StatelessWidget {
  const _CompactNavItem({
    required this.icon,
    required this.label,
    required this.selected,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final fg = selected ? scheme.onPrimaryContainer : scheme.onSurfaceVariant;

    return InkWell(
      onTap: onTap,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 2, vertical: 6),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            AnimatedContainer(
              duration: M3Motion.medium3,
              curve: M3Motion.emphasizedDecelerate,
              width: 40,
              height: 30,
              decoration: BoxDecoration(
                color: selected ? scheme.primaryContainer : Colors.transparent,
                borderRadius: BorderRadius.circular(18),
              ),
              child: Center(
                child: TweenAnimationBuilder<double>(
                  duration: M3Motion.medium2,
                  curve: M3Motion.emphasizedDecelerate,
                  tween: Tween(end: selected ? 1.0 : 0.0),
                  builder: (context, fill, _) => Icon(
                    icon,
                    color: fg,
                    size: 22,
                    fill: fill,
                    weight: selected ? 500 : 400,
                  ),
                ),
              ),
            ),
            const SizedBox(height: 3),
            Text(
              label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: theme.textTheme.labelSmall?.copyWith(
                color: fg,
                fontWeight: selected ? FontWeight.w700 : FontWeight.w500,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _HomeTab — the actual dashboard
// ---------------------------------------------------------------------------

Future<void>? _tenantSessionRecoveryInFlight;

Future<void> _restoreTenantSessionSilently(WidgetRef ref) async {
  final controller = ref.read(authControllerProvider.notifier);
  final recovery = _tenantSessionRecoveryInFlight ??= controller
      .restoreSession();
  try {
    await recovery;
  } catch (_) {
    // Best effort only. Callers keep a neutral retry state visible.
  } finally {
    if (identical(_tenantSessionRecoveryInFlight, recovery)) {
      _tenantSessionRecoveryInFlight = null;
    }
  }
}

class _TenantHomeTab extends ConsumerStatefulWidget {
  const _TenantHomeTab({required this.user});

  final AuthUser? user;

  @override
  ConsumerState<_TenantHomeTab> createState() => _TenantHomeTabState();
}

class _TenantHomeTabState extends ConsumerState<_TenantHomeTab> {
  /// Payment id currently starting a Checkout session (button shows a spinner).
  int? _payingPaymentId;

  /// Tenant-account id whose autopay enroll/cancel is in flight.
  int? _busyAutopayAccountId;

  int? _selectedTenantAccountId;
  int _chargeSkip = 0;
  static const _chargePageSize = 20;

  AuthUser? get user => widget.user;

  /// Opens [url] in an external browser. Returns true on success; on a malformed
  /// URL or when no browser/handler is available (or `launchUrl` throws), it
  /// snackbars a clear message and returns false so a caller never treats a
  /// silent dead-end as success. Mirrors the vendor/work-order launch pattern.
  Future<bool> _open(String url) async {
    final messenger = ScaffoldMessenger.of(context);
    const failureMessage =
        "Couldn't open the payment page. Make sure you have a web browser "
        'installed, then try again.';
    final uri = Uri.tryParse(url);
    if (uri == null) {
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text(failureMessage)));
      return false;
    }
    try {
      final ok = await launchUrl(uri, mode: LaunchMode.externalApplication);
      if (!ok && mounted) {
        messenger
          ..hideCurrentSnackBar()
          ..showSnackBar(const SnackBar(content: Text(failureMessage)));
      }
      return ok;
    } catch (_) {
      if (mounted) {
        messenger
          ..hideCurrentSnackBar()
          ..showSnackBar(const SnackBar(content: Text(failureMessage)));
      }
      return false;
    }
  }

  /// Starts hosted Checkout for one rent item and opens it in the browser.
  /// A 503 (Stripe off) shows a gentle, non-error message.
  Future<void> _payNow(PortalTenantCharge charge) async {
    if (_payingPaymentId != null) return;
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _payingPaymentId = charge.tenantLedgerEntryId);
    try {
      final url = await ref
          .read(tenantPortalRepositoryProvider)
          .payCheckout(charge.tenantAccountId, charge.tenantLedgerEntryId);
      if (url.isEmpty) return;
      await _open(url);
    } on ApiException catch (e) {
      if (e.statusCode == 401) {
        unawaited(_restoreTenantSessionSilently(ref));
      }
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(
              e.statusCode == 401
                  ? "We couldn't complete that right now. Please try again."
                  : e.statusCode == 503
                  ? "Online payments aren't set up yet."
                  : e.message,
            ),
          ),
        );
    } finally {
      if (mounted) setState(() => _payingPaymentId = null);
    }
  }

  /// Enrolls the tenant account in autopay and opens setup Checkout in the browser.
  Future<void> _enrollAutopay(int tenantAccountId) async {
    if (_busyAutopayAccountId != null) return;
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _busyAutopayAccountId = tenantAccountId);
    try {
      final url = await ref
          .read(tenantPortalRepositoryProvider)
          .autopayEnroll(tenantAccountId);
      // Only refresh status if the browser actually opened — otherwise the
      // tenant never reached the hosted setup, so there's nothing new to read
      // (and _open has already told them the browser couldn't open).
      if (url.isNotEmpty && await _open(url)) {
        // The tenant finishes setup in the browser; refresh status on return.
        ref.invalidate(tenantAutopayStatusProvider(tenantAccountId));
      }
    } on ApiException catch (e) {
      if (e.statusCode == 401) {
        unawaited(_restoreTenantSessionSilently(ref));
      }
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(
              e.statusCode == 401
                  ? "We couldn't complete that right now. Please try again."
                  : e.statusCode == 503
                  ? "Online payments aren't set up yet."
                  : e.message,
            ),
          ),
        );
    } finally {
      if (mounted) setState(() => _busyAutopayAccountId = null);
    }
  }

  /// Turns autopay off for the tenant account, then refreshes the status.
  Future<void> _cancelAutopay(int tenantAccountId) async {
    if (_busyAutopayAccountId != null) return;
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _busyAutopayAccountId = tenantAccountId);
    try {
      await ref
          .read(tenantPortalRepositoryProvider)
          .autopayCancel(tenantAccountId);
      ref.invalidate(tenantAutopayStatusProvider(tenantAccountId));
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Autopay turned off.')));
    } on ApiException catch (e) {
      if (e.statusCode == 401) {
        unawaited(_restoreTenantSessionSilently(ref));
      }
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(
              e.statusCode == 401
                  ? "We couldn't complete that right now. Please try again."
                  : e.message,
            ),
          ),
        );
    } finally {
      if (mounted) setState(() => _busyAutopayAccountId = null);
    }
  }

  @override
  Widget build(BuildContext context) {
    final snapshot = ref.watch(tenantPortalSnapshotProvider);
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Tenant Dashboard'),
        actions: const [MobileNotificationBell(), MobileAccountMenu()],
      ),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(tenantPortalSnapshotProvider);
          // Refresh autopay state too; the tenant may have just returned from
          // a hosted Checkout in the browser.
          final snapshotValue = ref.read(tenantPortalSnapshotProvider).value;
          final tenantAccountId =
              _selectedTenantAccountId ??
              (snapshotValue != null &&
                      snapshotValue.accounts.totalCount == 1 &&
                      snapshotValue.accounts.items.isNotEmpty
                  ? snapshotValue.accounts.items.first.tenantAccountId
                  : null);
          if (tenantAccountId != null) {
            ref.invalidate(tenantAutopayStatusProvider(tenantAccountId));
            ref.invalidate(tenantPortalAccountProvider(tenantAccountId));
            ref.invalidate(
              tenantPortalChargesPageProvider((
                tenantAccountId: tenantAccountId,
                skip: _chargeSkip,
                take: _chargePageSize,
              )),
            );
          }
        },
        child: snapshot.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (err, _) => ListView(
            padding: const EdgeInsets.all(20),
            children: [
              _TenantLoadError(
                error: err,
                title: "We couldn't load your home.",
                onRetry: () => ref.invalidate(tenantPortalSnapshotProvider),
              ),
            ],
          ),
          data: (data) {
            final accountId =
                _selectedTenantAccountId ??
                (data.accounts.totalCount == 1 && data.accounts.items.isNotEmpty
                    ? data.accounts.items.first.tenantAccountId
                    : null);
            final accountAsync = accountId == null
                ? null
                : ref.watch(tenantPortalAccountProvider(accountId));
            final account = accountAsync?.value;
            final chargesAsync = accountId == null
                ? null
                : ref.watch(
                    tenantPortalChargesPageProvider((
                      tenantAccountId: accountId,
                      skip: _chargeSkip,
                      take: _chargePageSize,
                    )),
                  );

            return ListView(
              padding: const EdgeInsets.all(20),
              children: [
                Text(
                  user?.displayName ?? 'My home',
                  style: theme.textTheme.headlineSmall?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
                ),
                const SizedBox(height: 16),
                if (data.accounts.totalCount > 1) ...[
                  DropdownButtonFormField<int>(
                    initialValue: _selectedTenantAccountId,
                    decoration: const InputDecoration(
                      labelText: 'Account',
                      border: OutlineInputBorder(),
                    ),
                    hint: const Text('Choose an account'),
                    items: [
                      for (final tenantAccount in data.accounts.items)
                        DropdownMenuItem<int>(
                          value: tenantAccount.tenantAccountId,
                          child: Text(
                            '${tenantAccount.propertyName} · Unit ${tenantAccount.unitNumber}',
                          ),
                        ),
                    ],
                    onChanged: (value) => setState(() {
                      _selectedTenantAccountId = value;
                      _chargeSkip = 0;
                    }),
                  ),
                  const SizedBox(height: 16),
                ],
                if (data.notifications.isNotEmpty)
                  _TenantCard(
                    icon: Icons.notifications_outlined,
                    title: 'Notifications',
                    value: 'Recent update',
                    subtitle: data.notifications.first.title,
                  ),
                _TenantCard(
                  icon: Icons.warning_amber_outlined,
                  title: 'Overdue',
                  value: account == null
                      ? '—'
                      : _money(account.pastDueAmount, account.currency),
                  subtitle: account == null
                      ? 'Choose an account'
                      : tenantOverdueItemsLabel(account.pastDueCount),
                ),
                if (data.accounts.items.isNotEmpty)
                  _TenantCard(
                    icon: Icons.receipt_long_outlined,
                    title: 'Account history',
                    value: 'View',
                    subtitle: 'Every charge and payment, explained',
                    onTap: () => Navigator.of(context).push<void>(
                      MaterialPageRoute<void>(
                        builder: (_) => TenantAccountHistoryScreen(
                          initialTenantAccountId: accountId,
                        ),
                      ),
                    ),
                  ),
                _TenantCard(
                  icon: Icons.payments_outlined,
                  title: 'Next due',
                  value: account?.nextDueOn == null
                      ? 'None'
                      : _shortDate(account!.nextDueOn!),
                  subtitle: account?.nextDueOn == null
                      ? (accountId == null
                            ? 'Choose an account'
                            : 'No upcoming charge')
                      : '${_money(account!.nextDueAmount, account.currency)} due',
                ),

                // The API owns charge filtering, ordering and paging. The UI
                // renders the returned page without rebuilding account state.
                if (chargesAsync != null)
                  chargesAsync.when(
                    loading: () => const Padding(
                      padding: EdgeInsets.all(16),
                      child: Center(child: CircularProgressIndicator()),
                    ),
                    error: (err, _) => _TenantLoadError(
                      error: err,
                      title: "We couldn't load charges.",
                      onRetry: () => ref.invalidate(
                        tenantPortalChargesPageProvider((
                          tenantAccountId: accountId!,
                          skip: _chargeSkip,
                          take: _chargePageSize,
                        )),
                      ),
                    ),
                    data: (charges) => Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        const SizedBox(height: 8),
                        Text(
                          'Charges',
                          style: theme.textTheme.titleMedium?.copyWith(
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                        const SizedBox(height: 8),
                        for (final charge in charges.items)
                          _PayItemCard(
                            charge: charge,
                            busy:
                                _payingPaymentId == charge.tenantLedgerEntryId,
                            enabled:
                                _payingPaymentId == null ||
                                _payingPaymentId == charge.tenantLedgerEntryId,
                            onPay: () => _payNow(charge),
                          ),
                        if (charges.totalCount > _chargePageSize)
                          Row(
                            mainAxisAlignment: MainAxisAlignment.spaceBetween,
                            children: [
                              OutlinedButton(
                                onPressed: _chargeSkip == 0
                                    ? null
                                    : () => setState(
                                        () => _chargeSkip =
                                            _chargeSkip >= _chargePageSize
                                            ? _chargeSkip - _chargePageSize
                                            : 0,
                                      ),
                                child: const Text('Previous'),
                              ),
                              Text(
                                '${_chargeSkip + 1}–${(_chargeSkip + _chargePageSize).clamp(0, charges.totalCount)} of ${charges.totalCount}',
                              ),
                              OutlinedButton(
                                onPressed:
                                    _chargeSkip + _chargePageSize >=
                                        charges.totalCount
                                    ? null
                                    : () => setState(
                                        () => _chargeSkip += _chargePageSize,
                                      ),
                                child: const Text('Next'),
                              ),
                            ],
                          ),
                      ],
                    ),
                  ),

                // ── Autopay ───────────────────────────────────────────────
                if (accountId != null) ...[
                  const SizedBox(height: 8),
                  _AutopayCard(
                    statusAsync: ref.watch(
                      tenantAutopayStatusProvider(accountId),
                    ),
                    busy: _busyAutopayAccountId == accountId,
                    onEnroll: () => _enrollAutopay(accountId),
                    onCancel: () => _cancelAutopay(accountId),
                  ),
                ],

                const SizedBox(height: 8),
                _TenantCard(
                  icon: Icons.build_outlined,
                  title: 'Maintenance',
                  value: 'View requests',
                  subtitle: data.workOrders.items.isEmpty
                      ? 'No requests'
                      : data.workOrders.items.first.title,
                ),
              ],
            );
          },
        ),
      ),
    );
  }
}

/// One server-projected charge. Open charges expose hosted Checkout.
class _PayItemCard extends StatelessWidget {
  const _PayItemCard({
    required this.charge,
    required this.busy,
    required this.enabled,
    required this.onPay,
  });

  final PortalTenantCharge charge;
  final bool busy;
  final bool enabled;
  final VoidCallback onPay;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final isLate = charge.isPastDue;
    final dueLabel = isLate
        ? 'Past due'
        : charge.dueOn == null
        ? 'No due date'
        : 'Due ${_shortDate(charge.dueOn!)}';

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(
              isLate ? Icons.warning_amber_outlined : Icons.payments_outlined,
              color: isLate ? cs.error : cs.primary,
            ),
            const SizedBox(width: 14),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    _money(charge.openAmount, charge.currency),
                    style: theme.textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  Text(
                    '${charge.description.isEmpty ? charge.entryType : charge.description} · $dueLabel',
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: isLate ? cs.error : cs.onSurfaceVariant,
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(width: 12),
            // Why `Flexible` (loose) and not a bare `FilledButton`: this Row
            // already has an `Expanded` child, so when Flex measures its
            // *inflexible* children it hands them an unbounded main-axis
            // (width) extent. A default Material button's
            // `ButtonStyle.maximumSize` is `Size.infinite`, so its internal
            // `RenderConstrainedBox` enforces that as a tight `w=Infinity` and
            // trips "BoxConstraints forces an infinite width" — aborting layout
            // of the whole ListView subtree and rendering tenant Home blank.
            // (`_TenantCard` escapes this only because its inflexible children
            // are Icons, which have a finite intrinsic width.) Making the button
            // a `Flexible` flex child means Flex sizes it against the *remaining
            // bounded* width instead of infinity; `FlexFit.loose` lets it shrink
            // to its content so the "Pay now" pill keeps its natural size.
            if (charge.openAmount > 0)
              Flexible(
                child: FilledButton(
                  onPressed: enabled && !busy ? onPay : null,
                  child: busy
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Text('Pay now'),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

/// Autopay enrollment card — plain language, with set-up / turn-off actions.
class _AutopayCard extends StatelessWidget {
  const _AutopayCard({
    required this.statusAsync,
    required this.busy,
    required this.onEnroll,
    required this.onCancel,
  });

  final AsyncValue<AutopayStatus> statusAsync;
  final bool busy;
  final VoidCallback onEnroll;
  final VoidCallback onCancel;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(Icons.autorenew, color: cs.primary),
            const SizedBox(width: 14),
            Expanded(
              child: statusAsync.when(
                loading: () => Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Autopay',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      'Checking…',
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: cs.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
                error: (_, _) => Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Autopay',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      "Couldn't load autopay status.",
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: cs.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
                data: (status) => Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Autopay',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      !status.onlinePaymentsAvailable
                          ? "Online payments aren't available yet."
                          : status.active
                          ? "You're set up. Rent is paid automatically each month."
                          : 'Set up autopay so rent is paid automatically each month.',
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: cs.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
              ),
            ),
            const SizedBox(width: 12),
            // Flexible for the same reason as _PayItemCard's "Pay now": this Row
            // has an Expanded sibling, so Flex measures this inflexible trailing
            // child with an unbounded width. A default Material button
            // (maximumSize == Size.infinite) cannot be measured at infinite
            // width and trips "BoxConstraints forces an infinite width", which
            // aborts the whole tenant-Home ListView subtree. Flexible makes Flex
            // size the button against the remaining bounded width; FlexFit.loose
            // keeps the button at its natural content width.
            Flexible(
              child: statusAsync.maybeWhen(
                data: (status) => busy
                    ? const SizedBox(
                        width: 18,
                        height: 18,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : status.active
                    ? OutlinedButton(
                        onPressed: onCancel,
                        child: const Text('Turn off'),
                      )
                    : FilledButton(
                        onPressed: status.onlinePaymentsAvailable
                            ? onEnroll
                            : null,
                        child: Text(
                          status.onlinePaymentsAvailable
                              ? 'Set up'
                              : 'Unavailable',
                        ),
                      ),
                orElse: () => const SizedBox.shrink(),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

String _shortDate(DateTime date) {
  const months = [
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
  if (date.year <= 1) return '';
  return '${months[date.month - 1]} ${date.day}';
}

class _TenantAccountLeaseTab extends StatelessWidget {
  const _TenantAccountLeaseTab();

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Account & lease'),
        actions: const [MobileNotificationBell(), MobileAccountMenu()],
      ),
      body: ListView(
        children: [
          ListTile(
            titleAlignment: ListTileTitleAlignment.center,
            leading: const Icon(Icons.receipt_long_outlined),
            title: const Text('Account history'),
            subtitle: const Text('Every charge and payment, explained.'),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => Navigator.of(context).push<void>(
              MaterialPageRoute<void>(
                builder: (_) => const TenantAccountHistoryScreen(),
              ),
            ),
          ),
          ListTile(
            titleAlignment: ListTileTitleAlignment.center,
            leading: const Icon(Icons.description_outlined),
            title: const Text('Lease'),
            subtitle: const Text('Your terms, rent and ledger.'),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => Navigator.of(context).push<void>(
              MaterialPageRoute<void>(
                builder: (_) => const TenantLeaseScreen(),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _TenantProfileTab extends ConsumerWidget {
  const _TenantProfileTab();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Profile'),
        actions: const [MobileNotificationBell(), MobileAccountMenu()],
      ),
      body: ListView(
        children: [
          ListTile(
            titleAlignment: ListTileTitleAlignment.center,
            leading: const Icon(Icons.event_outlined),
            title: const Text('Appointments'),
            subtitle: const Text('Upcoming showings and visits.'),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => Navigator.of(context).push<void>(
              MaterialPageRoute<void>(
                builder: (_) => const TenantAppointmentsScreen(),
              ),
            ),
          ),
          ListTile(
            titleAlignment: ListTileTitleAlignment.center,
            leading: const Icon(Icons.manage_accounts_outlined),
            title: const Text('Account settings'),
            subtitle: const Text('Sign-in, security and personal alerts.'),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => context.push('/settings'),
          ),
          ListTile(
            titleAlignment: ListTileTitleAlignment.center,
            leading: const Icon(Icons.logout_outlined),
            title: const Text('Sign out'),
            onTap: () async {
              await ref.read(authControllerProvider.notifier).logout();
            },
          ),
        ],
      ),
    );
  }
}

class _TenantLoadError extends ConsumerStatefulWidget {
  const _TenantLoadError({
    required this.error,
    required this.title,
    required this.onRetry,
  });

  final Object error;
  final String title;
  final VoidCallback onRetry;

  @override
  ConsumerState<_TenantLoadError> createState() => _TenantLoadErrorState();
}

class _TenantLoadErrorState extends ConsumerState<_TenantLoadError> {
  bool _recoveryAttempted = false;
  bool _recovering = false;

  bool get _sessionExpired {
    final error = widget.error;
    return error is ApiException && error.statusCode == 401;
  }

  @override
  void initState() {
    super.initState();
    _scheduleSilentSessionRecovery();
  }

  @override
  void didUpdateWidget(covariant _TenantLoadError oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (identical(oldWidget.error, widget.error)) return;
    _recoveryAttempted = false;
    _scheduleSilentSessionRecovery();
  }

  void _scheduleSilentSessionRecovery() {
    if (!_sessionExpired || _recoveryAttempted) return;
    _recoveryAttempted = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) unawaited(_recoverSession());
    });
  }

  Future<void> _recoverSession() async {
    if (_recovering) return;
    setState(() => _recovering = true);

    try {
      await _restoreTenantSessionSilently(ref);
      if (!mounted) return;
      if (ref.read(authControllerProvider) is AuthStateAuthenticated) {
        widget.onRetry();
      }
    } finally {
      if (mounted) setState(() => _recovering = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final message = _sessionExpired
        ? _recovering
              ? "We're reconnecting your account."
              : "We couldn't reconnect your account. Please try again."
        : 'Please check your connection and try again.';

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(Icons.cloud_off_outlined, color: colorScheme.onSurfaceVariant),
            const SizedBox(height: 12),
            Text(widget.title, style: theme.textTheme.titleMedium),
            const SizedBox(height: 4),
            Text(
              message,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 12),
            OutlinedButton.icon(
              onPressed: _recovering
                  ? null
                  : _sessionExpired
                  ? _recoverSession
                  : widget.onRetry,
              icon: _recovering
                  ? const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.refresh),
              label: _recovering
                  ? const Text('Reconnecting…')
                  : const Text('Try again'),
            ),
          ],
        ),
      ),
    );
  }
}

class _TenantCard extends StatelessWidget {
  const _TenantCard({
    required this.icon,
    required this.title,
    required this.value,
    required this.subtitle,
    this.onTap,
  });

  final IconData icon;
  final String title;
  final String value;
  final String subtitle;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final content = Padding(
      padding: const EdgeInsets.all(16),
      child: Row(
        children: [
          Icon(icon),
          const SizedBox(width: 14),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title, style: theme.textTheme.labelLarge),
                const SizedBox(height: 4),
                Text(value, style: theme.textTheme.headlineSmall),
                Text(subtitle, style: theme.textTheme.bodySmall),
              ],
            ),
          ),
          if (onTap != null)
            Icon(
              Icons.chevron_right,
              color: theme.colorScheme.onSurfaceVariant,
            ),
        ],
      ),
    );

    if (onTap == null) {
      return Card(child: content);
    }

    return Card(
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: content,
      ),
    );
  }
}

String tenantOverdueItemsLabel(int count) =>
    '$count overdue ${count == 1 ? 'item' : 'items'}';

String _money(num value, [String currency = 'USD']) {
  final amount = value
      .toStringAsFixed(2)
      .replaceAllMapped(RegExp(r'\B(?=(\d{3})+(?!\d))'), (m) => ',');
  return currency == 'USD' ? '\$$amount' : '$currency $amount';
}

class _HomeTab extends ConsumerWidget {
  const _HomeTab({
    required this.user,
    required this.onSwitchToTab,
    required this.onOpenAssistant,
    required this.onOpenCapture,
    required this.onOpenOverdue,
  });

  final AuthUser? user;

  /// Callback to switch the shell's active tab (0-based index).
  final void Function(int index) onSwitchToTab;
  final VoidCallback onOpenAssistant;

  /// Opens the Capture FAB menu (scan/gallery/PDF/voice/type).
  final VoidCallback onOpenCapture;

  /// Drills into the Money "Who's behind" overdue view.
  final VoidCallback onOpenOverdue;

  static const _moneyTabIndex = 2;
  static const _workTabIndex = 3;
  static const _inboxTabIndex = 4;

  String get _greeting {
    final hour = DateTime.now().hour;
    if (hour < 12) return 'Good morning';
    if (hour < 17) return 'Good afternoon';
    return 'Good evening';
  }

  String get _displayName {
    if (user == null) return '';
    final name = user!.displayName.trim();
    if (name.isEmpty) return user!.email;
    // First name only keeps it friendly.
    return name.split(' ').first;
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final briefingAsync = ref.watch(homeBriefingProvider);
    final messagesAsync = ref.watch(homeLatestMessagesProvider);
    final fieldQueueAsync = ref.watch(homeFieldQueueProvider);
    final moneyAsync = ref.watch(moneySnapshotProvider);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Rental Command'),
        actions: const [MobileNotificationBell(), MobileAccountMenu()],
      ),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(homeBriefingProvider);
          ref.invalidate(moneySnapshotProvider);
        },
        child: CustomScrollView(
          physics: const AlwaysScrollableScrollPhysics(),
          slivers: [
            SliverPadding(
              padding: const EdgeInsets.fromLTRB(20, 20, 20, 0),
              sliver: SliverToBoxAdapter(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    // ── Greeting (art band header, §7.7) ──────────────────
                    M3ArtBand(
                      pattern: 6,
                      eyebrow: _formattedDate(),
                      title:
                          '$_greeting${_displayName.isNotEmpty ? ", $_displayName" : ""}!',
                      subtitle: "Here's your command center for today.",
                    ),
                    const SizedBox(height: 24),

                    // ── Quick actions (A3: two doorways, not four) ────────
                    _QuickActions(
                      onScanOrAdd: onOpenCapture,
                      onAskAi: onOpenAssistant,
                    ),
                    const SizedBox(height: 32),

                    // ── Getting started checklist (hides when all done) ───
                    const _GettingStartedCard(),

                    // ── Today section header ──────────────────────────────
                    Text(
                      'Today',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                        color: cs.onSurface,
                      ),
                    ),
                    const SizedBox(height: 12),
                  ],
                ),
              ),
            ),

            // ── Briefing content ──────────────────────────────────────────
            SliverPadding(
              padding: const EdgeInsets.fromLTRB(20, 0, 20, 32),
              sliver: briefingAsync.when(
                loading: () => const SliverToBoxAdapter(
                  child: Center(
                    child: Padding(
                      padding: EdgeInsets.symmetric(vertical: 32),
                      child: CircularProgressIndicator(),
                    ),
                  ),
                ),
                error: (e, _) => SliverToBoxAdapter(
                  child: _BriefingError(
                    onRetry: () => ref.invalidate(homeBriefingProvider),
                  ),
                ),
                data: (briefing) => _BriefingContent(briefing: briefing),
              ),
            ),

            // ── Money snapshot (shared widget; past-due is actionable) ────
            SliverPadding(
              padding: const EdgeInsets.fromLTRB(20, 0, 20, 8),
              sliver: SliverToBoxAdapter(
                child: MoneySnapshotCard(
                  snapshotAsync: moneyAsync,
                  compact: true,
                  onRetry: () => ref.invalidate(moneySnapshotProvider),
                  onPastDueTap: onOpenOverdue,
                ),
              ),
            ),
            // Open the full Money tab from the dashboard headline.
            SliverPadding(
              padding: const EdgeInsets.fromLTRB(20, 0, 20, 32),
              sliver: SliverToBoxAdapter(
                child: Align(
                  alignment: Alignment.centerRight,
                  child: TextButton(
                    onPressed: () => onSwitchToTab(_moneyTabIndex),
                    child: const Text('Open Money'),
                  ),
                ),
              ),
            ),

            SliverPadding(
              padding: const EdgeInsets.fromLTRB(20, 0, 20, 32),
              sliver: SliverList(
                delegate: SliverChildListDelegate([
                  _HomeSectionHeader(
                    title: 'Latest messages',
                    actionLabel: 'Open inbox',
                    onAction: () => onSwitchToTab(_inboxTabIndex),
                  ),
                  const SizedBox(height: 8),
                  _LatestMessagesSection(messagesAsync: messagesAsync),
                  const SizedBox(height: 24),
                  _HomeSectionHeader(
                    // A6: one professional term — "Work Orders" — for the
                    // "things to fix" concept (was "Field queue").
                    title: 'Work Orders',
                    actionLabel: 'View all',
                    onAction: () => onSwitchToTab(_workTabIndex),
                  ),
                  const SizedBox(height: 8),
                  _FieldQueueSection(queueAsync: fieldQueueAsync),
                ]),
              ),
            ),
          ],
        ),
      ),
    );
  }

  String _formattedDate() {
    final now = DateTime.now();
    const months = [
      'January',
      'February',
      'March',
      'April',
      'May',
      'June',
      'July',
      'August',
      'September',
      'October',
      'November',
      'December',
    ];
    const weekdays = [
      'Monday',
      'Tuesday',
      'Wednesday',
      'Thursday',
      'Friday',
      'Saturday',
      'Sunday',
    ];
    final weekday = weekdays[now.weekday - 1];
    final month = months[now.month - 1];
    return '$weekday, $month ${now.day}';
  }
}

class _HomeSectionHeader extends StatelessWidget {
  const _HomeSectionHeader({
    required this.title,
    required this.actionLabel,
    required this.onAction,
  });

  final String title;
  final String actionLabel;
  final VoidCallback onAction;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Row(
      children: [
        Expanded(
          child: Text(
            title,
            style: theme.textTheme.titleMedium?.copyWith(
              fontWeight: FontWeight.w700,
            ),
          ),
        ),
        TextButton(onPressed: onAction, child: Text(actionLabel)),
      ],
    );
  }
}

class _LatestMessagesSection extends StatelessWidget {
  const _LatestMessagesSection({required this.messagesAsync});

  final AsyncValue<List<Conversation>> messagesAsync;

  @override
  Widget build(BuildContext context) {
    return messagesAsync.when(
      loading: () => const _LoadingCard(label: 'Loading messages...'),
      error: (_, _) => const _EmptyInlineCard(
        icon: Icons.forum_outlined,
        text: "Couldn't load messages.",
      ),
      data: (messages) {
        if (messages.isEmpty) {
          return const _EmptyInlineCard(
            icon: Icons.forum_outlined,
            text: 'No recent messages.',
          );
        }

        return Column(
          children: [
            for (final message in messages) ...[
              _MessageCard(conversation: message),
              const SizedBox(height: 8),
            ],
          ],
        );
      },
    );
  }
}

class _FieldQueueSection extends StatelessWidget {
  const _FieldQueueSection({required this.queueAsync});

  final AsyncValue<List<WorkOrder>> queueAsync;

  @override
  Widget build(BuildContext context) {
    return queueAsync.when(
      loading: () => const _LoadingCard(label: 'Loading work orders...'),
      error: (_, _) => const _EmptyInlineCard(
        icon: Icons.build_outlined,
        text: "Couldn't load work orders.",
      ),
      data: (queue) {
        if (queue.isEmpty) {
          return const _EmptyInlineCard(
            icon: Icons.check_circle_outline,
            text: 'No open work orders right now.',
          );
        }

        return Column(
          children: [
            for (final order in queue) ...[
              _FieldQueueCard(workOrder: order),
              const SizedBox(height: 8),
            ],
          ],
        );
      },
    );
  }
}

class _MessageCard extends StatelessWidget {
  const _MessageCard({required this.conversation});

  final Conversation conversation;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final preview = conversation.lastMessagePreview ?? '';

    return Card(
      child: ListTile(
        titleAlignment: ListTileTitleAlignment.center,
        onTap: () {
          Widget detailBuilder(BuildContext _) => MessageDetailScreen(
            conversationId: conversation.id,
            title: conversation.tenantName,
            subtitle: conversation.subject,
          );
          final shellNavigator = mobileShellNavigatorOf(context);
          if (shellNavigator != null) {
            shellNavigator.openTab(
              MobileShellTabId.inbox,
              destination: MobileDestinationId.messages,
              detailBuilder: detailBuilder,
            );
            revealMobileShellIfDetached(context);
            return;
          }

          Navigator.of(
            context,
          ).push<void>(MaterialPageRoute<void>(builder: detailBuilder));
        },
        leading: CircleAvatar(
          backgroundColor: conversation.hasUnread
              ? cs.primaryContainer
              : cs.surfaceContainerHighest,
          child: Icon(
            conversation.hasUnread ? Icons.mark_chat_unread : Icons.forum,
            color: conversation.hasUnread
                ? cs.onPrimaryContainer
                : cs.onSurfaceVariant,
            size: 18,
          ),
        ),
        title: Text(
          conversation.tenantName,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: theme.textTheme.bodyMedium?.copyWith(
            fontWeight: conversation.hasUnread
                ? FontWeight.w700
                : FontWeight.w600,
          ),
        ),
        subtitle: Text(
          preview.isEmpty ? conversation.subject : preview,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
        trailing: conversation.hasUnread
            ? Badge(label: Text('${conversation.unreadCount}'))
            : const Icon(Icons.chevron_right),
      ),
    );
  }
}

void _pushTodayDetail(BuildContext context, WidgetBuilder detailBuilder) {
  Navigator.of(
    context,
  ).push<void>(MaterialPageRoute<void>(builder: detailBuilder));
}

class _FieldQueueCard extends StatelessWidget {
  const _FieldQueueCard({required this.workOrder});

  final WorkOrder workOrder;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final propertyName = workOrder.propertyName?.trim() ?? '';
    final status = plainEnglishLabel(workOrder.status);
    final priority = plainEnglishLabel(workOrder.priority);

    return Card(
      child: ListTile(
        titleAlignment: ListTileTitleAlignment.center,
        onTap: () {
          Widget detailBuilder(BuildContext _) =>
              WorkOrderUnitAwareLoaderScreen(workOrderId: workOrder.id);
          _pushTodayDetail(context, detailBuilder);
        },
        leading: CircleAvatar(
          backgroundColor: _priorityBg(workOrder.priority, cs),
          child: Icon(
            Icons.build_outlined,
            color: _priorityFg(workOrder.priority, cs),
            size: 18,
          ),
        ),
        title: Text(
          workOrder.title,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: theme.textTheme.bodyMedium?.copyWith(
            fontWeight: FontWeight.w600,
          ),
        ),
        subtitle: Text(
          [
            if (propertyName.isNotEmpty) propertyName,
            '$status · $priority priority',
          ].join('\n'),
          maxLines: 2,
          overflow: TextOverflow.ellipsis,
        ),
        trailing: const Icon(Icons.chevron_right),
      ),
    );
  }

  Color _priorityBg(String priority, ColorScheme cs) {
    switch (priority.toLowerCase()) {
      case 'emergency':
      case 'high':
        return cs.errorContainer;
      case 'normal':
        return cs.secondaryContainer;
      default:
        return cs.surfaceContainerHighest;
    }
  }

  Color _priorityFg(String priority, ColorScheme cs) {
    switch (priority.toLowerCase()) {
      case 'emergency':
      case 'high':
        return cs.onErrorContainer;
      case 'normal':
        return cs.onSecondaryContainer;
      default:
        return cs.onSurfaceVariant;
    }
  }
}

class _LoadingCard extends StatelessWidget {
  const _LoadingCard({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            const SizedBox(
              width: 18,
              height: 18,
              child: CircularProgressIndicator(strokeWidth: 2),
            ),
            const SizedBox(width: 12),
            Text(label),
          ],
        ),
      ),
    );
  }
}

class _EmptyInlineCard extends StatelessWidget {
  const _EmptyInlineCard({required this.icon, required this.text});

  final IconData icon;
  final String text;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(icon, color: cs.onSurfaceVariant, size: 20),
            const SizedBox(width: 12),
            Expanded(child: Text(text)),
          ],
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// Getting-started checklist card (M-10)
// ---------------------------------------------------------------------------

/// Persistent "Getting started" nudge on the landlord dashboard. Shows the
/// onboarding checklist's progress and opens the full checklist on tap. Mirrors
/// the web `GettingStartedCard`: it renders NOTHING (zero height) while the
/// signals are still loading or errored (no layout jank / no flash of an
/// "all to-do" card) AND once every task is complete, so it never nags a
/// set-up landlord.
///
/// A1: in Sandbox the checklist is auto-satisfied by SEEDED demo records, so
/// "core setup complete" / "all done" would be a lie — the account has no REAL
/// property/tenant/lease. In Sandbox we therefore keep the card up with honest
/// "Exploring with sample data" framing plus a "set up my own rentals" nudge
/// (the actionable go-live control, shared with A11), and never celebrate the
/// seeded spine. Only a Live account celebrates / hides on completion.
class _GettingStartedCard extends ConsumerWidget {
  const _GettingStartedCard();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // Decide Sandbox-vs-Live from the dedicated sandbox-state provider (shared
    // autoDispose cache with the _SandboxIndicator). While it's still loading we
    // fall back to the honest "not sandbox" path — the card already hides itself
    // until the checklist signals settle, so nothing flashes.
    final isSandbox = ref
        .watch(sandboxStateProvider)
        .maybeWhen(data: (s) => s.isSandbox, orElse: () => false);

    final progress = ref.watch(gettingStartedProgressProvider);
    // Hidden until data settles. In Live, also hidden once everything's done. In
    // Sandbox we NEVER treat the seeded "all done" as real completion (A1), so
    // the card stays up to point the user at setting up their own rentals.
    if (progress == null) return const SizedBox.shrink();
    if (!isSandbox && progress.allDone) return const SizedBox.shrink();

    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final fraction = progress.totalCount == 0
        ? 0.0
        : progress.doneCount / progress.totalCount;

    void openChecklist() => Navigator.of(context).push<void>(
      MaterialPageRoute<void>(builder: (_) => const GettingStartedScreen()),
    );

    // The next not-yet-done task, surfaced inline as a one-tap hint (Live only —
    // in Sandbox the seeded records auto-check everything, so there is no "next").
    GettingStartedTask? nextTask;
    if (!isSandbox) {
      final signals = ref.watch(gettingStartedSignalsProvider).value;
      if (signals != null) {
        for (final task in kGettingStartedTasks) {
          if (!task.isComplete(signals)) {
            nextTask = task;
            break;
          }
        }
      }
    }

    return Padding(
      padding: const EdgeInsets.only(bottom: 24),
      child: Card(
        color: cs.primaryContainer.withValues(alpha: 0.35),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: openChecklist,
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Container(
                      padding: const EdgeInsets.all(10),
                      decoration: BoxDecoration(
                        color: cs.primary.withValues(alpha: 0.14),
                        borderRadius: BorderRadius.circular(12),
                      ),
                      child: Icon(
                        Symbols.checklist_rounded,
                        color: cs.primary,
                        size: 22,
                        fill: 1,
                      ),
                    ),
                    const SizedBox(width: 14),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            'Getting started',
                            style: theme.textTheme.titleSmall?.copyWith(
                              fontWeight: FontWeight.w700,
                            ),
                          ),
                          const SizedBox(height: 2),
                          Text(
                            isSandbox
                                ? "This is example data so you can look around. "
                                      "When you're ready, set up your own rentals."
                                : 'A short checklist to get your rentals set up '
                                      '— each step takes you to the right spot.',
                            style: theme.textTheme.bodySmall?.copyWith(
                              color: cs.onSurfaceVariant,
                            ),
                          ),
                        ],
                      ),
                    ),
                    Icon(
                      Symbols.chevron_right_rounded,
                      color: cs.onSurfaceVariant,
                    ),
                  ],
                ),
                const SizedBox(height: 14),
                if (isSandbox)
                  // A1/A11: honest "sample data" framing + the actionable
                  // go-live nudge — no progress bar, no "core setup complete".
                  Align(
                    alignment: Alignment.centerLeft,
                    child: FilledButton.tonalIcon(
                      onPressed: () =>
                          _openGoLiveSheetAndRefreshHome(context, ref),
                      icon: const Icon(Symbols.rocket_launch_rounded, fill: 1),
                      label: const Text('Set up my rentals'),
                    ),
                  )
                else ...[
                  Row(
                    children: [
                      Text(
                        '${progress.coreDoneCount} of '
                        '${progress.coreTotalCount} essentials',
                        style: theme.textTheme.labelMedium?.copyWith(
                          color: cs.onSurfaceVariant,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                      const Spacer(),
                      if (progress.allCoreDone)
                        Text(
                          'Core setup complete',
                          style: theme.textTheme.labelMedium?.copyWith(
                            color: cs.primary,
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                    ],
                  ),
                  const SizedBox(height: 8),
                  ClipRRect(
                    borderRadius: BorderRadius.circular(8),
                    child: LinearProgressIndicator(
                      value: fraction,
                      minHeight: 8,
                      backgroundColor: cs.surfaceContainerHighest,
                    ),
                  ),
                  if (nextTask != null) ...[
                    const SizedBox(height: 14),
                    Container(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 12,
                        vertical: 10,
                      ),
                      decoration: BoxDecoration(
                        color: cs.surface.withValues(alpha: 0.6),
                        borderRadius: BorderRadius.circular(14),
                      ),
                      child: Row(
                        children: [
                          Icon(
                            nextTask.icon,
                            size: 18,
                            color: cs.primary,
                            fill: 1,
                          ),
                          const SizedBox(width: 10),
                          Expanded(
                            child: Text(
                              'Next: ${nextTask.label}',
                              style: theme.textTheme.bodyMedium?.copyWith(
                                fontWeight: FontWeight.w600,
                              ),
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                            ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ],
              ],
            ),
          ),
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// Quick-action buttons
// ---------------------------------------------------------------------------

/// The two home quick actions.
///
/// A3 collapsed the old four tiles ("Tell me", "Scan a document", "Ask AI",
/// "Add expense") to two: "Scan or add" and "Ask". The two removed tiles were
/// duplicate doorways — "Scan a document" and "Add expense" opened the *same*
/// capture sheet, and "Tell me" (voice) already lives inside that sheet — so no
/// capability is lost: the capture sheet still covers scan / gallery / PDF /
/// voice ("Tell me") / type. A4 also makes "Ask" the single AI entry point.
class _QuickActions extends StatelessWidget {
  const _QuickActions({required this.onScanOrAdd, required this.onAskAi});

  /// Opens the capture sheet (scan / gallery / PDF / voice / type).
  final VoidCallback onScanOrAdd;

  /// Opens the AI assistant (the single "Ask" entry point).
  final VoidCallback onAskAi;

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Expanded(
          child: _QuickActionButton(
            icon: Symbols.add_a_photo_rounded,
            label: 'Scan or add',
            family: M3TonalFamily.sky,
            onTap: onScanOrAdd,
          ),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: _QuickActionButton(
            icon: Symbols.auto_awesome_rounded,
            label: 'Ask',
            family: M3TonalFamily.mint,
            onTap: onAskAi,
          ),
        ),
      ],
    );
  }
}

class _QuickActionButton extends StatelessWidget {
  const _QuickActionButton({
    required this.icon,
    required this.label,
    required this.family,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final M3TonalFamily family;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return M3TonalCard(
      family: family,
      padding: const EdgeInsets.symmetric(vertical: 16, horizontal: 8),
      onTap: onTap,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, color: cs.onSurface, size: 26, fill: 1),
          const SizedBox(height: 6),
          Text(
            label,
            textAlign: TextAlign.center,
            style: Theme.of(context).textTheme.labelMedium?.copyWith(
              color: cs.onSurface,
              height: 1.25,
            ),
          ),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// Briefing content
// ---------------------------------------------------------------------------

class _BriefingContent extends StatelessWidget {
  const _BriefingContent({required this.briefing});

  final BriefingResponse briefing;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final bullets = briefing.bullets.take(5).toList();

    if (briefing.summary == null && bullets.isEmpty) {
      return SliverToBoxAdapter(child: _AllClearCard());
    }

    return SliverList(
      delegate: SliverChildListDelegate([
        // Optional AI-composed summary
        if (briefing.summary != null && briefing.summary!.isNotEmpty) ...[
          Card(
            child: Padding(
              padding: const EdgeInsets.all(14),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Icon(Icons.auto_awesome, color: cs.primary, size: 18),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      briefing.summary!,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color: cs.onSurface,
                        height: 1.5,
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(height: 10),
        ],

        // Bullet items
        if (bullets.isEmpty) _AllClearCard(),

        for (final bullet in bullets) ...[
          _BulletRow(bullet: bullet),
          const SizedBox(height: 8),
        ],
      ]),
    );
  }
}

class _AllClearCard extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(
              Icons.check_circle_outline,
              color: Colors.green.shade600,
              size: 22,
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                'All clear — nothing urgent today.',
                style: Theme.of(
                  context,
                ).textTheme.bodyMedium?.copyWith(color: cs.onSurface),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _BulletRow extends StatelessWidget {
  const _BulletRow({required this.bullet});

  final BriefingBullet bullet;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final (iconData, iconColor, bgColor) = _severityStyle(bullet.severity, cs);

    final target = _targetFor(bullet);

    final content = Padding(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.center,
        children: [
          Container(
            padding: const EdgeInsets.all(6),
            decoration: BoxDecoration(
              color: bgColor,
              borderRadius: BorderRadius.circular(8),
            ),
            child: Icon(iconData, color: iconColor, size: 16),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  bullet.title,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    fontWeight: FontWeight.w600,
                    color: cs.onSurface,
                  ),
                ),
                if (bullet.detail.isNotEmpty) ...[
                  const SizedBox(height: 2),
                  Text(
                    bullet.detail,
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: cs.onSurfaceVariant,
                      height: 1.4,
                    ),
                  ),
                ],
              ],
            ),
          ),
          if (target != null) ...[
            const SizedBox(width: 8),
            Icon(Icons.chevron_right, color: cs.onSurfaceVariant, size: 18),
          ],
        ],
      ),
    );

    if (target == null) {
      return Card(child: content);
    }

    return Card(
      child: InkWell(
        onTap: () => _openTarget(context, target),
        borderRadius: BorderRadius.circular(12),
        child: content,
      ),
    );
  }

  void _openTarget(BuildContext context, _BriefingTarget target) {
    if (target.preserveTodayOrigin && target.detailBuilder != null) {
      _pushTodayDetail(context, target.detailBuilder!);
      return;
    }

    final shellNavigator = mobileShellNavigatorOf(context);
    if (shellNavigator != null) {
      shellNavigator.openTab(
        target.tab,
        destination: target.destination,
        detailBuilder: target.detailBuilder,
      );
      revealMobileShellIfDetached(context);
      return;
    }

    if (target.fallbackBuilder != null) {
      Navigator.of(
        context,
      ).push<void>(MaterialPageRoute<void>(builder: target.fallbackBuilder!));
    }
  }

  /// Maps a briefing bullet's referenced entity to the screen that shows it.
  ///
  /// Returns `null` when the bullet has no entity reference or the type isn't
  /// navigable, in which case the card is rendered without a tap handler.
  _BriefingTarget? _targetFor(BriefingBullet bullet) {
    final type = bullet.entityType;
    final id = bullet.entityId;

    // An overdue-rent bullet must land on the actionable "Who's behind" view
    // (Mark paid / Text / edit per tenant), NOT the generic payments list. The
    // server tags these with category "RentLate". If the bullet includes a
    // specific payment id, the entity detail route below is more precise.
    if (bullet.category == 'RentLate' && !(type == 'Payment' && id != null)) {
      return _BriefingTarget(
        tab: MobileShellTabId.money,
        destination: MobileDestinationId.moneyOverview,
        detailBuilder: (_) => const OverdueScreen(),
      );
    }

    if (type == null) return null;

    switch (type) {
      case 'WorkOrder':
        if (id == null) return null;
        return _BriefingTarget(
          tab: MobileShellTabId.work,
          destination: MobileDestinationId.workOrders,
          detailBuilder: (_) => WorkOrderUnitAwareLoaderScreen(workOrderId: id),
          preserveTodayOrigin: true,
        );
      case 'Payment':
        return _BriefingTarget(
          tab: MobileShellTabId.money,
          destination: MobileDestinationId.payments,
          detailBuilder: (_) => const PaymentsScreen(),
        );
      case 'LeaseManagement':
        if (id != null) {
          return _BriefingTarget(
            tab: MobileShellTabId.rentals,
            destination: MobileDestinationId.units,
            detailBuilder: (_) =>
                LeaseManagementDetailLoaderScreen(leaseManagementId: id),
          );
        }
        return _BriefingTarget(
          tab: MobileShellTabId.rentals,
          destination: MobileDestinationId.leases,
          fallbackBuilder: (_) => const LeasesListScreen(),
        );
      case 'Tenant':
        if (id != null) {
          return _BriefingTarget(
            tab: MobileShellTabId.rentals,
            destination: MobileDestinationId.tenants,
            detailBuilder: (_) => TenantDetailLoaderScreen(tenantId: id),
          );
        }
        return _BriefingTarget(
          tab: MobileShellTabId.rentals,
          destination: MobileDestinationId.tenants,
          fallbackBuilder: (_) => const TenantsListScreen(),
        );
      // Appointment/Inspection detail screens need a fully-loaded model (or are a
      // "run" action), so land on their list — still actionable, never a dead end.
      case 'Appointment':
        return _BriefingTarget(
          tab: MobileShellTabId.work,
          destination: MobileDestinationId.calendar,
          fallbackBuilder: (_) => const AppointmentsScreen(),
        );
      case 'Inspection':
        return _BriefingTarget(
          tab: MobileShellTabId.work,
          destination: MobileDestinationId.inspections,
          fallbackBuilder: (_) => const InspectionsListScreen(),
        );
      default:
        return null;
    }
  }

  (IconData, Color, Color) _severityStyle(
    BulletSeverity severity,
    ColorScheme cs,
  ) {
    switch (severity) {
      case BulletSeverity.critical:
        return (Icons.warning_rounded, cs.error, cs.errorContainer);
      case BulletSeverity.warning:
        return (
          Icons.info_outline,
          Colors.amber.shade700,
          Colors.amber.shade100,
        );
      case BulletSeverity.info:
        return (Icons.info_outline, cs.primary, cs.primaryContainer);
    }
  }
}

class _BriefingTarget {
  const _BriefingTarget({
    required this.tab,
    required this.destination,
    this.detailBuilder,
    this.fallbackBuilder,
    this.preserveTodayOrigin = false,
  });

  final MobileShellTabId tab;
  final MobileDestinationId destination;
  final MobileDetailBuilder? detailBuilder;
  final WidgetBuilder? fallbackBuilder;
  final bool preserveTodayOrigin;
}

// ---------------------------------------------------------------------------
// Error state
// ---------------------------------------------------------------------------

class _BriefingError extends StatelessWidget {
  const _BriefingError({required this.onRetry});

  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(Icons.wifi_off_outlined, color: cs.error, size: 22),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                "Couldn't load today's briefing.",
                style: Theme.of(context).textTheme.bodyMedium,
              ),
            ),
            TextButton(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _TabItem helper
// ---------------------------------------------------------------------------

class _TabItem {
  const _TabItem({required this.label, required this.icon});

  final String label;

  /// A `Symbols.*_rounded` glyph (Material Symbols Rounded) whose FILL axis is
  /// animated 0→1 when the tab is active.
  final IconData icon;
}
