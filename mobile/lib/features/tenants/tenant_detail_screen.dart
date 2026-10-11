import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../activity/activity_history_screen.dart';
import '../leases/leases_repository.dart';
import '../notices/create_tenant_notice.dart';
import '../units/unit_command_center_screen.dart';
import '../units/unit_navigation.dart';
import 'tenants_list_screen.dart';
import 'tenants_repository.dart';
import '../../core/presentation/formatting.dart';

/// Loads a tenant by id, then shows [TenantDetailScreen]. Use this when the
/// caller only has a tenant id (e.g. a relationship's party link, or an approved
/// application that created a tenant).
class TenantDetailLoaderScreen extends ConsumerWidget {
  const TenantDetailLoaderScreen({super.key, required this.tenantId});

  final int tenantId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(tenantDetailProvider(tenantId));
    return async.when(
      loading: () =>
          const Scaffold(body: Center(child: CircularProgressIndicator())),
      error: (e, _) => Scaffold(
        appBar: AppBar(title: const Text('Tenant')),
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Text(
              e is ApiException ? e.message : e.toString(),
              textAlign: TextAlign.center,
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          ),
        ),
      ),
      data: (tenant) => TenantDetailScreen(tenant: tenant),
    );
  }
}

/// Detail screen for a single tenant.
///
/// Shows tenant info with an edit button and their canonical relationship
/// history, filtered server-side by tenant id.
class TenantDetailScreen extends ConsumerStatefulWidget {
  const TenantDetailScreen({super.key, required this.tenant});

  final Tenant tenant;

  @override
  ConsumerState<TenantDetailScreen> createState() => _TenantDetailScreenState();
}

class _TenantDetailScreenState extends ConsumerState<TenantDetailScreen> {
  late Tenant _tenant;

  @override
  void initState() {
    super.initState();
    _tenant = widget.tenant;
  }

  Future<void> _refresh() async {
    ref.invalidate(tenantsPageProvider);
    ref.invalidate(tenantsProvider);
    await Future.wait<void>([
      ref.read(tenantDetailProvider(_tenant.id).notifier).refresh(),
      Future<void>.sync(
        () => ref.invalidate(tenantLeaseManagementsProvider(_tenant.id)),
      ),
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
      builder: (_) => TenantFormSheet(existing: _tenant, onSaved: _refresh),
    );
  }

  Future<void> _createNotice() async {
    await showCreateTenantNoticeFlow(
      context,
      ref,
      recipientTenantId: _tenant.id,
      tenantName: '${_tenant.firstName} ${_tenant.lastName}'.trim(),
    );
  }

  void _showActivityHistory() {
    final name = '${_tenant.firstName} ${_tenant.lastName}'.trim();
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => ActivityHistoryScreen(
          entityType: 'Tenant',
          entityId: _tenant.id,
          title: 'Tenant activity',
          subtitle: name.isEmpty ? null : name,
        ),
      ),
    );
  }

  Future<void> _confirmDelete() async {
    final name = '${_tenant.firstName} ${_tenant.lastName}'.trim();
    final messenger = ScaffoldMessenger.of(context);
    final navigator = Navigator.of(context);
    final activeLeaseCount = _tenant.activeLeaseCount ?? 0;
    final leaseHistoryCount = _tenant.leaseHistoryCount ?? 0;
    final hasActiveLease = activeLeaseCount > 0;
    final hasLeaseHistory = leaseHistoryCount > 0;
    final canDelete = _tenant.canDelete && !hasActiveLease && !hasLeaseHistory;
    final blockedReason = _tenant.deleteBlockedReason;
    final title = canDelete
        ? 'Delete tenant?'
        : hasActiveLease
        ? 'Tenant has an active lease'
        : 'Tenant history is preserved';
    final message = canDelete
        ? (name.isEmpty ? 'Delete this tenant?' : 'Delete $name?')
        : (blockedReason != null && blockedReason.isNotEmpty)
        ? blockedReason
        : hasActiveLease
        ? 'End or reassign this tenant’s active leases before deleting them.'
        : hasLeaseHistory
        ? 'This tenant has lease history, so the record is kept for past leases and payments.'
        : 'This tenant cannot be deleted right now.';
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(title),
        content: Text(message),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: canDelete
                ? () => Navigator.of(dialogContext).pop(true)
                : null,
            child: const Text('Delete'),
          ),
        ],
      ),
    );

    if (confirmed != true || !mounted) return;

    try {
      await ref.read(tenantsRepositoryProvider).deleteTenant(_tenant.id);
      ref.invalidate(tenantDetailProvider(_tenant.id));
      ref.invalidate(tenantsPageProvider);
      ref.invalidate(tenantsProvider);
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Tenant deleted.')));
      navigator.pop();
    } on ApiException catch (e) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  void _toast(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(message)));
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final leasesAsync = ref.watch(tenantLeaseManagementsProvider(_tenant.id));

    return Scaffold(
      appBar: AppBar(
        title: Text(
          '${_tenant.firstName} ${_tenant.lastName}',
          overflow: TextOverflow.ellipsis,
        ),
        actions: [
          IconButton(
            icon: const Icon(Icons.campaign_outlined),
            tooltip: 'Create / send notice',
            onPressed: _createNotice,
          ),
          IconButton(
            icon: const Icon(Icons.history_outlined),
            tooltip: 'View tenant activity',
            onPressed: _showActivityHistory,
          ),
          IconButton(
            icon: const Icon(Icons.edit_outlined),
            tooltip: 'Edit tenant',
            onPressed: () => _showEditSheet(context),
          ),
          IconButton(
            icon: const Icon(Icons.delete_outline),
            tooltip: 'Delete tenant',
            onPressed: _confirmDelete,
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
            const SizedBox(height: 16),

            // ── Leases ─────────────────────────────────────────────────────
            Text(
              'Tenant & lease history',
              style: theme.textTheme.titleMedium?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 8),

            leasesAsync.when(
              loading: () => const Center(child: CircularProgressIndicator()),
              error: (e, _) => _InlineError(
                message: e is ApiException ? e.message : e.toString(),
              ),
              data: (leases) {
                if (leases.isEmpty) {
                  return Padding(
                    padding: const EdgeInsets.symmetric(vertical: 16),
                    child: Text(
                      'No tenancies for this person.',
                      style: TextStyle(color: colorScheme.onSurfaceVariant),
                    ),
                  );
                }
                // Open relationships first, then closed history.
                final sorted = [...leases]
                  ..sort((a, b) {
                    final aActive = a.isOpen ? 0 : 1;
                    final bActive = b.isOpen ? 0 : 1;
                    return aActive.compareTo(bActive);
                  });
                return Column(
                  children: sorted
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
                        style: theme.textTheme.titleLarge?.copyWith(
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                      if (tenant.email != null)
                        Text(
                          tenant.email!,
                          style: theme.textTheme.bodyMedium?.copyWith(
                            color: colorScheme.onSurfaceVariant,
                          ),
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
                  value: dateFmt(tenant.createdAt),
                ),
                if (tenant.activeLeaseCount != null)
                  _KeyValue(
                    label: 'Active leases',
                    value: '${tenant.activeLeaseCount}',
                  ),
                if (tenant.leaseHistoryCount != null)
                  _KeyValue(
                    label: 'Lease history',
                    value: '${tenant.leaseHistoryCount}',
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
        Text(
          label,
          style: TextStyle(fontSize: 11, color: colorScheme.onSurfaceVariant),
        ),
        Text(
          value,
          style: theme.textTheme.bodyMedium?.copyWith(
            fontWeight: FontWeight.w600,
          ),
        ),
      ],
    );
  }
}

// ── Lease summary tile (read-only) ────────────────────────────────────────────

class _LeaseSummaryTile extends StatelessWidget {
  const _LeaseSummaryTile({required this.lease});

  final LeaseManagementSummary lease;

  void _openLease(BuildContext context) {
    openUnitCommandCenter(
      context,
      unitId: lease.unitId,
      initialTab: UnitCommandCenterTab.tenantLease,
      initialView: UnitCommandCenterView.agreements,
      leaseManagementId: lease.id,
      tenantId: lease.primaryTenantId,
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    final isActive = lease.isOpen;

    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: InkWell(
        onTap: () => _openLease(context),
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      lease.propertyName,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                  ),
                  Text(
                    moneyFmt(lease.baseRentAmount ?? 0, whole: true),
                    style: theme.textTheme.bodyMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                      color: colorScheme.primary,
                    ),
                  ),
                  const Text('/mo', style: TextStyle(fontSize: 12)),
                  Icon(
                    Icons.chevron_right,
                    size: 18,
                    color: colorScheme.onSurfaceVariant,
                  ),
                ],
              ),
              const SizedBox(height: 4),
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'Unit ${lease.unitNumber}  ·  '
                      '${lease.termStartOn == null ? 'Agreement not issued' : '${dateFmt(lease.termStartOn!)} – ${lease.termEndOn == null ? 'Month-to-month' : dateFmt(lease.termEndOn!)}'}',
                      style: TextStyle(
                        fontSize: 12,
                        color: colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ),
                  if (!isActive)
                    Container(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 7,
                        vertical: 2,
                      ),
                      decoration: BoxDecoration(
                        color: colorScheme.surfaceContainerHighest,
                        borderRadius: BorderRadius.circular(12),
                      ),
                      child: Text(
                        lease.lifecycle,
                        style: TextStyle(
                          fontSize: 10,
                          fontWeight: FontWeight.w600,
                          color: colorScheme.onSurfaceVariant,
                        ),
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
