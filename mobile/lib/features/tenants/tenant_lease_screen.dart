import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/models.dart';
import '../money/money_format.dart';
import '../portal/tenant_account_history_screen.dart';
import '../portal/tenant_portal_repository.dart';

/// Read-only lease detail for the signed-in tenant: terms + a link to the full
/// account ledger. Sourced from the tenant portal snapshot (`/portal/leases`)
/// — no landlord endpoints are touched.
class TenantLeaseScreen extends ConsumerWidget {
  const TenantLeaseScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final snapshot = ref.watch(tenantPortalSnapshotProvider);

    return Scaffold(
      appBar: AppBar(title: const Text('My lease')),
      body: snapshot.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => Center(
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Text(
              'Could not load your lease.\n$e',
              textAlign: TextAlign.center,
            ),
          ),
        ),
        data: (data) {
          final lease = data.leases.firstOrNull;
          if (lease == null) {
            return const Center(
              child: Padding(
                padding: EdgeInsets.all(24),
                child: Text('No active lease on file.'),
              ),
            );
          }
          return _LeaseBody(lease: lease);
        },
      ),
    );
  }
}

class _LeaseBody extends StatelessWidget {
  const _LeaseBody({required this.lease});

  final PortalLeaseRelationship lease;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return ListView(
      padding: const EdgeInsets.fromLTRB(20, 20, 20, 32),
      children: [
        Text(
          lease.propertyName,
          style: theme.textTheme.headlineSmall?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        Text('Unit ${lease.unitNumber}', style: theme.textTheme.bodyMedium),
        const SizedBox(height: 20),
        _Row(label: 'Relationship', value: lease.lifecycle),
        if (lease.agreement case final agreement?) ...[
          _Row(label: 'Status', value: agreement.status),
          _Row(label: 'Agreement #', value: agreement.agreementNumber),
          _Row(
            label: 'Term',
            value:
                '${dateFmt(agreement.termStartOn)} – '
                '${agreement.termEndOn == null ? 'Month-to-month' : dateFmt(agreement.termEndOn!)}',
          ),
          _Row(
            label: 'Monthly rent',
            value: moneyFmt(agreement.baseRentAmount),
          ),
          _Row(label: 'Rent due day', value: 'Day ${agreement.rentDueDay}'),
          _Row(
            label: 'Deposit',
            value: moneyFmt(agreement.securityDepositObligation),
          ),
          if (agreement.lateFeeAmount > 0)
            _Row(label: 'Late fee', value: moneyFmt(agreement.lateFeeAmount)),
        ] else
          const _Row(label: 'Agreement', value: 'No agreement on file'),
        const SizedBox(height: 24),
        FilledButton.tonalIcon(
          onPressed: () => Navigator.of(context).push<void>(
            MaterialPageRoute<void>(
              builder: (_) => const TenantAccountHistoryScreen(),
            ),
          ),
          icon: const Icon(Icons.receipt_long_outlined),
          label: const Text('View account history'),
        ),
      ],
    );
  }
}

class _Row extends StatelessWidget {
  const _Row({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 110,
            child: Text(
              label,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: cs.onSurfaceVariant,
              ),
            ),
          ),
          Expanded(
            child: Text(
              value,
              style: theme.textTheme.bodyMedium?.copyWith(
                fontWeight: FontWeight.w600,
              ),
            ),
          ),
        ],
      ),
    );
  }
}
