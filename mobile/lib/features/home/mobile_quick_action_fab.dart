import 'package:flutter/material.dart';

class MobileQuickAction {
  const MobileQuickAction({
    required this.label,
    required this.icon,
    required this.onPressed,
  });

  final String label;
  final IconData icon;
  final VoidCallback onPressed;
}

class MobileQuickActionFabRegistry extends ChangeNotifier {
  int _mountedFabCount = 0;
  bool _notificationScheduled = false;
  bool _disposed = false;

  bool get hasMountedFab => _mountedFabCount > 0;

  void register() {
    _mountedFabCount++;
    _scheduleNotify();
  }

  void unregister() {
    if (_mountedFabCount == 0) return;
    _mountedFabCount--;
    _scheduleNotify();
  }

  void _scheduleNotify() {
    if (_disposed || _notificationScheduled) return;
    _notificationScheduled = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _notificationScheduled = false;
      if (!_disposed) notifyListeners();
    });
  }

  @override
  void dispose() {
    _disposed = true;
    super.dispose();
  }
}

class MobileQuickActionFabHost
    extends InheritedNotifier<MobileQuickActionFabRegistry> {
  const MobileQuickActionFabHost({
    super.key,
    required MobileQuickActionFabRegistry registry,
    required super.child,
  }) : super(notifier: registry);

  static MobileQuickActionFabRegistry? maybeOf(BuildContext context) {
    return context
        .dependOnInheritedWidgetOfExactType<MobileQuickActionFabHost>()
        ?.notifier;
  }
}

class MobileQuickActionFab extends StatefulWidget {
  const MobileQuickActionFab({
    super.key,
    this.primaryAction,
    required this.onChat,
    required this.onRecord,
    required this.onScan,
    this.heroTag = 'mobile-quick-action-fab',
    this.registerWithHost = true,
  });

  final MobileQuickAction? primaryAction;
  final VoidCallback onChat;
  final VoidCallback onRecord;
  final VoidCallback onScan;
  final Object heroTag;
  final bool registerWithHost;

  @override
  State<MobileQuickActionFab> createState() => _MobileQuickActionFabState();
}

class _MobileQuickActionFabState extends State<MobileQuickActionFab> {
  bool _open = false;
  MobileQuickActionFabRegistry? _registry;
  bool _registered = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _syncRegistry();
  }

  @override
  void didUpdateWidget(covariant MobileQuickActionFab oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.registerWithHost != widget.registerWithHost) {
      _syncRegistry();
    }
  }

  @override
  void dispose() {
    _unregister();
    super.dispose();
  }

  void _syncRegistry() {
    final nextRegistry = widget.registerWithHost
        ? MobileQuickActionFabHost.maybeOf(context)
        : null;
    if (identical(nextRegistry, _registry)) return;

    _unregister();
    _registry = nextRegistry;
    if (_registry != null) {
      _registry!.register();
      _registered = true;
    }
  }

  void _unregister() {
    if (!_registered) return;
    _registry?.unregister();
    _registered = false;
  }

  void _toggle() => setState(() => _open = !_open);

  void _run(VoidCallback callback) {
    if (_open) setState(() => _open = false);
    callback();
  }

  List<MobileQuickAction> get _actions => [
    ?widget.primaryAction,
    MobileQuickAction(
      label: 'Chat',
      icon: Icons.auto_awesome,
      onPressed: widget.onChat,
    ),
    MobileQuickAction(
      label: 'Record',
      icon: Icons.mic_none_rounded,
      onPressed: widget.onRecord,
    ),
    MobileQuickAction(
      label: 'Scan',
      icon: Icons.document_scanner_outlined,
      onPressed: widget.onScan,
    ),
  ];

  @override
  Widget build(BuildContext context) {
    final actions = _actions;

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.end,
      children: [
        AnimatedSize(
          duration: const Duration(milliseconds: 220),
          curve: Curves.easeOutCubic,
          alignment: Alignment.bottomRight,
          child: _open
              ? Padding(
                  padding: const EdgeInsets.only(bottom: 12),
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    crossAxisAlignment: CrossAxisAlignment.end,
                    children: [
                      for (var i = 0; i < actions.length; i++)
                        Padding(
                          padding: EdgeInsets.only(top: i == 0 ? 0 : 8),
                          child: _QuickActionButton(
                            action: actions[i],
                            delay: i * 32,
                            onPressed: () => _run(actions[i].onPressed),
                          ),
                        ),
                    ],
                  ),
                )
              : const SizedBox.shrink(),
        ),
        FloatingActionButton(
          heroTag: widget.heroTag,
          onPressed: _toggle,
          tooltip: _open ? 'Close quick actions' : 'Open quick actions',
          elevation: 3,
          child: AnimatedSwitcher(
            duration: const Duration(milliseconds: 160),
            transitionBuilder: (child, animation) {
              return ScaleTransition(
                scale: animation,
                child: RotationTransition(
                  turns: Tween<double>(begin: -0.08, end: 0).animate(animation),
                  child: child,
                ),
              );
            },
            child: Icon(
              _open ? Icons.close_rounded : Icons.add_rounded,
              key: ValueKey(_open),
            ),
          ),
        ),
      ],
    );
  }
}

class _QuickActionButton extends StatelessWidget {
  const _QuickActionButton({
    required this.action,
    required this.delay,
    required this.onPressed,
  });

  final MobileQuickAction action;
  final int delay;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;

    return TweenAnimationBuilder<double>(
      tween: Tween(begin: 0, end: 1),
      duration: Duration(milliseconds: 180 + delay),
      curve: Curves.easeOutCubic,
      builder: (context, value, child) {
        return Opacity(
          opacity: value,
          child: Transform.translate(
            offset: Offset(0, (1 - value) * 8),
            child: child,
          ),
        );
      },
      child: Semantics(
        button: true,
        label: action.label,
        child: Material(
          color: colorScheme.secondaryContainer,
          elevation: 2,
          shadowColor: colorScheme.shadow.withValues(alpha: 0.18),
          shape: const StadiumBorder(),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            customBorder: const StadiumBorder(),
            onTap: onPressed,
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 260),
              child: Padding(
                padding: const EdgeInsets.symmetric(
                  horizontal: 16,
                  vertical: 12,
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(
                      action.icon,
                      size: 20,
                      color: colorScheme.onSecondaryContainer,
                    ),
                    const SizedBox(width: 12),
                    Flexible(
                      child: Text(
                        action.label,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: Theme.of(context).textTheme.labelLarge?.copyWith(
                          color: colorScheme.onSecondaryContainer,
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
