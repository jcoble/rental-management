import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/realtime/realtime_providers.dart';
import '../ai/ai_models.dart';
import '../ai/ai_repository.dart';
import '../ai/ai_tab.dart';
import '../properties/properties_tab.dart';
import '../scan/scan_tab.dart';
import 'more_tab.dart';

// ---------------------------------------------------------------------------
// Briefing provider (home-tab only, autoDispose)
// ---------------------------------------------------------------------------

final _briefingProvider = FutureProvider.autoDispose<BriefingResponse>((ref) {
  return ref.watch(aiRepositoryProvider).briefing();
});

// ---------------------------------------------------------------------------
// HomeShell
// ---------------------------------------------------------------------------

/// Bottom-navigation app shell.
///
/// Tabs: Home · Scan · Properties · AI · More
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
  void initState() {
    super.initState();
    // Initialise the realtime watcher so it stays alive for the shell lifetime.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) ref.read(realtimeWatcherProvider);
    });
  }

  @override
  Widget build(BuildContext context) {
    // Keep the watcher alive while the shell is in the tree.
    ref.watch(realtimeWatcherProvider);

    final authState = ref.watch(authControllerProvider);
    final user = authState is AuthStateAuthenticated ? authState.user : null;

    return Scaffold(
      body: IndexedStack(
        index: _selectedIndex,
        children: [
          _HomeTab(
            user: user,
            onSwitchToTab: (index) => setState(() => _selectedIndex = index),
          ),
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

// ---------------------------------------------------------------------------
// _HomeTab — the actual dashboard
// ---------------------------------------------------------------------------

class _HomeTab extends ConsumerWidget {
  const _HomeTab({
    required this.user,
    required this.onSwitchToTab,
  });

  final AuthUser? user;

  /// Callback to switch the shell's active tab (0-based index).
  final void Function(int index) onSwitchToTab;

  // Tab indices
  static const _scanTabIndex = 1;
  static const _aiTabIndex = 3;

  String get _greeting {
    final hour = DateTime.now().hour;
    if (hour < 12) return 'Good morning';
    if (hour < 17) return 'Good afternoon';
    return 'Good evening';
  }

  String get _displayName {
    if (user == null) return '';
    final name = user!.displayName.trim();
    if (name.isEmpty) return user!.email;
    // First name only keeps it friendly.
    return name.split(' ').first;
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final briefingAsync = ref.watch(_briefingProvider);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Rental Command'),
        actions: [
          IconButton(
            icon: const Icon(Icons.logout_outlined),
            tooltip: 'Sign out',
            onPressed: () async {
              await ref.read(authControllerProvider.notifier).logout();
            },
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: () async => ref.invalidate(_briefingProvider),
        child: CustomScrollView(
          physics: const AlwaysScrollableScrollPhysics(),
          slivers: [
            SliverPadding(
              padding: const EdgeInsets.fromLTRB(20, 24, 20, 0),
              sliver: SliverToBoxAdapter(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    // ── Greeting ──────────────────────────────────────────
                    Text(
                      '$_greeting${_displayName.isNotEmpty ? ", $_displayName" : ""}!',
                      style: theme.textTheme.headlineSmall?.copyWith(
                        fontWeight: FontWeight.w700,
                        color: cs.onSurface,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      _formattedDate(),
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color: cs.onSurfaceVariant,
                      ),
                    ),
                    const SizedBox(height: 28),

                    // ── Quick actions ─────────────────────────────────────
                    _QuickActions(
                      onScan: () => onSwitchToTab(_scanTabIndex),
                      onAskAi: () => onSwitchToTab(_aiTabIndex),
                      onAddExpense: () => onSwitchToTab(_scanTabIndex),
                    ),
                    const SizedBox(height: 32),

                    // ── Today section header ──────────────────────────────
                    Text(
                      'Today',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                        color: cs.onSurface,
                      ),
                    ),
                    const SizedBox(height: 12),
                  ],
                ),
              ),
            ),

            // ── Briefing content ──────────────────────────────────────────
            SliverPadding(
              padding: const EdgeInsets.fromLTRB(20, 0, 20, 32),
              sliver: briefingAsync.when(
                loading: () => const SliverToBoxAdapter(
                  child: Center(
                    child: Padding(
                      padding: EdgeInsets.symmetric(vertical: 32),
                      child: CircularProgressIndicator(),
                    ),
                  ),
                ),
                error: (e, _) => SliverToBoxAdapter(
                  child: _BriefingError(
                    onRetry: () => ref.invalidate(_briefingProvider),
                  ),
                ),
                data: (briefing) => _BriefingContent(briefing: briefing),
              ),
            ),
          ],
        ),
      ),
    );
  }

  String _formattedDate() {
    final now = DateTime.now();
    const months = [
      'January', 'February', 'March', 'April', 'May', 'June',
      'July', 'August', 'September', 'October', 'November', 'December',
    ];
    const weekdays = [
      'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday',
      'Saturday', 'Sunday',
    ];
    final weekday = weekdays[now.weekday - 1];
    final month = months[now.month - 1];
    return '$weekday, $month ${now.day}';
  }
}

// ---------------------------------------------------------------------------
// Quick-action buttons
// ---------------------------------------------------------------------------

class _QuickActions extends StatelessWidget {
  const _QuickActions({
    required this.onScan,
    required this.onAskAi,
    required this.onAddExpense,
  });

  final VoidCallback onScan;
  final VoidCallback onAskAi;
  final VoidCallback onAddExpense;

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Expanded(
          child: _QuickActionButton(
            icon: Icons.document_scanner_outlined,
            label: 'Scan a\ndocument',
            onTap: onScan,
          ),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: _QuickActionButton(
            icon: Icons.auto_awesome_outlined,
            label: 'Ask\nAI',
            onTap: onAskAi,
          ),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: _QuickActionButton(
            icon: Icons.receipt_long_outlined,
            label: 'Add\nexpense',
            onTap: onAddExpense,
          ),
        ),
      ],
    );
  }
}

class _QuickActionButton extends StatelessWidget {
  const _QuickActionButton({
    required this.icon,
    required this.label,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Material(
      color: cs.surfaceContainerHighest,
      borderRadius: BorderRadius.circular(14),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(14),
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 14, horizontal: 8),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(icon, color: cs.primary, size: 26),
              const SizedBox(height: 6),
              Text(
                label,
                textAlign: TextAlign.center,
                style: TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w600,
                  color: cs.onSurface,
                  height: 1.25,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// Briefing content
// ---------------------------------------------------------------------------

class _BriefingContent extends StatelessWidget {
  const _BriefingContent({required this.briefing});

  final BriefingResponse briefing;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final bullets = briefing.bullets.take(5).toList();

    if (briefing.summary == null && bullets.isEmpty) {
      return SliverToBoxAdapter(child: _AllClearCard());
    }

    return SliverList(
      delegate: SliverChildListDelegate([
        // Optional AI-composed summary
        if (briefing.summary != null && briefing.summary!.isNotEmpty) ...[
          Card(
            child: Padding(
              padding: const EdgeInsets.all(14),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Icon(
                    Icons.auto_awesome,
                    color: cs.primary,
                    size: 18,
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      briefing.summary!,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color: cs.onSurface,
                        height: 1.5,
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(height: 10),
        ],

        // Bullet items
        if (bullets.isEmpty) _AllClearCard(),

        for (final bullet in bullets) ...[
          _BulletRow(bullet: bullet),
          const SizedBox(height: 8),
        ],
      ]),
    );
  }
}

class _AllClearCard extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(Icons.check_circle_outline, color: Colors.green.shade600, size: 22),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                'All clear — nothing urgent today.',
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: cs.onSurface,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _BulletRow extends StatelessWidget {
  const _BulletRow({required this.bullet});

  final BriefingBullet bullet;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final (iconData, iconColor, bgColor) = _severityStyle(bullet.severity, cs);

    return Card(
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Container(
              padding: const EdgeInsets.all(6),
              decoration: BoxDecoration(
                color: bgColor,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Icon(iconData, color: iconColor, size: 16),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    bullet.title,
                    style: theme.textTheme.bodyMedium?.copyWith(
                      fontWeight: FontWeight.w600,
                      color: cs.onSurface,
                    ),
                  ),
                  if (bullet.detail.isNotEmpty) ...[
                    const SizedBox(height: 2),
                    Text(
                      bullet.detail,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: cs.onSurfaceVariant,
                        height: 1.4,
                      ),
                    ),
                  ],
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  (IconData, Color, Color) _severityStyle(
      BulletSeverity severity, ColorScheme cs) {
    switch (severity) {
      case BulletSeverity.critical:
        return (
          Icons.warning_rounded,
          cs.error,
          cs.errorContainer,
        );
      case BulletSeverity.warning:
        return (
          Icons.info_outline,
          Colors.amber.shade700,
          Colors.amber.shade100,
        );
      case BulletSeverity.info:
        return (
          Icons.info_outline,
          cs.primary,
          cs.primaryContainer,
        );
    }
  }
}

// ---------------------------------------------------------------------------
// Error state
// ---------------------------------------------------------------------------

class _BriefingError extends StatelessWidget {
  const _BriefingError({required this.onRetry});

  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(Icons.wifi_off_outlined, color: cs.error, size: 22),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                "Couldn't load today's briefing.",
                style: Theme.of(context).textTheme.bodyMedium,
              ),
            ),
            TextButton(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _TabItem helper
// ---------------------------------------------------------------------------

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
