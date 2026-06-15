import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../leases/leases_list_screen.dart';
import '../properties/properties_list_screen.dart';
import '../settings/settings_screen.dart';
import '../tenants/tenants_list_screen.dart';
import 'getting_started_provider.dart';
import 'getting_started_tasks.dart';

/// The durable "Getting started" checklist — every onboarding task with its
/// plain-English explanation, a checkmark that fills in as the underlying data
/// appears, and a deep-link into the exact create flow for each incomplete step.
///
/// Mirrors the web `/get-started` checklist (`web/src/routes/(protected)/
/// get-started/+page.svelte`): completion is auto-derived from the list/settings
/// data the app already loads — there's no manual "mark done" here (mobile has
/// no per-portfolio local store wired up, so data-derived completion is the only
/// source of truth; see `getting_started_tasks.dart`).
class GettingStartedScreen extends ConsumerWidget {
  const GettingStartedScreen({super.key});

  void _openDest(BuildContext context, GettingStartedDest dest) {
    Widget? screen;
    switch (dest) {
      case GettingStartedDest.properties:
        screen = const PropertiesListScreen();
      case GettingStartedDest.tenants:
        screen = const TenantsListScreen();
      case GettingStartedDest.leases:
        screen = const LeasesListScreen();
      case GettingStartedDest.settings:
        screen = const SettingsScreen();
      case GettingStartedDest.none:
        screen = null;
    }
    if (screen == null) return;
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(builder: (_) => screen!),
    );
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final signalsAsync = ref.watch(gettingStartedSignalsProvider);

    final coreTasks =
        kGettingStartedTasks.where((t) => t.core).toList(growable: false);
    final optionalTasks =
        kGettingStartedTasks.where((t) => !t.core).toList(growable: false);

    return Scaffold(
      appBar: AppBar(title: const Text('Getting started')),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(gettingStartedSignalsProvider);
          await ref.read(gettingStartedSignalsProvider.future);
        },
        child: signalsAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (err, _) => ListView(
            padding: const EdgeInsets.all(20),
            children: [
              Text(
                "Couldn't check what's already set up.",
                style: theme.textTheme.titleMedium,
              ),
              const SizedBox(height: 8),
              Text('$err', style: theme.textTheme.bodySmall),
              const SizedBox(height: 16),
              FilledButton.tonal(
                onPressed: () => ref.invalidate(gettingStartedSignalsProvider),
                child: const Text('Try again'),
              ),
            ],
          ),
          data: (signals) {
            final progress = computeGettingStartedProgress(signals);
            return ListView(
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
              children: [
                _Intro(progress: progress),
                const SizedBox(height: 16),
                _SectionLabel(label: 'The essentials'),
                const SizedBox(height: 8),
                for (final task in coreTasks)
                  _TaskRow(
                    task: task,
                    done: task.isComplete(signals),
                    onTap: task.dest == GettingStartedDest.none
                        ? null
                        : () => _openDest(context, task.dest),
                  ),
                const SizedBox(height: 20),
                _SectionLabel(label: 'Turn on reminders (optional)'),
                const SizedBox(height: 8),
                for (final task in optionalTasks)
                  _TaskRow(
                    task: task,
                    done: task.isComplete(signals),
                    onTap: task.dest == GettingStartedDest.none
                        ? null
                        : () => _openDest(context, task.dest),
                  ),
                const SizedBox(height: 24),
                if (progress.allDone)
                  _AllDoneNote(scheme: cs)
                else
                  _ProgressFootnote(progress: progress, theme: theme),
              ],
            );
          },
        ),
      ),
    );
  }
}

/// Header: a friendly explainer plus the overall progress bar.
class _Intro extends StatelessWidget {
  const _Intro({required this.progress});

  final GettingStartedProgress progress;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final fraction = progress.totalCount == 0
        ? 0.0
        : progress.doneCount / progress.totalCount;

    return Card(
      color: cs.primaryContainer.withValues(alpha: 0.35),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Container(
                  padding: const EdgeInsets.all(10),
                  decoration: BoxDecoration(
                    color: cs.primary.withValues(alpha: 0.14),
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: Icon(Symbols.checklist_rounded,
                      color: cs.primary, size: 22, fill: 1),
                ),
                const SizedBox(width: 14),
                Expanded(
                  child: Text(
                    'Work through these to get up and running. Tap any step and '
                    "we'll take you to the right spot. Steps check themselves "
                    'off as you go.',
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: cs.onSurfaceVariant,
                    ),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 16),
            Row(
              children: [
                Text(
                  progress.allDone
                      ? "You're all set!"
                      : '${progress.doneCount} of ${progress.totalCount} done',
                  style: theme.textTheme.titleSmall?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
                ),
                const Spacer(),
                if (progress.allCoreDone && !progress.allDone)
                  Text(
                    'Core setup complete',
                    style: theme.textTheme.labelMedium?.copyWith(
                      color: cs.primary,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
              ],
            ),
            const SizedBox(height: 8),
            ClipRRect(
              borderRadius: BorderRadius.circular(8),
              child: LinearProgressIndicator(
                value: fraction,
                minHeight: 8,
                backgroundColor: cs.surfaceContainerHighest,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _SectionLabel extends StatelessWidget {
  const _SectionLabel({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 4),
      child: Text(
        label.toUpperCase(),
        style: theme.textTheme.labelMedium?.copyWith(
          color: theme.colorScheme.primary,
          fontWeight: FontWeight.w700,
          letterSpacing: 0.8,
        ),
      ),
    );
  }
}

/// A single checklist row. Done → filled check + tinted surface; not done →
/// tappable row that deep-links to the create flow with a trailing chevron.
class _TaskRow extends StatelessWidget {
  const _TaskRow({
    required this.task,
    required this.done,
    required this.onTap,
  });

  final GettingStartedTask task;
  final bool done;

  /// Null when the task is informational (auto-complete, no destination).
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    final content = Padding(
      padding: const EdgeInsets.all(14),
      child: Row(
        children: [
          // Status check.
          Container(
            width: 26,
            height: 26,
            decoration: BoxDecoration(
              shape: BoxShape.circle,
              color: done ? cs.primary : Colors.transparent,
              border: Border.all(
                color: done ? cs.primary : cs.outlineVariant,
                width: 1.5,
              ),
            ),
            child: done
                ? Icon(Symbols.check_rounded,
                    size: 16, color: cs.onPrimary, fill: 1)
                : null,
          ),
          const SizedBox(width: 14),
          Icon(
            task.icon,
            size: 22,
            fill: 1,
            color: done ? cs.primary : cs.onSurfaceVariant,
          ),
          const SizedBox(width: 14),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  task.label,
                  style: theme.textTheme.titleSmall?.copyWith(
                    color: done ? cs.onSurfaceVariant : cs.onSurface,
                    decoration: done ? TextDecoration.lineThrough : null,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  task.eli5,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: cs.onSurfaceVariant,
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(width: 8),
          if (done)
            Text(
              'Done',
              style: theme.textTheme.labelMedium?.copyWith(
                color: cs.primary,
                fontWeight: FontWeight.w700,
              ),
            )
          else if (onTap != null)
            Icon(Symbols.chevron_right_rounded, color: cs.onSurfaceVariant),
        ],
      ),
    );

    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Card(
        color: done ? cs.surfaceContainerLow : null,
        child: onTap == null
            ? content
            : InkWell(
                onTap: onTap,
                borderRadius: const BorderRadius.all(Radius.circular(28)),
                child: content,
              ),
      ),
    );
  }
}

class _ProgressFootnote extends StatelessWidget {
  const _ProgressFootnote({required this.progress, required this.theme});

  final GettingStartedProgress progress;
  final ThemeData theme;

  @override
  Widget build(BuildContext context) {
    if (progress.allCoreDone) {
      return Text(
        'The essentials are done — the optional steps turn on automatic '
        'reminders so you have less to remember.',
        style: theme.textTheme.bodySmall?.copyWith(
          color: theme.colorScheme.onSurfaceVariant,
        ),
      );
    }
    return Text(
      'The first ${progress.coreTotalCount} steps are the essentials. The rest '
      'turn on automatic reminders.',
      style: theme.textTheme.bodySmall?.copyWith(
        color: theme.colorScheme.onSurfaceVariant,
      ),
    );
  }
}

class _AllDoneNote extends StatelessWidget {
  const _AllDoneNote({required this.scheme});

  final ColorScheme scheme;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      color: scheme.primaryContainer.withValues(alpha: 0.4),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(Symbols.celebration_rounded,
                color: scheme.primary, fill: 1),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                "You're all set up. Everything on the checklist is done.",
                style: theme.textTheme.bodyMedium,
              ),
            ),
          ],
        ),
      ),
    );
  }
}
