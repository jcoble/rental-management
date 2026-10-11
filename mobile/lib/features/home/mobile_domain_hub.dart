import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/auth/solo_landlord.dart';
import '../../core/widgets/mobile_pill_tab_bar.dart';
import 'mobile_destination.dart';
import 'mobile_domain_chrome.dart';
import 'mobile_domain_navigation.dart';
import 'mobile_quick_action_fab.dart';
import 'mobile_quick_action_helpers.dart';
import 'mobile_shell_actions.dart';

class RentalsHubScreen extends ConsumerWidget {
  const RentalsHubScreen({
    super.key,
    this.handleSystemBack = true,
    this.onControllerReady,
    this.onControllerDisposed,
  });

  final ValueChanged<MobileDomainNavigator>? onControllerReady;
  final ValueChanged<MobileDomainNavigator>? onControllerDisposed;
  final bool handleSystemBack;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final auth = ref.watch(authControllerProvider);
    if (auth is! AuthStateAuthenticated) return const SizedBox.shrink();
    return MobileDomainHubScreen(
      title: 'Rentals',
      subtitle: 'Properties, people, agreements and applications.',
      destinations: _withoutSoloHidden(
        rentalHubDestinationsFor(
          experience: auth.activeExperience,
          capabilities: auth.capabilities,
        ),
        solo: isSoloLandlord(ref),
      ),
      onControllerReady: onControllerReady,
      onControllerDisposed: onControllerDisposed,
      handleSystemBack: handleSystemBack,
    );
  }
}

class MoneyHubScreen extends ConsumerWidget {
  const MoneyHubScreen({
    super.key,
    this.handleSystemBack = true,
    this.onControllerReady,
    this.onControllerDisposed,
  });

  final ValueChanged<MobileDomainNavigator>? onControllerReady;
  final ValueChanged<MobileDomainNavigator>? onControllerDisposed;
  final bool handleSystemBack;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final auth = ref.watch(authControllerProvider);
    if (auth is! AuthStateAuthenticated) return const SizedBox.shrink();
    return MobileDomainHubScreen(
      title: 'Money',
      subtitle: 'Snapshot, ledger, deposits, banking and reports.',
      destinations: _withoutSoloHidden(
        visibleMobileDestinations(moneyHubDestinations, auth.capabilities),
        solo: isSoloLandlord(ref),
      ),
      onControllerReady: onControllerReady,
      onControllerDisposed: onControllerDisposed,
      handleSystemBack: handleSystemBack,
    );
  }
}

class WorkHubScreen extends ConsumerWidget {
  const WorkHubScreen({
    super.key,
    this.handleSystemBack = true,
    this.onControllerReady,
    this.onControllerDisposed,
  });

  final ValueChanged<MobileDomainNavigator>? onControllerReady;
  final ValueChanged<MobileDomainNavigator>? onControllerDisposed;
  final bool handleSystemBack;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final auth = ref.watch(authControllerProvider);
    if (auth is! AuthStateAuthenticated) {
      return const SizedBox.shrink();
    }
    final capabilities = auth.capabilities;
    final assignedWorkExperience =
        auth.activeExperience == WorkspaceExperience.maintenance;
    final destinations = workDestinationsFor(
      capabilities,
      assignedWorkExperience: assignedWorkExperience,
    );

    return MobileDomainHubScreen(
      title: assignedWorkExperience ? 'My work' : 'Work',
      subtitle: assignedWorkExperience
          ? 'Your assigned repairs, updates and conversations.'
          : 'Maintenance, inspections, vendors and scheduled work.',
      destinations: destinations,
      onControllerReady: onControllerReady,
      onControllerDisposed: onControllerDisposed,
      handleSystemBack: handleSystemBack,
    );
  }
}

class InboxHubScreen extends ConsumerWidget {
  const InboxHubScreen({
    super.key,
    this.handleSystemBack = true,
    this.onControllerReady,
    this.onControllerDisposed,
  });

  final ValueChanged<MobileDomainNavigator>? onControllerReady;
  final ValueChanged<MobileDomainNavigator>? onControllerDisposed;
  final bool handleSystemBack;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final auth = ref.watch(authControllerProvider);
    if (auth is! AuthStateAuthenticated) return const SizedBox.shrink();
    return MobileDomainHubScreen(
      title: 'Inbox',
      subtitle: 'Messages and notifications in one place.',
      destinations: visibleMobileDestinations(
        inboxHubDestinations,
        auth.capabilities,
      ),
      onControllerReady: onControllerReady,
      onControllerDisposed: onControllerDisposed,
      handleSystemBack: handleSystemBack,
    );
  }
}

/// A solo landlord has no co-owners to keep a directory or send statements to,
/// so those two sections stay out of the hubs.
List<MobileDestination> _withoutSoloHidden(
  List<MobileDestination> destinations, {
  required bool solo,
}) {
  if (!solo) return destinations;
  return destinations
      .where(
        (destination) =>
            destination.id != MobileDestinationId.owners &&
            destination.id != MobileDestinationId.reports,
      )
      .toList(growable: false);
}

class MobileDomainHubScreen extends ConsumerStatefulWidget {
  const MobileDomainHubScreen({
    super.key,
    required this.title,
    required this.subtitle,
    required this.destinations,
    this.handleSystemBack = true,
    this.onControllerReady,
    this.onControllerDisposed,
  });

  final String title;
  final String subtitle;
  final List<MobileDestination> destinations;
  final bool handleSystemBack;
  final ValueChanged<MobileDomainNavigator>? onControllerReady;
  final ValueChanged<MobileDomainNavigator>? onControllerDisposed;

  @override
  ConsumerState<MobileDomainHubScreen> createState() =>
      _MobileDomainHubScreenState();
}

class _MobileDomainHubScreenState extends ConsumerState<MobileDomainHubScreen> {
  final _contentNavigatorKey = GlobalKey<NavigatorState>();
  late final MobileDomainHeaderController _headerController;
  late final MobileQuickActionFabRegistry _quickActionFabRegistry;
  late final MobileDomainNavigator _domainNavigator;
  int _selectedIndex = 0;
  bool _headerCollapsed = false;
  bool _quickActionFallbackReady = false;
  bool _quickActionFallbackCheckScheduled = false;

  @override
  void initState() {
    super.initState();
    _headerController = MobileDomainHeaderController()
      ..addListener(_handleHeaderChanged);
    _quickActionFabRegistry = MobileQuickActionFabRegistry()
      ..addListener(_handleQuickActionFabChanged);
    _domainNavigator = MobileDomainNavigator(
      openDestination: _openDestination,
      popToCurrentRoot: _popToCurrentRoot,
    );
    widget.onControllerReady?.call(_domainNavigator);
    _scheduleQuickActionFallbackCheck();
  }

  @override
  void dispose() {
    widget.onControllerDisposed?.call(_domainNavigator);
    _quickActionFabRegistry
      ..removeListener(_handleQuickActionFabChanged)
      ..dispose();
    _headerController
      ..removeListener(_handleHeaderChanged)
      ..dispose();
    super.dispose();
  }

  void _handleHeaderChanged() {
    if (mounted) setState(() {});
  }

  void _handleQuickActionFabChanged() {
    if (mounted) setState(() {});
  }

  void _scheduleQuickActionFallbackCheck() {
    if (_quickActionFallbackCheckScheduled) return;
    _quickActionFallbackCheckScheduled = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _quickActionFallbackCheckScheduled = false;
      if (!mounted) return;
      setState(() => _quickActionFallbackReady = true);
    });
  }

  @override
  void didUpdateWidget(covariant MobileDomainHubScreen oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.onControllerReady != widget.onControllerReady) {
      widget.onControllerReady?.call(_domainNavigator);
    }
    final previousDestination = _selectedIndex < oldWidget.destinations.length
        ? oldWidget.destinations[_selectedIndex].id
        : null;
    final nextIndex = previousDestination == null
        ? -1
        : widget.destinations.indexWhere(
            (destination) => destination.id == previousDestination,
          );
    if (nextIndex >= 0) {
      _selectedIndex = nextIndex;
      return;
    }

    _selectedIndex = 0;
    _replaceContentRoot(widget.destinations.first);
  }

  void _selectIndex(int index) {
    if (index == _selectedIndex) return;
    _openDestination(widget.destinations[index].id);
  }

  void _openDestination(
    MobileDestinationId destination, {
    MobileDetailBuilder? detailBuilder,
    MobileNavigationGuard? canNavigate,
  }) {
    final index = widget.destinations.indexWhere((d) => d.id == destination);
    if (index < 0) {
      if (detailBuilder != null) {
        if (canNavigate?.call() == false) return;
        Navigator.of(context).push<void>(_detailRoute(detailBuilder));
      }
      return;
    }
    if (canNavigate?.call() == false) return;

    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      if (canNavigate?.call() == false) return;
      setState(() {
        _selectedIndex = index;
        _headerCollapsed = false;
        _quickActionFallbackReady = false;
        if (detailBuilder == null) {
          _headerController.clearActiveDetail();
        }
      });
      _scheduleQuickActionFallbackCheck();
      _replaceContentRoot(widget.destinations[index]);
      if (detailBuilder != null) {
        if (canNavigate?.call() == false) return;
        _contentNavigatorKey.currentState?.push<void>(
          _detailRoute(detailBuilder),
        );
      }
    });
  }

  void _replaceContentRoot(MobileDestination destination) {
    _headerController.clearActiveDetail();
    _contentNavigatorKey.currentState?.pushAndRemoveUntil<void>(
      _rootRoute(destination),
      (_) => false,
    );
  }

  PageRoute<void> _rootRoute(MobileDestination destination) {
    return PageRouteBuilder<void>(
      settings: RouteSettings(name: destination.id.name),
      pageBuilder: (context, _, _) => Builder(builder: destination.builder),
      transitionDuration: Duration.zero,
      reverseTransitionDuration: Duration.zero,
    );
  }

  MaterialPageRoute<void> _detailRoute(MobileDetailBuilder builder) {
    return MaterialPageRoute<void>(builder: builder);
  }

  Future<void> _handleSystemBack(bool didPop) async {
    if (didPop || !mounted) return;

    final contentNavigator = _contentNavigatorKey.currentState;
    if (contentNavigator != null && await contentNavigator.maybePop()) {
      return;
    }

    if (!mounted) return;
    final rootNavigator = Navigator.of(context);
    if (rootNavigator.canPop()) {
      rootNavigator.pop();
      return;
    }

    mobileShellNavigatorOf(context)?.openTab(MobileShellTabId.today);
  }

  Future<void> _popContentDetail() async {
    final contentNavigator = _contentNavigatorKey.currentState;
    if (contentNavigator != null && await contentNavigator.maybePop()) {
      return;
    }

    if (!mounted) return;
    final rootNavigator = Navigator.of(context);
    if (rootNavigator.canPop()) {
      rootNavigator.pop();
    }
  }

  void _popToCurrentRoot() {
    _headerController.clearActiveDetail();
    _contentNavigatorKey.currentState?.popUntil((route) => route.isFirst);
    if (!mounted) return;
    setState(() => _headerCollapsed = false);
  }

  Widget _appBarTitle(BuildContext context) {
    final detail = _headerController.detail;
    if (detail == null) {
      final selected = widget.destinations[_selectedIndex];
      return _HubHeaderTitle(
        title: selected.label,
        subtitle: selected.subtitle,
      );
    }

    return _HubHeaderTitle(title: detail.title, subtitle: detail.subtitle);
  }

  void _handleContentScroll(ScrollNotification notification) {
    if (!mounted || notification.metrics.axis != Axis.vertical) return;

    final shouldCollapse = notification.metrics.pixels > 12;
    if (shouldCollapse == _headerCollapsed) return;
    setState(() => _headerCollapsed = shouldCollapse);
  }

  @override
  Widget build(BuildContext context) {
    final selected = widget.destinations[_selectedIndex];
    final showingDetailHeader = _headerController.detail != null;

    return Scaffold(
      appBar: AppBar(
        automaticallyImplyLeading: false,
        toolbarHeight: _headerCollapsed ? 0 : kToolbarHeight,
        leading: showingDetailHeader
            ? IconButton(
                tooltip: 'Back',
                icon: const Icon(Icons.arrow_back),
                onPressed: () => unawaited(_popContentDetail()),
              )
            : null,
        title: _headerCollapsed ? null : _appBarTitle(context),
        actions: _headerCollapsed
            ? null
            : const [MobileNotificationBell(), MobileAccountMenu()],
      ),
      body: SafeArea(
        top: false,
        child: Stack(
          children: [
            Positioned.fill(
              child: MobileQuickActionFabHost(
                registry: _quickActionFabRegistry,
                child: MobileDomainHeaderScope(
                  controller: _headerController,
                  child: MobileDomainNavigation(
                    controller: _domainNavigator,
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        if (!showingDetailHeader)
                          MobilePillTabBar(
                            scrollKey: const Key('hub-segment-scroll'),
                            tabs: [
                              for (final destination in widget.destinations)
                                MobilePillTab(label: destination.label),
                            ],
                            selectedIndex: _selectedIndex,
                            onSelected: _selectIndex,
                          ),
                        Expanded(
                          child: PopScope<void>(
                            canPop: !widget.handleSystemBack,
                            onPopInvokedWithResult: (didPop, _) {
                              unawaited(_handleSystemBack(didPop));
                            },
                            child: ScrollNotificationObserver(
                              child: _DomainScrollCollapseObserver(
                                onNotification: _handleContentScroll,
                                child: MobileDomainChromeScope(
                                  embedded: true,
                                  child: Navigator(
                                    key: _contentNavigatorKey,
                                    onGenerateRoute: (_) =>
                                        _rootRoute(selected),
                                  ),
                                ),
                              ),
                            ),
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ),
            if (_quickActionFallbackReady &&
                !_quickActionFabRegistry.hasMountedFab)
              Positioned(
                right: 16,
                bottom: 16,
                child: MobileQuickActionFab(
                  heroTag: '${widget.title.toLowerCase()}-hub-quick-action-fab',
                  registerWithHost: false,
                  onChat: () => openMobileAssistant(context),
                  onRecord: () => openMobileRecord(context),
                  onScan: () => openAuthorizedMobileScan(context, ref),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _HubHeaderTitle extends StatelessWidget {
  const _HubHeaderTitle({required this.title, this.subtitle});

  final String title;
  final String? subtitle;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(title, maxLines: 1, overflow: TextOverflow.ellipsis),
        if (subtitle != null)
          Text(
            subtitle!,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: theme.textTheme.bodySmall?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
      ],
    );
  }
}

class _DomainScrollCollapseObserver extends StatefulWidget {
  const _DomainScrollCollapseObserver({
    required this.onNotification,
    required this.child,
  });

  final ValueChanged<ScrollNotification> onNotification;
  final Widget child;

  @override
  State<_DomainScrollCollapseObserver> createState() =>
      _DomainScrollCollapseObserverState();
}

class _DomainScrollCollapseObserverState
    extends State<_DomainScrollCollapseObserver> {
  ScrollNotificationObserverState? _observer;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final nextObserver = ScrollNotificationObserver.maybeOf(context);
    if (identical(_observer, nextObserver)) return;
    _observer?.removeListener(widget.onNotification);
    _observer = nextObserver;
    _observer?.addListener(widget.onNotification);
  }

  @override
  void didUpdateWidget(covariant _DomainScrollCollapseObserver oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.onNotification == widget.onNotification) return;
    _observer?.removeListener(oldWidget.onNotification);
    _observer?.addListener(widget.onNotification);
  }

  @override
  void dispose() {
    _observer?.removeListener(widget.onNotification);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
