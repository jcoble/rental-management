import 'package:flutter/material.dart';

import 'app_tokens.dart';

/// Reusable EdiPlatform design-system "recipes" (§7) ported to Flutter widgets.
///
///   * [M3ArtSurface]   — §7.1 image-overlay surface (Stack: art image at low
///     opacity + directional legibility scrim + content).
///   * [M3ArtBand]      — §7.7 page-header art band (extra-large strip, art
///     biased right over a primary-tinted wash).
///   * [M3TonalCard]    — §7.3 tinted tonal card (6 color families).
///   * [M3MorphNavItem] — §3.2 selection shape-morph (rounded-rect ↔ pill) for a
///     nav/segmented item, with a Material Symbols outline→fill cross-fade.
///
/// The bundled art assets are the EdiPlatform pattern set, copied to
/// `assets/patterns/bg-NN.webp` (declared in pubspec.yaml).

/// One of the six tonal-card / tinted-surface families (§2.5 / §7.3).
enum M3TonalFamily { mint, sky, amber, rose, violet, coral }

extension _TonalFamilyColors on M3TonalFamily {
  /// (background, accent) for the family, resolved off [tokens] (light pastels
  /// or dark faint-tints) and the [scheme] for accent ink.
  (Color bg, Color accent) resolve(AppTokens tokens, ColorScheme scheme) {
    switch (this) {
      case M3TonalFamily.mint:
        return (tokens.surfaceMint, tokens.success);
      case M3TonalFamily.sky:
        return (tokens.surfaceSky, tokens.info);
      case M3TonalFamily.amber:
        return (tokens.surfaceAmber, tokens.warning);
      case M3TonalFamily.rose:
        return (tokens.surfaceRose, scheme.error);
      case M3TonalFamily.violet:
        return (tokens.surfaceViolet, scheme.primary);
      case M3TonalFamily.coral:
        // Coral keeps its own warm family — tertiary is amber after the
        // TSK-162 accent pin and would duplicate the amber card.
        return (tokens.surfaceCoral, tokens.accentCoral);
    }
  }
}

/// §7.1 — A surface with a low-opacity background art image and a directional
/// legibility scrim, so [child] content stays readable while the surface gains
/// depth/color. Light-mode first; in dark the art drops to a faint haze.
class M3ArtSurface extends StatelessWidget {
  const M3ArtSurface({
    super.key,
    required this.child,
    this.pattern = 9,
    this.borderRadius = const BorderRadius.all(Radius.circular(28)),
    this.padding = const EdgeInsets.all(20),
    this.hero = false,
    this.scrimFrom,
    this.height,
    this.opacityOverride,
  });

  /// Pattern index 1..10 → `assets/patterns/bg-NN.webp`.
  final int pattern;
  final BorderRadius borderRadius;
  final EdgeInsetsGeometry padding;
  final bool hero;
  final Color? scrimFrom;
  final double? height;

  /// Explicit art-opacity override (e.g. the band strip uses
  /// `tokens.artBandOpacity` instead of the hero/base pair).
  final double? opacityOverride;
  final Widget child;

  String get _asset =>
      'assets/patterns/bg-${pattern.clamp(1, 10).toString().padLeft(2, '0')}.webp';

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final tokens = context.tokens;
    final opacity =
        opacityOverride ?? (hero ? tokens.artHeroOpacity : tokens.artOpacity);
    final scrim = scrimFrom ?? scheme.surfaceContainerLow;

    return ClipRRect(
      borderRadius: borderRadius,
      child: SizedBox(
        height: height,
        child: Stack(
          children: [
            // Art image, low opacity, biased to the right/top (≈ web
            // `right -8% top -28%`).
            Positioned.fill(
              child: Opacity(
                opacity: opacity,
                child: Image.asset(
                  _asset,
                  fit: BoxFit.cover,
                  alignment: const Alignment(1.0, -0.6),
                  errorBuilder: (_, _, _) => const SizedBox.shrink(),
                ),
              ),
            ),
            // Directional legibility scrim: 100°-ish left→right gradient that
            // keeps the content (left) side opaque enough to read.
            Positioned.fill(
              child: DecoratedBox(
                decoration: BoxDecoration(
                  gradient: LinearGradient(
                    begin: Alignment.centerLeft,
                    end: Alignment.centerRight,
                    colors: [
                      scrim.withValues(alpha: 0.88),
                      scrim.withValues(alpha: 0.42),
                      Colors.transparent,
                    ],
                    stops: const [0, 0.46, 0.84],
                  ),
                ),
              ),
            ),
            // Bottom-up gradient for vertical legibility.
            Positioned.fill(
              child: DecoratedBox(
                decoration: BoxDecoration(
                  gradient: LinearGradient(
                    begin: Alignment.bottomCenter,
                    end: Alignment.topCenter,
                    colors: [
                      scrim.withValues(alpha: 0.55),
                      Colors.transparent,
                    ],
                    stops: const [0, 0.6],
                  ),
                ),
              ),
            ),
            Padding(padding: padding, child: child),
          ],
        ),
      ),
    );
  }
}

/// §7.7 — Header art band: an extra-large strip whose art is biased to the
/// right edge over a primary-tinted wash, with a title block on the left.
class M3ArtBand extends StatelessWidget {
  const M3ArtBand({
    super.key,
    required this.title,
    this.subtitle,
    this.eyebrow,
    this.pattern = 6,
    this.trailing,
  });

  final String title;
  final String? subtitle;
  final String? eyebrow;
  final int pattern;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final tokens = context.tokens;
    // Violet-washed scrim behind the band art (TSK-162 calibration:
    // dark = 72% container + primary container, light = 84% low + primary).
    final wash = tokens.isDark
        ? Color.lerp(scheme.primaryContainer, scheme.surfaceContainer, 0.72)!
        : Color.lerp(
            scheme.primaryContainer,
            scheme.surfaceContainerLow,
            0.84,
          )!;

    return M3ArtSurface(
      pattern: pattern,
      scrimFrom: wash,
      opacityOverride: tokens.artBandOpacity,
      padding: const EdgeInsets.fromLTRB(20, 22, 20, 22),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.center,
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                if (eyebrow != null) ...[
                  Text(
                    eyebrow!.toUpperCase(),
                    style: theme.textTheme.labelSmall?.copyWith(
                      color: scheme.onSurfaceVariant,
                      letterSpacing: 0.6,
                    ),
                  ),
                  const SizedBox(height: 4),
                ],
                Text(title, style: theme.textTheme.headlineSmall),
                if (subtitle != null) ...[
                  const SizedBox(height: 4),
                  Text(
                    subtitle!,
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: scheme.onSurfaceVariant,
                    ),
                  ),
                ],
              ],
            ),
          ),
          if (trailing != null) ...[const SizedBox(width: 12), trailing!],
        ],
      ),
    );
  }
}

/// §7.3 — A tinted tonal card. Background is a tinted surface (per [family]),
/// flat with a hairline tonal border. Optional [onTap] adds an ink state layer.
class M3TonalCard extends StatelessWidget {
  const M3TonalCard({
    super.key,
    required this.family,
    required this.child,
    this.padding = const EdgeInsets.all(16),
    this.onTap,
  });

  final M3TonalFamily family;
  final Widget child;
  final EdgeInsetsGeometry padding;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final tokens = context.tokens;
    final (bg, accent) = family.resolve(tokens, scheme);
    final border = accent.withValues(alpha: tokens.tonalBorderStrength * 0.5);

    return Material(
      color: bg,
      borderRadius: const BorderRadius.all(Radius.circular(28)),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Container(
          decoration: BoxDecoration(
            borderRadius: const BorderRadius.all(Radius.circular(28)),
            border: Border.all(color: border),
          ),
          padding: padding,
          child: child,
        ),
      ),
    );
  }
}

/// §3.2 / §7.5 — A nav/segmented item that morphs its corner shape on selection
/// (rounded-rect ↔ pill) and morphs its Material Symbol's **FILL axis** 0→1,
/// mirroring the web `--msym-fill` flip exactly (one variable glyph, not two).
///
/// Pass a `Symbols.*_rounded` glyph for [icon] (Material Symbols Rounded, the
/// font the web uses). The label is optional so this also works as an
/// icon-only bottom-nav destination.
class M3MorphNavItem extends StatelessWidget {
  const M3MorphNavItem({
    super.key,
    required this.icon,
    required this.selected,
    required this.onTap,
    this.label,
    this.iconSize = 24,
  });

  final IconData icon;
  final String? label;
  final bool selected;
  final double iconSize;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    // Violet active pill — matches the web sidebar's primary-container voice.
    final fg = selected ? scheme.onPrimaryContainer : scheme.onSurfaceVariant;

    return InkWell(
      onTap: onTap,
      borderRadius: const BorderRadius.all(Radius.circular(28)),
      child: AnimatedContainer(
        duration: M3Motion.medium3,
        curve: M3Motion.emphasizedDecelerate,
        padding: EdgeInsets.symmetric(
          horizontal: label == null ? 18 : 16,
          vertical: 8,
        ),
        decoration: BoxDecoration(
          color: selected ? scheme.primaryContainer : Colors.transparent,
          // Rest = pill (full); selected = rounded-rect (large). Mirrors the M3
          // Expressive selected-container shape morph.
          borderRadius: BorderRadius.circular(
            selected ? M3Shape.large : M3Shape.full,
          ),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            // Animate the variable font's FILL axis 0→1 on selection (§7.5).
            TweenAnimationBuilder<double>(
              duration: M3Motion.medium2,
              curve: M3Motion.emphasizedDecelerate,
              tween: Tween(end: selected ? 1.0 : 0.0),
              builder: (context, fill, _) => Icon(
                icon,
                color: fg,
                size: iconSize,
                fill: fill,
                weight: selected ? 500 : 400,
              ),
            ),
            if (label != null) ...[
              const SizedBox(width: 8),
              Text(
                label!,
                style: theme.textTheme.labelLarge?.copyWith(
                  color: fg,
                  fontWeight: selected ? FontWeight.w700 : FontWeight.w500,
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}
