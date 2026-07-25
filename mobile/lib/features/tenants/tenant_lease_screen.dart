import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/files/document_opener.dart';
import '../../core/models/models.dart';
import '../money/money_format.dart';
import '../portal/tenant_account_history_screen.dart';
import '../portal/tenant_portal_repository.dart';

typedef TenantLeaseDocumentOpener =
    Future<String> Function({
      required Uint8List bytes,
      required String fileName,
      required String mimeType,
    });

final tenantLeaseDocumentOpenerProvider = Provider<TenantLeaseDocumentOpener>(
  (ref) => DocumentOpener.openBytes,
);

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

class _LeaseBody extends ConsumerWidget {
  const _LeaseBody({required this.lease});

  final PortalLeaseRelationship lease;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
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
          const SizedBox(height: 16),
          _SignedLeaseAction(lease: lease, agreement: agreement),
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

class _SignedLeaseAction extends ConsumerStatefulWidget {
  const _SignedLeaseAction({required this.lease, required this.agreement});

  final PortalLeaseRelationship lease;
  final PortalLeaseAgreement agreement;

  @override
  ConsumerState<_SignedLeaseAction> createState() => _SignedLeaseActionState();
}

class _SignedLeaseActionState extends ConsumerState<_SignedLeaseAction> {
  bool _opening = false;

  Future<void> _openSignedLease() async {
    final fileName = widget.agreement.executedDocumentFileName;
    final contentType = widget.agreement.executedDocumentContentType;
    if (!widget.agreement.executedDocumentAvailable ||
        fileName == null ||
        fileName.isEmpty ||
        contentType == null ||
        contentType.isEmpty) {
      _showMessage(
        'Signed lease PDF is not available yet. Contact management.',
      );
      return;
    }

    setState(() => _opening = true);
    try {
      final document = await ref
          .read(tenantPortalRepositoryProvider)
          .executedAgreementDocument(
            leaseManagementId: widget.lease.leaseManagementId,
            leaseAgreementId: widget.agreement.id,
            fileName: fileName,
            contentType: contentType,
          );
      await ref.read(tenantLeaseDocumentOpenerProvider)(
        bytes: document.bytes,
        fileName: document.fileName,
        mimeType: document.contentType,
      );
    } catch (_) {
      _showMessage(
        'Could not open the signed lease PDF. Please try again or contact management.',
      );
    } finally {
      if (mounted) setState(() => _opening = false);
    }
  }

  void _showMessage(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(
      context,
    ).showSnackBar(SnackBar(content: Text(message)));
  }

  @override
  Widget build(BuildContext context) {
    if (!widget.agreement.executedDocumentAvailable) {
      return const Padding(
        padding: EdgeInsets.only(top: 4),
        child: Text(
          'Signed lease PDF is not available yet. Contact management.',
        ),
      );
    }

    return FilledButton.tonalIcon(
      onPressed: _opening ? null : _openSignedLease,
      icon: const Icon(Icons.picture_as_pdf_outlined),
      label: Text(
        _opening
            ? 'Opening signed lease PDF'
            : 'View/download signed lease PDF',
      ),
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
