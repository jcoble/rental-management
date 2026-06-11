import 'package:flutter/material.dart';

/// EdiPlatform "M3 Expressive + business twist" design tokens, ported to Flutter.
///
/// Every value here is pulled verbatim from the EdiPlatform web design system
/// (`ediplatform-web/src/lib/styles/m3-theme.css`, `m3-base.css`, `app.css`) as
/// documented in `DESIGN-SYSTEM.md`. Nothing is invented.
///
/// Layout:
///   * [M3Colors]  — the generated `--m3c-*` color roles, light + dark.
///   * [M3Shape]   — the shape scale as `BorderRadius`.
///   * [M3Motion]  — easing `Cubic`s + `Duration` tokens.
///   * [M3Type]    — the 5×3 type scale wired into a `TextTheme`.
///   * [AppTokens] — a `ThemeExtension` holding everything that doesn't fit in
///                   `ColorScheme` (semantics, tinted families, pattern colors,
///                   border-strength knobs, art-overlay assets).
///
/// `color-mix(in srgb, A x%, B)` from the web maps to `Color.lerp(B, A, x/100)`.
///
/// Principles preserved: near-zero elevation (depth from borders + tone), muted
/// color via tonal surfaces/overlays (neutral body text), generous radii, spring
/// motion, shape + fill morphing.
abstract final class M3Colors {
  // ---------------------------------------------------------------------------
  // DARK — §2.2 generated roles (the portable baseline)
  // ---------------------------------------------------------------------------

  // Surfaces & neutrals
  static const darkSurface = Color(0xFF121217);
  static const darkSurfaceDim = Color(0xFF121217);
  static const darkSurfaceBright = Color(0xFF3A3A42);
  static const darkSurfaceContainerLowest = Color(0xFF0D0D11);
  static const darkSurfaceContainerLow = Color(0xFF1A1A20);
  static const darkSurfaceContainer = Color(0xFF202026);
  static const darkSurfaceContainerHigh = Color(0xFF2A2A31);
  static const darkSurfaceContainerHighest = Color(0xFF363640);
  static const darkOnSurface = Color(0xFFF2F0F7);
  static const darkOnSurfaceVariant = Color(0xFFCBC7D2);
  static const darkOutline = Color(0xFF948F9C);
  static const darkOutlineVariant = Color(0xFF494750);
  static const darkInverseSurface = Color(0xFFE7E4ED);
  static const darkInverseOnSurface = Color(0xFF303038);

  // Primary (violet)
  static const darkPrimary = Color(0xFFD4BBFC);
  static const darkOnPrimary = Color(0xFF3A255B);
  static const darkPrimaryContainer = Color(0xFF513C73);
  static const darkOnPrimaryContainer = Color(0xFFECDCFF);
  static const darkInversePrimary = Color(0xFF69548D);

  // Secondary (pinned vivid cyan, #22d3ee seed — TSK-162 accent pin; TonalSpot
  // collapsed secondary into grey-mauve). Selection/active "pop" accent.
  static const darkSecondary = Color(0xFF2FD9F4);
  static const darkOnSecondary = Color(0xFF00363E);
  static const darkSecondaryContainer = Color(0xFF004E5A);
  static const darkOnSecondaryContainer = Color(0xFFA2EEFF);

  // Tertiary (pinned warm amber, #f59e0b seed — maintenance/work-order warmth)
  static const darkTertiary = Color(0xFFFFB95F);
  static const darkOnTertiary = Color(0xFF472A00);
  static const darkTertiaryContainer = Color(0xFF653E00);
  static const darkOnTertiaryContainer = Color(0xFFFFDDB8);

  // Error
  static const darkError = Color(0xFFFFB4AB);
  static const darkOnError = Color(0xFF690005);
  static const darkErrorContainer = Color(0xFF93000A);
  static const darkOnErrorContainer = Color(0xFFFFDAD6);

  static const darkScrim = Color(0xFF000000);
  static const darkShadow = Color(0xFF000000);

  // ---------------------------------------------------------------------------
  // LIGHT — §2.2 generated roles
  // ---------------------------------------------------------------------------

  // Surfaces & neutrals — M3-standard tonal direction: the page (surface) is the
  // near-white tone; container tiers step progressively DARKER so tinted cards
  // recede into the page rather than floating above it (depth from tone + border,
  // near-zero elevation).
  static const lightSurface = Color(0xFFFDF9FF);
  static const lightSurfaceDim = Color(0xFFE4DDEE);
  static const lightSurfaceBright = Color(0xFFFFFBFF);
  static const lightSurfaceContainerLowest = Color(0xFFFFFFFF);
  static const lightSurfaceContainerLow = Color(0xFFF7F1FB);
  static const lightSurfaceContainer = Color(0xFFF1EAF7);
  static const lightSurfaceContainerHigh = Color(0xFFEBE3F2);
  static const lightSurfaceContainerHighest = Color(0xFFE4DBEE);
  static const lightOnSurface = Color(0xFF18181B);
  static const lightOnSurfaceVariant = Color(0xFF52525B);
  static const lightOutline = Color(0xFF71717A);
  static const lightOutlineVariant = Color(0xFFD4D4D8);
  static const lightInverseSurface = Color(0xFF322F35);
  static const lightInverseOnSurface = Color(0xFFF5EEF7);

  // Primary (violet)
  static const lightPrimary = Color(0xFF69548D);
  static const lightOnPrimary = Color(0xFFFFFFFF);
  static const lightPrimaryContainer = Color(0xFFECDCFF);
  static const lightOnPrimaryContainer = Color(0xFF513C73);
  static const lightInversePrimary = Color(0xFFD4BBFC);

  // Secondary (pinned cyan — light tones)
  static const lightSecondary = Color(0xFF006877);
  static const lightOnSecondary = Color(0xFFFFFFFF);
  static const lightSecondaryContainer = Color(0xFFA2EEFF);
  static const lightOnSecondaryContainer = Color(0xFF004E5A);

  // Tertiary (pinned amber — light tones)
  static const lightTertiary = Color(0xFF855300);
  static const lightOnTertiary = Color(0xFFFFFFFF);
  static const lightTertiaryContainer = Color(0xFFFFDDB8);
  static const lightOnTertiaryContainer = Color(0xFF653E00);

  // Error
  static const lightError = Color(0xFFBA1A1A);
  static const lightOnError = Color(0xFFFFFFFF);
  static const lightErrorContainer = Color(0xFFFFDAD6);
  static const lightOnErrorContainer = Color(0xFF93000A);

  static const lightScrim = Color(0xFF000000);
  static const lightShadow = Color(0xFF000000);

  // ---------------------------------------------------------------------------
  // ColorScheme builders — map the §2.2 roles into Flutter's ColorScheme slots.
  // ---------------------------------------------------------------------------

  static const ColorScheme darkScheme = ColorScheme(
    brightness: Brightness.dark,
    primary: darkPrimary,
    onPrimary: darkOnPrimary,
    primaryContainer: darkPrimaryContainer,
    onPrimaryContainer: darkOnPrimaryContainer,
    secondary: darkSecondary,
    onSecondary: darkOnSecondary,
    secondaryContainer: darkSecondaryContainer,
    onSecondaryContainer: darkOnSecondaryContainer,
    tertiary: darkTertiary,
    onTertiary: darkOnTertiary,
    tertiaryContainer: darkTertiaryContainer,
    onTertiaryContainer: darkOnTertiaryContainer,
    error: darkError,
    onError: darkOnError,
    errorContainer: darkErrorContainer,
    onErrorContainer: darkOnErrorContainer,
    surface: darkSurface,
    onSurface: darkOnSurface,
    surfaceDim: darkSurfaceDim,
    surfaceBright: darkSurfaceBright,
    surfaceContainerLowest: darkSurfaceContainerLowest,
    surfaceContainerLow: darkSurfaceContainerLow,
    surfaceContainer: darkSurfaceContainer,
    surfaceContainerHigh: darkSurfaceContainerHigh,
    surfaceContainerHighest: darkSurfaceContainerHighest,
    onSurfaceVariant: darkOnSurfaceVariant,
    outline: darkOutline,
    outlineVariant: darkOutlineVariant,
    inverseSurface: darkInverseSurface,
    onInverseSurface: darkInverseOnSurface,
    inversePrimary: darkInversePrimary,
    scrim: darkScrim,
    shadow: darkShadow,
  );

  static const ColorScheme lightScheme = ColorScheme(
    brightness: Brightness.light,
    primary: lightPrimary,
    onPrimary: lightOnPrimary,
    primaryContainer: lightPrimaryContainer,
    onPrimaryContainer: lightOnPrimaryContainer,
    secondary: lightSecondary,
    onSecondary: lightOnSecondary,
    secondaryContainer: lightSecondaryContainer,
    onSecondaryContainer: lightOnSecondaryContainer,
    tertiary: lightTertiary,
    onTertiary: lightOnTertiary,
    tertiaryContainer: lightTertiaryContainer,
    onTertiaryContainer: lightOnTertiaryContainer,
    error: lightError,
    onError: lightOnError,
    errorContainer: lightErrorContainer,
    onErrorContainer: lightOnErrorContainer,
    surface: lightSurface,
    onSurface: lightOnSurface,
    surfaceDim: lightSurfaceDim,
    surfaceBright: lightSurfaceBright,
    surfaceContainerLowest: lightSurfaceContainerLowest,
    surfaceContainerLow: lightSurfaceContainerLow,
    surfaceContainer: lightSurfaceContainer,
    surfaceContainerHigh: lightSurfaceContainerHigh,
    surfaceContainerHighest: lightSurfaceContainerHighest,
    onSurfaceVariant: lightOnSurfaceVariant,
    outline: lightOutline,
    outlineVariant: lightOutlineVariant,
    inverseSurface: lightInverseSurface,
    onInverseSurface: lightInverseOnSurface,
    inversePrimary: lightInversePrimary,
    scrim: lightScrim,
    shadow: lightShadow,
  );
}

/// Shape scale (§3.1) as Flutter radii.
abstract final class M3Shape {
  static const double none = 0;
  static const double extraSmall = 4;
  static const double small = 8;
  static const double medium = 12;
  static const double large = 16;
  static const double largeIncreased = 20;
  static const double extraLarge = 28;
  static const double full = 9999;

  static const radiusNone = BorderRadius.zero;
  static const radiusExtraSmall = BorderRadius.all(Radius.circular(extraSmall));
  static const radiusSmall = BorderRadius.all(Radius.circular(small));
  static const radiusMedium = BorderRadius.all(Radius.circular(medium));
  static const radiusLarge = BorderRadius.all(Radius.circular(large));
  static const radiusLargeIncreased = BorderRadius.all(
    Radius.circular(largeIncreased),
  );
  static const radiusExtraLarge = BorderRadius.all(Radius.circular(extraLarge));
  static const radiusFull = BorderRadius.all(Radius.circular(full));
}

/// Motion tokens (§6): easings as [Cubic], durations as [Duration].
abstract final class M3Motion {
  // Official M3 easing slots (§6.1)
  static const Cubic standard = Cubic(0.2, 0.0, 0.0, 1.0);
  static const Cubic standardDecelerate = Cubic(0.0, 0.0, 0.0, 1.0);
  static const Cubic standardAccelerate = Cubic(0.3, 0.0, 1.0, 1.0);
  static const Cubic emphasizedDecelerate = Cubic(0.05, 0.7, 0.1, 1.0);
  static const Cubic emphasizedAccelerate = Cubic(0.3, 0.0, 0.8, 0.15);

  // Spring/spatial approximations (overshoot)
  static const Cubic fastSpatial = Cubic(0.27, 1.06, 0.18, 1.0);
  static const Cubic spatial = Cubic(0.27, 1.04, 0.21, 1.0);
  static const Cubic slowSpatial = Cubic(0.27, 1.02, 0.24, 1.0);

  // Duration tokens (§6.2)
  static const Duration short1 = Duration(milliseconds: 50);
  static const Duration short2 = Duration(milliseconds: 100);
  static const Duration short3 = Duration(milliseconds: 150);
  static const Duration short4 = Duration(milliseconds: 200);
  static const Duration medium1 = Duration(milliseconds: 250);
  static const Duration medium2 = Duration(milliseconds: 300);
  static const Duration medium3 = Duration(milliseconds: 350);
  static const Duration medium4 = Duration(milliseconds: 400);
  static const Duration long1 = Duration(milliseconds: 450);
  static const Duration long2 = Duration(milliseconds: 500);
  static const Duration long3 = Duration(milliseconds: 560);
  static const Duration long4 = Duration(milliseconds: 640);

  // Enter translate distance + cascade step (§6.2)
  static const double enterY = 14;
  static const Duration staggerStep = Duration(milliseconds: 36);
}

/// Semantic, tinted-surface, pattern, and recipe tokens that don't fit in a
/// [ColorScheme]. Held as a [ThemeExtension] so widgets read them via
/// `Theme.of(context).extension<AppTokens>()!` (see the `AppTokensX` helper).
@immutable
class AppTokens extends ThemeExtension<AppTokens> {
  const AppTokens({
    required this.brightness,
    // Semantic status colors (§2.4)
    required this.success,
    required this.onSuccess,
    required this.warning,
    required this.onWarning,
    required this.info,
    required this.onInfo,
    required this.accentCyan,
    required this.onAccentCyan,
    required this.accentTeal,
    required this.tealContainer,
    required this.onTealContainer,
    required this.accentCoral,
    required this.coralContainer,
    required this.onCoralContainer,
    required this.successContainer,
    required this.onSuccessContainer,
    required this.warningContainer,
    required this.onWarningContainer,
    required this.infoContainer,
    required this.onInfoContainer,
    // Tinted surface families (§2.5) — base + high pairs.
    required this.surfaceMint,
    required this.surfaceMintHigh,
    required this.surfaceSky,
    required this.surfaceSkyHigh,
    required this.surfaceAmber,
    required this.surfaceAmberHigh,
    required this.surfaceRose,
    required this.surfaceRoseHigh,
    required this.surfaceViolet,
    required this.surfaceVioletHigh,
    required this.surfaceCoral,
    required this.surfaceCoralHigh,
    // Pattern accents (§2.6)
    required this.patternA,
    required this.patternB,
    required this.patternC,
    required this.patternD,
    // Border-strength knobs (§2.7) — 0..1 alpha applied to outlineVariant.
    required this.cardBorderStrength,
    required this.tonalBorderStrength,
    // Art overlay tuning (§7.1)
    required this.artOpacity,
    required this.artHeroOpacity,
    required this.artBandOpacity,
  });

  final Brightness brightness;
  bool get isDark => brightness == Brightness.dark;

  // Semantics
  final Color success;
  final Color onSuccess;
  final Color warning;
  final Color onWarning;
  final Color info;
  final Color onInfo;
  final Color accentCyan;
  final Color onAccentCyan;

  // Teal + coral accent families (TSK-162 app extensions; tones via
  // TonalPalette — teal seed #2dd4bf, coral seed #ff7a8a). Teal carries
  // units/inventory hues; coral is the error-adjacent warm family.
  final Color accentTeal;
  final Color tealContainer;
  final Color onTealContainer;
  final Color accentCoral;
  final Color coralContainer;
  final Color onCoralContainer;

  final Color successContainer;
  final Color onSuccessContainer;
  final Color warningContainer;
  final Color onWarningContainer;
  final Color infoContainer;
  final Color onInfoContainer;

  // Tinted families
  final Color surfaceMint;
  final Color surfaceMintHigh;
  final Color surfaceSky;
  final Color surfaceSkyHigh;
  final Color surfaceAmber;
  final Color surfaceAmberHigh;
  final Color surfaceRose;
  final Color surfaceRoseHigh;
  final Color surfaceViolet;
  final Color surfaceVioletHigh;
  final Color surfaceCoral;
  final Color surfaceCoralHigh;

  // Pattern accents
  final Color patternA;
  final Color patternB;
  final Color patternC;
  final Color patternD;

  // Border strengths (alpha 0..1)
  final double cardBorderStrength;
  final double tonalBorderStrength;

  // Art overlay
  final double artOpacity;
  final double artHeroOpacity;
  final double artBandOpacity;

  /// DARK token set. Tinted families are intentionally NOT bright pastels in
  /// dark mode (§2.5): tonal cards fall back to a faint tint of the dark card
  /// surface. We model that by mixing 8% of the semantic color into the dark
  /// card tier (`surfaceContainerLow`).
  static const Color _darkCard = M3Colors.darkSurfaceContainerLow;

  static AppTokens dark = AppTokens(
    brightness: Brightness.dark,
    // Emerald seed t80 — softer than the old #34D399 neon in dark (TSK-162).
    success: const Color(0xFF45DFA4),
    onSuccess: const Color(0xFF052E16),
    warning: const Color(0xFFF59E0B),
    onWarning: const Color(0xFF451A03),
    info: const Color(0xFF60A5FA),
    onInfo: const Color(0xFF07111F),
    accentCyan: const Color(0xFF22D3EE),
    onAccentCyan: const Color(0xFF04222B),
    accentTeal: const Color(0xFF3CDDC7),
    tealContainer: const Color(0xFF005047),
    onTealContainer: const Color(0xFF62FAE3),
    accentCoral: const Color(0xFFFF8794),
    coralContainer: const Color(0xFF861E32),
    onCoralContainer: const Color(0xFFFFDADB),
    successContainer: const Color(0xFF0F4B2F),
    onSuccessContainer: const Color(0xFFC3F4D8),
    warningContainer: const Color(0xFF563900),
    onWarningContainer: const Color(0xFFFFDDA1),
    infoContainer: const Color(0xFF123B63),
    onInfoContainer: const Color(0xFFD5E3FF),
    // Dark tonal families: 8% semantic mixed into the dark card (§2.5 note).
    surfaceMint: _mix(const Color(0xFF34D399), _darkCard, 0.08),
    surfaceMintHigh: _mix(const Color(0xFF34D399), _darkCard, 0.14),
    surfaceSky: _mix(const Color(0xFF60A5FA), _darkCard, 0.08),
    surfaceSkyHigh: _mix(const Color(0xFF60A5FA), _darkCard, 0.14),
    surfaceAmber: _mix(const Color(0xFFF59E0B), _darkCard, 0.08),
    surfaceAmberHigh: _mix(const Color(0xFFF59E0B), _darkCard, 0.14),
    surfaceRose: _mix(const Color(0xFFFFB4AB), _darkCard, 0.08),
    surfaceRoseHigh: _mix(const Color(0xFFFFB4AB), _darkCard, 0.14),
    surfaceViolet: _mix(const Color(0xFFD4BBFC), _darkCard, 0.08),
    surfaceVioletHigh: _mix(const Color(0xFFD4BBFC), _darkCard, 0.14),
    // Coral mixes from the warm coral accent (the old source was the retired
    // cool-cyan tertiary — coral cards read blue-grey in dark, a frozen bug).
    surfaceCoral: _mix(const Color(0xFFFF8794), _darkCard, 0.08),
    surfaceCoralHigh: _mix(const Color(0xFFFF8794), _darkCard, 0.14),
    patternA: const Color(0xFF7668FF),
    patternB: const Color(0xFF2DD4BF),
    patternC: const Color(0xFFF1B7C4),
    patternD: const Color(0xFFFFD166),
    cardBorderStrength: 0.0,
    tonalBorderStrength: 0.42,
    // Dark art push (TSK-162): visible art, not a rumor.
    artOpacity: 0.30,
    artHeroOpacity: 0.50,
    artBandOpacity: 0.52,
  );

  /// LIGHT token set. Tinted families use the §2.5 published pastels.
  static const AppTokens light = AppTokens(
    brightness: Brightness.light,
    success: Color(0xFF18794E),
    onSuccess: Color(0xFFFFFFFF),
    warning: Color(0xFFA15C00),
    onWarning: Color(0xFFFFFFFF),
    info: Color(0xFF007789),
    onInfo: Color(0xFFFFFFFF),
    accentCyan: Color(0xFF00A3B8),
    onAccentCyan: Color(0xFF001F24),
    accentTeal: Color(0xFF006B5F),
    tealContainer: Color(0xFF62FAE3),
    onTealContainer: Color(0xFF005047),
    accentCoral: Color(0xFFA63648),
    coralContainer: Color(0xFFFFDADB),
    onCoralContainer: Color(0xFF861E32),
    successContainer: Color(0xFFD6F5DF),
    onSuccessContainer: Color(0xFF063D24),
    warningContainer: Color(0xFFFFDFAA),
    onWarningContainer: Color(0xFF4B2A00),
    infoContainer: Color(0xFFC4EEF7),
    onInfoContainer: Color(0xFF002F38),
    surfaceMint: Color(0xFFE2F3EC),
    surfaceMintHigh: Color(0xFFCAE8DC),
    surfaceSky: Color(0xFFE2F0F8),
    surfaceSkyHigh: Color(0xFFC8E3EF),
    surfaceAmber: Color(0xFFFFF0D1),
    surfaceAmberHigh: Color(0xFFFFDDA1),
    surfaceRose: Color(0xFFFBE3EC),
    surfaceRoseHigh: Color(0xFFF5C7D7),
    surfaceViolet: Color(0xFFE8E4F8),
    surfaceVioletHigh: Color(0xFFD8D1F2),
    surfaceCoral: Color(0xFFFFE5D8),
    surfaceCoralHigh: Color(0xFFFFCDB7),
    patternA: Color(0xFF7F67BE),
    patternB: Color(0xFF3AA99E),
    patternC: Color(0xFFD66F93),
    patternD: Color(0xFFE0A12B),
    cardBorderStrength: 0.46,
    tonalBorderStrength: 0.72,
    artOpacity: 0.82,
    artHeroOpacity: 0.92,
    artBandOpacity: 0.95,
  );

  /// `color-mix(in srgb, [a] [t]%, [b])` ≡ `Color.lerp(b, a, t)`.
  static Color _mix(Color a, Color b, double t) => Color.lerp(b, a, t)!;

  @override
  AppTokens copyWith({Brightness? brightness}) => this;

  @override
  AppTokens lerp(ThemeExtension<AppTokens>? other, double t) {
    if (other is! AppTokens) return this;
    // Snap at the midpoint — these are two discrete (light/dark) sets, not a
    // continuous space; a mid-lerp pastel would look wrong.
    return t < 0.5 ? this : other;
  }
}

/// Sugar for reading [AppTokens] off the current theme.
extension AppTokensX on BuildContext {
  AppTokens get tokens =>
      Theme.of(this).extension<AppTokens>() ?? AppTokens.dark;
}
