import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'inspection_run_screen.dart';
import 'inspections_models.dart';
import 'inspections_repository.dart';
import 'new_inspection_screen.dart';

const _months = [
  '',
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec',
];

String fmtInspectionDate(DateTime d) =>
    '${_months[d.month]} ${d.day}, ${d.year}';

/// On-site inspections list: each row shows the property/unit, type, scheduled
/// date and status. A "New inspection" flow picks a template + property/unit.
class InspectionsListScreen extends ConsumerWidget {
  const InspectionsListScreen({super.key});

  Future<void> _newInspection(BuildContext context, WidgetRef ref) async {
    final created = await Navigator.of(context).push<int>(
      MaterialPageRoute<int>(builder: (_) => const NewInspectionScreen()),
    );
    if (created == null || !context.mounted) return;
    ref.invalidate(inspectionsProvider);
    await Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => InspectionRunScreen(inspectionId: created),
      ),
    );
    ref.invalidate(inspectionsProvider);
  }

  Future<void> _openDetail(
    BuildContext context,
    WidgetRef ref,
    Inspection inspection,
  ) async {
    await Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => InspectionRunScreen(inspectionId: inspection.id),
      ),
    );
    ref.invalidate(inspectionsProvider);
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final inspectionsAsync = ref.watch(inspectionsProvider);

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Inspections')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'inspections-fab',
        primaryAction: MobileQuickAction(
          label: 'New inspection',
          icon: Icons.add,
          onPressed: () => _newInspection(context, ref),
        ),
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: RefreshIndicator(
        onRefresh: () async => ref.invalidate(inspectionsProvider),
        child: inspectionsAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(
            message: e is ApiException ? e.message : e.toString(),
            onRetry: () => ref.invalidate(inspectionsProvider),
          ),
          data: (inspections) {
            if (inspections.isEmpty) {
              return const _EmptyBody();
            }
            final sorted = List<Inspection>.from(inspections)
              ..sort((a, b) => b.scheduledFor.compareTo(a.scheduledFor));
            return ListView.separated(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 96),
              itemCount: sorted.length,
              separatorBuilder: (_, _) => const SizedBox(height: 8),
              itemBuilder: (_, i) => _InspectionCard(
                inspection: sorted[i],
                onTap: () => _openDetail(context, ref, sorted[i]),
              ),
            );
          },
        ),
      ),
    );
  }
}

class _InspectionCard extends StatelessWidget {
  const _InspectionCard({required this.inspection, required this.onTap});

  final Inspection inspection;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final i = inspection;

    final where = StringBuffer(
      i.propertyName?.isNotEmpty == true
          ? i.propertyName!
          : 'Property #${i.propertyId}',
    );
    if (i.unitNumber?.isNotEmpty == true) {
      where.write('  ·  Unit ${i.unitNumber}');
    } else if (i.unitId != null) {
      where.write('  ·  Unit #${i.unitId}');
    }

    return Card(
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(14),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: Text(
                      where.toString(),
                      style: theme.textTheme.titleSmall?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                  const SizedBox(width: 8),
                  InspectionStatusChip(status: i.status),
                ],
              ),
              const SizedBox(height: 6),
              Row(
                children: [
                  Icon(
                    Icons.fact_check_outlined,
                    size: 14,
                    color: cs.onSurfaceVariant,
                  ),
                  const SizedBox(width: 4),
                  Text(
                    friendlyInspectionType(i.type),
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: cs.onSurfaceVariant,
                    ),
                  ),
                  const SizedBox(width: 12),
                  Icon(
                    Icons.schedule_outlined,
                    size: 14,
                    color: cs.onSurfaceVariant,
                  ),
                  const SizedBox(width: 4),
                  Text(
                    fmtInspectionDate(i.scheduledFor.toLocal()),
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: cs.onSurfaceVariant,
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Coloured status pill shared across the inspections screens.
class InspectionStatusChip extends StatelessWidget {
  const InspectionStatusChip({super.key, required this.status});

  final String status;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    Color bg;
    Color fg;
    switch (status) {
      case 'Completed':
      case 'Reviewed':
        bg = cs.primaryContainer;
        fg = cs.onPrimaryContainer;
      case 'NeedsFollowUp':
        bg = cs.errorContainer;
        fg = cs.onErrorContainer;
      case 'Cancelled':
      case 'Archived':
        bg = cs.surfaceContainerHighest;
        fg = cs.onSurfaceVariant;
      default:
        bg = cs.secondaryContainer;
        fg = cs.onSecondaryContainer;
    }
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        friendlyInspectionStatus(status),
        style: TextStyle(color: fg, fontSize: 12, fontWeight: FontWeight.w600),
      ),
    );
  }
}

class _EmptyBody extends StatelessWidget {
  const _EmptyBody();

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(24, 80, 24, 24),
          child: Column(
            children: [
              Icon(
                Icons.fact_check_outlined,
                size: 40,
                color: cs.onSurfaceVariant,
              ),
              const SizedBox(height: 12),
              Text(
                'No inspections yet.',
                textAlign: TextAlign.center,
                style: TextStyle(color: cs.onSurfaceVariant),
              ),
              const SizedBox(height: 6),
              Text(
                'Tap "New inspection" to walk a unit with a checklist.',
                textAlign: TextAlign.center,
                style: Theme.of(
                  context,
                ).textTheme.bodySmall?.copyWith(color: cs.onSurfaceVariant),
              ),
            ],
          ),
        ),
      ],
    );
  }
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(24, 80, 24, 24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(Icons.error_outline, size: 40, color: cs.error),
              const SizedBox(height: 12),
              Text(
                message,
                textAlign: TextAlign.center,
                style: TextStyle(color: cs.error),
              ),
              const SizedBox(height: 16),
              FilledButton.tonal(
                onPressed: onRetry,
                child: const Text('Retry'),
              ),
            ],
          ),
        ),
      ],
    );
  }
}
