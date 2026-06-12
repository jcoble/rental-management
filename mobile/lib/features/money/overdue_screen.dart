import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../payments/payment_detail_screen.dart';
import '../payments/payments_repository.dart';
import 'money_format.dart';

/// "Who's behind" — every tenant with a past-due, unpaid payment, with one-tap
/// Mark-paid and Text actions.
class OverdueScreen extends ConsumerStatefulWidget {
  const OverdueScreen({super.key});

  @override
  ConsumerState<OverdueScreen> createState() => _OverdueScreenState();
}

class _OverdueScreenState extends ConsumerState<OverdueScreen> {
  static const _settled = {'paid', 'waived', 'refunded', 'cancelled'};

  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(paymentsProvider.notifier).load());
  }

  bool _isOverdue(Payment p) {
    if (_settled.contains(p.status.toLowerCase())) return false;
    final lower = p.status.toLowerCase();
    if (lower == 'late' || lower == 'overdue') return true;
    // Scheduled/Partial with a due date in the past also counts as behind.
    return p.dueDate.year > 1 &&
        p.dueDate.isBefore(DateTime.now().subtract(const Duration(days: 1)));
  }

  @override
  Widget build(BuildContext context) {
    final async = ref.watch(paymentsProvider);

    return Scaffold(
      appBar: AppBar(title: const Text("Who's behind")),
      body: RefreshIndicator(
        onRefresh: () => ref.read(paymentsProvider.notifier).refresh(),
        child: async.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => ListView(
            children: [
              const SizedBox(height: 120),
              Center(
                child: Text(
                  e is ApiException ? e.message : "Couldn't load payments.",
                ),
              ),
            ],
          ),
          data: (payments) {
            final overdue = payments.where(_isOverdue).toList()
              ..sort((a, b) => a.dueDate.compareTo(b.dueDate));
            if (overdue.isEmpty) {
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
              itemCount: overdue.length,
              separatorBuilder: (_, _) => const SizedBox(height: 8),
              itemBuilder: (_, i) => _OverdueCard(
                payment: overdue[i],
                onMarkPaid: () =>
                    ref.read(paymentsProvider.notifier).markPaid(overdue[i].id),
              ),
            );
          },
        ),
      ),
    );
  }
}

class _OverdueCard extends StatelessWidget {
  const _OverdueCard({required this.payment, required this.onMarkPaid});

  final Payment payment;
  final VoidCallback onMarkPaid;

  int get _daysLate {
    if (payment.dueDate.year <= 1) return 0;
    return DateTime.now().difference(payment.dueDate).inDays;
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final days = _daysLate;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            InkWell(
              onTap: () => Navigator.of(context).push<void>(
                MaterialPageRoute<void>(
                  builder: (_) => PaymentDetailScreen(paymentId: payment.id),
                ),
              ),
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
                          payment.tenantName ??
                              (payment.leaseNumber != null
                                  ? 'Lease ${payment.leaseNumber}'
                                  : 'Payment #${payment.id}'),
                          style: theme.textTheme.titleSmall
                              ?.copyWith(fontWeight: FontWeight.w700),
                        ),
                        Text(
                          '${moneyFmt(payment.amount)} · '
                          '${days > 0 ? '$days days late' : 'Past due'}',
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: cs.error,
                            fontWeight: FontWeight.w600,
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
                    onPressed: onMarkPaid,
                    icon: const Icon(Icons.check_circle_outline, size: 18),
                    label: const Text('Mark paid'),
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
    // No phone number is projected onto the payment row yet; open the SMS
    // composer with a prefilled reminder so the landlord can pick the contact.
    final body = Uri.encodeComponent(
      'Hi${payment.tenantName != null ? ' ${payment.tenantName!.split(' ').first}' : ''}, '
      'a friendly reminder that ${moneyFmt(payment.amount)} rent is past due.',
    );
    final uri = Uri.parse('sms:?body=$body');
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
