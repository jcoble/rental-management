import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import 'mobile_destination.dart';
import 'mobile_domain_chrome.dart';
import 'mobile_domain_navigation.dart';
import 'mobile_quick_action_fab.dart';
import 'mobile_quick_action_helpers.dart';
import 'mobile_shell_actions.dart';

class RentalsHubScreen extends ConsumerWidget {
  const RentalsHubScreen({
    super.key,
    this.onControllerReady,
    this.onControllerDisposed,
  });

  final ValueChanged<MobileDomainNavigator>? onControllerReady;
  final ValueChanged<MobileDomainNavigator>? onControllerDisposed;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final auth = ref.watch(authControllerProvider);
    if (auth is! AuthStateAuthenticated) return const SizedBox.shrink();
    return MobileDomainHubScreen(
      title: 'Rentals',
      subtitle: 'Properties, people, agreements and applications.',
      destinations: rentalHubDestinationsFor(
        experience: auth.activeExperience,
        capabilities: auth.capabilities,
      ),
      onControllerReady: onControllerReady,
      onControllerDisposed: onControllerDisposed,
    );
  }
}

class MoneyHubScreen extends ConsumerWidget {
  const MoneyHubScreen({
    super.key,
    this.onControllerReady,
    this.onControllerDisposed,
  });

  final ValueChanged<MobileDomainNavigator>? onControllerReady;
  final ValueChanged<MobileDomainNavigator>? onControllerDisposed;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final auth = ref.watch(authControllerProvider);
    if (auth is! AuthStateAuthenticated) return const SizedBox.shrink();
    return MobileDomainHubScreen(
      title: 'Money',
      subtitle: 'Snapshot, ledger, deposits, banking and reports.',
      destinations: visibleMobileDestinations(
        moneyHubDestinations,
        auth.capabilities,
      ),
      onControllerReady: onControllerReady,
      onControllerDisposed: onControllerDisposed,
    );
  }
}

class WorkHubScreen extends ConsumerWidget {
  const WorkHubScreen({
    super.key,
    this.onControllerReady,
    this.onControllerDisposed,
  });

  final ValueChanged<MobileDomainNavigator>? onControllerReady;
  final ValueChanged<MobileDomainNavigator>? onControllerDisposed;

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
    );
  }
}

class InboxHubScreen extends ConsumerWidget {
  const InboxHubScreen({
    super.key,
    this.onControllerReady,
    this.onControllerDisposed,
  });

  final ValueChanged<MobileDomainNavigator>? onControllerReady;
  final ValueChanged<MobileDomainNavigator>? onControllerDisposed;

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
    );
  }
}

class MobileDomainHubScreen extends StatefulWidget {
  const MobileDomainHubScreen({
    super.key,
    required this.title,
    required this.subtitle,
    required this.destinations,
    this.onControllerReady,
    this.onControllerDisposed,
  });

  final String title;
  final String subtitle;
  final List<MobileDestination> destinations;
  final ValueChanged<MobileDomainNavigator>? onControllerReady;
  final ValueChanged<MobileDomainNavigator>? onControllerDisposed;

  @override
  State<MobileDomainHubScreen> createState() => _MobileDomainHubScreenState();
}

class _MobileDomainHubScreenState extends State<MobileDomainHubScreen> {
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
  }) {
    final index = widget.destinations.indexWhere((d) => d.id == destination);
    if (index < 0) {
      if (detailBuilder != null) {
        Navigator.of(context).push<void>(_detailRoute(detailBuilder));
      }
      return;
    }

    setState(() {
      _selectedIndex = index;
      _headerCollapsed = false;
      _quickActionFallbackReady = false;
      if (detailBuilder == null) {
        _headerController.clearActiveDetail();
      }
    });
    _scheduleQuickActionFallbackCheck();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      _replaceContentRoot(widget.destinations[index]);
      if (detailBuilder != null) {
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
    }
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
                          _HubSegmentBar(
                            destinations: widget.destinations,
                            selectedIndex: _selectedIndex,
                            onSelected: _selectIndex,
                          ),
                        Expanded(
                          child: PopScope<void>(
                            canPop: false,
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

class _HubSegmentBar extends StatefulWidget {
  const _HubSegmentBar({
    required this.destinations,
    required this.selectedIndex,
    required this.onSelected,
  });

  final List<MobileDestination> destinations;
  final int selectedIndex;
  final ValueChanged<int> onSelected;

  @override
  State<_HubSegmentBar> createState() => _HubSegmentBarState();
}

class _HubSegmentBarState extends State<_HubSegmentBar> {
  static const _tabMotion = Cubic(0.2, 0, 0, 1);
  final _scrollController = ScrollController();

  double _lastItemWidth = 0;
  double _lastViewportWidth = 0;

  @override
  void didUpdateWidget(covariant _HubSegmentBar oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.selectedIndex != widget.selectedIndex) {
      WidgetsBinding.instance.addPostFrameCallback((_) => _centerSelectedTab());
    }
  }

  @override
  void dispose() {
    _scrollController.dispose();
    super.dispose();
  }

  void _centerSelectedTab() {
    if (!_scrollController.hasClients || _lastItemWidth <= 0) return;

    final targetCenter =
        widget.selectedIndex * _lastItemWidth + (_lastItemWidth / 2);
    final targetOffset = targetCenter - (_lastViewportWidth / 2);
    final clampedOffset = targetOffset.clamp(
      _scrollController.position.minScrollExtent,
      _scrollController.position.maxScrollExtent,
    );

    _scrollController.animateTo(
      clampedOffset,
      duration: const Duration(milliseconds: 260),
      curve: _tabMotion,
    );
  }

  @override
  Widget build(BuildContext context) {
    return SizedBox(
      height: 66,
      child: LayoutBuilder(
        builder: (context, constraints) {
          final count = widget.destinations.length;
          final viewportWidth = constraints.maxWidth;
          final availableWidth = math.max(0.0, viewportWidth - 32);
          final visibleSlots = count <= 3 ? count.toDouble() : 2.82;
          final itemWidth = math.max(112.0, availableWidth / visibleSlots);
          final trackWidth = math.max(availableWidth, itemWidth * count);
          final selectedLeft = itemWidth * widget.selectedIndex;

          _lastItemWidth = itemWidth;
          _lastViewportWidth = viewportWidth;

          return SingleChildScrollView(
            controller: _scrollController,
            scrollDirection: Axis.horizontal,
            physics: const BouncingScrollPhysics(),
            padding: const EdgeInsets.fromLTRB(16, 8, 16, 10),
            child: _M3StateLayerTrack(
              width: trackWidth,
              selectedLeft: selectedLeft,
              itemWidth: itemWidth,
              child: Row(
                children: [
                  for (var index = 0; index < count; index++)
                    SizedBox(
                      width: itemWidth,
                      child: _M3StateLayerTab(
                        label: widget.destinations[index].label,
                        selected: widget.selectedIndex == index,
                        onTap: () => widget.onSelected(index),
                      ),
                    ),
                ],
              ),
            ),
          );
        },
      ),
    );
  }
}

class _M3StateLayerTrack extends StatelessWidget {
  const _M3StateLayerTrack({
    required this.width,
    required this.selectedLeft,
    required this.itemWidth,
    required this.child,
  });

  static const _tabMotion = Cubic(0.2, 0, 0, 1);

  final double width;
  final double selectedLeft;
  final double itemWidth;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final selectedFill = Color.alphaBlend(
      colorScheme.primary.withValues(alpha: isDark ? 0.24 : 0.16),
      isDark
          ? colorScheme.surfaceContainerHigh
          : colorScheme.surfaceContainerHighest,
    );

    return Container(
      width: width,
      height: 48,
      clipBehavior: Clip.antiAlias,
      decoration: BoxDecoration(
        color: isDark
            ? colorScheme.surfaceContainerHighest.withValues(alpha: 0.28)
            : colorScheme.surfaceContainerHighest.withValues(alpha: 0.72),
        borderRadius: BorderRadius.circular(999),
        boxShadow: [
          BoxShadow(
            color: colorScheme.shadow.withValues(alpha: isDark ? 0.28 : 0.10),
            blurRadius: 14,
            offset: const Offset(0, 3),
          ),
        ],
      ),
      child: Stack(
        children: [
          TweenAnimationBuilder<double>(
            tween: Tween<double>(end: selectedLeft),
            duration: const Duration(milliseconds: 280),
            curve: _tabMotion,
            builder: (context, left, child) {
              return Transform.translate(
                offset: Offset(left, 0),
                child: SizedBox(
                  width: itemWidth,
                  height: double.infinity,
                  child: child,
                ),
              );
            },
            child: Padding(
              padding: const EdgeInsets.all(2),
              child: DecoratedBox(
                decoration: BoxDecoration(
                  color: selectedFill,
                  borderRadius: BorderRadius.circular(999),
                ),
              ),
            ),
          ),
          child,
        ],
      ),
    );
  }
}

class _M3StateLayerTab extends StatelessWidget {
  const _M3StateLayerTab({
    required this.label,
    required this.selected,
    required this.onTap,
  });

  final String label;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    final textColor = selected
        ? colorScheme.onSurface
        : colorScheme.onSurfaceVariant;

    return Semantics(
      button: true,
      selected: selected,
      label: label,
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          borderRadius: BorderRadius.circular(999),
          overlayColor: WidgetStateProperty.resolveWith((states) {
            if (states.contains(WidgetState.pressed)) {
              return textColor.withValues(alpha: 0.12);
            }
            if (states.contains(WidgetState.focused)) {
              return textColor.withValues(alpha: 0.10);
            }
            if (states.contains(WidgetState.hovered)) {
              return textColor.withValues(alpha: 0.08);
            }
            return null;
          }),
          onTap: onTap,
          child: Center(
            child: AnimatedDefaultTextStyle(
              duration: const Duration(milliseconds: 180),
              curve: Curves.easeOutCubic,
              style:
                  Theme.of(context).textTheme.labelLarge?.copyWith(
                    color: textColor,
                    fontWeight: selected ? FontWeight.w700 : FontWeight.w500,
                    letterSpacing: 0,
                  ) ??
                  TextStyle(
                    color: textColor,
                    fontWeight: selected ? FontWeight.w700 : FontWeight.w500,
                  ),
              child: ExcludeSemantics(
                child: Text(
                  label,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  textAlign: TextAlign.center,
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
