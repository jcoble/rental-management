import 'package:flutter/material.dart';

import '../../core/models/models.dart';
import '../../core/presentation/formatting.dart';

String formatTimelineMoment(DateTime d) {
  final h = d.hour % 12 == 0 ? 12 : d.hour % 12;
  final m = d.minute.toString().padLeft(2, '0');
  final ampm = d.hour < 12 ? 'AM' : 'PM';
  return '${dateFmt(d)} · $h:$m $ampm';
}

/// Human-readable label for a WorkOrderStatus enum value.
String workOrderStatusLabel(String s) {
  switch (s) {
    case 'InProgress':
      return 'In Progress';
    case 'WaitingParts':
      return 'Waiting Parts';
    default:
      return s;
  }
}

/// Vertical status timeline rendered newest-first (the source list arrives
/// oldest → newest). Shared by the staff and tenant work-order detail screens.
class WorkOrderTimeline extends StatelessWidget {
  const WorkOrderTimeline({super.key, required this.events});

  final List<WorkOrderStatusEvent> events;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    if (events.isEmpty) {
      return Container(
        padding: const EdgeInsets.all(16),
        decoration: BoxDecoration(
          color: cs.surfaceContainerLowest,
          borderRadius: BorderRadius.circular(12),
          border: Border.all(color: cs.outlineVariant),
        ),
        child: Row(
          children: [
            Icon(Icons.history, color: cs.onSurfaceVariant, size: 20),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                'No status history yet.',
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              ),
            ),
          ],
        ),
      );
    }

    final ordered = events.reversed.toList();

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
      decoration: BoxDecoration(
        color: cs.surfaceContainerLowest,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: cs.outlineVariant),
      ),
      child: Column(
        children: [
          for (var i = 0; i < ordered.length; i++)
            _TimelineRow(
              event: ordered[i],
              isFirst: i == 0,
              isLast: i == ordered.length - 1,
              colorScheme: cs,
              theme: theme,
            ),
        ],
      ),
    );
  }
}

class _TimelineRow extends StatelessWidget {
  const _TimelineRow({
    required this.event,
    required this.isFirst,
    required this.isLast,
    required this.colorScheme,
    required this.theme,
  });

  final WorkOrderStatusEvent event;
  final bool isFirst;
  final bool isLast;
  final ColorScheme colorScheme;
  final ThemeData theme;

  @override
  Widget build(BuildContext context) {
    final dotColor = isFirst ? colorScheme.primary : colorScheme.outlineVariant;
    final isEditEvent =
        event.fromStatus != null && event.fromStatus == event.toStatus;
    final transition = isEditEvent
        ? 'Work order updated'
        : event.fromStatus == null
        ? 'Created as ${workOrderStatusLabel(event.toStatus)}'
        : '${workOrderStatusLabel(event.fromStatus!)} → '
              '${workOrderStatusLabel(event.toStatus)}';

    return IntrinsicHeight(
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Column(
            children: [
              Container(
                width: 14,
                height: 14,
                margin: const EdgeInsets.only(top: 2),
                decoration: BoxDecoration(
                  color: dotColor,
                  shape: BoxShape.circle,
                  border: Border.all(
                    color: colorScheme.surfaceContainerLowest,
                    width: 2,
                  ),
                ),
              ),
              if (!isLast)
                Expanded(
                  child: Container(width: 2, color: colorScheme.outlineVariant),
                ),
            ],
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Padding(
              padding: EdgeInsets.only(bottom: isLast ? 0 : 18),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    transition,
                    style: theme.textTheme.bodyMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                      color: colorScheme.onSurface,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    [
                      formatTimelineMoment(event.createdAtUtc),
                      if (event.changedByLabel != null &&
                          event.changedByLabel!.isNotEmpty)
                        event.changedByLabel!,
                    ].join(' · '),
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: colorScheme.onSurfaceVariant,
                    ),
                  ),
                  if (event.note != null && event.note!.isNotEmpty) ...[
                    const SizedBox(height: 6),
                    Container(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 10,
                        vertical: 8,
                      ),
                      decoration: BoxDecoration(
                        color: colorScheme.surfaceContainerHighest,
                        borderRadius: BorderRadius.circular(8),
                      ),
                      child: Text(
                        event.note!,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: colorScheme.onSurface,
                        ),
                      ),
                    ),
                  ],
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}
