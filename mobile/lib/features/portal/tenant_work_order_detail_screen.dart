import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../maintenance/work_order_timeline.dart';
import 'tenant_portal_repository.dart';

/// Read-only maintenance-request detail for tenants: shows the request summary
/// plus its live status timeline (Received → … → Done) so they can follow
/// progress without messaging the landlord.
class TenantWorkOrderDetailScreen extends ConsumerWidget {
  const TenantWorkOrderDetailScreen({super.key, required this.workOrderId});

  final int workOrderId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final detailAsync =
        ref.watch(tenantWorkOrderDetailProvider(workOrderId));
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(title: const Text('Request status')),
      body: RefreshIndicator(
        onRefresh: () async =>
            ref.invalidate(tenantWorkOrderDetailProvider(workOrderId)),
        child: detailAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => ListView(
            padding: const EdgeInsets.all(20),
            children: [
              Icon(Icons.error_outline, color: cs.error, size: 40),
              const SizedBox(height: 12),
              Text(
                e is ApiException ? e.message : 'Could not load this request.',
                style: TextStyle(color: cs.error),
              ),
              const SizedBox(height: 16),
              FilledButton.tonal(
                onPressed: () =>
                    ref.invalidate(tenantWorkOrderDetailProvider(workOrderId)),
                child: const Text('Retry'),
              ),
            ],
          ),
          data: (detail) {
            final wo = detail.workOrder;
            return ListView(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.all(20),
              children: [
                Text(
                  wo.title,
                  style: theme.textTheme.headlineSmall
                      ?.copyWith(fontWeight: FontWeight.w700),
                ),
                const SizedBox(height: 8),
                Container(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
                  decoration: BoxDecoration(
                    color: cs.secondaryContainer,
                    borderRadius: BorderRadius.circular(20),
                  ),
                  child: Text(
                    workOrderStatusLabel(wo.status),
                    style: TextStyle(
                      fontSize: 12,
                      fontWeight: FontWeight.w600,
                      color: cs.onSecondaryContainer,
                    ),
                  ),
                ),
                if (wo.description.isNotEmpty) ...[
                  const SizedBox(height: 16),
                  Text(wo.description, style: theme.textTheme.bodyMedium),
                ],
                const SizedBox(height: 24),
                Text(
                  'Progress',
                  style: theme.textTheme.labelLarge?.copyWith(
                    fontWeight: FontWeight.w700,
                    color: cs.onSurfaceVariant,
                    letterSpacing: 0.5,
                  ),
                ),
                const SizedBox(height: 8),
                WorkOrderTimeline(events: detail.timeline),
              ],
            );
          },
        ),
      ),
    );
  }
}
