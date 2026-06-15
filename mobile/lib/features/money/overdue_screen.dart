import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_exception.dart';
import '../accounting/accounting_models.dart';
import '../accounting/accounting_repository.dart';
import '../payments/payment_detail_screen.dart';
import '../payments/payments_repository.dart';
import 'money_format.dart';

/// "Who's behind" — one row per tenant/lease behind on rent, with one-tap
/// Mark-paid and Text actions.
///
/// The rows come from `GET /accounting/past-due`, which shares the snapshot's
/// past-due definition. That guarantees the row count here equals the dashboard
/// "tenants behind" KPI — they read from the same source, so they can't drift.
class OverdueScreen extends ConsumerStatefulWidget {
  const OverdueScreen({super.key});

  @override
  ConsumerState<OverdueScreen> createState() => _OverdueScreenState();
}

class _OverdueScreenState extends ConsumerState<OverdueScreen> {
  /// Lease id currently being marked caught-up (its row shows a spinner).
  int? _busyLeaseId;

  /// Marks every past-due payment on [lease] as paid, then refreshes the list and
  /// the dashboard KPI together so the count and the rows stay in lockstep.
  ///
  /// Mark-paid is a per-payment mutation (not an aggregate), so we resolve this
  /// lease's still-owed payments and settle them. A lease has only a handful of
  /// open payments, so this is a small, bounded set — not an N+1 over the list.
  Future<void> _markLeasePaid(PastDueLease lease) async {
    if (_busyLeaseId != null) return;
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _busyLeaseId = lease.leaseId);
    try {
      final repo = ref.read(paymentsRepositoryProvider);
      final leasePayments = await repo.listPayments(leaseId: lease.leaseId);
      final today = DateTime.now().toIso8601String().split('T').first;
      final now = DateTime.now();
      final overdue = leasePayments.where((p) {
        final s = p.status.toLowerCase();
        if (s == 'paid' || s == 'waived' || s == 'refunded' || s == 'failed') {
          return false;
        }
        return s == 'late' || (p.dueDate.year > 1 && p.dueDate.isBefore(now));
      }).toList();

      for (final p in overdue) {
        await repo.markPaid(p.id, paidDate: today);
      }

      // Refresh the shared sources so the KPI count and this list update together.
      ref.invalidate(pastDueProvider);
      ref.invalidate(moneySnapshotProvider);

      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(
              overdue.length <= 1
                  ? 'Marked paid.'
                  : 'Marked ${overdue.length} payments paid.',
            ),
          ),
        );
    } on ApiException catch (e) {
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    } finally {
      if (mounted) setState(() => _busyLeaseId = null);
    }
  }

  @override
  Widget build(BuildContext context) {
    final async = ref.watch(pastDueProvider);

    return Scaffold(
      appBar: AppBar(title: const Text("Who's behind")),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(pastDueProvider);
          await ref.read(pastDueProvider.future);
        },
        child: async.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => ListView(
            children: [
              const SizedBox(height: 120),
              Center(
                child: Text(
                  e is ApiException ? e.message : "Couldn't load who's behind.",
                ),
              ),
            ],
          ),
          data: (result) {
            final behind = result.items;
            if (behind.isEmpty) {
              return ListView(
                children: [
                  const SizedBox(height: 120),
                  Center(
                    child: Column(
                      children: [
                        Icon(Icons.check_circle_outline,
                            size: 48, color: Colors.green.shade600),
                        const SizedBox(height: 12),
                        const Text('Everyone is current. Nice.'),
                      ],
                    ),
                  ),
                ],
              );
            }
            return ListView.separated(
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 24),
              // +1 for the summary header that states the headline total, so the
              // count the landlord sees on the dashboard is restated here.
              itemCount: behind.length + 1,
              separatorBuilder: (_, _) => const SizedBox(height: 8),
              itemBuilder: (_, i) {
                if (i == 0) {
                  return _BehindSummary(
                    count: result.totalCount,
                    amount: result.totalPastDueAmount,
                  );
                }
                final lease = behind[i - 1];
                return _OverdueLeaseCard(
                  lease: lease,
                  busy: _busyLeaseId == lease.leaseId,
                  onMarkPaid: () => _markLeasePaid(lease),
                );
              },
            );
          },
        ),
      ),
    );
  }
}

/// Restates the headline so the row count is explicitly tied to the KPI number.
class _BehindSummary extends StatelessWidget {
  const _BehindSummary({required this.count, required this.amount});

  final int count;
  final double amount;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final tenantWord = count == 1 ? 'tenant' : 'tenants';
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Text(
        '$count $tenantWord behind, owing ${moneyFmt(amount)}',
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w700,
          color: cs.onSurface,
        ),
      ),
    );
  }
}

class _OverdueLeaseCard extends StatelessWidget {
  const _OverdueLeaseCard({
    required this.lease,
    required this.busy,
    required this.onMarkPaid,
  });

  final PastDueLease lease;
  final bool busy;
  final VoidCallback onMarkPaid;

  int get _daysLate {
    if (lease.oldestDueDate.year <= 1) return 0;
    return DateTime.now().difference(lease.oldestDueDate).inDays;
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final days = _daysLate;
    final paymentsWord =
        lease.overduePaymentCount == 1 ? 'payment' : 'payments';

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            InkWell(
              // Tap drills into the oldest past-due payment, where the landlord
              // can view/edit it or mark it paid individually.
              onTap: lease.oldestPaymentId > 0
                  ? () => Navigator.of(context).push<void>(
                        MaterialPageRoute<void>(
                          builder: (_) =>
                              PaymentDetailScreen(paymentId: lease.oldestPaymentId),
                        ),
                      )
                  : null,
              child: Row(
                children: [
                  Container(
                    padding: const EdgeInsets.all(8),
                    decoration: BoxDecoration(
                      color: cs.errorContainer,
                      borderRadius: BorderRadius.circular(10),
                    ),
                    child: Icon(Icons.person_outline,
                        color: cs.onErrorContainer, size: 20),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          lease.displayName,
                          style: theme.textTheme.titleSmall
                              ?.copyWith(fontWeight: FontWeight.w700),
                        ),
                        Text(
                          '${moneyFmt(lease.pastDueAmount)} · '
                          '${days > 0 ? '$days days late' : 'Past due'}',
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: cs.error,
                            fontWeight: FontWeight.w600,
                          ),
                        ),
                        if (lease.overduePaymentCount > 1 ||
                            (lease.propertyName != null &&
                                lease.propertyName!.isNotEmpty))
                          Text(
                            [
                              if (lease.overduePaymentCount > 1)
                                '${lease.overduePaymentCount} $paymentsWord',
                              if (lease.propertyName != null &&
                                  lease.propertyName!.isNotEmpty)
                                lease.unitNumber != null &&
                                        lease.unitNumber!.isNotEmpty
                                    ? '${lease.propertyName} · Unit ${lease.unitNumber}'
                                    : lease.propertyName!,
                            ].join(' · '),
                            style: theme.textTheme.bodySmall?.copyWith(
                              color: cs.onSurfaceVariant,
                            ),
                          ),
                      ],
                    ),
                  ),
                  Icon(Icons.chevron_right, color: cs.onSurfaceVariant),
                ],
              ),
            ),
            const SizedBox(height: 10),
            Row(
              children: [
                Expanded(
                  child: FilledButton.tonalIcon(
                    onPressed: busy ? null : onMarkPaid,
                    icon: busy
                        ? const SizedBox(
                            width: 18,
                            height: 18,
                            child: CircularProgressIndicator(strokeWidth: 2),
                          )
                        : const Icon(Icons.check_circle_outline, size: 18),
                    label: Text(
                      lease.overduePaymentCount > 1 ? 'Mark all paid' : 'Mark paid',
                    ),
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: OutlinedButton.icon(
                    onPressed: () => _text(context),
                    icon: const Icon(Icons.sms_outlined, size: 18),
                    label: const Text('Text'),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _text(BuildContext context) async {
    final messenger = ScaffoldMessenger.of(context);
    final firstName = lease.tenantName != null && lease.tenantName!.isNotEmpty
        ? ' ${lease.tenantName!.split(' ').first}'
        : '';
    final body = Uri.encodeComponent(
      'Hi$firstName, a friendly reminder that '
      '${moneyFmt(lease.pastDueAmount)} rent is past due.',
    );
    // Pre-fill the recipient when we have a phone on file; otherwise let the
    // landlord pick the contact in their messaging app.
    final phone = lease.tenantPhone?.trim() ?? '';
    final uri = Uri.parse(phone.isEmpty ? 'sms:?body=$body' : 'sms:$phone?body=$body');
    if (await canLaunchUrl(uri)) {
      await launchUrl(uri);
    } else {
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          const SnackBar(content: Text('No messaging app available.')),
        );
    }
  }
}
