import 'dart:async';

import 'package:flutter/material.dart';

class TabbedFormStepSpec {
  const TabbedFormStepSpec({
    required this.label,
    required this.child,
    this.isComplete,
    this.validate,
  });

  final String label;
  final Widget child;
  final bool Function()? isComplete;
  final bool Function()? validate;
}

class TabbedFormSheet extends StatefulWidget {
  const TabbedFormSheet({
    super.key,
    required this.title,
    required this.tabs,
    required this.saveLabel,
    required this.saving,
    required this.onSave,
    this.error,
    this.heightFactor = 0.86,
  });

  final String title;
  final List<TabbedFormStepSpec> tabs;
  final String saveLabel;
  final bool saving;
  final FutureOr<void> Function() onSave;
  final String? error;
  final double heightFactor;

  @override
  State<TabbedFormSheet> createState() => _TabbedFormSheetState();
}

class _TabbedFormSheetState extends State<TabbedFormSheet> {
  static const _successDuration = Duration(milliseconds: 360);
  static const _successColor = Color(0xFF2E7D32);

  late List<GlobalKey<FormState>> _stepFormKeys;
  final Set<int> _completedSteps = <int>{};
  int _currentIndex = 0;
  int? _celebratingIndex;
  bool _forward = true;
  bool _actionInFlight = false;

  @override
  void initState() {
    super.initState();
    _stepFormKeys = _buildStepKeys();
  }

  @override
  void didUpdateWidget(covariant TabbedFormSheet oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.tabs.length != widget.tabs.length) {
      final oldKeys = _stepFormKeys;
      _stepFormKeys = [
        for (var i = 0; i < widget.tabs.length; i++)
          if (i < oldKeys.length) oldKeys[i] else GlobalKey<FormState>(),
      ];
      _completedSteps.removeWhere((index) => index >= widget.tabs.length);
      if (_currentIndex >= widget.tabs.length) {
        _currentIndex = widget.tabs.isEmpty ? 0 : widget.tabs.length - 1;
      }
    }
  }

  List<GlobalKey<FormState>> _buildStepKeys() => [
    for (var i = 0; i < widget.tabs.length; i++) GlobalKey<FormState>(),
  ];

  bool get _isLastStep => _currentIndex == widget.tabs.length - 1;

  bool get _isBusy => widget.saving || _actionInFlight;

  void _selectTab(int nextIndex) {
    if (nextIndex == _currentIndex || _isBusy) return;
    if (nextIndex < _currentIndex) {
      _moveToStep(nextIndex);
      return;
    }

    _completeCurrentStep(
      nextIndex: _isComplete(nextIndex) || nextIndex == _currentIndex + 1
          ? nextIndex
          : (_currentIndex + 1).clamp(0, widget.tabs.length - 1),
    );
  }

  void _moveToStep(int nextIndex) {
    setState(() {
      _forward = nextIndex > _currentIndex;
      _currentIndex = nextIndex;
    });
  }

  bool _isComplete(int index) {
    return _completedSteps.contains(index) ||
        (widget.tabs[index].isComplete?.call() ?? false);
  }

  bool _validateCurrentStep() {
    final formValid =
        _stepFormKeys[_currentIndex].currentState?.validate() ?? true;
    final customValid = widget.tabs[_currentIndex].validate?.call() ?? true;
    return formValid && customValid;
  }

  Widget _buildStepper(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;

    return SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      child: Row(
        children: [
          for (var i = 0; i < widget.tabs.length; i++) ...[
            _StepperCard(
              index: i,
              label: widget.tabs[i].label,
              selected: i == _currentIndex,
              complete: _isComplete(i),
              celebrating: _celebratingIndex == i,
              enabled: !_isBusy,
              onTap: () => _selectTab(i),
            ),
            if (i < widget.tabs.length - 1)
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: 6),
                child: Icon(
                  Icons.chevron_right,
                  size: 18,
                  color: colorScheme.onSurfaceVariant.withValues(alpha: 0.7),
                ),
              ),
          ],
        ],
      ),
    );
  }

  Future<void> _completeCurrentStep({required int nextIndex}) async {
    if (_isBusy || !_validateCurrentStep()) return;

    setState(() {
      _completedSteps.add(_currentIndex);
      _celebratingIndex = _currentIndex;
      _actionInFlight = true;
    });

    await Future<void>.delayed(_successDuration);
    if (!mounted) return;

    setState(() {
      _celebratingIndex = null;
      _actionInFlight = false;
      _forward = nextIndex > _currentIndex;
      _currentIndex = nextIndex;
    });
  }

  Future<void> _save() async {
    if (_isBusy || !_validateCurrentStep()) return;

    final messenger = ScaffoldMessenger.of(context);

    setState(() {
      _completedSteps.add(_currentIndex);
      _celebratingIndex = _currentIndex;
      _actionInFlight = true;
    });

    await Future<void>.delayed(_successDuration);

    try {
      await Future<void>.sync(widget.onSave);
      if (messenger.mounted) {
        messenger
          ..hideCurrentSnackBar()
          ..showSnackBar(
            SnackBar(
              content: const Text('Save complete'),
              backgroundColor: _successColor,
            ),
          );
      }
    } catch (_) {
      // Form callbacks own their visible error state. Do not show the success
      // snackbar when the save path reports a failure.
    } finally {
      if (mounted) {
        setState(() {
          _celebratingIndex = null;
          _actionInFlight = false;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;
    final height = MediaQuery.sizeOf(context).height * widget.heightFactor;
    final isCelebrating = _celebratingIndex == _currentIndex;

    return SafeArea(
      top: false,
      child: Padding(
        padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottomPadding),
        child: SizedBox(
          height: height,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      widget.title,
                      style: theme.textTheme.titleLarge?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                  IconButton(
                    icon: const Icon(Icons.close),
                    onPressed: () => Navigator.of(context).pop(),
                  ),
                ],
              ),
              const SizedBox(height: 8),
              _buildStepper(context),
              const SizedBox(height: 12),
              Expanded(
                child: AnimatedSwitcher(
                  duration: const Duration(milliseconds: 240),
                  switchInCurve: Curves.easeOutCubic,
                  switchOutCurve: Curves.easeInCubic,
                  transitionBuilder: (child, animation) {
                    final begin = Offset(_forward ? 0.06 : -0.06, 0);
                    return FadeTransition(
                      opacity: animation,
                      child: SlideTransition(
                        position: Tween<Offset>(
                          begin: begin,
                          end: Offset.zero,
                        ).animate(animation),
                        child: child,
                      ),
                    );
                  },
                  child: SingleChildScrollView(
                    key: Key('tabbed-form-current-$_currentIndex'),
                    padding: const EdgeInsets.only(bottom: 12),
                    child: Form(
                      key: _stepFormKeys[_currentIndex],
                      child: widget.tabs[_currentIndex].child,
                    ),
                  ),
                ),
              ),
              if (widget.error != null) ...[
                const SizedBox(height: 12),
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 12,
                    vertical: 10,
                  ),
                  decoration: BoxDecoration(
                    color: colorScheme.errorContainer,
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Text(
                    widget.error!,
                    style: TextStyle(
                      color: colorScheme.onErrorContainer,
                      fontSize: 13,
                    ),
                  ),
                ),
              ],
              const SizedBox(height: 12),
              Row(
                children: [
                  if (_currentIndex > 0) ...[
                    Expanded(
                      child: OutlinedButton.icon(
                        onPressed: _isBusy
                            ? null
                            : () => _moveToStep(_currentIndex - 1),
                        icon: const Icon(Icons.arrow_back),
                        label: const Text('Back'),
                      ),
                    ),
                    const SizedBox(width: 12),
                  ],
                  Expanded(
                    flex: 2,
                    child: FilledButton.icon(
                      style: FilledButton.styleFrom(
                        backgroundColor: isCelebrating ? _successColor : null,
                        foregroundColor: isCelebrating ? Colors.white : null,
                      ),
                      onPressed: _isBusy
                          ? null
                          : _isLastStep
                          ? _save
                          : () => _completeCurrentStep(
                              nextIndex: _currentIndex + 1,
                            ),
                      icon: widget.saving
                          ? const SizedBox(
                              height: 18,
                              width: 18,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : AnimatedSwitcher(
                              duration: const Duration(milliseconds: 180),
                              transitionBuilder: (child, animation) =>
                                  ScaleTransition(
                                    scale: CurvedAnimation(
                                      parent: animation,
                                      curve: Curves.easeOutBack,
                                    ),
                                    child: FadeTransition(
                                      opacity: animation,
                                      child: child,
                                    ),
                                  ),
                              child: Icon(
                                isCelebrating
                                    ? Icons.check
                                    : _isLastStep
                                    ? Icons.check_circle_outline
                                    : Icons.arrow_forward,
                                key: ValueKey(
                                  isCelebrating
                                      ? 'complete'
                                      : _isLastStep
                                      ? 'save'
                                      : 'next',
                                ),
                              ),
                            ),
                      label: Text(_isLastStep ? widget.saveLabel : 'Next'),
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _StepperCard extends StatelessWidget {
  const _StepperCard({
    required this.index,
    required this.label,
    required this.selected,
    required this.complete,
    required this.celebrating,
    required this.enabled,
    required this.onTap,
  });

  final int index;
  final String label;
  final bool selected;
  final bool complete;
  final bool celebrating;
  final bool enabled;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final successSurface = _TabbedFormSheetState._successColor.withValues(
      alpha: 0.16,
    );
    final successBorder = _TabbedFormSheetState._successColor.withValues(
      alpha: 0.72,
    );
    final background = complete
        ? successSurface
        : selected
        ? colorScheme.primaryContainer
        : colorScheme.surfaceContainerHighest.withValues(alpha: 0.55);
    final borderColor = complete
        ? successBorder
        : selected
        ? colorScheme.primary
        : colorScheme.outlineVariant;
    final foreground = complete
        ? _TabbedFormSheetState._successColor
        : selected
        ? colorScheme.onPrimaryContainer
        : colorScheme.onSurfaceVariant;

    return Material(
      color: background,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(14),
        side: BorderSide(color: borderColor),
      ),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        key: Key('tabbed-form-step-$index'),
        onTap: enabled ? onTap : null,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 180),
          curve: Curves.easeOutCubic,
          constraints: const BoxConstraints(minHeight: 48, minWidth: 118),
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              AnimatedSwitcher(
                duration: const Duration(milliseconds: 180),
                transitionBuilder: (child, animation) => ScaleTransition(
                  scale: CurvedAnimation(
                    parent: animation,
                    curve: Curves.easeOutBack,
                  ),
                  child: FadeTransition(opacity: animation, child: child),
                ),
                child: complete || celebrating
                    ? Icon(
                        Icons.check,
                        key: Key('tabbed-form-complete-$index'),
                        size: 18,
                        color: foreground,
                      )
                    : CircleAvatar(
                        key: Key('tabbed-form-number-$index'),
                        radius: 10,
                        backgroundColor: foreground.withValues(alpha: 0.16),
                        child: Text(
                          '${index + 1}',
                          style: theme.textTheme.labelSmall?.copyWith(
                            color: foreground,
                            fontWeight: FontWeight.w800,
                          ),
                        ),
                      ),
              ),
              const SizedBox(width: 8),
              Text(
                label,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: theme.textTheme.labelLarge?.copyWith(
                  color: foreground,
                  fontWeight: selected || complete
                      ? FontWeight.w800
                      : FontWeight.w600,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
