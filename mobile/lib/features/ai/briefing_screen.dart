import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'ai_models.dart';
import 'ai_repository.dart';

// ── Provider ─────────────────────────────────────────────────────────────────

final _briefingProvider =
    FutureProvider.autoDispose<BriefingResponse>((ref) async {
  return ref.watch(aiRepositoryProvider).briefing();
});

// ── Screen ────────────────────────────────────────────────────────────────────

/// Daily Briefing screen.
///
/// Shows the date, optional AI summary, and severity-coloured bullets.
/// Supports pull-to-refresh.
class BriefingScreen extends ConsumerWidget {
  const BriefingScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final briefingAsync = ref.watch(_briefingProvider);

    return RefreshIndicator(
      onRefresh: () async {
        ref.invalidate(_briefingProvider);
        // Await the next value so the indicator stays visible until done.
        await ref.read(_briefingProvider.future);
      },
      child: briefingAsync.when(
        loading: () => const _LoadingView(),
        error: (e, _) => _ErrorView(message: e.toString()),
        data: (b) => _BriefingBody(briefing: b),
      ),
    );
  }
}

// ── Body ──────────────────────────────────────────────────────────────────────

class _BriefingBody extends StatelessWidget {
  const _BriefingBody({required this.briefing});

  final BriefingResponse briefing;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return CustomScrollView(
      physics: const AlwaysScrollableScrollPhysics(),
      slivers: [
        SliverPadding(
          padding: const EdgeInsets.fromLTRB(16, 16, 16, 0),
          sliver: SliverToBoxAdapter(
            child: _DateHeader(date: briefing.date),
          ),
        ),
        if (briefing.summary != null && briefing.summary!.isNotEmpty)
          SliverPadding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 0),
            sliver: SliverToBoxAdapter(
              child: _SummaryCard(
                summary: briefing.summary!,
                llmEnhanced: briefing.llmEnhanced,
                theme: theme,
              ),
            ),
          ),
        briefing.bullets.isEmpty
            ? const SliverFillRemaining(
                hasScrollBody: false,
                child: _EmptyState(),
              )
            : SliverPadding(
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 24),
                sliver: SliverList.separated(
                  itemCount: briefing.bullets.length,
                  separatorBuilder: (context, i) => const SizedBox(height: 8),
                  itemBuilder: (context, i) =>
                      _BulletTile(bullet: briefing.bullets[i]),
                ),
              ),
      ],
    );
  }
}

// ── Sub-widgets ───────────────────────────────────────────────────────────────

class _DateHeader extends StatelessWidget {
  const _DateHeader({required this.date});

  final String date;

  String _format(String raw) {
    final dt = DateTime.tryParse(raw);
    if (dt == null) return raw;
    const months = [
      'January',
      'February',
      'March',
      'April',
      'May',
      'June',
      'July',
      'August',
      'September',
      'October',
      'November',
      'December',
    ];
    const weekdays = [
      'Monday',
      'Tuesday',
      'Wednesday',
      'Thursday',
      'Friday',
      'Saturday',
      'Sunday',
    ];
    // DateTime.weekday: 1=Mon, 7=Sun
    final weekday = weekdays[dt.weekday - 1];
    return '$weekday, ${months[dt.month - 1]} ${dt.day}';
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Text(
      _format(date),
      style: theme.textTheme.titleMedium?.copyWith(
        fontWeight: FontWeight.w600,
        color: theme.colorScheme.onSurfaceVariant,
      ),
    );
  }
}

class _SummaryCard extends StatelessWidget {
  const _SummaryCard({
    required this.summary,
    required this.llmEnhanced,
    required this.theme,
  });

  final String summary;
  final bool llmEnhanced;
  final ThemeData theme;

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: BoxDecoration(
        color: theme.colorScheme.surfaceContainerHighest.withValues(alpha: 0.4),
        borderRadius: BorderRadius.circular(10),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(summary, style: theme.textTheme.bodyMedium),
          if (llmEnhanced) ...[
            const SizedBox(height: 4),
            Row(
              children: [
                Icon(
                  Icons.auto_awesome,
                  size: 12,
                  color: theme.colorScheme.onSurfaceVariant,
                ),
                const SizedBox(width: 4),
                Text(
                  'AI summary',
                  style: theme.textTheme.labelSmall?.copyWith(
                    color: theme.colorScheme.onSurfaceVariant,
                  ),
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

class _BulletTile extends StatelessWidget {
  const _BulletTile({required this.bullet});

  final BriefingBullet bullet;

  Color _borderColor(BuildContext context) {
    switch (bullet.severity) {
      case BulletSeverity.critical:
        return Colors.red.shade500;
      case BulletSeverity.warning:
        return Colors.amber.shade600;
      case BulletSeverity.info:
        return Theme.of(context).colorScheme.primary.withValues(alpha: 0.7);
    }
  }

  Color _badgeBackground(BuildContext context) {
    switch (bullet.severity) {
      case BulletSeverity.critical:
        return Colors.red.shade50;
      case BulletSeverity.warning:
        return Colors.amber.shade50;
      case BulletSeverity.info:
        return Theme.of(context).colorScheme.primaryContainer.withValues(alpha: 0.4);
    }
  }

  Color _badgeText(BuildContext context) {
    switch (bullet.severity) {
      case BulletSeverity.critical:
        return Colors.red.shade700;
      case BulletSeverity.warning:
        return Colors.amber.shade800;
      case BulletSeverity.info:
        return Theme.of(context).colorScheme.primary;
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Container(
      decoration: BoxDecoration(
        color: theme.colorScheme.surface,
        borderRadius: BorderRadius.circular(10),
        border: Border(
          left: BorderSide(color: _borderColor(context), width: 4),
          top: BorderSide(
            color: theme.colorScheme.outlineVariant,
            width: 0.5,
          ),
          right: BorderSide(
            color: theme.colorScheme.outlineVariant,
            width: 0.5,
          ),
          bottom: BorderSide(
            color: theme.colorScheme.outlineVariant,
            width: 0.5,
          ),
        ),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 11),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Text(
                  bullet.title,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ),
              const SizedBox(width: 8),
              Container(
                padding:
                    const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                decoration: BoxDecoration(
                  color: _badgeBackground(context),
                  borderRadius: BorderRadius.circular(20),
                ),
                child: Text(
                  bullet.severity.name,
                  style: theme.textTheme.labelSmall?.copyWith(
                    color: _badgeText(context),
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ),
            ],
          ),
          if (bullet.detail.isNotEmpty) ...[
            const SizedBox(height: 3),
            Text(
              bullet.detail,
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _EmptyState extends StatelessWidget {
  const _EmptyState();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              Icons.check_circle_outline,
              size: 48,
              color: Colors.green.shade500,
            ),
            const SizedBox(height: 12),
            Text(
              'All clear — nothing needs attention today.',
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyMedium?.copyWith(
                fontWeight: FontWeight.w600,
                color: Colors.green.shade700,
              ),
            ),
            const SizedBox(height: 6),
            Text(
              'Check back tomorrow for your next briefing.',
              textAlign: TextAlign.center,
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _LoadingView extends StatelessWidget {
  const _LoadingView();

  @override
  Widget build(BuildContext context) {
    return const Center(child: CircularProgressIndicator());
  }
}

class _ErrorView extends StatelessWidget {
  const _ErrorView({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return ListView(
      // Wrapped in ListView so pull-to-refresh still works.
      physics: const AlwaysScrollableScrollPhysics(),
      children: [
        Padding(
          padding: const EdgeInsets.all(32),
          child: Column(
            children: [
              const SizedBox(height: 40),
              Icon(
                Icons.cloud_off_outlined,
                size: 48,
                color: theme.colorScheme.error,
              ),
              const SizedBox(height: 12),
              Text(
                'Could not load briefing.',
                style: theme.textTheme.bodyMedium?.copyWith(
                  fontWeight: FontWeight.w600,
                ),
              ),
              const SizedBox(height: 4),
              Text(
                message,
                textAlign: TextAlign.center,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 8),
              Text(
                'Pull down to try again.',
                style: theme.textTheme.labelSmall?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }
}
