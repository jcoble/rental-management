import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../leases/leases_repository.dart';
import 'tenants_list_screen.dart';
import 'tenants_repository.dart';

const _monthNames = [
  '', 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
  'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
];

String _fmt(DateTime d) => '${_monthNames[d.month]} ${d.day}, ${d.year}';

String _formatCurrency(double amount) {
  final rounded = amount.round();
  final s = rounded.toString();
  final buf = StringBuffer(r'$');
  final start = s.length % 3;
  if (start > 0) buf.write(s.substring(0, start));
  for (var i = start; i < s.length; i += 3) {
    if (i > 0) buf.write(',');
    buf.write(s.substring(i, i + 3));
  }
  return buf.toString();
}

/// Detail screen for a single tenant.
///
/// Shows tenant info with an edit button and their active leases (read-only),
/// loaded from GET /leases filtered client-side by tenantId.
class TenantDetailScreen extends ConsumerStatefulWidget {
  const TenantDetailScreen({super.key, required this.tenant});

  final Tenant tenant;

  @override
  ConsumerState<TenantDetailScreen> createState() =>
      _TenantDetailScreenState();
}

class _TenantDetailScreenState extends ConsumerState<TenantDetailScreen> {
  late Tenant _tenant;

  @override
  void initState() {
    super.initState();
    _tenant = widget.tenant;
    Future.microtask(
      () => ref
          .read(tenantLeasesProvider(_tenant.id).notifier)
          .load(),
    );
  }

  Future<void> _refresh() async {
    await Future.wait<void>([
      ref.read(tenantDetailProvider(_tenant.id).notifier).refresh(),
      ref.read(tenantLeasesProvider(_tenant.id).notifier).refresh(),
    ]);
    final updated = ref.read(tenantDetailProvider(_tenant.id));
    updated.whenData((t) {
      if (mounted) setState(() => _tenant = t);
    });
  }

  void _showEditSheet(BuildContext context) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => TenantFormSheet(
        existing: _tenant,
        onSaved: _refresh,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final leasesAsync = ref.watch(tenantLeasesProvider(_tenant.id));

    return Scaffold(
      appBar: AppBar(
        title: Text(
          '${_tenant.firstName} ${_tenant.lastName}',
          overflow: TextOverflow.ellipsis,
        ),
        actions: [
          IconButton(
            icon: const Icon(Icons.edit_outlined),
            tooltip: 'Edit tenant',
            onPressed: () => _showEditSheet(context),
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            // ── Tenant info card ───────────────────────────────────────────
            _TenantInfoCard(
              tenant: _tenant,
              theme: theme,
              colorScheme: colorScheme,
            ),
            const SizedBox(height: 24),

            // ── Active leases ──────────────────────────────────────────────
            Text(
              'Active Leases',
              style: theme.textTheme.titleMedium
                  ?.copyWith(fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 8),

            leasesAsync.when(
              loading: () =>
                  const Center(child: CircularProgressIndicator()),
              error: (e, _) => _InlineError(
                message:
                    e is ApiException ? e.message : e.toString(),
              ),
              data: (leases) {
                final active = leases
                    .where((l) =>
                        l.status.toLowerCase() == 'active')
                    .toList();
                if (active.isEmpty) {
                  return Padding(
                    padding: const EdgeInsets.symmetric(vertical: 16),
                    child: Text(
                      'No active leases for this tenant.',
                      style: TextStyle(
                          color: colorScheme.onSurfaceVariant),
                    ),
                  );
                }
                return Column(
                  children: active
                      .map((l) => _LeaseSummaryTile(lease: l))
                      .toList(),
                );
              },
            ),

            const SizedBox(height: 32),
          ],
        ),
      ),
    );
  }
}

// ── Tenant info card ──────────────────────────────────────────────────────────

class _TenantInfoCard extends StatelessWidget {
  const _TenantInfoCard({
    required this.tenant,
    required this.theme,
    required this.colorScheme,
  });

  final Tenant tenant;
  final ThemeData theme;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                CircleAvatar(
                  radius: 26,
                  backgroundColor: colorScheme.primaryContainer,
                  child: Text(
                    _initials(tenant.firstName, tenant.lastName),
                    style: TextStyle(
                      fontWeight: FontWeight.w700,
                      fontSize: 16,
                      color: colorScheme.onPrimaryContainer,
                    ),
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        '${tenant.firstName} ${tenant.lastName}',
                        style: theme.textTheme.titleLarge
                            ?.copyWith(fontWeight: FontWeight.w700),
                      ),
                      if (tenant.email != null)
                        Text(
                          tenant.email!,
                          style: theme.textTheme.bodyMedium?.copyWith(
                              color: colorScheme.onSurfaceVariant),
                        ),
                    ],
                  ),
                ),
              ],
            ),
            const SizedBox(height: 16),
            const Divider(height: 1),
            const SizedBox(height: 12),
            Wrap(
              spacing: 24,
              runSpacing: 8,
              children: [
                if (tenant.phone != null)
                  _KeyValue(label: 'Phone', value: tenant.phone!),
                if (tenant.emergencyContact != null)
                  _KeyValue(
                    label: 'Emergency',
                    value: tenant.emergencyContact!,
                  ),
                _KeyValue(
                  label: 'Member since',
                  value: _fmt(tenant.createdAt),
                ),
                if (tenant.activeLeaseCount != null)
                  _KeyValue(
                    label: 'Active leases',
                    value: '${tenant.activeLeaseCount}',
                  ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  static String _initials(String first, String last) {
    final f = first.isNotEmpty ? first[0].toUpperCase() : '';
    final l = last.isNotEmpty ? last[0].toUpperCase() : '';
    return '$f$l';
  }
}

class _KeyValue extends StatelessWidget {
  const _KeyValue({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(label,
            style:
                TextStyle(fontSize: 11, color: colorScheme.onSurfaceVariant)),
        Text(value,
            style: theme.textTheme.bodyMedium
                ?.copyWith(fontWeight: FontWeight.w600)),
      ],
    );
  }
}

// ── Lease summary tile (read-only) ────────────────────────────────────────────

class _LeaseSummaryTile extends StatelessWidget {
  const _LeaseSummaryTile({required this.lease});

  final Lease lease;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    lease.propertyName ?? 'Lease #${lease.leaseNumber}',
                    style: theme.textTheme.bodyMedium
                        ?.copyWith(fontWeight: FontWeight.w600),
                  ),
                ),
                Text(
                  _formatCurrency(lease.monthlyRent),
                  style: theme.textTheme.bodyMedium?.copyWith(
                    fontWeight: FontWeight.w700,
                    color: colorScheme.primary,
                  ),
                ),
                const Text('/mo', style: TextStyle(fontSize: 12)),
              ],
            ),
            const SizedBox(height: 4),
            Text(
              'Unit ${lease.unitNumber ?? lease.unitId}  ·  '
              '${_fmt(lease.startDate)} – ${_fmt(lease.endDate)}',
              style: TextStyle(
                  fontSize: 12, color: colorScheme.onSurfaceVariant),
            ),
          ],
        ),
      ),
    );
  }
}

// ── Inline error ──────────────────────────────────────────────────────────────

class _InlineError extends StatelessWidget {
  const _InlineError({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 12),
      child: Row(
        children: [
          Icon(Icons.error_outline, size: 18, color: colorScheme.error),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              message,
              style: TextStyle(color: colorScheme.error, fontSize: 13),
            ),
          ),
        ],
      ),
    );
  }
}
