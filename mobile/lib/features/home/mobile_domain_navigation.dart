import 'package:flutter/material.dart';

enum MobileShellTabId { today, rentals, money, work, inbox }

enum MobileDestinationId {
  gettingStarted,
  properties,
  units,
  tenants,
  leases,
  applications,
  moneyOverview,
  moneyLedger,
  deposits,
  banking,
  reports,
  payments,
  expenses,
  insights,
  workOrders,
  calendar,
  inspections,
  vendors,
  automations,
  notices,
  messages,
  notifications,
  activityHistory,
  assistant,
  team,
  settings,
}

typedef MobileDetailBuilder = WidgetBuilder;

class MobileDomainHeaderSnapshot {
  const MobileDomainHeaderSnapshot({required this.title, this.subtitle});

  final String title;
  final String? subtitle;
}

class MobileDomainHeaderController extends ChangeNotifier {
  Object? _activeToken;
  MobileDomainHeaderSnapshot? _detail;

  MobileDomainHeaderSnapshot? get detail => _detail;

  void showDetail(Object token, {required String title, String? subtitle}) {
    final normalizedSubtitle = subtitle?.trim();
    final next = MobileDomainHeaderSnapshot(
      title: title.trim(),
      subtitle: normalizedSubtitle == null || normalizedSubtitle.isEmpty
          ? null
          : normalizedSubtitle,
    );
    if (identical(_activeToken, token) &&
        _detail?.title == next.title &&
        _detail?.subtitle == next.subtitle) {
      return;
    }

    _activeToken = token;
    _detail = next;
    notifyListeners();
  }

  void clearDetail(Object token) {
    if (!identical(_activeToken, token)) return;
    clearActiveDetail();
  }

  void clearActiveDetail() {
    if (_detail == null && _activeToken == null) return;
    _activeToken = null;
    _detail = null;
    notifyListeners();
  }
}

class MobileDomainHeaderScope extends InheritedWidget {
  const MobileDomainHeaderScope({
    super.key,
    required MobileDomainHeaderController controller,
    required super.child,
  }) : controller = controller;

  final MobileDomainHeaderController controller;

  static MobileDomainHeaderController? maybeOf(BuildContext context) {
    return context
        .dependOnInheritedWidgetOfExactType<MobileDomainHeaderScope>()
        ?.controller;
  }

  @override
  bool updateShouldNotify(MobileDomainHeaderScope oldWidget) =>
      controller != oldWidget.controller;
}

class MobileDomainDetailHeader extends StatefulWidget {
  const MobileDomainDetailHeader({
    super.key,
    required this.title,
    this.subtitle,
    required this.child,
  });

  final String title;
  final String? subtitle;
  final Widget child;

  @override
  State<MobileDomainDetailHeader> createState() =>
      _MobileDomainDetailHeaderState();
}

class _MobileDomainDetailHeaderState extends State<MobileDomainDetailHeader> {
  final Object _token = Object();
  MobileDomainHeaderController? _controller;
  int _syncGeneration = 0;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _syncHeader();
  }

  @override
  void didUpdateWidget(covariant MobileDomainDetailHeader oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.title != widget.title ||
        oldWidget.subtitle != widget.subtitle) {
      _syncHeader();
    }
  }

  @override
  void dispose() {
    _clearHeader();
    super.dispose();
  }

  void _syncHeader() {
    final nextController = MobileDomainHeaderScope.maybeOf(context);
    if (!identical(nextController, _controller)) {
      _clearHeader();
      _controller = nextController;
    }
    final generation = ++_syncGeneration;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || generation != _syncGeneration) return;
      _controller?.showDetail(
        _token,
        title: widget.title,
        subtitle: widget.subtitle,
      );
    });
  }

  void _clearHeader() {
    final controller = _controller;
    _syncGeneration++;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      controller?.clearDetail(_token);
    });
  }

  @override
  Widget build(BuildContext context) => widget.child;
}

class MobileShellNavigator {
  const MobileShellNavigator({required this.openTab, required this.openRoute});

  final void Function(
    MobileShellTabId tab, {
    MobileDestinationId? destination,
    MobileDetailBuilder? detailBuilder,
  })
  openTab;

  final bool Function(String route) openRoute;
}

class MobileShellNavigation extends InheritedWidget {
  const MobileShellNavigation({
    super.key,
    required this.controller,
    required super.child,
  });

  final MobileShellNavigator controller;

  static MobileShellNavigator? maybeOf(BuildContext context) {
    return context
        .dependOnInheritedWidgetOfExactType<MobileShellNavigation>()
        ?.controller;
  }

  @override
  bool updateShouldNotify(MobileShellNavigation oldWidget) =>
      controller != oldWidget.controller;
}

class MobileShellNavigationRegistry {
  static MobileShellNavigator? _current;

  static MobileShellNavigator? get current => _current;

  static void attach(MobileShellNavigator controller) {
    _current = controller;
  }

  static void detach(MobileShellNavigator controller) {
    if (identical(_current, controller)) {
      _current = null;
    }
  }
}

MobileShellNavigator? mobileShellNavigatorOf(BuildContext context) {
  return MobileShellNavigation.maybeOf(context) ??
      MobileShellNavigationRegistry.current;
}

void revealMobileShellIfDetached(BuildContext context) {
  if (MobileShellNavigation.maybeOf(context) != null) return;
  Navigator.of(context).popUntil((route) => route.isFirst);
}

class MobileDomainNavigator {
  const MobileDomainNavigator(this._openDestination);

  final void Function(
    MobileDestinationId destination, {
    MobileDetailBuilder? detailBuilder,
  })
  _openDestination;

  void openDestination(
    MobileDestinationId destination, {
    MobileDetailBuilder? detailBuilder,
  }) {
    _openDestination(destination, detailBuilder: detailBuilder);
  }
}

class MobileDomainNavigation extends InheritedWidget {
  const MobileDomainNavigation({
    super.key,
    required this.controller,
    required super.child,
  });

  final MobileDomainNavigator controller;

  static MobileDomainNavigator? maybeOf(BuildContext context) {
    return context
        .dependOnInheritedWidgetOfExactType<MobileDomainNavigation>()
        ?.controller;
  }

  @override
  bool updateShouldNotify(MobileDomainNavigation oldWidget) =>
      controller != oldWidget.controller;
}
