import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/files/document_opener.dart';
import '../../core/models/lease.dart';
import '../tenants/tenant_detail_screen.dart';
import 'lease_ledger_view.dart';
import 'leases_repository.dart';

class LeaseManagementDetailLoaderScreen extends StatelessWidget {
  const LeaseManagementDetailLoaderScreen({
    super.key,
    required this.leaseManagementId,
  });

  final int leaseManagementId;

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Tenant & lease')),
    body: LeaseManagementDetailScreen(leaseManagementId: leaseManagementId),
  );
}

class LeaseManagementDetailScreen extends ConsumerWidget {
  const LeaseManagementDetailScreen({
    super.key,
    required this.leaseManagementId,
    this.leadingContent,
  });

  final int leaseManagementId;
  final Widget? leadingContent;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final detail = ref.watch(leaseManagementDetailProvider(leaseManagementId));
    final agreements = ref.watch(
      leaseAgreementHistoryProvider(leaseManagementId),
    );
    return detail.when(
      loading: () => const Center(child: CircularProgressIndicator()),
      error: (error, _) => _ErrorState(
        error: error,
        onRetry: () =>
            ref.invalidate(leaseManagementDetailProvider(leaseManagementId)),
      ),
      data: (management) => RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(leaseManagementDetailProvider(leaseManagementId));
          ref.invalidate(leaseAgreementHistoryProvider(leaseManagementId));
        },
        child: ListView(
          physics: const AlwaysScrollableScrollPhysics(),
          padding: const EdgeInsets.fromLTRB(16, 16, 16, 120),
          children: [
            if (leadingContent != null) ...[
              leadingContent!,
              const SizedBox(height: 12),
            ],
            _RelationshipHeader(summary: management.summary),
            const SizedBox(height: 12),
            _HouseholdCard(parties: management.parties),
            const SizedBox(height: 12),
            agreements.when(
              loading: () => const Card(
                child: Padding(
                  padding: EdgeInsets.all(24),
                  child: Center(child: CircularProgressIndicator()),
                ),
              ),
              error: (error, _) => _ErrorState(
                error: error,
                onRetry: () => ref.invalidate(
                  leaseAgreementHistoryProvider(leaseManagementId),
                ),
              ),
              data: (history) => _AgreementHistoryCard(
                leaseManagementId: leaseManagementId,
                agreements: history.items,
              ),
            ),
            const SizedBox(height: 12),
            _ActionsCard(
              management: management,
              onAsk: () => _ask(context, ref),
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _ask(BuildContext context, WidgetRef ref) async {
    final controller = TextEditingController();
    final question = await showDialog<String>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Ask this tenant & lease'),
        content: TextField(
          controller: controller,
          autofocus: true,
          minLines: 2,
          maxLines: 5,
          decoration: const InputDecoration(
            hintText: 'What does the governing agreement say about pets?',
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, controller.text),
            child: const Text('Ask'),
          ),
        ],
      ),
    );
    controller.dispose();
    if (question == null || question.trim().isEmpty || !context.mounted) return;
    try {
      final answer = await ref
          .read(leaseManagementsRepositoryProvider)
          .ask(leaseManagementId, question.trim());
      if (!context.mounted) return;
      await showDialog<void>(
        context: context,
        builder: (dialogContext) => AlertDialog(
          title: const Text('Answer'),
          content: SingleChildScrollView(child: Text(answer.answer)),
          actions: [
            FilledButton(
              onPressed: () => Navigator.pop(dialogContext),
              child: const Text('Done'),
            ),
          ],
        ),
      );
    } catch (error) {
      if (context.mounted) _showError(context, error);
    }
  }
}

class _RelationshipHeader extends StatelessWidget {
  const _RelationshipHeader({required this.summary});

  final LeaseManagementSummary summary;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    summary.primaryTenantName?.trim().isNotEmpty == true
                        ? summary.primaryTenantName!
                        : summary.relationshipNumber,
                    style: theme.textTheme.titleLarge?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                Chip(label: Text(summary.lifecycle)),
              ],
            ),
            Text('${summary.propertyName} · Unit ${summary.unitNumber}'),
            const SizedBox(height: 16),
            _Fact(
              label: 'Current agreement',
              value: summary.agreementNumber ?? 'Not issued',
            ),
            _Fact(
              label: 'Agreement status',
              value: summary.agreementStatus ?? 'No governing agreement',
            ),
            _Fact(
              label: 'Term',
              value: summary.termStartOn == null
                  ? 'Not set'
                  : '${_date(summary.termStartOn!)} – '
                        '${summary.termEndOn == null ? 'Month-to-month' : _date(summary.termEndOn!)}',
            ),
            _Fact(
              label: 'Base rent',
              value: summary.baseRentAmount == null
                  ? 'Not set'
                  : _money(summary.baseRentAmount!),
            ),
            if (summary.upcomingAgreementId != null)
              const _Fact(
                label: 'Upcoming',
                value: 'A future agreement is already prepared',
              ),
            if (summary.hasReconciliationException)
              Padding(
                padding: const EdgeInsets.only(top: 12),
                child: Text(
                  'Needs review: the lifecycle facts do not agree.',
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: theme.colorScheme.error,
                    fontWeight: FontWeight.w700,
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _HouseholdCard extends StatelessWidget {
  const _HouseholdCard({required this.parties});

  final List<LeaseManagementParty> parties;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Household', style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 8),
          if (parties.isEmpty)
            const Text('No household members are attached.')
          else
            for (final party in parties)
              ListTile(
                contentPadding: EdgeInsets.zero,
                leading: const CircleAvatar(child: Icon(Icons.person_outline)),
                title: Text(party.tenantName),
                subtitle: Text(
                  party.effectiveThrough == null
                      ? party.role
                      : '${party.role} · ended ${_date(party.effectiveThrough!)}',
                ),
                trailing: const Icon(Icons.chevron_right),
                onTap: () => Navigator.of(context).push<void>(
                  MaterialPageRoute<void>(
                    builder: (_) =>
                        TenantDetailLoaderScreen(tenantId: party.tenantId),
                  ),
                ),
              ),
        ],
      ),
    ),
  );
}

class _AgreementHistoryCard extends ConsumerWidget {
  const _AgreementHistoryCard({
    required this.leaseManagementId,
    required this.agreements,
  });

  final int leaseManagementId;
  final List<LeaseAgreementHistory> agreements;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final current = agreements.where((item) => item.isGoverning).firstOrNull;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    'Agreement history',
                    style: Theme.of(context).textTheme.titleMedium,
                  ),
                ),
                Text(
                  '${agreements.length} version${agreements.length == 1 ? '' : 's'}',
                ),
              ],
            ),
            const SizedBox(height: 8),
            if (agreements.isEmpty)
              const Text('No agreement draft has been created.')
            else
              for (final agreement in agreements) ...[
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: Icon(
                    agreement.isGoverning
                        ? Icons.verified_outlined
                        : Icons.history_outlined,
                  ),
                  title: Text(
                    '${agreement.agreementNumber} · v${agreement.versionNumber}',
                  ),
                  subtitle: Text(
                    '${agreement.changeType} · ${agreement.status}\n'
                    '${_date(agreement.termStartOn)} – '
                    '${agreement.termEndOn == null ? 'Month-to-month' : _date(agreement.termEndOn!)}',
                  ),
                  isThreeLine: true,
                  trailing:
                      agreement.executedArtifact == null &&
                          agreement.issuedArtifact == null
                      ? null
                      : IconButton(
                          tooltip: 'Open agreement PDF',
                          icon: const Icon(Icons.picture_as_pdf_outlined),
                          onPressed: () =>
                              _openArtifact(context, ref, agreement),
                        ),
                ),
                if (agreement == current)
                  Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                      OutlinedButton(
                        onPressed: () =>
                            _openSuccessor(context, ref, agreement, 'correct'),
                        child: const Text('Correct'),
                      ),
                      OutlinedButton(
                        onPressed: () =>
                            _openSuccessor(context, ref, agreement, 'renew'),
                        child: const Text('Renew'),
                      ),
                      OutlinedButton(
                        onPressed: () => _openSuccessor(
                          context,
                          ref,
                          agreement,
                          'month-to-month',
                        ),
                        child: const Text('Month-to-month'),
                      ),
                    ],
                  ),
                const Divider(),
              ],
          ],
        ),
      ),
    );
  }

  Future<void> _openArtifact(
    BuildContext context,
    WidgetRef ref,
    LeaseAgreementHistory agreement,
  ) async {
    final artifact = agreement.executedArtifact ?? agreement.issuedArtifact;
    if (artifact == null) return;
    try {
      final bytes = await ref
          .read(leaseManagementsRepositoryProvider)
          .artifactBytes(
            leaseManagementId: leaseManagementId,
            leaseAgreementId: agreement.id,
            artifactId: artifact.id,
          );
      await DocumentOpener.openBytes(
        bytes: bytes,
        fileName: artifact.fileName,
        mimeType: artifact.contentType,
      );
    } catch (error) {
      if (context.mounted) _showError(context, error);
    }
  }

  Future<void> _openSuccessor(
    BuildContext context,
    WidgetRef ref,
    LeaseAgreementHistory source,
    String operation,
  ) async {
    final result = await showModalBottomSheet<_SuccessorDates>(
      context: context,
      isScrollControlled: true,
      builder: (_) =>
          _SuccessorAgreementSheet(source: source, operation: operation),
    );
    if (result == null || !context.mounted) return;
    try {
      await ref
          .read(leaseManagementsRepositoryProvider)
          .createSuccessorDraft(
            leaseManagementId: leaseManagementId,
            sourceAgreementId: source.id,
            operation: operation,
            termStartOn: result.termStart,
            termEndOn: result.termEnd,
            governingFromOn: result.governingFrom,
          );
      ref.invalidate(leaseManagementDetailProvider(leaseManagementId));
      ref.invalidate(leaseAgreementHistoryProvider(leaseManagementId));
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(
            content: Text(
              'New agreement version created as a draft. Review it before issuing it for signature.',
            ),
          ),
        );
      }
    } catch (error) {
      if (context.mounted) _showError(context, error);
    }
  }
}

class _ActionsCard extends StatelessWidget {
  const _ActionsCard({required this.management, required this.onAsk});

  final LeaseManagementDetail management;
  final VoidCallback onAsk;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          FilledButton.tonalIcon(
            onPressed: onAsk,
            icon: const Icon(Icons.auto_awesome_outlined),
            label: const Text('Ask this tenant & lease'),
          ),
          if (management.summary.tenantAccountId != null) ...[
            const SizedBox(height: 8),
            OutlinedButton.icon(
              onPressed: () => Navigator.of(context).push<void>(
                MaterialPageRoute<void>(
                  builder: (_) => Scaffold(
                    appBar: AppBar(title: const Text('Tenant account')),
                    body: LeaseLedgerView(
                      leaseManagementId: management.summary.id,
                    ),
                  ),
                ),
              ),
              icon: const Icon(Icons.account_balance_wallet_outlined),
              label: const Text('View tenant account'),
            ),
          ],
        ],
      ),
    ),
  );
}

class _SuccessorDates {
  const _SuccessorDates({
    required this.termStart,
    required this.termEnd,
    required this.governingFrom,
  });

  final DateTime termStart;
  final DateTime? termEnd;
  final DateTime governingFrom;
}

class _SuccessorAgreementSheet extends StatefulWidget {
  const _SuccessorAgreementSheet({
    required this.source,
    required this.operation,
  });

  final LeaseAgreementHistory source;
  final String operation;

  @override
  State<_SuccessorAgreementSheet> createState() =>
      _SuccessorAgreementSheetState();
}

class _SuccessorAgreementSheetState extends State<_SuccessorAgreementSheet> {
  late DateTime _start;
  DateTime? _end;
  late DateTime _governingFrom;

  @override
  void initState() {
    super.initState();
    final source = widget.source;
    final nextDay = source.termEndOn?.add(const Duration(days: 1));
    _start = widget.operation == 'renew'
        ? nextDay ?? source.termStartOn
        : source.termStartOn;
    _end = widget.operation == 'month-to-month'
        ? null
        : widget.operation == 'renew'
        ? DateTime(_start.year + 1, _start.month, _start.day)
        : source.termEndOn;
    _governingFrom = widget.operation == 'correct' ? DateTime.now() : _start;
  }

  @override
  Widget build(BuildContext context) {
    final label = switch (widget.operation) {
      'correct' => 'Correct agreement',
      'renew' => 'Renew agreement',
      _ => 'Offer month-to-month',
    };
    return SafeArea(
      child: Padding(
        padding: EdgeInsets.fromLTRB(
          20,
          20,
          20,
          20 + MediaQuery.viewInsetsOf(context).bottom,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(label, style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 8),
            const Text(
              'This creates a new agreement version. The signed agreement is never edited. Review the copied terms before sending the new version for signature.',
            ),
            const SizedBox(height: 16),
            _DateButton(
              label: 'Term starts',
              value: _start,
              onChanged: (value) => setState(() => _start = value),
            ),
            if (widget.operation != 'month-to-month')
              _DateButton(
                label: 'Term ends',
                value: _end!,
                onChanged: (value) => setState(() => _end = value),
              ),
            _DateButton(
              label: 'Becomes governing',
              value: _governingFrom,
              onChanged: (value) => setState(() => _governingFrom = value),
            ),
            const SizedBox(height: 16),
            FilledButton(
              onPressed: () => Navigator.pop(
                context,
                _SuccessorDates(
                  termStart: _start,
                  termEnd: _end,
                  governingFrom: _governingFrom,
                ),
              ),
              child: const Text('Create draft version'),
            ),
          ],
        ),
      ),
    );
  }
}

class _DateButton extends StatelessWidget {
  const _DateButton({
    required this.label,
    required this.value,
    required this.onChanged,
  });

  final String label;
  final DateTime value;
  final ValueChanged<DateTime> onChanged;

  @override
  Widget build(BuildContext context) => ListTile(
    contentPadding: EdgeInsets.zero,
    title: Text(label),
    subtitle: Text(_date(value)),
    trailing: const Icon(Icons.calendar_month_outlined),
    onTap: () async {
      final selected = await showDatePicker(
        context: context,
        firstDate: DateTime(2000),
        lastDate: DateTime(2100),
        initialDate: value,
      );
      if (selected != null) onChanged(selected);
    },
  );
}

class _Fact extends StatelessWidget {
  const _Fact({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 4),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        SizedBox(
          width: 130,
          child: Text(
            label,
            style: TextStyle(
              color: Theme.of(context).colorScheme.onSurfaceVariant,
            ),
          ),
        ),
        Expanded(child: Text(value)),
      ],
    ),
  );
}

class _ErrorState extends StatelessWidget {
  const _ErrorState({required this.error, required this.onRetry});

  final Object error;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => Center(
    child: Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            error is ApiException
                ? (error as ApiException).message
                : error.toString(),
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 12),
          FilledButton.tonal(
            onPressed: onRetry,
            child: const Text('Try again'),
          ),
        ],
      ),
    ),
  );
}

void _showError(BuildContext context, Object error) {
  ScaffoldMessenger.of(context).showSnackBar(
    SnackBar(
      content: Text(error is ApiException ? error.message : error.toString()),
    ),
  );
}

String _date(DateTime value) => '${value.month}/${value.day}/${value.year}';

String _money(double value) => '\$${value.toStringAsFixed(2)}';
