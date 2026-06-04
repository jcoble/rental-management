import 'package:flutter/material.dart';

/// Read-only star display for an average rating (e.g. 4.3 of 5), drawing full,
/// half, and empty stars. Renders nothing meaningful when [rating] is null —
/// callers should show an "unrated" affordance instead in that case.
class StarRatingDisplay extends StatelessWidget {
  const StarRatingDisplay({
    super.key,
    required this.rating,
    this.size = 18,
    this.color,
  });

  final double rating;
  final double size;
  final Color? color;

  @override
  Widget build(BuildContext context) {
    final starColor = color ?? Colors.amber.shade600;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: List.generate(5, (i) {
        final filled = rating - i;
        final IconData icon;
        if (filled >= 0.75) {
          icon = Icons.star_rounded;
        } else if (filled >= 0.25) {
          icon = Icons.star_half_rounded;
        } else {
          icon = Icons.star_outline_rounded;
        }
        return Icon(icon, size: size, color: starColor);
      }),
    );
  }
}

/// Interactive 1–5 star picker. Calls [onChanged] with the tapped star count.
class StarRatingInput extends StatelessWidget {
  const StarRatingInput({
    super.key,
    required this.value,
    required this.onChanged,
    this.size = 40,
    this.color,
  });

  /// Currently selected star count (0 = none chosen yet).
  final int value;
  final ValueChanged<int> onChanged;
  final double size;
  final Color? color;

  @override
  Widget build(BuildContext context) {
    final starColor = color ?? Colors.amber.shade600;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: List.generate(5, (i) {
        final star = i + 1;
        final selected = star <= value;
        return IconButton(
          onPressed: () => onChanged(star),
          visualDensity: VisualDensity.compact,
          padding: EdgeInsets.zero,
          tooltip: '$star star${star == 1 ? '' : 's'}',
          icon: Icon(
            selected ? Icons.star_rounded : Icons.star_outline_rounded,
            size: size,
            color: selected ? starColor : Theme.of(context).colorScheme.outline,
          ),
        );
      }),
    );
  }
}
