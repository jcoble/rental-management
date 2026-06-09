import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';

/// EdiPlatform type scale (§4) ported to a Flutter [TextTheme].
///
/// Fonts (§4.1):
///   * Display + Headline roles  → **Funnel Display** (`--m3-font-display`).
///   * Title / Body / Label      → **Funnel Sans** (the public companion to
///     Funnel Display; stands in for the internal "Google Sans Flex" body font
///     whose documented fallback is a clean grotesque).
///
/// Both are fetched via `google_fonts` (the directory string API, so the call
/// compiles regardless of generated accessor names) — this matches "use
/// google_fonts if that's the pattern" and avoids bundling woff2 (which Flutter
/// can't consume) or unverified TTF binaries.
///
/// Flutter `height` = `lineHeight / fontSize` (e.g. body-large 24/16 = 1.5).
/// `letterSpacing` is 0 for every role (§4.2).
abstract final class M3Type {
  static const String displayFamily = 'Funnel Display';
  static const String bodyFamily = 'Funnel Sans';

  /// The display/headline font as a single resolved [TextStyle] to copy from.
  static TextStyle _display(double size, double line, FontWeight weight) =>
      GoogleFonts.getFont(
        displayFamily,
        fontSize: size,
        height: line / size,
        fontWeight: weight,
        letterSpacing: 0,
      );

  static TextStyle _body(double size, double line, FontWeight weight) =>
      GoogleFonts.getFont(
        bodyFamily,
        fontSize: size,
        height: line / size,
        fontWeight: weight,
        letterSpacing: 0,
      );

  /// Builds the full type theme, with [onSurface]/[onSurfaceVariant] applied so
  /// body text stays neutral (near-white in dark, near-black in light) rather
  /// than primary-tinted, per the design philosophy.
  static TextTheme textTheme({
    required Color onSurface,
    required Color onSurfaceVariant,
  }) {
    Color c(bool muted) => muted ? onSurfaceVariant : onSurface;
    return TextTheme(
      // Display — Funnel Display, w400 baseline
      displayLarge: _display(57, 64, FontWeight.w400).copyWith(color: c(false)),
      displayMedium: _display(45, 52, FontWeight.w400).copyWith(color: c(false)),
      displaySmall: _display(36, 44, FontWeight.w400).copyWith(color: c(false)),
      // Headline — Funnel Display. Authored 400 in the scale; the app renders
      // emphasized titles heavier, so headline gets w600 (the documented
      // "emphasized title" override).
      headlineLarge: _display(32, 40, FontWeight.w600).copyWith(color: c(false)),
      headlineMedium: _display(
        28,
        36,
        FontWeight.w600,
      ).copyWith(color: c(false)),
      headlineSmall: _display(24, 32, FontWeight.w600).copyWith(color: c(false)),
      // Title — Funnel Sans, w500
      titleLarge: _body(22, 28, FontWeight.w600).copyWith(color: c(false)),
      titleMedium: _body(16, 24, FontWeight.w600).copyWith(color: c(false)),
      titleSmall: _body(14, 20, FontWeight.w600).copyWith(color: c(false)),
      // Body — Funnel Sans, w400
      bodyLarge: _body(16, 24, FontWeight.w400).copyWith(color: c(false)),
      bodyMedium: _body(14, 20, FontWeight.w400).copyWith(color: c(false)),
      bodySmall: _body(12, 16, FontWeight.w400).copyWith(color: c(true)),
      // Label — Funnel Sans, w500
      labelLarge: _body(14, 20, FontWeight.w500).copyWith(color: c(false)),
      labelMedium: _body(12, 16, FontWeight.w500).copyWith(color: c(true)),
      labelSmall: _body(11, 16, FontWeight.w500).copyWith(color: c(true)),
    );
  }
}
