import 'dart:math' as math;

import 'package:flutter/material.dart';

class MobilePillTab {
  const MobilePillTab({required this.label, this.key});

  final String label;
  final Key? key;
}

class MobilePillTabBar extends StatefulWidget {
  const MobilePillTabBar({
    super.key,
    required this.tabs,
    required this.selectedIndex,
    required this.onSelected,
    this.scrollKey,
    this.semanticLabel,
  });

  final List<MobilePillTab> tabs;
  final int selectedIndex;
  final ValueChanged<int> onSelected;
  final Key? scrollKey;
  final String? semanticLabel;

  @override
  State<MobilePillTabBar> createState() => _MobilePillTabBarState();
}

class _MobilePillTabBarState extends State<MobilePillTabBar> {
  static const _tabMotion = Cubic(0.2, 0, 0, 1);
  final _scrollController = ScrollController();

  double _lastItemWidth = 0;
  double _lastViewportWidth = 0;

  @override
  void didUpdateWidget(covariant MobilePillTabBar oldWidget) {
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
    return Semantics(
      label: widget.semanticLabel,
      child: SizedBox(
        height: 66,
        child: LayoutBuilder(
          builder: (context, constraints) {
            final count = widget.tabs.length;
            final viewportWidth = constraints.maxWidth;
            final availableWidth = math.max(0.0, viewportWidth - 32);
            final visibleSlots = count <= 3 ? count.toDouble() : 2.82;
            final itemWidth = math.max(112.0, availableWidth / visibleSlots);
            final trackWidth = math.max(availableWidth, itemWidth * count);
            final selectedLeft = itemWidth * widget.selectedIndex;

            _lastItemWidth = itemWidth;
            _lastViewportWidth = viewportWidth;

            return SingleChildScrollView(
              key: widget.scrollKey,
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
                          key: widget.tabs[index].key,
                          label: widget.tabs[index].label,
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
    super.key,
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
