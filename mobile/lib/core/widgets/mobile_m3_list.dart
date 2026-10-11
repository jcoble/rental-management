import 'package:flutter/material.dart';

import '../theme/app_tokens.dart';

enum MobileM3ListItemPosition { single, first, middle, last }

extension MobileM3ListItemPositionForIndex on MobileM3ListItemPosition {
  static MobileM3ListItemPosition forIndex(int index, int count) {
    if (count <= 1) return MobileM3ListItemPosition.single;
    if (index == 0) return MobileM3ListItemPosition.first;
    if (index == count - 1) return MobileM3ListItemPosition.last;
    return MobileM3ListItemPosition.middle;
  }
}

class MobileM3ListItem extends StatelessWidget {
  const MobileM3ListItem({
    super.key,
    required this.position,
    required this.title,
    this.leading,
    this.supporting = const [],
    this.meta,
    this.trailing,
    this.actions = const [],
    this.onTap,
    this.promoted = false,
    this.contentPadding = const EdgeInsets.symmetric(
      horizontal: 16,
      vertical: 12,
    ),
  });

  final MobileM3ListItemPosition position;
  final Widget title;
  final Widget? leading;
  final List<Widget> supporting;
  final Widget? meta;
  final Widget? trailing;
  final List<Widget> actions;
  final VoidCallback? onTap;
  final bool promoted;
  final EdgeInsetsGeometry contentPadding;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final radius = _borderRadiusFor(position);

    return AnimatedContainer(
      duration: M3Motion.medium2,
      curve: M3Motion.emphasizedDecelerate,
      decoration: BoxDecoration(
        color: promoted ? cs.surfaceContainerHighest : cs.surfaceContainerHigh,
        borderRadius: radius,
        border: Border.all(
          color: promoted
              ? cs.outlineVariant.withValues(alpha: 0.58)
              : Colors.transparent,
        ),
      ),
      clipBehavior: Clip.antiAlias,
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: onTap,
          borderRadius: radius,
          child: Padding(
            padding: contentPadding,
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.center,
              children: [
                if (leading != null) ...[
                  SizedBox.square(dimension: 48, child: Center(child: leading)),
                  const SizedBox(width: 12),
                ],
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      title,
                      for (final line in supporting) ...[
                        const SizedBox(height: 2),
                        line,
                      ],
                      if (meta != null) ...[const SizedBox(height: 8), meta!],
                    ],
                  ),
                ),
                if (trailing != null) ...[
                  const SizedBox(width: 8),
                  ConstrainedBox(
                    constraints: const BoxConstraints(
                      minWidth: 48,
                      minHeight: 48,
                    ),
                    child: Center(child: trailing),
                  ),
                ],
                for (final action in actions) ...[
                  const SizedBox(width: 4),
                  ConstrainedBox(
                    constraints: const BoxConstraints(
                      minWidth: 48,
                      minHeight: 48,
                    ),
                    child: Center(child: action),
                  ),
                ],
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class MobileM3ListDivider extends StatelessWidget {
  const MobileM3ListDivider({super.key, this.indent = 76, this.endIndent = 16});

  final double indent;
  final double endIndent;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Container(
      height: 1,
      color: cs.surfaceContainerHigh,
      child: Align(
        alignment: Alignment.center,
        child: Container(
          height: 1,
          margin: EdgeInsetsDirectional.only(start: indent, end: endIndent),
          color: cs.outlineVariant.withValues(alpha: 0.32),
        ),
      ),
    );
  }
}

class MobileM3LeadingIcon extends StatelessWidget {
  const MobileM3LeadingIcon({
    super.key,
    required this.icon,
    required this.backgroundColor,
    required this.foregroundColor,
  });

  final IconData icon;
  final Color backgroundColor;
  final Color foregroundColor;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 44,
      height: 44,
      decoration: BoxDecoration(
        color: backgroundColor,
        borderRadius: M3Shape.radiusLarge,
      ),
      child: Icon(icon, color: foregroundColor, size: 22),
    );
  }
}

BorderRadius _borderRadiusFor(MobileM3ListItemPosition position) {
  const top = Radius.circular(M3Shape.largeIncreased);
  const bottom = Radius.circular(M3Shape.largeIncreased);
  return switch (position) {
    MobileM3ListItemPosition.single => const BorderRadius.all(top),
    MobileM3ListItemPosition.first => const BorderRadius.vertical(top: top),
    MobileM3ListItemPosition.middle => BorderRadius.zero,
    MobileM3ListItemPosition.last => const BorderRadius.vertical(
      bottom: bottom,
    ),
  };
}
