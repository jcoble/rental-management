import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../ai/ai_tab.dart';
import '../properties/properties_tab.dart';
import '../scan/scan_tab.dart';
import 'more_tab.dart';

/// Bottom-navigation app shell.
///
/// Tabs: Home · Scan · Properties · AI · More
/// Only the Home tab has real content for now; others show placeholder screens.
class HomeShell extends ConsumerStatefulWidget {
  const HomeShell({super.key});

  @override
  ConsumerState<HomeShell> createState() => _HomeShellState();
}

class _HomeShellState extends ConsumerState<HomeShell> {
  int _selectedIndex = 0;

  static const _tabs = [
    _TabItem(label: 'Home', icon: Icons.home_outlined, activeIcon: Icons.home),
    _TabItem(
      label: 'Scan',
      icon: Icons.document_scanner_outlined,
      activeIcon: Icons.document_scanner,
    ),
    _TabItem(
      label: 'Properties',
      icon: Icons.apartment_outlined,
      activeIcon: Icons.apartment,
    ),
    _TabItem(
      label: 'AI',
      icon: Icons.auto_awesome_outlined,
      activeIcon: Icons.auto_awesome,
    ),
    _TabItem(label: 'More', icon: Icons.more_horiz, activeIcon: Icons.menu),
  ];

  @override
  Widget build(BuildContext context) {
    final authState = ref.watch(authControllerProvider);
    final user = authState is AuthStateAuthenticated ? authState.user : null;

    return Scaffold(
      body: IndexedStack(
        index: _selectedIndex,
        children: [
          _HomeTab(user: user),
          const ScanTab(),
          const PropertiesTab(),
          const AiTab(),
          const MoreTab(),
        ],
      ),
      bottomNavigationBar: NavigationBar(
        selectedIndex: _selectedIndex,
        onDestinationSelected: (index) =>
            setState(() => _selectedIndex = index),
        destinations: _tabs
            .map(
              (tab) => NavigationDestination(
                icon: Icon(tab.icon),
                selectedIcon: Icon(tab.activeIcon ?? tab.icon),
                label: tab.label,
              ),
            )
            .toList(),
      ),
    );
  }
}

class _HomeTab extends ConsumerWidget {
  const _HomeTab({required this.user});

  final AuthUser? user;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Rental Command'),
        actions: [
          IconButton(
            icon: const Icon(Icons.logout_outlined),
            tooltip: 'Sign out',
            onPressed: () async {
              await ref.read(authControllerProvider.notifier).logout();
              // Router redirect handles navigation to /login.
            },
          ),
        ],
      ),
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(20),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                user != null
                    ? 'Welcome back, ${user!.displayName.isEmpty ? user!.email : user!.displayName}!'
                    : 'Welcome back!',
                style: theme.textTheme.headlineSmall?.copyWith(
                  fontWeight: FontWeight.w700,
                  color: colorScheme.onSurface,
                ),
              ),
              const SizedBox(height: 8),
              Text(
                'Your property dashboard is coming soon.',
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: colorScheme.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 32),
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Row(
                    children: [
                      Icon(
                        Icons.check_circle_outline,
                        color: colorScheme.primary,
                      ),
                      const SizedBox(width: 12),
                      Expanded(
                        child: Text(
                          'Auth foundation ready — Phase 5 features coming next.',
                          style: theme.textTheme.bodyMedium,
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _TabItem {
  const _TabItem({
    required this.label,
    required this.icon,
    this.activeIcon,
  });

  final String label;
  final IconData icon;
  final IconData? activeIcon;
}
