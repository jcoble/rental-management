import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter/scheduler.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/auth/mobile_access_policy.dart';

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
  final Set<Object> _hiddenOwners = <Object>{};
  bool _disposed = false;
  bool _notifyScheduled = false;

  bool get hidden => _hiddenOwners.isNotEmpty;

  List<MobileQuickAction> get primaryActions {
    for (final registration in _registrations.reversed) {
      if (registration.actions.isNotEmpty) return registration.actions;
    }
    return const [];
  }

  MobileQuickAction? get primaryAction =>
      primaryActions.isEmpty ? null : primaryActions.first;

  void setPrimaryAction(Object owner, MobileQuickAction? action) {
    setPrimaryActions(owner, action == null ? const [] : [action]);
  }

  void setPrimaryActions(Object owner, List<MobileQuickAction> actions) {
    if (_disposed) return;

    final index = _registrations.indexWhere(
      (entry) => identical(entry.owner, owner),
    );
    if (index == -1) {
      _registrations.add(_MobileQuickActionRegistration(owner, actions));
      _notifyChanged();
      return;
    }

    final existing = _registrations[index];
    if (identical(existing.actions, actions) ||
        _listEquals(existing.actions, actions)) {
      return;
    }
    _registrations[index] = _MobileQuickActionRegistration(owner, actions);
    _notifyChanged();
  }

  void clearPrimaryAction(Object owner) {
    if (_disposed) return;

    final previousLength = _registrations.length;
    _registrations.removeWhere((entry) => identical(entry.owner, owner));
    if (_registrations.length == previousLength) return;
    _notifyChanged();
  }

  void setHidden(Object owner, bool hidden) {
    if (_disposed) return;

    final changed = hidden
        ? _hiddenOwners.add(owner)
        : _hiddenOwners.remove(owner);
    if (changed) _notifyChanged();
  }

  void clearHidden(Object owner) {
    setHidden(owner, false);
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
  const _MobileQuickActionRegistration(this.owner, this.actions);

  final Object owner;
  final List<MobileQuickAction> actions;
}

bool _listEquals<T>(List<T> a, List<T> b) {
  if (a.length != b.length) return false;
  for (var i = 0; i < a.length; i++) {
    if (a[i] != b[i]) return false;
  }
  return true;
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

class MobileQuickActionFabRegistry extends ChangeNotifier {
  int _mountedFabCount = 0;
  bool _notificationScheduled = false;
  bool _disposed = false;

  bool get hasMountedFab => _mountedFabCount > 0;

  void register() {
    if (_disposed) return;
    _mountedFabCount++;
    _scheduleNotify();
  }

  void unregister() {
    if (_disposed || _mountedFabCount == 0) return;
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

class MobileQuickActionFab extends ConsumerStatefulWidget {
  const MobileQuickActionFab({
    super.key,
    this.primaryAction,
    this.primaryActions = const [],
    required this.onChat,
    required this.onRecord,
    required this.onScan,
    this.heroTag = 'mobile-quick-action-fab',
    this.useNearestScope = true,
    this.registerWithHost = true,
  });

  final MobileQuickAction? primaryAction;
  final List<MobileQuickAction> primaryActions;
  final VoidCallback onChat;
  final VoidCallback onRecord;
  final VoidCallback onScan;
  final Object heroTag;
  final bool useNearestScope;
  final bool registerWithHost;

  @override
  ConsumerState<MobileQuickActionFab> createState() =>
      _MobileQuickActionFabState();
}

class _MobileQuickActionFabState extends ConsumerState<MobileQuickActionFab> {
  final Object _scopeOwner = Object();
  MobileQuickActionController? _scopeController;
  MobileQuickActionFabRegistry? _registry;
  bool _open = false;
  bool _registered = false;

  bool get _usesScope => _scopeController != null && widget.useNearestScope;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _syncRegistry();
    _syncScopedAction();
  }

  @override
  void didUpdateWidget(covariant MobileQuickActionFab oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.registerWithHost != widget.registerWithHost) {
      _syncRegistry();
    }
    _syncScopedAction();
  }

  @override
  void dispose() {
    _scopeController?.clearPrimaryAction(_scopeOwner);
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

  void _syncScopedAction() {
    final nextController = MobileQuickActionScope.maybeOf(context);
    if (!identical(_scopeController, nextController)) {
      _scopeController?.clearPrimaryAction(_scopeOwner);
      _scopeController = nextController;
    }

    if (_usesScope) {
      _scopeController!.setPrimaryActions(_scopeOwner, _primaryActions);
    } else {
      _scopeController?.clearPrimaryAction(_scopeOwner);
    }
  }

  void _toggle() => setState(() => _open = !_open);

  void _run(VoidCallback callback) {
    if (_open) setState(() => _open = false);
    callback();
  }

  List<MobileQuickAction> get _primaryActions => [
    ?widget.primaryAction,
    ...widget.primaryActions,
  ];

  @override
  Widget build(BuildContext context) {
    if (_usesScope) return const SizedBox.shrink();

    final auth = ref.watch(authControllerProvider);
    final capabilities = auth is AuthStateAuthenticated
        ? auth.capabilities
        : const <String>{};
    final hasGlobalScan = canUseGlobalScan(capabilities);
    final actions = <MobileQuickAction>[
      if (hasGlobalScan)
        MobileQuickAction(
          label: 'Scan / Add',
          icon: Icons.document_scanner_outlined,
          onPressed: widget.onScan,
        ),
      ..._primaryActions,
      if (canUseVoiceRecord(capabilities))
        MobileQuickAction(
          label: 'Record',
          icon: Icons.mic_none_rounded,
          onPressed: widget.onRecord,
        ),
      if (canUseAssistant(capabilities))
        MobileQuickAction(
          label: 'Assistant',
          icon: Icons.auto_awesome,
          onPressed: widget.onChat,
        ),
    ];
    if (actions.isEmpty) return const SizedBox.shrink();

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
          tooltip: _open
              ? 'Close quick actions'
              : hasGlobalScan
              ? 'Scan / Add'
              : 'Open quick actions',
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
              _open
                  ? Icons.close_rounded
                  : hasGlobalScan
                  ? Icons.document_scanner_outlined
                  : Icons.add_rounded,
              key: ValueKey((_open, hasGlobalScan)),
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
