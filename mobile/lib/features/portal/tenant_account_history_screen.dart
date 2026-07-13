import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/models.dart';
import '../leases/lease_ledger_view.dart';
import 'tenant_portal_repository.dart';

/// Tenant-facing "Account history" — the transparent ledger for the tenant's
/// lease, with a plain-English "why" on every charge and payment.
///
/// Resolves the tenant's lease(s) from the portal snapshot. With a single
/// lease it shows the ledger directly; with multiple it shows a chooser first.
class TenantAccountHistoryScreen extends ConsumerWidget {
  const TenantAccountHistoryScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final snapshot = ref.watch(tenantPortalSnapshotProvider);

    return Scaffold(
      appBar: AppBar(title: const Text('Account history')),
      body: snapshot.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (err, _) => RefreshIndicator(
          onRefresh: () async => ref.invalidate(tenantPortalSnapshotProvider),
          child: ListView(
            padding: const EdgeInsets.all(20),
            children: [
              const SizedBox(height: 40),
              Text(
                "Couldn't load your account history.",
                textAlign: TextAlign.center,
                style: theme.textTheme.titleMedium,
              ),
              const SizedBox(height: 8),
              Text('$err', textAlign: TextAlign.center),
            ],
          ),
        ),
        data: (data) {
          final leases = data.leases;
          if (leases.isEmpty) {
            return RefreshIndicator(
              onRefresh: () async =>
                  ref.invalidate(tenantPortalSnapshotProvider),
              child: ListView(
                padding: const EdgeInsets.all(20),
                children: [
                  const SizedBox(height: 40),
                  Icon(
                    Icons.receipt_long_outlined,
                    size: 40,
                    color: theme.colorScheme.onSurfaceVariant,
                  ),
                  const SizedBox(height: 12),
                  Text(
                    'No lease on file yet.',
                    textAlign: TextAlign.center,
                    style: theme.textTheme.titleMedium,
                  ),
                ],
              ),
            );
          }

          if (leases.length == 1) {
            return LeaseLedgerView(
              leaseManagementId: leases.first.leaseManagementId,
            );
          }

          return _LeaseChooser(leases: leases);
        },
      ),
    );
  }
}

class _LeaseChooser extends StatelessWidget {
  const _LeaseChooser({required this.leases});

  final List<PortalLeaseRelationship> leases;

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        for (final lease in leases)
          Card(
            child: ListTile(
              titleAlignment: ListTileTitleAlignment.center,
              leading: const Icon(Icons.description_outlined),
              title: Text(lease.propertyName),
              subtitle: Text(
                'Unit ${lease.unitNumber}'
                '${lease.agreement == null ? '' : ' · #${lease.agreement!.agreementNumber}'}',
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => Navigator.of(context).push<void>(
                MaterialPageRoute<void>(
                  builder: (_) => Scaffold(
                    appBar: AppBar(title: Text(lease.propertyName)),
                    body: LeaseLedgerView(
                      leaseManagementId: lease.leaseManagementId,
                    ),
                  ),
                ),
              ),
            ),
          ),
      ],
    );
  }
}
