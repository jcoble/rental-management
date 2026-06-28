import 'package:flutter/material.dart';

class TabbedFormStepSpec {
  const TabbedFormStepSpec({
    required this.label,
    required this.child,
    this.isComplete,
  });

  final String label;
  final Widget child;
  final bool Function()? isComplete;
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
  final VoidCallback onSave;
  final String? error;
  final double heightFactor;

  @override
  State<TabbedFormSheet> createState() => _TabbedFormSheetState();
}

class _TabbedFormSheetState extends State<TabbedFormSheet> {
  final Set<int> _visitedTabs = <int>{};
  int _currentIndex = 0;
  bool _forward = true;

  void _selectTab(int nextIndex) {
    if (nextIndex == _currentIndex) return;
    setState(() {
      _visitedTabs.add(_currentIndex);
      _forward = nextIndex > _currentIndex;
      _currentIndex = nextIndex;
    });
  }

  bool _isComplete(int index) {
    return _visitedTabs.contains(index) ||
        (widget.tabs[index].isComplete?.call() ?? false);
  }

  void _save() {
    setState(() => _visitedTabs.add(_currentIndex));
    widget.onSave();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;
    final height = MediaQuery.sizeOf(context).height * widget.heightFactor;

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
              DefaultTabController(
                length: widget.tabs.length,
                child: TabBar(
                  isScrollable: true,
                  tabAlignment: TabAlignment.start,
                  dividerColor: Colors.transparent,
                  onTap: _selectTab,
                  tabs: [
                    for (var i = 0; i < widget.tabs.length; i++)
                      Tab(
                        child: Row(
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            AnimatedSwitcher(
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
                              child: _isComplete(i)
                                  ? Padding(
                                      key: Key('tabbed-form-complete-$i'),
                                      padding: const EdgeInsets.only(right: 6),
                                      child: Icon(
                                        Icons.check_circle,
                                        size: 16,
                                        color: colorScheme.primary,
                                      ),
                                    )
                                  : const SizedBox.shrink(),
                            ),
                            Text(widget.tabs[i].label),
                          ],
                        ),
                      ),
                  ],
                ),
              ),
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
                    child: widget.tabs[_currentIndex].child,
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
              FilledButton(
                onPressed: widget.saving ? null : _save,
                child: widget.saving
                    ? const SizedBox(
                        height: 20,
                        width: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : Text(widget.saveLabel),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
