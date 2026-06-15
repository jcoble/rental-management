import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'briefing_screen.dart';
import 'qa_screen.dart';

/// Self-contained AI tab: a top tab bar switching between
/// **Briefing** and **Ask** panels.
///
/// Mount this widget as the body of a [Scaffold] in the navigation shell.
class AiTab extends ConsumerStatefulWidget {
  const AiTab({super.key});

  @override
  ConsumerState<AiTab> createState() => _AiTabState();
}

class _AiTabState extends ConsumerState<AiTab>
    with SingleTickerProviderStateMixin {
  late final TabController _tabController;

  @override
  void initState() {
    super.initState();
    _tabController = TabController(length: 2, vsync: this);
  }

  @override
  void dispose() {
    _tabController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              Icons.auto_awesome,
              size: 18,
              color: theme.colorScheme.primary,
            ),
            const SizedBox(width: 6),
            const Text('Assistant'),
          ],
        ),
        bottom: TabBar(
          controller: _tabController,
          tabs: const [
            Tab(text: 'Briefing'),
            Tab(text: 'Ask'),
          ],
        ),
      ),
      body: TabBarView(
        controller: _tabController,
        children: const [
          BriefingScreen(),
          QaScreen(),
        ],
      ),
    );
  }
}
