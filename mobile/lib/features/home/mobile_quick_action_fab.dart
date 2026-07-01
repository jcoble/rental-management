import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';

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

class MobileQuickActionController extends ChangeNotifier {
  final List<_MobileQuickActionRegistration> _registrations = [];
  bool _disposed = false;
  bool _notifyScheduled = false;

  MobileQuickAction? get primaryAction =>
      _registrations.isEmpty ? null : _registrations.last.action;

  void setPrimaryAction(Object owner, MobileQuickAction? action) {
    if (_disposed) return;

    final index = _registrations.indexWhere(
      (entry) => identical(entry.owner, owner),
    );
    if (index == -1) {
      _registrations.add(_MobileQuickActionRegistration(owner, action));
      _notifyChanged();
      return;
    }

    final existing = _registrations[index];
    if (existing.action == action) return;
    _registrations[index] = _MobileQuickActionRegistration(owner, action);
    _notifyChanged();
  }

  void clearPrimaryAction(Object owner) {
    if (_disposed) return;

    final previousLength = _registrations.length;
    _registrations.removeWhere((entry) => identical(entry.owner, owner));
    if (_registrations.length == previousLength) return;
    _notifyChanged();
  }

  void _notifyChanged() {
    if (_disposed) return;

    final phase = SchedulerBinding.instance.schedulerPhase;
    final canNotifyNow =
        phase == SchedulerPhase.idle ||
        phase == SchedulerPhase.postFrameCallbacks;
    if (canNotifyNow) {
      notifyListeners();
      return;
    }

    if (_notifyScheduled) return;
    _notifyScheduled = true;
    SchedulerBinding.instance.addPostFrameCallback((_) {
      _notifyScheduled = false;
      if (!_disposed) notifyListeners();
    });
  }

  @override
  void dispose() {
    _disposed = true;
    super.dispose();
  }
}

class _MobileQuickActionRegistration {
  const _MobileQuickActionRegistration(this.owner, this.action);

  final Object owner;
  final MobileQuickAction? action;
}

class MobileQuickActionScope
    extends InheritedNotifier<MobileQuickActionController> {
  const MobileQuickActionScope({
    super.key,
    required MobileQuickActionController controller,
    required super.child,
  }) : super(notifier: controller);

  static MobileQuickActionController? maybeOf(BuildContext context) {
    return context
        .dependOnInheritedWidgetOfExactType<MobileQuickActionScope>()
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
    this.useNearestScope = true,
  });

  final MobileQuickAction? primaryAction;
  final VoidCallback onChat;
  final VoidCallback onRecord;
  final VoidCallback onScan;
  final Object heroTag;
  final bool useNearestScope;

  @override
  State<MobileQuickActionFab> createState() => _MobileQuickActionFabState();
}

class _MobileQuickActionFabState extends State<MobileQuickActionFab> {
  final Object _scopeOwner = Object();
  MobileQuickActionController? _scopeController;
  bool _open = false;

  bool get _usesScope => _scopeController != null && widget.useNearestScope;

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
  void didChangeDependencies() {
    super.didChangeDependencies();
    final nextController = MobileQuickActionScope.maybeOf(context);
    if (!identical(_scopeController, nextController)) {
      _scopeController?.clearPrimaryAction(_scopeOwner);
      _scopeController = nextController;
    }
    _syncScopedAction();
  }

  @override
  void didUpdateWidget(covariant MobileQuickActionFab oldWidget) {
    super.didUpdateWidget(oldWidget);
    _syncScopedAction();
  }

  @override
  void dispose() {
    _scopeController?.clearPrimaryAction(_scopeOwner);
    super.dispose();
  }

  void _syncScopedAction() {
    if (_usesScope) {
      _scopeController!.setPrimaryAction(_scopeOwner, widget.primaryAction);
    } else {
      _scopeController?.clearPrimaryAction(_scopeOwner);
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_usesScope) return const SizedBox.shrink();

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
