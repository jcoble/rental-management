import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'properties_list_screen.dart';

/// Self-contained Properties tab with its own internal Navigator so that
/// tapping into a property detail doesn't replace the shell's content.
///
/// Mount this widget at index 2 of the [HomeShell] IndexedStack.
class PropertiesTab extends ConsumerWidget {
  const PropertiesTab({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Navigator(
      onGenerateRoute: (_) => MaterialPageRoute<void>(
        builder: (_) => const PropertiesListScreen(),
      ),
    );
  }
}
