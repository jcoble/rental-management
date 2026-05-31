import 'package:flutter/material.dart';

import 'scan_list_screen.dart';

/// Entry-point widget for the Scan tab.
///
/// Wraps [ScanListScreen] in its own [Navigator] so that the scan-capture and
/// review screens push within the tab without affecting the root navigator.
///
/// Mount this in [HomeShell] (index 1 of the IndexedStack):
///
/// ```dart
/// // In _HomeShellState.build:
/// const ScanTab(),   // replace _PlaceholderTab(label: 'Scan')
/// ```
class ScanTab extends StatelessWidget {
  const ScanTab({super.key});

  @override
  Widget build(BuildContext context) {
    return const Navigator(
      onGenerateRoute: _generateRoute,
    );
  }

  static Route<dynamic> _generateRoute(RouteSettings settings) {
    return MaterialPageRoute<void>(
      settings: settings,
      builder: (_) => const ScanListScreen(),
    );
  }
}
