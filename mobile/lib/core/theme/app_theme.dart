import 'package:flutter/material.dart';

/// Material 3 themes for Rental Command.
///
/// Mirrors the web app's customized M3 Expressive command surface: off-black
/// backgrounds, lavender primary roles, and restrained cyan accents.
abstract final class AppTheme {
  static const Color _darkBackground = Color(0xFF121017);
  static const Color _darkSurface = Color(0xFF1A1720);
  static const Color _darkSurfaceHigh = Color(0xFF2B2730);
  static const Color _darkBorder = Color(0xFF4C4655);
  static const Color _darkForeground = Color(0xFFFBF8FF);
  static const Color _darkMuted = Color(0xFFD0CAD8);
  static const Color _primary = Color(0xFFD3BCFF);
  static const Color _primaryStrong = Color(0xFF503B77);
  static const Color _primaryInk = Color(0xFF38245D);
  static const Color _cyan = Color(0xFF22D3EE);

  static const Color _lightBackground = Color(0xFFFAFAFA);
  static const Color _lightSurface = Color(0xFFFFFFFF);
  static const Color _lightSurfaceHigh = Color(0xFFF4F4F5);
  static const Color _lightBorder = Color(0xFFE4E4E7);
  static const Color _lightForeground = Color(0xFF18181B);
  static const Color _lightMuted = Color(0xFF71717A);

  static const double _radiusSm = 10;
  static const double _radiusMd = 12;
  static const double _radiusLg = 16;
  static const double _radiusXl = 24;

  static ThemeData get light => _buildTheme(
    brightness: Brightness.light,
    scheme: const ColorScheme.light(
      primary: Color(0xFF69548D),
      onPrimary: Colors.white,
      primaryContainer: Color(0xFFECDCFF),
      onPrimaryContainer: Color(0xFF240E45),
      secondary: Color(0xFF625B71),
      onSecondary: Colors.white,
      secondaryContainer: Color(0xFFEDE0F8),
      onSecondaryContainer: _lightForeground,
      tertiary: Color(0xFF006875),
      onTertiary: Colors.white,
      tertiaryContainer: Color(0xFFC2F4FF),
      onTertiaryContainer: Color(0xFF002025),
      error: Color(0xFFDC2626),
      onError: Colors.white,
      errorContainer: Color(0xFFFEE2E2),
      onErrorContainer: Color(0xFF450A0A),
      surface: _lightBackground,
      onSurface: _lightForeground,
      surfaceContainerLowest: _lightSurface,
      surfaceContainerLow: _lightSurface,
      surfaceContainer: _lightSurfaceHigh,
      surfaceContainerHigh: Color(0xFFEDEDF0),
      surfaceContainerHighest: Color(0xFFE4E4E7),
      onSurfaceVariant: _lightMuted,
      outline: _lightBorder,
      outlineVariant: Color(0xFFF0F0F2),
      inverseSurface: _darkSurface,
      onInverseSurface: _darkForeground,
      inversePrimary: _primary,
    ),
    background: _lightBackground,
    surface: _lightSurface,
    surfaceHigh: _lightSurfaceHigh,
    border: _lightBorder,
    foreground: _lightForeground,
    muted: _lightMuted,
  );

  static ThemeData get dark => _buildTheme(
    brightness: Brightness.dark,
    scheme: const ColorScheme.dark(
      primary: _primary,
      onPrimary: _primaryInk,
      primaryContainer: _primaryStrong,
      onPrimaryContainer: Color(0xFFECDCFF),
      secondary: Color(0xFFD0C2DE),
      onSecondary: Color(0xFF342D40),
      secondaryContainer: Color(0xFF4B4358),
      onSecondaryContainer: Color(0xFFEDE0F8),
      tertiary: _cyan,
      onTertiary: Color(0xFF04222B),
      tertiaryContainer: Color(0xFF164E59),
      onTertiaryContainer: Color(0xFFC7F7FF),
      error: Color(0xFFFFB4AB),
      onError: Color(0xFF690005),
      errorContainer: Color(0xFF93000A),
      onErrorContainer: Color(0xFFFFDAD6),
      surface: _darkBackground,
      onSurface: _darkForeground,
      surfaceContainerLowest: _darkBackground,
      surfaceContainerLow: _darkSurface,
      surfaceContainer: Color(0xFF201D26),
      surfaceContainerHigh: _darkSurfaceHigh,
      surfaceContainerHighest: _darkBorder,
      onSurfaceVariant: _darkMuted,
      outline: _darkBorder,
      outlineVariant: Color(0xFF2F2938),
      inverseSurface: _darkForeground,
      onInverseSurface: _darkBackground,
      inversePrimary: Color(0xFF69548D),
    ),
    background: _darkBackground,
    surface: _darkSurface,
    surfaceHigh: _darkSurfaceHigh,
    border: _darkBorder,
    foreground: _darkForeground,
    muted: _darkMuted,
  );

  static ThemeData _buildTheme({
    required Brightness brightness,
    required ColorScheme scheme,
    required Color background,
    required Color surface,
    required Color surfaceHigh,
    required Color border,
    required Color foreground,
    required Color muted,
  }) {
    final isDark = brightness == Brightness.dark;
    final base = ThemeData(
      useMaterial3: true,
      colorScheme: scheme,
      brightness: brightness,
      scaffoldBackgroundColor: background,
      canvasColor: background,
      dividerColor: border,
    );
    final textTheme = _textTheme(base.textTheme, foreground, muted);

    return base.copyWith(
      textTheme: textTheme,
      primaryTextTheme: textTheme,
      splashColor: scheme.primary.withAlpha(isDark ? 22 : 16),
      highlightColor: scheme.primary.withAlpha(isDark ? 18 : 12),
      focusColor: scheme.primary.withAlpha(isDark ? 42 : 30),
      appBarTheme: AppBarTheme(
        centerTitle: false,
        elevation: 0,
        backgroundColor: background,
        foregroundColor: foreground,
        scrolledUnderElevation: 1,
        surfaceTintColor: Colors.transparent,
        titleTextStyle: TextStyle(
          color: foreground,
          fontSize: 20,
          fontWeight: FontWeight.w600,
          letterSpacing: 0,
        ),
      ),
      navigationBarTheme: NavigationBarThemeData(
        height: 72,
        elevation: 0,
        backgroundColor: isDark ? const Color(0xFF101114) : _lightSurface,
        surfaceTintColor: Colors.transparent,
        indicatorColor: scheme.primary.withAlpha(isDark ? 38 : 28),
        indicatorShape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(_radiusLg),
        ),
        iconTheme: WidgetStateProperty.resolveWith((states) {
          final selected = states.contains(WidgetState.selected);
          return IconThemeData(
            color: selected ? scheme.primary : muted,
            size: selected ? 26 : 24,
          );
        }),
        labelTextStyle: WidgetStateProperty.resolveWith((states) {
          final selected = states.contains(WidgetState.selected);
          return TextStyle(
            color: selected ? foreground : muted,
            fontSize: 12,
            fontWeight: selected ? FontWeight.w700 : FontWeight.w500,
            letterSpacing: 0,
          );
        }),
      ),
      navigationDrawerTheme: NavigationDrawerThemeData(
        backgroundColor: background,
        indicatorColor: scheme.primary.withAlpha(isDark ? 34 : 24),
        indicatorShape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(_radiusLg),
        ),
        labelTextStyle: WidgetStateProperty.resolveWith((states) {
          final selected = states.contains(WidgetState.selected);
          return TextStyle(
            color: selected ? foreground : muted,
            fontWeight: selected ? FontWeight.w600 : FontWeight.w500,
          );
        }),
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: isDark ? surfaceHigh.withAlpha(120) : _lightSurface,
        hintStyle: TextStyle(color: muted),
        labelStyle: TextStyle(color: muted, fontWeight: FontWeight.w500),
        floatingLabelStyle: TextStyle(
          color: scheme.primary,
          fontWeight: FontWeight.w600,
        ),
        helperStyle: TextStyle(color: muted),
        errorStyle: TextStyle(color: scheme.error),
        prefixIconColor: muted,
        suffixIconColor: muted,
        contentPadding: const EdgeInsets.symmetric(
          horizontal: 16,
          vertical: 14,
        ),
        border: _outlineInputBorder(border),
        enabledBorder: _outlineInputBorder(border),
        focusedBorder: _outlineInputBorder(scheme.primary, width: 1.5),
        errorBorder: _outlineInputBorder(scheme.error),
        focusedErrorBorder: _outlineInputBorder(scheme.error, width: 1.5),
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          minimumSize: const Size.fromHeight(48),
          padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 12),
          foregroundColor: scheme.onPrimary,
          backgroundColor: scheme.primary,
          disabledForegroundColor: muted.withAlpha(130),
          disabledBackgroundColor: surfaceHigh,
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(_radiusMd),
          ),
        ),
      ),
      elevatedButtonTheme: ElevatedButtonThemeData(
        style: ElevatedButton.styleFrom(
          minimumSize: const Size.fromHeight(48),
          elevation: 0,
          foregroundColor: scheme.onPrimary,
          backgroundColor: scheme.primary,
          surfaceTintColor: Colors.transparent,
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(_radiusMd),
          ),
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          minimumSize: const Size.fromHeight(48),
          padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 12),
          foregroundColor: foreground,
          side: BorderSide(color: border),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(_radiusMd),
          ),
        ),
      ),
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(
          foregroundColor: scheme.primary,
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(_radiusSm),
          ),
        ),
      ),
      iconButtonTheme: IconButtonThemeData(
        style: IconButton.styleFrom(
          foregroundColor: muted,
          hoverColor: scheme.primary.withAlpha(isDark ? 20 : 14),
          focusColor: scheme.primary.withAlpha(isDark ? 28 : 20),
          highlightColor: scheme.primary.withAlpha(isDark ? 24 : 18),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(_radiusSm),
          ),
        ),
      ),
      cardTheme: CardThemeData(
        elevation: 0,
        color: surface,
        surfaceTintColor: Colors.transparent,
        margin: const EdgeInsets.symmetric(horizontal: 0, vertical: 6),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(_radiusLg),
          side: BorderSide(color: border),
        ),
      ),
      chipTheme: ChipThemeData(
        backgroundColor: surfaceHigh,
        deleteIconColor: muted,
        disabledColor: surfaceHigh.withAlpha(120),
        selectedColor: scheme.primary.withAlpha(isDark ? 34 : 24),
        secondarySelectedColor: scheme.primaryContainer,
        labelStyle: TextStyle(color: foreground, fontWeight: FontWeight.w600),
        secondaryLabelStyle: TextStyle(
          color: scheme.onPrimaryContainer,
          fontWeight: FontWeight.w700,
        ),
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
        side: BorderSide(color: border),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(_radiusSm),
        ),
      ),
      dialogTheme: DialogThemeData(
        backgroundColor: surface,
        surfaceTintColor: Colors.transparent,
        titleTextStyle: textTheme.titleLarge,
        contentTextStyle: textTheme.bodyMedium,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(_radiusXl),
          side: BorderSide(color: border),
        ),
      ),
      bottomSheetTheme: BottomSheetThemeData(
        backgroundColor: surface,
        surfaceTintColor: Colors.transparent,
        modalBackgroundColor: surface,
        modalBarrierColor: Colors.black.withAlpha(isDark ? 150 : 70),
        shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.vertical(top: Radius.circular(_radiusXl)),
        ),
      ),
      snackBarTheme: SnackBarThemeData(
        behavior: SnackBarBehavior.floating,
        backgroundColor: isDark ? const Color(0xFFE4E4E7) : _darkSurface,
        contentTextStyle: TextStyle(
          color: isDark ? _darkBackground : _darkForeground,
          fontWeight: FontWeight.w600,
        ),
        actionTextColor: isDark ? _primaryStrong : _primary,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(_radiusMd),
        ),
      ),
      progressIndicatorTheme: ProgressIndicatorThemeData(
        color: scheme.primary,
        circularTrackColor: surfaceHigh,
        linearTrackColor: surfaceHigh,
        refreshBackgroundColor: surface,
      ),
      dividerTheme: DividerThemeData(color: border, thickness: 1),
      listTileTheme: ListTileThemeData(
        iconColor: muted,
        textColor: foreground,
        selectedColor: scheme.primary,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(_radiusMd),
        ),
      ),
      switchTheme: SwitchThemeData(
        thumbColor: WidgetStateProperty.resolveWith((states) {
          if (states.contains(WidgetState.selected)) {
            return scheme.onPrimary;
          }
          return muted;
        }),
        trackColor: WidgetStateProperty.resolveWith((states) {
          if (states.contains(WidgetState.selected)) {
            return scheme.primary;
          }
          return surfaceHigh;
        }),
        trackOutlineColor: WidgetStateProperty.all(border),
      ),
      checkboxTheme: CheckboxThemeData(
        fillColor: WidgetStateProperty.resolveWith((states) {
          if (states.contains(WidgetState.selected)) {
            return scheme.primary;
          }
          return Colors.transparent;
        }),
        checkColor: WidgetStateProperty.all(scheme.onPrimary),
        side: BorderSide(color: border, width: 1.5),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(4)),
      ),
      radioTheme: RadioThemeData(
        fillColor: WidgetStateProperty.resolveWith((states) {
          if (states.contains(WidgetState.selected)) {
            return scheme.primary;
          }
          return muted;
        }),
      ),
    );
  }

  static OutlineInputBorder _outlineInputBorder(
    Color color, {
    double width = 1,
  }) {
    return OutlineInputBorder(
      borderRadius: BorderRadius.circular(_radiusMd),
      borderSide: BorderSide(color: color, width: width),
    );
  }

  static TextTheme _textTheme(TextTheme base, Color foreground, Color muted) {
    return base.copyWith(
      displayLarge: base.displayLarge?.copyWith(
        color: foreground,
        fontWeight: FontWeight.w700,
        letterSpacing: 0,
      ),
      displayMedium: base.displayMedium?.copyWith(
        color: foreground,
        fontWeight: FontWeight.w700,
        letterSpacing: 0,
      ),
      headlineLarge: base.headlineLarge?.copyWith(
        color: foreground,
        fontWeight: FontWeight.w700,
        letterSpacing: 0,
      ),
      headlineMedium: base.headlineMedium?.copyWith(
        color: foreground,
        fontWeight: FontWeight.w700,
        letterSpacing: 0,
      ),
      titleLarge: base.titleLarge?.copyWith(
        color: foreground,
        fontWeight: FontWeight.w600,
        letterSpacing: 0,
      ),
      titleMedium: base.titleMedium?.copyWith(
        color: foreground,
        fontWeight: FontWeight.w600,
        letterSpacing: 0,
      ),
      titleSmall: base.titleSmall?.copyWith(
        color: foreground,
        fontWeight: FontWeight.w600,
        letterSpacing: 0,
      ),
      bodyLarge: base.bodyLarge?.copyWith(color: foreground, letterSpacing: 0),
      bodyMedium: base.bodyMedium?.copyWith(
        color: foreground,
        letterSpacing: 0,
      ),
      bodySmall: base.bodySmall?.copyWith(color: muted, letterSpacing: 0),
      labelLarge: base.labelLarge?.copyWith(
        color: foreground,
        fontWeight: FontWeight.w700,
        letterSpacing: 0,
      ),
      labelMedium: base.labelMedium?.copyWith(
        color: muted,
        fontWeight: FontWeight.w600,
        letterSpacing: 0,
      ),
      labelSmall: base.labelSmall?.copyWith(
        color: muted,
        fontWeight: FontWeight.w600,
        letterSpacing: 0,
      ),
    );
  }
}
