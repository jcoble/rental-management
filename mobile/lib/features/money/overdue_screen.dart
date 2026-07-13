import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../accounting/accounting_models.dart';
import '../accounting/accounting_repository.dart';
import '../payments/payments_screen.dart';
import '../units/unit_command_center_screen.dart';
import '../units/unit_command_center_tabs.dart';
import 'money_format.dart';

/// "Who's behind" — one row per canonical tenant account behind on rent, with
/// receipt, account-ledger, and reminder actions.
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
  Future<void> _openLedger(PastDueLease account) async {
    await Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => UnitCommandCenterLoaderScreen(
          unitId: account.unitId,
          initialTab: UnitCommandCenterTab.ledger,
          initialLeaseManagementId: account.leaseManagementId,
        ),
      ),
    );
  }

  Future<void> _recordReceipt(PastDueLease account) async {
    final result = await showRecordTenantReceiptSheet(
      context,
      ref,
      tenantAccountId: account.tenantAccountId,
      leaseManagementId: account.leaseManagementId,
      tenantName: account.tenantName,
      rentalLabel:
          [
                account.propertyName,
                if (account.unitNumber?.trim().isNotEmpty == true)
                  'Unit ${account.unitNumber!.trim()}',
              ]
              .whereType<String>()
              .where((value) => value.trim().isNotEmpty)
              .join(' · '),
      initialAmount: account.pastDueAmount,
    );
    if (result == null || !mounted) return;
    ref.invalidate(moneySnapshotProvider);
    await ref.read(pastDueProvider.notifier).refresh();
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(pastDueProvider);

    return Scaffold(
      appBar: AppBar(title: const Text("Who's behind")),
      body: RefreshIndicator(
        onRefresh: ref.read(pastDueProvider.notifier).refresh,
        child: state.loading && state.items.isEmpty
            ? ListView(
                physics: const AlwaysScrollableScrollPhysics(),
                children: const [
                  SizedBox(height: 120),
                  Center(child: CircularProgressIndicator()),
                ],
              )
            : state.error != null && state.items.isEmpty
            ? ListView(
                physics: const AlwaysScrollableScrollPhysics(),
                children: [
                  const SizedBox(height: 120),
                  Center(child: Text(state.error!)),
                ],
              )
            : state.items.isEmpty
            ? ListView(
                physics: const AlwaysScrollableScrollPhysics(),
                children: [
                  const SizedBox(height: 120),
                  Center(
                    child: Column(
                      children: [
                        Icon(
                          Icons.check_circle_outline,
                          size: 48,
                          color: Colors.green.shade600,
                        ),
                        const SizedBox(height: 12),
                        const Text('Everyone is current. Nice.'),
                      ],
                    ),
                  ),
                ],
              )
            : state.businessDate == null
            ? ListView(
                physics: const AlwaysScrollableScrollPhysics(),
                children: const [
                  SizedBox(height: 120),
                  Center(
                    child: Text("Couldn't read the portfolio business date."),
                  ),
                ],
              )
            : ListView.separated(
                physics: const AlwaysScrollableScrollPhysics(),
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 24),
                itemCount:
                    state.items.length +
                    1 +
                    (state.hasMore || state.loadingMore ? 1 : 0),
                separatorBuilder: (_, _) => const SizedBox(height: 8),
                itemBuilder: (_, index) {
                  if (index == 0) {
                    return _BehindSummary(
                      count: state.totalCount,
                      amount: state.totalPastDueAmount,
                    );
                  }
                  final itemIndex = index - 1;
                  if (itemIndex == state.items.length) {
                    if (state.loadingMore) {
                      return const Padding(
                        padding: EdgeInsets.all(16),
                        child: Center(child: CircularProgressIndicator()),
                      );
                    }
                    return Center(
                      child: Column(
                        children: [
                          if (state.error != null) ...[
                            Text(
                              state.error!,
                              style: TextStyle(
                                color: Theme.of(context).colorScheme.error,
                              ),
                            ),
                            const SizedBox(height: 8),
                          ],
                          OutlinedButton(
                            onPressed: ref
                                .read(pastDueProvider.notifier)
                                .loadMore,
                            child: Text(
                              'Load more (${state.items.length} of ${state.totalCount})',
                            ),
                          ),
                        ],
                      ),
                    );
                  }
                  final account = state.items[itemIndex];
                  return _OverdueLeaseCard(
                    lease: account,
                    businessDate: state.businessDate!,
                    onOpenLedger: () => _openLedger(account),
                    onRecordReceipt: () => _recordReceipt(account),
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
    required this.businessDate,
    required this.onOpenLedger,
    required this.onRecordReceipt,
  });

  final PastDueLease lease;
  final DateTime businessDate;
  final VoidCallback onOpenLedger;
  final VoidCallback onRecordReceipt;

  int get _daysLate {
    return businessDate.difference(lease.oldestDueOn).inDays;
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final days = _daysLate;
    final chargesWord = lease.overduePaymentCount == 1 ? 'charge' : 'charges';

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Container(
                  padding: const EdgeInsets.all(8),
                  decoration: BoxDecoration(
                    color: cs.errorContainer,
                    borderRadius: BorderRadius.circular(10),
                  ),
                  child: Icon(
                    Icons.person_outline,
                    color: cs.onErrorContainer,
                    size: 20,
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        lease.displayName,
                        style: theme.textTheme.titleSmall?.copyWith(
                          fontWeight: FontWeight.w700,
                        ),
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
                              '${lease.overduePaymentCount} $chargesWord',
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
              ],
            ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                FilledButton.tonalIcon(
                  onPressed: onRecordReceipt,
                  icon: const Icon(Icons.add_card_outlined, size: 18),
                  label: const Text('Record receipt'),
                ),
                OutlinedButton.icon(
                  onPressed: onOpenLedger,
                  icon: const Icon(
                    Icons.account_balance_wallet_outlined,
                    size: 18,
                  ),
                  label: const Text('Open ledger'),
                ),
                OutlinedButton.icon(
                  onPressed: () => _text(context),
                  icon: const Icon(Icons.sms_outlined, size: 18),
                  label: const Text('Text'),
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
    final uri = Uri.parse(
      phone.isEmpty ? 'sms:?body=$body' : 'sms:$phone?body=$body',
    );
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
