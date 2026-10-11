import 'package:flutter/material.dart';

import 'app_tokens.dart';
import 'app_typography.dart';

/// Material 3 themes for Rental Command.
///
/// Ports EdiPlatform's "M3 Expressive + business twist" design system (see
/// `app_tokens.dart` / `app_typography.dart`, sourced verbatim from
/// `DESIGN-SYSTEM.md`). Identity: violet seed `#A36BFF`, TonalSpot scheme, **dark
/// mode is the default**. Near-zero elevation — depth comes from borders + tone,
/// not shadows. Generous radii (cards/dialogs `extra-large` 28px, inputs/rows
/// `large` 16px). Funnel Display for display/headline, Funnel Sans for the rest.
abstract final class AppTheme {
  static ThemeData get light => _build(M3Colors.lightScheme, AppTokens.light);
  static ThemeData get dark => _build(M3Colors.darkScheme, AppTokens.dark);

  static ThemeData _build(ColorScheme scheme, AppTokens tokens) {
    final isDark = scheme.brightness == Brightness.dark;
    final textTheme = M3Type.textTheme(
      onSurface: scheme.onSurface,
      onSurfaceVariant: scheme.onSurfaceVariant,
    );

    // Border drawn as outlineVariant @ the card-border strength (§2.7): a
    // hairline in light, effectively absent in dark (depth from tone).
    final cardBorderColor = scheme.outlineVariant.withValues(
      alpha: tokens.cardBorderStrength,
    );
    final cardBorderSide = tokens.cardBorderStrength <= 0
        ? BorderSide.none
        : BorderSide(color: cardBorderColor);

    // Control (input) surface tokens (§2.7, TSK-162): dark fields are faintly
    // violet-washed (82% highest container + primary container) so they read as
    // interactive against plain containers; light uses the whitest container.
    final controlFill = isDark
        ? Color.lerp(
            scheme.primaryContainer,
            scheme.surfaceContainerHighest,
            0.82,
          )!
        : scheme.surfaceContainerLowest;
    final controlBorder = isDark
        ? scheme.outline.withValues(alpha: 0.38)
        : scheme.outline.withValues(alpha: 0.64);

    final base = ThemeData(
      useMaterial3: true,
      colorScheme: scheme,
      brightness: scheme.brightness,
      scaffoldBackgroundColor: scheme.surface,
      canvasColor: scheme.surface,
      dividerColor: scheme.outlineVariant,
      splashColor: scheme.primary.withValues(alpha: isDark ? 0.10 : 0.07),
      highlightColor: scheme.primary.withValues(alpha: isDark ? 0.08 : 0.05),
      focusColor: scheme.primary.withValues(alpha: isDark ? 0.16 : 0.12),
      extensions: [tokens],
    );

    return base.copyWith(
      textTheme: textTheme,
      primaryTextTheme: textTheme,

      // ── Top app bar — translucent flat header, bottom hairline (§8 TopAppBar)
      appBarTheme: AppBarTheme(
        centerTitle: false,
        elevation: 0,
        scrolledUnderElevation: 0,
        backgroundColor: scheme.surface,
        foregroundColor: scheme.onSurface,
        surfaceTintColor: Colors.transparent,
        titleTextStyle: textTheme.titleLarge,
      ),

      // ── Bottom nav — flat, tonal selected indicator, large radius, fill morph.
      // Selected indicator = primary container (violet "platform voice"),
      // matching the web sidebar's active pill; cyan secondary stays the
      // chip/tab selection accent.
      navigationBarTheme: NavigationBarThemeData(
        height: 72,
        elevation: 0,
        backgroundColor: scheme.surfaceContainerLow,
        surfaceTintColor: Colors.transparent,
        indicatorColor: scheme.primaryContainer,
        indicatorShape: const RoundedRectangleBorder(
          borderRadius: M3Shape.radiusLarge,
        ),
        iconTheme: WidgetStateProperty.resolveWith((states) {
          final selected = states.contains(WidgetState.selected);
          return IconThemeData(
            color: selected
                ? scheme.onPrimaryContainer
                : scheme.onSurfaceVariant,
            size: 24,
          );
        }),
        labelTextStyle: WidgetStateProperty.resolveWith((states) {
          final selected = states.contains(WidgetState.selected);
          return textTheme.labelMedium?.copyWith(
            color: selected ? scheme.onSurface : scheme.onSurfaceVariant,
            fontWeight: selected ? FontWeight.w700 : FontWeight.w500,
          );
        }),
      ),

      navigationDrawerTheme: NavigationDrawerThemeData(
        backgroundColor: scheme.surface,
        surfaceTintColor: Colors.transparent,
        indicatorColor: scheme.primaryContainer,
        indicatorShape: const RoundedRectangleBorder(
          borderRadius: M3Shape.radiusLarge,
        ),
        labelTextStyle: WidgetStateProperty.resolveWith((states) {
          final selected = states.contains(WidgetState.selected);
          return textTheme.labelLarge?.copyWith(
            color: selected
                ? scheme.onPrimaryContainer
                : scheme.onSurfaceVariant,
            fontWeight: selected ? FontWeight.w700 : FontWeight.w500,
          );
        }),
      ),

      // ── Inputs — large radius, control surface, primary focus ring (§7.8)
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: controlFill,
        hintStyle: textTheme.bodyLarge?.copyWith(
          color: scheme.onSurfaceVariant,
        ),
        labelStyle: textTheme.bodyLarge?.copyWith(
          color: scheme.onSurfaceVariant,
          fontWeight: FontWeight.w500,
        ),
        floatingLabelStyle: textTheme.bodyMedium?.copyWith(
          color: scheme.primary,
          fontWeight: FontWeight.w600,
        ),
        helperStyle: textTheme.bodySmall,
        errorStyle: textTheme.bodySmall?.copyWith(color: scheme.error),
        prefixIconColor: scheme.onSurfaceVariant,
        suffixIconColor: scheme.onSurfaceVariant,
        contentPadding: const EdgeInsets.symmetric(
          horizontal: 16,
          vertical: 14,
        ),
        border: _inputBorder(controlBorder),
        enabledBorder: _inputBorder(controlBorder),
        focusedBorder: _inputBorder(scheme.primary, width: 2),
        errorBorder: _inputBorder(scheme.error),
        focusedErrorBorder: _inputBorder(scheme.error, width: 2),
      ),

      // ── Buttons — pill (full) radius, label-large font, flat (§8 Button)
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          minimumSize: const Size(64, 48),
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
          foregroundColor: scheme.onPrimary,
          backgroundColor: scheme.primary,
          disabledForegroundColor: scheme.onSurfaceVariant.withValues(
            alpha: 0.5,
          ),
          disabledBackgroundColor: scheme.surfaceContainerHigh,
          elevation: 0,
          textStyle: textTheme.labelLarge,
          shape: const RoundedRectangleBorder(borderRadius: M3Shape.radiusFull),
        ),
      ),
      elevatedButtonTheme: ElevatedButtonThemeData(
        style: ElevatedButton.styleFrom(
          minimumSize: const Size(64, 48),
          elevation: 0,
          foregroundColor: scheme.primary,
          backgroundColor: scheme.surfaceContainerHigh,
          surfaceTintColor: Colors.transparent,
          textStyle: textTheme.labelLarge,
          shape: const RoundedRectangleBorder(borderRadius: M3Shape.radiusFull),
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          minimumSize: const Size(64, 48),
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
          foregroundColor: scheme.onSurface,
          side: BorderSide(color: controlBorder),
          textStyle: textTheme.labelLarge,
          shape: const RoundedRectangleBorder(borderRadius: M3Shape.radiusFull),
        ),
      ),
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(
          foregroundColor: scheme.primary,
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
          textStyle: textTheme.labelLarge,
          shape: const RoundedRectangleBorder(borderRadius: M3Shape.radiusFull),
        ),
      ),
      iconButtonTheme: IconButtonThemeData(
        style: IconButton.styleFrom(
          foregroundColor: scheme.onSurfaceVariant,
          highlightColor: scheme.primary.withValues(alpha: isDark ? 0.1 : 0.07),
          shape: const RoundedRectangleBorder(borderRadius: M3Shape.radiusFull),
        ),
      ),
      segmentedButtonTheme: SegmentedButtonThemeData(
        style: SegmentedButton.styleFrom(
          backgroundColor: scheme.surfaceContainerLow,
          foregroundColor: scheme.onSurfaceVariant,
          selectedBackgroundColor: scheme.secondaryContainer,
          selectedForegroundColor: scheme.onSecondaryContainer,
          side: BorderSide(color: controlBorder),
        ),
      ),

      // ── Cards — flat, extra-large radius, tonal + hairline border (§5/§8)
      cardTheme: CardThemeData(
        elevation: 0,
        color: scheme.surfaceContainerLow,
        surfaceTintColor: Colors.transparent,
        margin: const EdgeInsets.symmetric(vertical: 6),
        clipBehavior: Clip.antiAlias,
        shape: RoundedRectangleBorder(
          borderRadius: M3Shape.radiusExtraLarge,
          side: cardBorderSide,
        ),
      ),

      // ── Chips — small radius (§8 Chip)
      chipTheme: ChipThemeData(
        backgroundColor: scheme.surfaceContainerLow,
        deleteIconColor: scheme.onSurfaceVariant,
        disabledColor: scheme.surfaceContainerLow.withValues(alpha: 0.5),
        selectedColor: scheme.secondaryContainer,
        secondarySelectedColor: scheme.secondaryContainer,
        labelStyle: textTheme.labelLarge,
        secondaryLabelStyle: textTheme.labelLarge?.copyWith(
          color: scheme.onSecondaryContainer,
        ),
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        side: BorderSide(
          color: scheme.outlineVariant.withValues(
            alpha: tokens.cardBorderStrength,
          ),
        ),
        shape: const RoundedRectangleBorder(borderRadius: M3Shape.radiusSmall),
      ),

      // ── Dialogs ≈ elevation 3, extra-large radius (§5/§7.8)
      dialogTheme: DialogThemeData(
        backgroundColor: scheme.surfaceContainerHigh,
        surfaceTintColor: Colors.transparent,
        elevation: isDark ? 0 : 3,
        shadowColor: Colors.black.withValues(alpha: isDark ? 0.0 : 0.10),
        titleTextStyle: textTheme.headlineSmall,
        contentTextStyle: textTheme.bodyMedium,
        shape: RoundedRectangleBorder(
          borderRadius: M3Shape.radiusExtraLarge,
          side: cardBorderSide,
        ),
      ),

      // ── Bottom sheets — extra-large top radius, flat
      bottomSheetTheme: BottomSheetThemeData(
        backgroundColor: scheme.surfaceContainerLow,
        surfaceTintColor: Colors.transparent,
        modalBackgroundColor: scheme.surfaceContainerLow,
        elevation: 0,
        modalBarrierColor: Colors.black.withValues(alpha: isDark ? 0.58 : 0.4),
        shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.vertical(
            top: Radius.circular(M3Shape.extraLarge),
          ),
        ),
      ),

      menuTheme: MenuThemeData(
        style: MenuStyle(
          backgroundColor: WidgetStatePropertyAll(scheme.surfaceContainerHigh),
          surfaceTintColor: const WidgetStatePropertyAll(Colors.transparent),
          elevation: WidgetStatePropertyAll(isDark ? 0 : 2),
          shape: WidgetStatePropertyAll(
            RoundedRectangleBorder(
              borderRadius: M3Shape.radiusLarge,
              side: cardBorderSide,
            ),
          ),
        ),
      ),
      popupMenuTheme: PopupMenuThemeData(
        color: scheme.surfaceContainerHigh,
        surfaceTintColor: Colors.transparent,
        elevation: isDark ? 0 : 2,
        shape: RoundedRectangleBorder(
          borderRadius: M3Shape.radiusLarge,
          side: cardBorderSide,
        ),
      ),

      // ── Snackbar — inverse surface, medium radius
      snackBarTheme: SnackBarThemeData(
        behavior: SnackBarBehavior.floating,
        backgroundColor: scheme.inverseSurface,
        contentTextStyle: textTheme.bodyMedium?.copyWith(
          color: scheme.onInverseSurface,
        ),
        actionTextColor: scheme.inversePrimary,
        elevation: isDark ? 0 : 3,
        shape: const RoundedRectangleBorder(borderRadius: M3Shape.radiusLarge),
      ),

      progressIndicatorTheme: ProgressIndicatorThemeData(
        color: scheme.primary,
        circularTrackColor: scheme.surfaceContainerHighest,
        linearTrackColor: scheme.surfaceContainerHighest,
        refreshBackgroundColor: scheme.surfaceContainerLow,
      ),
      dividerTheme: DividerThemeData(
        color: scheme.outlineVariant,
        thickness: 1,
        space: 1,
      ),

      // ── List rows — large radius (§8 ListRow)
      listTileTheme: ListTileThemeData(
        iconColor: scheme.onSurfaceVariant,
        textColor: scheme.onSurface,
        selectedColor: scheme.onSecondaryContainer,
        selectedTileColor: scheme.secondaryContainer,
        titleTextStyle: textTheme.bodyLarge?.copyWith(
          fontWeight: FontWeight.w600,
        ),
        subtitleTextStyle: textTheme.bodyMedium?.copyWith(
          color: scheme.onSurfaceVariant,
        ),
        shape: const RoundedRectangleBorder(borderRadius: M3Shape.radiusLarge),
      ),

      tabBarTheme: TabBarThemeData(
        labelColor: scheme.onSecondaryContainer,
        unselectedLabelColor: scheme.onSurfaceVariant,
        labelStyle: textTheme.titleSmall,
        unselectedLabelStyle: textTheme.titleSmall,
        indicatorColor: scheme.primary,
        dividerColor: Colors.transparent,
      ),

      switchTheme: SwitchThemeData(
        thumbColor: WidgetStateProperty.resolveWith((states) {
          if (states.contains(WidgetState.selected)) return scheme.onPrimary;
          return scheme.outline;
        }),
        trackColor: WidgetStateProperty.resolveWith((states) {
          if (states.contains(WidgetState.selected)) return scheme.primary;
          return scheme.surfaceContainerHighest;
        }),
        trackOutlineColor: WidgetStateProperty.all(scheme.outlineVariant),
      ),
      checkboxTheme: CheckboxThemeData(
        fillColor: WidgetStateProperty.resolveWith((states) {
          if (states.contains(WidgetState.selected)) return scheme.primary;
          return Colors.transparent;
        }),
        checkColor: WidgetStateProperty.all(scheme.onPrimary),
        side: BorderSide(color: scheme.outline, width: 1.5),
        shape: const RoundedRectangleBorder(
          borderRadius: M3Shape.radiusExtraSmall,
        ),
      ),
      radioTheme: RadioThemeData(
        fillColor: WidgetStateProperty.resolveWith((states) {
          if (states.contains(WidgetState.selected)) return scheme.primary;
          return scheme.outline;
        }),
      ),
      badgeTheme: BadgeThemeData(
        backgroundColor: scheme.primary,
        textColor: scheme.onPrimary,
        textStyle: textTheme.labelSmall?.copyWith(color: scheme.onPrimary),
      ),
      floatingActionButtonTheme: FloatingActionButtonThemeData(
        backgroundColor: scheme.primaryContainer,
        foregroundColor: scheme.onPrimaryContainer,
        elevation: isDark ? 0 : 2,
        focusElevation: isDark ? 0 : 2,
        hoverElevation: isDark ? 0 : 3,
        highlightElevation: isDark ? 0 : 3,
        shape: const RoundedRectangleBorder(borderRadius: M3Shape.radiusLarge),
      ),
    );
  }

  static OutlineInputBorder _inputBorder(Color color, {double width = 1}) {
    return OutlineInputBorder(
      borderRadius: M3Shape.radiusLarge,
      borderSide: BorderSide(color: color, width: width),
    );
  }
}
