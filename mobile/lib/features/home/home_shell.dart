import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:material_symbols_icons/symbols.dart';
import 'package:url_launcher/url_launcher.dart';
import 'package:uuid/uuid.dart';

import 'package:go_router/go_router.dart';

import '../../core/api/api_exception.dart';
import '../../core/theme/app_recipes.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/models/models.dart';
import '../../core/push/push_service.dart';
import '../../core/realtime/realtime_providers.dart';
import '../../core/voice/voice_command.dart';
import '../../core/voice/voice_command_controller.dart';
import '../accounting/accounting_repository.dart';
import '../onboarding/onboarding_repository.dart';
import '../notifications/notifications_inbox_screen.dart';
import '../ai/ai_models.dart';
import '../ai/ai_repository.dart';
import '../appointments/appointments_screen.dart';
import '../appointments/tenant_appointments_screen.dart';
import '../inspections/inspections_list_screen.dart';
import '../leases/lease_detail_screen.dart';
import '../leases/leases_list_screen.dart';
import '../maintenance/work_order_unit_aware_loader.dart';
import '../maintenance/work_orders_repository.dart';
import '../messages/message_detail_screen.dart';
import '../messages/message_models.dart';
import '../messages/messages_list_screen.dart';
import '../messages/messages_repository.dart';
import '../money/expense_detail_screen.dart';
import '../money/money_snapshot_card.dart';
import '../money/overdue_screen.dart';
import '../notifications/notifications_repository.dart';
import '../onboarding/go_live_sheet.dart';
import '../onboarding/getting_started_provider.dart';
import '../onboarding/getting_started_screen.dart';
import '../onboarding/getting_started_tasks.dart';
import '../payments/payment_detail_screen.dart';
import '../payments/payment_lease_labels.dart';
import '../portal/tenant_account_history_screen.dart';
import '../portal/tenant_portal_repository.dart';
import '../portal/tenant_work_order_detail_screen.dart';
import '../scan/scan_review_screen.dart';
import '../tenants/tenant_detail_screen.dart';
import '../tenants/tenants_list_screen.dart';
import '../tenants/tenant_lease_screen.dart';
import '../units/unit_command_center_screen.dart';
import 'mobile_domain_hub.dart';
import 'mobile_domain_navigation.dart';
import 'mobile_quick_action_fab.dart';
import 'mobile_quick_action_helpers.dart';
import 'mobile_shell_actions.dart';

// ---------------------------------------------------------------------------
// Briefing provider (home-tab only, autoDispose)
// ---------------------------------------------------------------------------

final _briefingProvider = FutureProvider.autoDispose<BriefingResponse>((ref) {
  return ref.watch(aiRepositoryProvider).briefing();
});

final _latestMessagesProvider = FutureProvider.autoDispose<List<Conversation>>((
  ref,
) async {
  final conversations = await ref
      .watch(messagesRepositoryProvider)
      .listConversations();
  return conversations.take(5).toList();
});

final _fieldQueueProvider = FutureProvider.autoDispose<List<WorkOrder>>((
  ref,
) async {
  final page = await ref
      .watch(workOrdersRepositoryProvider)
      .listWorkOrdersPage(
        const WorkOrderListQuery(openOnly: true, take: 5, sort: 'fieldQueue'),
      );
  return page.items;
});

Future<void> _openGoLiveSheetAndRefreshHome(
  BuildContext context,
  WidgetRef ref,
) async {
  final wentLive = await showGoLiveSheet(context);
  if (wentLive != true) return;
  ref.invalidate(_briefingProvider);
  ref.invalidate(_latestMessagesProvider);
  ref.invalidate(_fieldQueueProvider);
}

// ---------------------------------------------------------------------------
// HomeShell
// ---------------------------------------------------------------------------

/// Bottom-navigation app shell.
///
/// Landlord tabs: Today · Rentals · Money · Work · Inbox.
/// Tenant tabs: Home · Messages · Maintenance · More
class HomeShell extends ConsumerStatefulWidget {
  const HomeShell({super.key});

  @override
  ConsumerState<HomeShell> createState() => _HomeShellState();
}

class _HomeShellState extends ConsumerState<HomeShell>
    with WidgetsBindingObserver {
  final _domainNavigators = <MobileShellTabId, MobileDomainNavigator>{};
  late final List<MobileQuickActionController> _quickActionControllers =
      List.generate(_tabs.length, (_) => MobileQuickActionController());
  late final MobileShellNavigator _shellNavigator;
  int _selectedIndex = 0;

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
    _TabItem(label: 'Messages', icon: Symbols.forum_rounded),
    _TabItem(label: 'Maintenance', icon: Symbols.build_rounded),
    _TabItem(label: 'More', icon: Symbols.more_horiz_rounded),
  ];

  void _openAssistant() => openMobileAssistant(context);
  void _openCapture() => openMobileScan(context);
  void _openRecord() => openMobileRecord(context);

  void _registerDomain(MobileShellTabId tab, MobileDomainNavigator controller) {
    _domainNavigators[tab] = controller;
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
    bool hasAny(Iterable<String> keys) => keys.any(capabilities.contains);
    final tabs = <MobileShellTabId>[
      if (auth.activeExperience == WorkspaceExperience.management)
        MobileShellTabId.today,
      if (hasAny(const [
        'rentals.read',
        'rentals.manage',
        'leasing.listings.manage',
        'leasing.applications.manage',
      ]))
        MobileShellTabId.rentals,
      if (hasAny(const [
        'money.balances.read',
        'money.payments.manage',
        'money.expenses.manage',
        'money.owner-reports.read',
      ]))
        MobileShellTabId.money,
      if (hasAny(const [
        'work.read',
        'work.manage',
        'maintenance.assigned-work.read',
        'maintenance.assigned-work.update',
      ]))
        MobileShellTabId.work,
      if (hasAny(const [
        'rentals.read',
        'work.read',
        'leasing.applications.manage',
        'maintenance.assigned-work.converse',
      ]))
        MobileShellTabId.inbox,
    ];
    return tabs.isEmpty ? const [MobileShellTabId.today] : tabs;
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

      final domainNavigator = _domainNavigators[tab];
      if (destination != null && domainNavigator != null) {
        domainNavigator.openDestination(
          destination,
          detailBuilder: detailBuilder,
        );
        return;
      }

      if (detailBuilder != null) {
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
        _openShellTab(
          MobileShellTabId.work,
          destination: MobileDestinationId.workOrders,
        );
        return true;
      case '/money':
        _openShellTab(
          MobileShellTabId.money,
          destination: MobileDestinationId.insights,
        );
        return true;
      case '/inbox':
        _openShellTab(
          MobileShellTabId.inbox,
          destination: MobileDestinationId.messages,
        );
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
      case 'payments':
        _openShellTab(
          MobileShellTabId.money,
          destination: MobileDestinationId.moneyLedger,
          detailBuilder: (_) => PaymentDetailScreen(paymentId: id),
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
    });
  }

  @override
  void dispose() {
    MobileShellNavigationRegistry.detach(_shellNavigator);
    WidgetsBinding.instance.removeObserver(this);
    for (final controller in _quickActionControllers) {
      controller.dispose();
    }
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
  void _handlePushLink(String route) {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      if (ref.read(authControllerProvider) is AuthStateAuthenticated) {
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
      final isTenant =
          authState is AuthStateAuthenticated && authState.isTenantExperience;
      if (isTenant) {
        toast("Voice commands aren't available for tenant accounts yet.");
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

  _TabItem _tabItemFor(MobileShellTabId tab) => _tabs[_tabIds.indexOf(tab)];

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
    ref.listen<String?>(pendingPushLinkProvider, (_, next) {
      if (next != null) _handlePushLink(next);
    });

    ref.listen<AuthState>(authControllerProvider, (previous, next) {
      if (previous is! AuthStateAuthenticated ||
          next is! AuthStateAuthenticated) {
        return;
      }
      final previousContext = previous.access.selectedContext;
      final nextContext = next.access.selectedContext;
      if (previousContext.accessContextId == nextContext.accessContextId &&
          previousContext.accessRevision == nextContext.accessRevision) {
        return;
      }
      _selectedIndex = 0;
      unawaited(resetAccessScopedClient(ref));
    });

    final authState = ref.watch(authControllerProvider);
    if (authState is! AuthStateAuthenticated) {
      return const SizedBox.shrink();
    }
    final user = authState.user;
    final tenantMode = authState.isTenantExperience;
    final landlordTabs = tenantMode
        ? const <MobileShellTabId>[]
        : _availableLandlordTabs(authState);
    final tabs = tenantMode
        ? _tenantTabs
        : landlordTabs.map(_tabItemFor).toList(growable: false);
    final selectedIndex = _selectedIndex >= tabs.length
        ? tabs.length - 1
        : _selectedIndex;
    final quickActionController = tenantMode
        ? null
        : _quickActionControllers[_tabIds.indexOf(landlordTabs[selectedIndex])];

    return MobileShellNavigation(
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
                        const MessagesListScreen(),
                        const _TenantMaintenanceTab(),
                        const _TenantMoreTab(),
                      ]
                    : landlordTabs
                          .map(
                            (tab) => _buildLandlordTab(tab, user, landlordTabs),
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
                    onScan: _openCapture,
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

  AuthUser? get user => widget.user;

  /// Rent items the tenant can pay online: anything not already settled.
  static const _settledStatuses = {'Paid', 'Waived', 'Refunded', 'Cancelled'};

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
  Future<void> _payNow(TenantPortalPayment payment) async {
    if (_payingPaymentId != null) return;
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _payingPaymentId = payment.id);
    try {
      final url = await ref
          .read(tenantPortalRepositoryProvider)
          .payCheckout(payment.tenantAccountId, payment.id);
      if (url.isEmpty) return;
      await _open(url);
    } on ApiException catch (e) {
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(
              e.statusCode == 503
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
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(
              e.statusCode == 503
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
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
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
        actions: const [MobileAccountMenu()],
      ),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(tenantPortalSnapshotProvider);
          // Refresh autopay state too; the tenant may have just returned from
          // a hosted Checkout in the browser.
          final tenantAccountId = ref
              .read(tenantPortalSnapshotProvider)
              .value
              ?.payments
              .firstOrNull
              ?.tenantAccountId;
          if (tenantAccountId != null) {
            ref.invalidate(tenantAutopayStatusProvider(tenantAccountId));
          }
        },
        child: snapshot.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (err, _) => ListView(
            padding: const EdgeInsets.all(20),
            children: [
              Text(
                'Could not load your dashboard.',
                style: theme.textTheme.titleMedium,
              ),
              const SizedBox(height: 8),
              Text('$err'),
            ],
          ),
          data: (data) {
            final openOrders = data.workOrders
                .where(
                  (w) => !{
                    'Completed',
                    'Cancelled',
                    'Archived',
                  }.contains(w.status),
                )
                .toList();
            final unpaid =
                data.payments
                    .where((p) => !_settledStatuses.contains(p.status))
                    .toList()
                  ..sort((a, b) => a.dueDate.compareTo(b.dueDate));
            final nextPayment = unpaid.isEmpty ? null : unpaid.first;
            final unreadNotifications = data.notifications
                .where((n) => !n.isRead)
                .length;
            final primaryTenantAccountId =
                data.payments.firstOrNull?.tenantAccountId;

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
                if (data.notifications.isNotEmpty)
                  _TenantCard(
                    icon: Icons.notifications_outlined,
                    title: 'Notifications',
                    value: '$unreadNotifications unread',
                    subtitle: data.notifications.first.title,
                  ),
                _TenantCard(
                  icon: Icons.warning_amber_outlined,
                  title: 'Overdue',
                  value: _money(data.balance.overdue),
                  subtitle: '${data.balance.overdueCount} overdue item(s)',
                ),
                if (data.leases.isNotEmpty)
                  _TenantCard(
                    icon: Icons.receipt_long_outlined,
                    title: 'Account history',
                    value: 'View',
                    subtitle: 'Every charge and payment, explained',
                    onTap: () => Navigator.of(context).push<void>(
                      MaterialPageRoute<void>(
                        builder: (_) => const TenantAccountHistoryScreen(),
                      ),
                    ),
                  ),
                _TenantCard(
                  icon: Icons.payments_outlined,
                  title: 'Next rent due',
                  value: nextPayment == null
                      ? 'None'
                      : _dueInLabel(nextPayment.dueDate),
                  subtitle: nextPayment == null
                      ? 'No unpaid rent scheduled'
                      : '${_money(nextPayment.amount)} due',
                ),

                // ── Pay rent ──────────────────────────────────────────────
                if (unpaid.isNotEmpty) ...[
                  const SizedBox(height: 8),
                  Text(
                    'Pay rent',
                    style: theme.textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  const SizedBox(height: 8),
                  for (final payment in unpaid)
                    _PayItemCard(
                      payment: payment,
                      busy: _payingPaymentId == payment.id,
                      // Disable other buttons while one Checkout is starting.
                      enabled:
                          _payingPaymentId == null ||
                          _payingPaymentId == payment.id,
                      onPay: () => _payNow(payment),
                    ),
                ],

                // ── Autopay ───────────────────────────────────────────────
                if (primaryTenantAccountId != null) ...[
                  const SizedBox(height: 8),
                  _AutopayCard(
                    statusAsync: ref.watch(
                      tenantAutopayStatusProvider(primaryTenantAccountId),
                    ),
                    busy: _busyAutopayAccountId == primaryTenantAccountId,
                    onEnroll: () => _enrollAutopay(primaryTenantAccountId),
                    onCancel: () => _cancelAutopay(primaryTenantAccountId),
                  ),
                ],

                const SizedBox(height: 8),
                _TenantCard(
                  icon: Icons.build_outlined,
                  title: 'Open maintenance',
                  value: '${openOrders.length}',
                  subtitle: openOrders.isEmpty
                      ? 'No open requests'
                      : openOrders.first.title,
                ),
              ],
            );
          },
        ),
      ),
    );
  }
}

/// A single unpaid/scheduled/late rent item with a "Pay now" action that opens
/// a hosted Stripe Checkout in the browser.
class _PayItemCard extends StatelessWidget {
  const _PayItemCard({
    required this.payment,
    required this.busy,
    required this.enabled,
    required this.onPay,
  });

  final TenantPortalPayment payment;
  final bool busy;
  final bool enabled;
  final VoidCallback onPay;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final isLate = payment.dueDate.isBefore(
      DateTime.now().subtract(const Duration(days: 1)),
    );
    final dueLabel = isLate ? 'Past due' : 'Due ${_shortDate(payment.dueDate)}';

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
                    _money(payment.amount),
                    style: theme.textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  Text(
                    '${payment.type.isEmpty ? 'Rent' : paymentTypeLabel(payment.type)} · $dueLabel',
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
                      status.active
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
                        onPressed: onEnroll,
                        child: const Text('Set up'),
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

/// Human-friendly "time until due" label for the next rent payment.
///
/// Past-due dates never render a negative number ("-70 days"); they read as
/// "Past due by N days", matching how [_PayItemCard] surfaces late rent.
String _dueInLabel(DateTime dueDate) {
  final now = DateTime.now();
  final today = DateTime(now.year, now.month, now.day);
  final due = DateTime(dueDate.year, dueDate.month, dueDate.day);
  final days = due.difference(today).inDays;
  if (days > 0) return '$days day${days == 1 ? '' : 's'}';
  if (days == 0) return 'Due today';
  final overdueBy = -days;
  return 'Past due by $overdueBy day${overdueBy == 1 ? '' : 's'}';
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

class _TenantMaintenanceTab extends ConsumerStatefulWidget {
  const _TenantMaintenanceTab();

  @override
  ConsumerState<_TenantMaintenanceTab> createState() =>
      _TenantMaintenanceTabState();
}

class _TenantMaintenanceTabState extends ConsumerState<_TenantMaintenanceTab> {
  final _title = TextEditingController();
  final _description = TextEditingController();
  String _priority = 'Normal';
  Uint8List? _photoBytes;
  String? _photoName;
  String? _photoContentType;
  String? _photoUploadOperationId;
  bool _saving = false;

  @override
  void dispose() {
    _title.dispose();
    _description.dispose();
    super.dispose();
  }

  Future<void> _pickPhoto(ImageSource source) async {
    final picked = await ImagePicker().pickImage(
      source: source,
      imageQuality: 80,
      maxWidth: 1600,
      maxHeight: 1600,
    );
    if (picked == null) return;
    final bytes = Uint8List.fromList(await picked.readAsBytes());
    if (!mounted) return;
    setState(() {
      _photoBytes = bytes;
      _photoName = picked.name;
      _photoContentType = _mimeFromExtension(picked.name);
      _photoUploadOperationId = const Uuid().v4();
    });
  }

  String _mimeFromExtension(String filename) {
    final lower = filename.toLowerCase();
    if (lower.endsWith('.png')) return 'image/png';
    if (lower.endsWith('.webp')) return 'image/webp';
    if (lower.endsWith('.heic')) return 'image/heic';
    return 'image/jpeg';
  }

  Future<void> _submit() async {
    if (_title.text.trim().isEmpty || _description.text.trim().isEmpty) return;
    setState(() => _saving = true);
    try {
      final repo = ref.read(tenantPortalRepositoryProvider);
      final created = await repo.createWorkOrder(
        title: _title.text.trim(),
        description: _description.text.trim(),
        priority: _priority,
      );
      final photoBytes = _photoBytes;
      final photoName = _photoName;
      final photoContentType = _photoContentType;
      if (photoBytes != null && photoName != null && photoContentType != null) {
        await repo.uploadWorkOrderPhoto(
          workOrderId: created.id,
          bytes: photoBytes,
          fileName: photoName,
          contentType: photoContentType,
          clientOperationId: _photoUploadOperationId ??= const Uuid().v4(),
        );
      }
      ref.invalidate(tenantPortalSnapshotProvider);
      _title.clear();
      _description.clear();
      _photoBytes = null;
      _photoName = null;
      _photoContentType = null;
      _photoUploadOperationId = null;
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Maintenance request submitted.')),
        );
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final snapshot = ref.watch(tenantPortalSnapshotProvider);

    return Scaffold(
      appBar: AppBar(title: const Text('Maintenance')),
      body: ListView(
        padding: const EdgeInsets.all(20),
        children: [
          snapshot.maybeWhen(
            data: (data) {
              final open = data.workOrders
                  .where(
                    (w) => !{
                      'Completed',
                      'Cancelled',
                      'Archived',
                    }.contains(w.status),
                  )
                  .toList();
              if (open.isEmpty) {
                return const Text('No open maintenance requests.');
              }
              return Column(
                children: open
                    .map(
                      (w) => Card(
                        child: ListTile(
                          titleAlignment: ListTileTitleAlignment.center,
                          title: Text(w.title),
                          subtitle: Text('${w.status} · ${w.priority}'),
                          trailing: const Icon(Icons.chevron_right),
                          onTap: () => Navigator.of(context).push<void>(
                            MaterialPageRoute<void>(
                              builder: (_) => TenantWorkOrderDetailScreen(
                                workOrderId: w.id,
                              ),
                            ),
                          ),
                        ),
                      ),
                    )
                    .toList(),
              );
            },
            orElse: () => const SizedBox.shrink(),
          ),
          const SizedBox(height: 16),
          TextField(
            controller: _title,
            decoration: const InputDecoration(labelText: 'Issue title'),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _description,
            minLines: 3,
            maxLines: 5,
            decoration: const InputDecoration(labelText: 'Description'),
          ),
          const SizedBox(height: 12),
          DropdownButtonFormField<String>(
            initialValue: _priority,
            decoration: const InputDecoration(labelText: 'Priority'),
            items: const [
              'Low',
              'Normal',
              'High',
              'Emergency',
            ].map((p) => DropdownMenuItem(value: p, child: Text(p))).toList(),
            onChanged: (value) => setState(() => _priority = value ?? 'Normal'),
          ),
          const SizedBox(height: 12),
          if (_photoBytes != null) ...[
            ClipRRect(
              borderRadius: BorderRadius.circular(12),
              child: Image.memory(
                _photoBytes!,
                height: 140,
                width: double.infinity,
                fit: BoxFit.cover,
              ),
            ),
            const SizedBox(height: 8),
          ],
          Row(
            children: [
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: _saving
                      ? null
                      : () => _pickPhoto(ImageSource.camera),
                  icon: const Icon(Icons.camera_alt_outlined),
                  label: Text(_photoBytes == null ? 'Take photo' : 'Retake'),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: _saving
                      ? null
                      : () => _pickPhoto(ImageSource.gallery),
                  icon: const Icon(Icons.photo_library_outlined),
                  label: const Text('Choose'),
                ),
              ),
            ],
          ),
          const SizedBox(height: 16),
          FilledButton(
            onPressed: _saving ? null : _submit,
            child: Text(_saving ? 'Submitting...' : 'Submit Request'),
          ),
        ],
      ),
    );
  }
}

class _TenantMoreTab extends ConsumerWidget {
  const _TenantMoreTab();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Scaffold(
      appBar: AppBar(title: const Text('More')),
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

String _money(num value) =>
    '\$${value.toStringAsFixed(2).replaceAllMapped(RegExp(r'\B(?=(\d{3})+(?!\d))'), (m) => ',')}';

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
    final briefingAsync = ref.watch(_briefingProvider);
    final messagesAsync = ref.watch(_latestMessagesProvider);
    final fieldQueueAsync = ref.watch(_fieldQueueProvider);
    final moneyAsync = ref.watch(moneySnapshotProvider);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Rental Command'),
        actions: const [MobileNotificationBell(), MobileAccountMenu()],
      ),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(_briefingProvider);
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
                    onRetry: () => ref.invalidate(_briefingProvider),
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

class _FieldQueueCard extends StatelessWidget {
  const _FieldQueueCard({required this.workOrder});

  final WorkOrder workOrder;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Card(
      child: ListTile(
        titleAlignment: ListTileTitleAlignment.center,
        onTap: () {
          Widget detailBuilder(BuildContext _) =>
              WorkOrderUnitAwareLoaderScreen(workOrderId: workOrder.id);
          final shellNavigator = mobileShellNavigatorOf(context);
          if (shellNavigator != null) {
            shellNavigator.openTab(
              MobileShellTabId.work,
              destination: MobileDestinationId.workOrders,
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
            if (workOrder.propertyName != null) workOrder.propertyName!,
            workOrder.status,
            workOrder.priority,
          ].join(' / '),
          maxLines: 1,
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
        );
      case 'Payment':
        if (id != null) {
          return _BriefingTarget(
            tab: MobileShellTabId.money,
            destination: MobileDestinationId.moneyLedger,
            detailBuilder: (_) => PaymentDetailScreen(paymentId: id),
          );
        }
        return _BriefingTarget(
          tab: MobileShellTabId.money,
          destination: MobileDestinationId.moneyOverview,
          detailBuilder: (_) => const OverdueScreen(),
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
  });

  final MobileShellTabId tab;
  final MobileDestinationId destination;
  final MobileDetailBuilder? detailBuilder;
  final WidgetBuilder? fallbackBuilder;
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
