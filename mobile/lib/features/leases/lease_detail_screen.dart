import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/files/document_opener.dart';
import '../../core/models/lease.dart';
import '../tenants/tenant_detail_screen.dart';
import 'addendum_action_sheets.dart';
import 'agreement_draft_action_sheets.dart';
import 'ending_disposition_sheet.dart';
import 'lease_ledger_view.dart';
import 'leases_repository.dart';
import 'return_possession_sheet.dart';
import 'successor_agreement_sheet.dart';

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
    final agreementHistory = agreements.asData?.value;
    return detail.when(
      loading: () => const Center(child: CircularProgressIndicator()),
      error: (error, _) => _ErrorState(
        error: error,
        onRetry: () =>
            ref.invalidate(leaseManagementDetailProvider(leaseManagementId)),
      ),
      data: (management) => RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(leaseAddendumHistoryProvider);
          await Future.wait([
            ref.refresh(
              leaseManagementDetailProvider(leaseManagementId).future,
            ),
            ref.refresh(
              leaseAgreementHistoryProvider(leaseManagementId).future,
            ),
          ]);
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
                propertyName: management.summary.propertyName,
                unitNumber: management.summary.unitNumber,
                managementBusinessDate: management.summary.businessDate,
              ),
            ),
            const SizedBox(height: 12),
            _AddendumHistoryCard(management: management),
            const SizedBox(height: 12),
            _ActionsCard(
              management: management,
              onAsk: () => _ask(context, ref),
              onGivePossession:
                  management.summary.possessionGivenAt == null &&
                      management.summary.canceledAt == null &&
                      management.summary.tenantAccountId != null &&
                      management.summary.currentResidentCount > 0 &&
                      (agreementHistory?.items.any(
                            (agreement) =>
                                agreement.isGoverning &&
                                agreement.executedArtifact != null,
                          ) ??
                          false)
                  ? () => _givePossession(context, ref, management)
                  : null,
              onReturnPossession:
                  management.summary.possessionGivenAt != null &&
                      management.summary.possessionReturnedAt == null &&
                      management.summary.canceledAt == null
                  ? () => _returnPossession(context, ref, management)
                  : null,
              onEndingDisposition:
                  management.summary.possessionGivenAt != null &&
                      management.summary.possessionReturnedAt == null &&
                      management.summary.canceledAt == null
                  ? () => _recordEndingDisposition(context, ref, management)
                  : null,
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

  Future<void> _givePossession(
    BuildContext context,
    WidgetRef ref,
    LeaseManagementDetail management,
  ) async {
    final summary = management.summary;
    final confirmed = await showModalBottomSheet<bool>(
      context: context,
      useSafeArea: true,
      builder: (sheetContext) => Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              'Give possession',
              style: Theme.of(sheetContext).textTheme.titleLarge,
            ),
            const SizedBox(height: 12),
            Text('${summary.propertyName} · Unit ${summary.unitNumber}'),
            const SizedBox(height: 8),
            const Text(
              'This records possession now. The server will recheck the executed governing agreement, open account, resident household, unit availability, and your current access before changing lifecycle state.',
            ),
            const SizedBox(height: 20),
            FilledButton.icon(
              onPressed: () => Navigator.of(sheetContext).pop(true),
              icon: const Icon(Icons.key_outlined),
              label: const Text('Confirm possession'),
            ),
            TextButton(
              onPressed: () => Navigator.of(sheetContext).pop(false),
              child: const Text('Cancel'),
            ),
          ],
        ),
      ),
    );
    if (confirmed != true || !context.mounted) return;

    final operationKey = LeaseManagementsRepository.newOperationKey();
    try {
      final result = await _runWithStableRetry(
        context,
        actionLabel: 'give possession',
        action: () => ref
            .read(leaseManagementsRepositoryProvider)
            .givePossession(
              leaseManagementId: summary.id,
              unitId: summary.unitId,
              operationKey: operationKey,
            ),
      );
      if (result == null || !context.mounted) return;
      ref.invalidate(leaseManagementDetailProvider(summary.id));
      await ref.read(leaseManagementDetailProvider(summary.id).future);
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              'Possession recorded for Unit ${summary.unitNumber}.',
            ),
          ),
        );
      }
    } catch (error) {
      if (context.mounted) _showError(context, error);
    }
  }

  Future<void> _returnPossession(
    BuildContext context,
    WidgetRef ref,
    LeaseManagementDetail management,
  ) async {
    final summary = management.summary;
    try {
      final returnContext = await ref
          .read(leaseManagementsRepositoryProvider)
          .returnPossessionContext(summary.id);
      if (!context.mounted) return;
      if (returnContext.parties.isEmpty) {
        _showError(
          context,
          const ApiException(
            statusCode: 0,
            message:
                'The server did not return a current household to disposition.',
          ),
        );
        return;
      }

      final result = await showReturnPossessionSheet(
        context,
        summary: summary,
        returnContext: returnContext,
      );
      if (result == null || !context.mounted) return;
      await Future.wait([
        ref.refresh(leaseManagementDetailProvider(summary.id).future),
        ref.refresh(leaseAgreementHistoryProvider(summary.id).future),
      ]);
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              'Possession returned for Unit ${summary.unitNumber}; turnover is open.',
            ),
          ),
        );
      }
    } catch (error) {
      if (context.mounted) _showError(context, error);
    }
  }

  Future<void> _recordEndingDisposition(
    BuildContext context,
    WidgetRef ref,
    LeaseManagementDetail management,
  ) async {
    final summary = management.summary;
    final input = await showEndingDispositionSheet(context, summary: summary);
    if (input == null || !context.mounted) return;
    try {
      final result = await _runWithStableRetry(
        context,
        actionLabel: 'record ending decision',
        action: () => ref
            .read(leaseManagementsRepositoryProvider)
            .recordEndingDisposition(
              leaseManagementId: summary.id,
              unitId: summary.unitId,
              disposition: input.disposition,
              noticeGivenAt: input.noticeGivenAt,
              plannedMoveOutAt: input.plannedMoveOutAt,
              decisionReason: input.decisionReason,
              operationKey: input.operationKey,
            ),
      );
      if (result == null || !context.mounted) return;
      await ref.refresh(leaseManagementDetailProvider(summary.id).future);
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Lease ending decision recorded.')),
        );
      }
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
              _Fact(
                label: 'Upcoming',
                value:
                    '${summary.upcomingAgreementNumber ?? 'Future agreement'}'
                    '${summary.upcomingTermStartOn == null ? '' : ' · starts ${_date(summary.upcomingTermStartOn!)}'}'
                    '${summary.upcomingAgreementStatus == null ? '' : ' · ${summary.upcomingAgreementStatus}'}',
              ),
            _Fact(
              label: 'Ending plan',
              value: _endingDispositionLabel(summary.endingDisposition),
            ),
            if (summary.endingDispositionDecidedAt != null)
              _Fact(
                label: 'Decision recorded',
                value: _date(summary.endingDispositionDecidedAt!),
              ),
            if (summary.plannedMoveOutAt != null)
              _Fact(
                label: 'Planned move-out',
                value: _date(summary.plannedMoveOutAt!),
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
    required this.propertyName,
    required this.unitNumber,
    required this.managementBusinessDate,
  });

  final int leaseManagementId;
  final List<LeaseAgreementHistory> agreements;
  final String propertyName;
  final String unitNumber;
  final DateTime managementBusinessDate;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
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
                    '${agreement.termEndOn == null ? 'Month-to-month' : _date(agreement.termEndOn!)}'
                    '${agreement.correctionReason == null ? '' : '\nReason: ${agreement.correctionReason}'}'
                    '${agreement.draftCancellationReason == null ? '' : '\nCanceled: ${agreement.draftCancellationReason}'}',
                  ),
                  isThreeLine: true,
                ),
                if (agreement.issuedArtifact != null ||
                    agreement.executedArtifact != null)
                  Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                      if (agreement.issuedArtifact != null)
                        OutlinedButton.icon(
                          onPressed: () => _openArtifact(
                            context,
                            ref,
                            agreement,
                            agreement.issuedArtifact!,
                          ),
                          icon: const Icon(Icons.picture_as_pdf_outlined),
                          label: const Text('Issued PDF'),
                        ),
                      if (agreement.executedArtifact != null)
                        OutlinedButton.icon(
                          onPressed: () => _openArtifact(
                            context,
                            ref,
                            agreement,
                            agreement.executedArtifact!,
                          ),
                          icon: const Icon(Icons.verified_outlined),
                          label: const Text('Executed PDF'),
                        ),
                    ],
                  ),
                if (agreement.isDraft)
                  Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                      OutlinedButton.icon(
                        onPressed: () => _editDraft(context, ref, agreement),
                        icon: const Icon(Icons.edit_outlined),
                        label: const Text('Edit draft'),
                      ),
                      FilledButton.icon(
                        onPressed: () => _issueDraft(context, ref, agreement),
                        icon: const Icon(Icons.send_outlined),
                        label: const Text('Issue for signature'),
                      ),
                      if (agreement.replacesAgreementId != null ||
                          agreement.renewsAgreementId != null)
                        OutlinedButton.icon(
                          onPressed: () =>
                              _cancelDraft(context, ref, agreement),
                          icon: const Icon(Icons.delete_outline),
                          label: const Text('Cancel draft'),
                        ),
                    ],
                  ),
                if (agreement.isGoverning)
                  Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                      OutlinedButton(
                        onPressed: () => _openSuccessor(
                          context,
                          ref,
                          agreement,
                          LeaseSuccessorOperation.correction,
                        ),
                        child: const Text('Create correction'),
                      ),
                      OutlinedButton(
                        onPressed: () => _openSuccessor(
                          context,
                          ref,
                          agreement,
                          LeaseSuccessorOperation.restatement,
                        ),
                        child: const Text('Create restatement'),
                      ),
                      OutlinedButton(
                        onPressed: () => _openSuccessor(
                          context,
                          ref,
                          agreement,
                          LeaseSuccessorOperation.renewal,
                        ),
                        child: const Text('Create renewal'),
                      ),
                      OutlinedButton(
                        onPressed: () => _openSuccessor(
                          context,
                          ref,
                          agreement,
                          LeaseSuccessorOperation.monthToMonth,
                        ),
                        child: const Text('Create month-to-month'),
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
    LegalArtifactSummary artifact,
  ) async {
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

  Future<LeaseAgreementDraftDetail?> _loadDraft(
    BuildContext context,
    WidgetRef ref,
    LeaseAgreementHistory agreement,
  ) async {
    try {
      return await ref
          .read(leaseManagementsRepositoryProvider)
          .agreementDraft(
            leaseManagementId: leaseManagementId,
            leaseAgreementId: agreement.id,
          );
    } catch (error) {
      if (context.mounted) _showError(context, error);
      return null;
    }
  }

  Future<void> _editDraft(
    BuildContext context,
    WidgetRef ref,
    LeaseAgreementHistory agreement,
  ) async {
    final draft = await _loadDraft(context, ref, agreement);
    if (draft == null || !context.mounted) return;
    if (draft.documentTemplateId == null) {
      _showError(
        context,
        const ApiException(
          statusCode: 0,
          message:
              'This source-scan draft has no template identity and cannot be edited with the template-backed mobile form.',
        ),
      );
      return;
    }
    final result = await showEditAgreementDraftSheet(
      context,
      draft: draft,
      propertyName: propertyName,
      unitNumber: unitNumber,
      source: _sourceFor(agreement),
    );
    if (result == null || !context.mounted) return;
    await _refresh(context, ref);
    if (context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text('Draft revision ${result.draftRevision} saved.'),
        ),
      );
    }
  }

  LeaseAgreementHistory? _sourceFor(LeaseAgreementHistory agreement) {
    final sourceId =
        agreement.replacesAgreementId ?? agreement.renewsAgreementId;
    if (sourceId == null) return null;
    for (final candidate in agreements) {
      if (candidate.id == sourceId) return candidate;
    }
    return null;
  }

  Future<void> _cancelDraft(
    BuildContext context,
    WidgetRef ref,
    LeaseAgreementHistory agreement,
  ) async {
    final reason = await showDialog<String>(
      context: context,
      builder: (_) => const _CancelSuccessorDraftDialog(),
    );
    if (reason == null || !context.mounted) return;
    final operationKey = LeaseManagementsRepository.newOperationKey();
    try {
      final canceled = await _runWithStableRetry(
        context,
        actionLabel: 'cancel successor draft',
        action: () => ref
            .read(leaseManagementsRepositoryProvider)
            .cancelSuccessorDraft(
              leaseManagementId: leaseManagementId,
              leaseAgreementId: agreement.id,
              cancellationReason: reason,
              operationKey: operationKey,
            ),
      );
      if (canceled == null || !context.mounted) return;
      await _refresh(context, ref);
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Successor draft canceled.')),
        );
      }
    } catch (error) {
      if (context.mounted) _showError(context, error);
    }
  }

  Future<void> _issueDraft(
    BuildContext context,
    WidgetRef ref,
    LeaseAgreementHistory agreement,
  ) async {
    final draft = await _loadDraft(context, ref, agreement);
    if (draft == null || !context.mounted) return;
    if (!draft.hasExactOrderedSigners) {
      _showError(
        context,
        const ApiException(
          statusCode: 0,
          message:
              'The draft does not have a complete, uniquely ordered signer snapshot.',
        ),
      );
      return;
    }
    final result = await showIssueAgreementSheet(
      context,
      draft: draft,
      propertyName: propertyName,
      unitNumber: unitNumber,
    );
    if (result == null || !context.mounted) return;
    await _refresh(context, ref);
    if (context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Agreement issued for signature.')),
      );
    }
  }

  Future<void> _refresh(BuildContext context, WidgetRef ref) async {
    try {
      await Future.wait([
        ref.refresh(leaseManagementDetailProvider(leaseManagementId).future),
        ref.refresh(leaseAgreementHistoryProvider(leaseManagementId).future),
      ]);
    } catch (error) {
      if (context.mounted) _showError(context, error);
    }
  }

  Future<void> _openSuccessor(
    BuildContext context,
    WidgetRef ref,
    LeaseAgreementHistory source,
    LeaseSuccessorOperation operation,
  ) async {
    LeaseAgreementEffectiveAddendumSeries? effectiveAddendumSeries;
    if (operation.requiresEffectiveAddendumDecisions) {
      try {
        effectiveAddendumSeries = await ref
            .read(leaseManagementsRepositoryProvider)
            .effectiveAddendumSeries(
              leaseManagementId: leaseManagementId,
              sourceAgreementId: source.id,
            );
      } catch (error) {
        if (context.mounted) _showError(context, error);
        return;
      }
      if (!context.mounted) return;
    }

    final result = await showSuccessorAgreementSheet(
      context,
      source: source,
      operation: operation,
      propertyName: propertyName,
      unitNumber: unitNumber,
      businessDate: managementBusinessDate,
      effectiveAddendumSeries: effectiveAddendumSeries,
    );
    if (result == null || !context.mounted) return;
    try {
      final created = await _runWithStableRetry(
        context,
        actionLabel: 'create successor draft',
        action: () => ref
            .read(leaseManagementsRepositoryProvider)
            .createSuccessorDraft(
              leaseManagementId: leaseManagementId,
              sourceAgreementId: source.id,
              changeType: operation.apiChangeType,
              termStartOn: result.termStart,
              termEndOn: result.termEnd,
              governingFromOn: result.governingFrom,
              correctionReason: result.correctionReason,
              addendumDecisions: result.addendumDecisions,
              operationKey: result.operationKey,
            ),
      );
      if (created == null || !context.mounted) return;
      await Future.wait([
        ref.refresh(leaseManagementDetailProvider(leaseManagementId).future),
        ref.refresh(leaseAgreementHistoryProvider(leaseManagementId).future),
      ]);
      if (!context.mounted) return;
      final createdDraft = await ref
          .read(leaseManagementsRepositoryProvider)
          .agreementDraft(
            leaseManagementId: leaseManagementId,
            leaseAgreementId: created.leaseAgreementId,
          );
      if (!context.mounted) return;
      if (createdDraft.documentTemplateId != null) {
        final edited = await showEditAgreementDraftSheet(
          context,
          draft: createdDraft,
          propertyName: propertyName,
          unitNumber: unitNumber,
          source: source,
        );
        if (edited != null && context.mounted) {
          await _refresh(context, ref);
        }
      } else if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              'Draft v${created.versionNumber} created. Open it from agreement history to continue.',
            ),
          ),
        );
      }
    } catch (error) {
      if (context.mounted) _showError(context, error);
    }
  }
}

class _CancelSuccessorDraftDialog extends StatefulWidget {
  const _CancelSuccessorDraftDialog();

  @override
  State<_CancelSuccessorDraftDialog> createState() =>
      _CancelSuccessorDraftDialogState();
}

class _CancelSuccessorDraftDialogState
    extends State<_CancelSuccessorDraftDialog> {
  final _reason = TextEditingController();

  @override
  void dispose() {
    _reason.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: const Text('Cancel successor draft?'),
    content: Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const Text(
          'The source agreement is unchanged and keeps governing. Explain why this draft is being abandoned.',
        ),
        const SizedBox(height: 12),
        TextField(
          controller: _reason,
          decoration: const InputDecoration(labelText: 'Cancellation reason'),
          minLines: 2,
          maxLines: 4,
          maxLength: 1000,
          onChanged: (_) => setState(() {}),
        ),
      ],
    ),
    actions: [
      TextButton(
        onPressed: () => Navigator.of(context).pop(),
        child: const Text('Keep draft'),
      ),
      FilledButton(
        onPressed: _reason.text.trim().isEmpty
            ? null
            : () => Navigator.of(context).pop(_reason.text.trim()),
        child: const Text('Cancel draft'),
      ),
    ],
  );
}

class _AddendumHistoryCard extends ConsumerStatefulWidget {
  const _AddendumHistoryCard({required this.management});

  final LeaseManagementDetail management;

  @override
  ConsumerState<_AddendumHistoryCard> createState() =>
      _AddendumHistoryCardState();
}

class _AddendumHistoryCardState extends ConsumerState<_AddendumHistoryCard> {
  static const _pageSize = 10;
  int _skip = 0;

  LeaseAddendumHistoryQuery get _query => LeaseAddendumHistoryQuery(
    leaseManagementId: widget.management.summary.id,
    skip: _skip,
    take: _pageSize,
  );

  @override
  Widget build(BuildContext context) {
    final page = ref.watch(leaseAddendumHistoryProvider(_query));
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
                    'Addenda',
                    style: Theme.of(context).textTheme.titleMedium,
                  ),
                ),
                FilledButton.tonalIcon(
                  onPressed: () => _create(context),
                  icon: const Icon(Icons.add),
                  label: const Text('New draft'),
                ),
              ],
            ),
            const SizedBox(height: 8),
            page.when(
              loading: () => const Padding(
                padding: EdgeInsets.all(24),
                child: Center(child: CircularProgressIndicator()),
              ),
              error: (error, _) => _ErrorState(
                error: error,
                onRetry: () =>
                    ref.invalidate(leaseAddendumHistoryProvider(_query)),
              ),
              data: (history) => _history(context, history),
            ),
          ],
        ),
      ),
    );
  }

  Widget _history(
    BuildContext context,
    LeaseAddendumHistoryPage page,
  ) => Column(
    children: [
      if (page.items.isEmpty)
        const Align(
          alignment: Alignment.centerLeft,
          child: Text('No addendum draft has been created.'),
        )
      else
        for (final addendum in page.items) ...[
          ListTile(
            contentPadding: EdgeInsets.zero,
            leading: const Icon(Icons.description_outlined),
            title: Text(
              '${addendum.addendumNumber} · v${addendum.versionNumber}',
            ),
            subtitle: Text(
              '${addendum.purpose} · ${addendum.status}\n'
              '${_date(addendum.effectiveFromOn)} – '
              '${addendum.effectiveThroughOn == null ? 'Open ended' : _date(addendum.effectiveThroughOn!)}\n'
              '${addendum.financialEffectCount} financial effect${addendum.financialEffectCount == 1 ? '' : 's'} · '
              '${addendum.signerCount} signer${addendum.signerCount == 1 ? '' : 's'}',
            ),
            isThreeLine: true,
          ),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              if (addendum.issuedArtifact != null)
                OutlinedButton.icon(
                  onPressed: () => _openArtifact(
                    context,
                    addendum,
                    addendum.issuedArtifact!,
                  ),
                  icon: const Icon(Icons.picture_as_pdf_outlined),
                  label: const Text('Issued PDF'),
                ),
              if (addendum.executedArtifact != null)
                OutlinedButton.icon(
                  onPressed: () => _openArtifact(
                    context,
                    addendum,
                    addendum.executedArtifact!,
                  ),
                  icon: const Icon(Icons.verified_outlined),
                  label: const Text('Executed PDF'),
                ),
              if (addendum.isDraft)
                OutlinedButton.icon(
                  onPressed: () => _edit(context, addendum),
                  icon: const Icon(Icons.edit_outlined),
                  label: const Text('Edit draft'),
                ),
              if (addendum.isDraft)
                FilledButton.icon(
                  onPressed: () => _issue(context, addendum),
                  icon: const Icon(Icons.send_outlined),
                  label: const Text('Issue'),
                ),
              if (addendum.canCorrect)
                OutlinedButton.icon(
                  onPressed: () => _correct(context, addendum),
                  icon: const Icon(Icons.content_copy_outlined),
                  label: const Text('Correct'),
                ),
            ],
          ),
          const Divider(),
        ],
      if (page.totalCount > 0)
        Row(
          children: [
            IconButton(
              onPressed: page.hasPrevious
                  ? () => setState(() {
                      _skip = _skip > _pageSize ? _skip - _pageSize : 0;
                    })
                  : null,
              icon: const Icon(Icons.chevron_left),
              tooltip: 'Previous addenda page',
            ),
            Expanded(
              child: Text(
                '${page.skip + 1}–${page.skip + page.items.length} of ${page.totalCount}',
                textAlign: TextAlign.center,
              ),
            ),
            IconButton(
              onPressed: page.hasNext
                  ? () => setState(() => _skip += _pageSize)
                  : null,
              icon: const Icon(Icons.chevron_right),
              tooltip: 'Next addenda page',
            ),
          ],
        ),
    ],
  );

  Future<void> _create(BuildContext context) async {
    final result = await showCreateAddendumDraftSheet(
      context,
      management: widget.management.summary,
    );
    if (result == null || !mounted) return;
    setState(() => _skip = 0);
    await _refresh();
  }

  Future<LeaseAddendumDraftDetail?> _loadDraft(
    BuildContext context,
    LeaseAddendumHistory addendum,
  ) async {
    try {
      return await ref
          .read(leaseManagementsRepositoryProvider)
          .addendumDraft(
            leaseManagementId: widget.management.summary.id,
            leaseAddendumId: addendum.id,
          );
    } catch (error) {
      if (context.mounted) _showError(context, error);
      return null;
    }
  }

  Future<void> _edit(
    BuildContext context,
    LeaseAddendumHistory addendum,
  ) async {
    final draft = await _loadDraft(context, addendum);
    if (draft == null || !context.mounted) return;
    if (draft.documentTemplateId == null) {
      _showError(
        context,
        const ApiException(
          statusCode: 0,
          message: 'This draft has no editable template identity.',
        ),
      );
      return;
    }
    final result = await showEditAddendumDraftSheet(
      context,
      draft: draft,
      propertyName: widget.management.summary.propertyName,
      unitNumber: widget.management.summary.unitNumber,
    );
    if (result != null && mounted) await _refresh();
  }

  Future<void> _issue(
    BuildContext context,
    LeaseAddendumHistory addendum,
  ) async {
    final draft = await _loadDraft(context, addendum);
    if (draft == null || !context.mounted) return;
    final result = await showIssueAddendumSheet(
      context,
      draft: draft,
      propertyName: widget.management.summary.propertyName,
      unitNumber: widget.management.summary.unitNumber,
    );
    if (result != null && mounted) await _refresh();
  }

  Future<void> _correct(
    BuildContext context,
    LeaseAddendumHistory addendum,
  ) async {
    final result = await showCorrectAddendumSheet(
      context,
      leaseManagementId: widget.management.summary.id,
      source: addendum,
      businessDate: widget.management.summary.businessDate,
    );
    if (result != null && mounted) {
      setState(() => _skip = 0);
      await _refresh();
    }
  }

  Future<void> _openArtifact(
    BuildContext context,
    LeaseAddendumHistory addendum,
    LegalArtifactSummary artifact,
  ) async {
    try {
      final bytes = await ref
          .read(leaseManagementsRepositoryProvider)
          .addendumArtifactBytes(
            leaseManagementId: widget.management.summary.id,
            leaseAddendumId: addendum.id,
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

  Future<void> _refresh() async {
    ref.invalidate(leaseAddendumHistoryProvider);
    await Future.wait([
      ref.refresh(
        leaseManagementDetailProvider(widget.management.summary.id).future,
      ),
      ref.refresh(leaseAddendumHistoryProvider(_query).future),
    ]);
  }
}

class _ActionsCard extends StatelessWidget {
  const _ActionsCard({
    required this.management,
    required this.onAsk,
    required this.onGivePossession,
    required this.onReturnPossession,
    required this.onEndingDisposition,
  });

  final LeaseManagementDetail management;
  final VoidCallback onAsk;
  final VoidCallback? onGivePossession;
  final VoidCallback? onReturnPossession;
  final VoidCallback? onEndingDisposition;

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
          if (onEndingDisposition != null) ...[
            const SizedBox(height: 8),
            OutlinedButton.icon(
              onPressed: onEndingDisposition,
              icon: const Icon(Icons.event_available_outlined),
              label: const Text('Record lease ending plan'),
            ),
          ],
          if (onGivePossession != null) ...[
            const SizedBox(height: 8),
            FilledButton.icon(
              onPressed: onGivePossession,
              icon: const Icon(Icons.key_outlined),
              label: const Text('Give possession'),
            ),
          ],
          if (onReturnPossession != null) ...[
            const SizedBox(height: 8),
            FilledButton.icon(
              onPressed: onReturnPossession,
              icon: const Icon(Icons.key_off_outlined),
              label: const Text('Return possession'),
            ),
          ],
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

Future<T?> _runWithStableRetry<T>(
  BuildContext context, {
  required String actionLabel,
  required Future<T> Function() action,
}) async {
  while (context.mounted) {
    try {
      return await action();
    } catch (error) {
      if (!context.mounted) return null;
      final retry = await showDialog<bool>(
        context: context,
        builder: (dialogContext) => AlertDialog(
          title: const Text('Request not confirmed'),
          content: Text(
            '${error is ApiException ? error.message : error}\n\n'
            'Retry $actionLabel with the same request key so the server can safely replay an earlier success.',
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(false),
              child: const Text('Cancel'),
            ),
            FilledButton(
              onPressed: () => Navigator.of(dialogContext).pop(true),
              child: const Text('Retry safely'),
            ),
          ],
        ),
      );
      if (retry != true) return null;
    }
  }
  return null;
}

String _date(DateTime value) => '${value.month}/${value.day}/${value.year}';

String _money(double value) => '\$${value.toStringAsFixed(2)}';

String _endingDispositionLabel(String value) => switch (value) {
  'OfferRenewal' => 'Renew / continue',
  'OfferMonthToMonth' => 'Continue month-to-month',
  'NonRenewalMoveOut' => 'Move out / end',
  _ => 'Not decided',
};
