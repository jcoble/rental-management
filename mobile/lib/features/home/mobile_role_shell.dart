import 'dart:async';

import 'package:flutter/material.dart';

class MobileRoleDestination {
  const MobileRoleDestination({
    required this.label,
    required this.icon,
    required this.builder,
    this.ownsScaffold = false,
  });

  final String label;
  final IconData icon;
  final WidgetBuilder builder;
  final bool ownsScaffold;
}

/// A role-focused bottom-navigation shell with a real Navigator per tab.
///
/// Switching tabs preserves each tab's navigation and scroll state. Selecting
/// the active tab again returns that tab to its root and scrolls its active
/// list to the top. Restricted experiences use this instead of pushing their
/// details onto one shared app navigator.
class MobileRoleShell extends StatefulWidget {
  const MobileRoleShell({
    super.key,
    required this.destinations,
    this.actions = const [],
    this.floatingActionButton,
  });

  final List<MobileRoleDestination> destinations;
  final List<Widget> actions;
  final Widget? floatingActionButton;

  @override
  State<MobileRoleShell> createState() => _MobileRoleShellState();
}

class _MobileRoleShellState extends State<MobileRoleShell> {
  late List<GlobalKey<NavigatorState>> _navigatorKeys;
  late List<_RoleTabScrollRegistry> _scrollRegistries;
  late List<_RoleTabNavigatorObserver> _navigatorObservers;
  late List<bool> _tabAtRoot;
  int _selectedIndex = 0;

  @override
  void initState() {
    super.initState();
    _resetControllers();
  }

  @override
  void didUpdateWidget(covariant MobileRoleShell oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.destinations.length != widget.destinations.length) {
      _resetControllers();
      _selectedIndex = 0;
    }
  }

  void _resetControllers() {
    _navigatorKeys = List.generate(
      widget.destinations.length,
      (_) => GlobalKey<NavigatorState>(),
    );
    _scrollRegistries = List.generate(
      widget.destinations.length,
      (_) => _RoleTabScrollRegistry(),
    );
    _tabAtRoot = List.filled(widget.destinations.length, true);
    _navigatorObservers = List.generate(
      widget.destinations.length,
      (index) => _RoleTabNavigatorObserver(
        onRootChanged: (atRoot) {
          WidgetsBinding.instance.addPostFrameCallback((_) {
            if (!mounted || _tabAtRoot[index] == atRoot) return;
            setState(() => _tabAtRoot[index] = atRoot);
          });
        },
      ),
    );
  }

  void _select(int index) {
    if (index != _selectedIndex) {
      setState(() => _selectedIndex = index);
      return;
    }

    _navigatorKeys[index].currentState?.popUntil((route) => route.isFirst);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      unawaited(_scrollRegistries[index].scrollToTop());
    });
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    body: IndexedStack(
      index: _selectedIndex,
      children: [
        for (var index = 0; index < widget.destinations.length; index++)
          NavigatorPopHandler<void>(
            enabled: index == _selectedIndex,
            onPopWithResult: (_) {
              _navigatorKeys[index].currentState?.pop();
            },
            child: Navigator(
              key: _navigatorKeys[index],
              observers: [_navigatorObservers[index]],
              onGenerateRoute: (_) => MaterialPageRoute<void>(
                settings: RouteSettings(
                  name: 'role-tab-${widget.destinations[index].label}',
                ),
                builder: (context) {
                  final destination = widget.destinations[index];
                  final content = destination.ownsScaffold
                      ? destination.builder(context)
                      : Scaffold(
                          appBar: AppBar(
                            title: Text(destination.label),
                            actions: widget.actions,
                          ),
                          body: destination.builder(context),
                        );
                  return _RoleTabScrollTracker(
                    registry: _scrollRegistries[index],
                    child: content,
                  );
                },
              ),
            ),
          ),
      ],
    ),
    floatingActionButton: _tabAtRoot[_selectedIndex]
        ? widget.floatingActionButton
        : null,
    bottomNavigationBar: NavigationBar(
      selectedIndex: _selectedIndex,
      onDestinationSelected: _select,
      destinations: [
        for (final destination in widget.destinations)
          NavigationDestination(
            icon: Icon(destination.icon),
            label: destination.label,
          ),
      ],
    ),
  );
}

class _RoleTabNavigatorObserver extends NavigatorObserver {
  _RoleTabNavigatorObserver({required this.onRootChanged});

  final ValueChanged<bool> onRootChanged;
  int _depth = 0;

  void _publish() => onRootChanged(_depth <= 1);

  @override
  void didPush(Route<dynamic> route, Route<dynamic>? previousRoute) {
    _depth++;
    _publish();
  }

  @override
  void didPop(Route<dynamic> route, Route<dynamic>? previousRoute) {
    if (_depth > 0) _depth--;
    _publish();
  }

  @override
  void didRemove(Route<dynamic> route, Route<dynamic>? previousRoute) {
    if (_depth > 0) _depth--;
    _publish();
  }
}

class _RoleTabScrollRegistry {
  ScrollPosition? _position;

  void track(ScrollPosition position) => _position = position;

  Future<void> scrollToTop() async {
    final position = _position;
    if (position == null || !position.hasPixels || position.pixels <= 0) return;
    await position.animateTo(
      position.minScrollExtent,
      duration: const Duration(milliseconds: 260),
      curve: Curves.easeOutCubic,
    );
  }
}

class _RoleTabScrollTracker extends StatelessWidget {
  const _RoleTabScrollTracker({required this.registry, required this.child});

  final _RoleTabScrollRegistry registry;
  final Widget child;

  @override
  Widget build(BuildContext context) =>
      NotificationListener<ScrollNotification>(
        onNotification: (notification) {
          if (notification.depth == 0) {
            final notificationContext = notification.context;
            final scrollable = notificationContext == null
                ? null
                : Scrollable.maybeOf(notificationContext);
            if (scrollable != null) registry.track(scrollable.position);
          }
          return false;
        },
        child: child,
      );
}
