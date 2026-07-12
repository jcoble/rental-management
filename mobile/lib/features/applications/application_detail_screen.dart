import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_exception.dart';
import '../../core/files/document_opener.dart';
import '../activity/activity_history_screen.dart';
import '../home/mobile_domain_navigation.dart';
import '../tenants/tenant_detail_screen.dart';
import '../units/unit_command_center_screen.dart';
import '../units/unit_navigation.dart';
import 'applications_models.dart';
import 'applications_repository.dart';
import 'applications_shared.dart';

/// Landlord-facing detail / review screen for a single rental application.
///
/// Shows every field plus the consent timestamp and offers Approve,
/// Decline-with-reason, and Withdraw actions (each confirmed, with snackbar
/// feedback). After approval the screen surfaces that a tenant was created.
class ApplicationDetailScreen extends ConsumerStatefulWidget {
  const ApplicationDetailScreen({super.key, required this.applicationId});

  final int applicationId;

  @override
  ConsumerState<ApplicationDetailScreen> createState() =>
      _ApplicationDetailScreenState();
}

class _ApplicationDetailScreenState
    extends ConsumerState<ApplicationDetailScreen> {
  bool _busy = false;

  // Set after a successful approve so we can surface the new tenant.
  int? _createdTenantId;

  // Set after generating an adverse-action notice so we can surface it inline.
  AdverseActionNotice? _adverseAction;
  String? _integratedScreeningOperationKey;
  String? _externalScreeningOperationKey;
  String? _completeExternalOperationKey;
  String? _screeningDecisionOperationKey;
  String? _updateScreeningAgencyOperationKey;

  int get _id => widget.applicationId;

  Future<void> _refresh() async {
    ref.invalidate(applicationDetailProvider(_id));
    ref.invalidate(applicationScreeningProvider(_id));
  }

  void _snack(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(
      context,
    ).showSnackBar(SnackBar(content: Text(message)));
  }

  Future<void> _approve() async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (_) => AlertDialog(
        title: const Text('Approve application?'),
        content: const Text(
          'This approves the applicant and creates a tenant record. '
          'You can set up their lease afterwards.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: const Text('Approve'),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;

    setState(() => _busy = true);
    try {
      final result = await ref
          .read(applicationsRepositoryProvider)
          .approve(_id);
      if (!mounted) return;
      setState(() => _createdTenantId = result.tenantId);
      await _refresh();
      _snack(
        result.tenantId != null
            ? 'Approved — tenant #${result.tenantId} created.'
            : 'Application approved.',
      );
    } on ApiException catch (e) {
      _snack(e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _decline() async {
    final reason = await showDialog<String?>(
      context: context,
      builder: (_) => const _DeclineDialog(),
    );
    // Dialog returns null on cancel, '' or a string on confirm.
    if (reason == null || !mounted) return;

    setState(() => _busy = true);
    try {
      await ref
          .read(applicationsRepositoryProvider)
          .decline(_id, reason: reason.isEmpty ? null : reason);
      await _refresh();
      _snack('Application declined.');
    } on ApiException catch (e) {
      _snack(e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _withdraw() async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (_) => AlertDialog(
        title: const Text('Withdraw application?'),
        content: const Text(
          'Mark this application as withdrawn. This cannot be undone.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: const Text('Withdraw'),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;

    setState(() => _busy = true);
    try {
      await ref.read(applicationsRepositoryProvider).withdraw(_id);
      await _refresh();
      _snack('Application withdrawn.');
    } on ApiException catch (e) {
      _snack(e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _recordFee() async {
    final input = await showDialog<_RecordFeeInput>(
      context: context,
      builder: (_) => const _RecordFeeDialog(),
    );
    if (input == null || !mounted) return;

    setState(() => _busy = true);
    try {
      final result = await ref
          .read(applicationsRepositoryProvider)
          .recordFee(
            _id,
            operationKey: input.operationKey,
            amount: input.amount,
            method: input.method,
            effectiveOn: input.effectiveOn,
          );
      await _refresh();
      _snack(
        result.replayed
            ? 'Application fee was already recorded.'
            : 'Application fee recorded.',
      );
    } on ApiException catch (e) {
      _snack(e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _edit(RentalApplication app) async {
    final input = await showDialog<UpdateApplicationInput>(
      context: context,
      builder: (_) => _EditApplicationDialog(application: app),
    );
    if (input == null || !mounted) return;

    setState(() => _busy = true);
    try {
      await ref.read(applicationsRepositoryProvider).update(_id, input);
      await _refresh();
      _snack('Application updated.');
    } on ApiException catch (e) {
      _snack(e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _showActivityHistory(RentalApplication app) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => ActivityHistoryScreen(
          entityType: 'RentalApplication',
          entityId: app.id,
          title: 'Application activity',
          subtitle: app.fullName,
        ),
      ),
    );
  }

  Future<void> _confirmDelete(RentalApplication app) async {
    final messenger = ScaffoldMessenger.of(context);
    final navigator = Navigator.of(context);
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (_) => AlertDialog(
        title: const Text('Delete application?'),
        content: Text('Delete ${app.fullName}? This cannot be undone.'),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;

    setState(() => _busy = true);
    try {
      await ref.read(applicationsRepositoryProvider).delete(app.id);
      ref.invalidate(applicationDetailProvider(app.id));
      ref.invalidate(applicationScreeningProvider(app.id));
      ref.invalidate(applicationsPageProvider);
      ref.invalidate(applicationsProvider(null));
      ref.invalidate(applicationsProvider(app.status));
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Application deleted.')));
      navigator.pop();
    } on ApiException catch (e) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  // ── Screening ─────────────────────────────────────────────────────────────

  Future<void> _runIntegratedScreening() async {
    setState(() => _busy = true);
    try {
      final screening = await ref
          .read(applicationsRepositoryProvider)
          .startIntegratedScreening(
            _id,
            operationKey: _integratedScreeningOperationKey ??=
                ApplicationsRepository.newOperationKey(),
          );
      _integratedScreeningOperationKey = null;
      ref.invalidate(applicationScreeningProvider(_id));
      await _refresh();
      _snack('Secure screening invitation created.');
      if (screening.providerHostedUrl != null && mounted) {
        await _openProvider(screening.providerHostedUrl!);
      }
    } on ApiException catch (e) {
      // Screening isn't configured (dormant provider) — gentle, not an error.
      if (e.statusCode == 503) {
        _snack("Screening isn't set up yet.");
      } else {
        _snack(e.message);
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _trackExternalScreening() async {
    final input = await showDialog<_ExternalScreeningInput>(
      context: context,
      builder: (_) => const _ExternalScreeningDialog(),
    );
    if (input == null || !mounted) return;

    setState(() => _busy = true);
    try {
      await ref
          .read(applicationsRepositoryProvider)
          .trackExternalScreening(
            _id,
            operationKey: _externalScreeningOperationKey ??=
                ApplicationsRepository.newOperationKey(),
            providerDisplayName: input.provider,
            providerReference: input.reference,
            providerHostedUrl: input.url,
            creditReportingAgencyName: input.agencyName,
            creditReportingAgencyAddress: input.agencyAddress,
            creditReportingAgencyPhone: input.agencyPhone,
          );
      _externalScreeningOperationKey = null;
      ref.invalidate(applicationScreeningProvider(_id));
      await _refresh();
      _snack('Outside screening added.');
    } on ApiException catch (e) {
      _snack(e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _markExternalScreeningComplete(int screeningId) async {
    setState(() => _busy = true);
    try {
      await ref
          .read(applicationsRepositoryProvider)
          .markExternalScreeningComplete(
            _id,
            screeningId,
            operationKey: _completeExternalOperationKey ??=
                ApplicationsRepository.newOperationKey(),
          );
      _completeExternalOperationKey = null;
      ref.invalidate(applicationScreeningProvider(_id));
      _snack('Outside screening marked complete.');
    } on ApiException catch (e) {
      _snack(e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _recordScreeningDecision(ApplicantScreening screening) async {
    final input = await showDialog<_ScreeningDecisionInput>(
      context: context,
      builder: (_) => _ScreeningDecisionDialog(screening: screening),
    );
    if (input == null || !mounted) return;

    setState(() => _busy = true);
    try {
      await ref
          .read(applicationsRepositoryProvider)
          .recordScreeningDecision(
            _id,
            screening.id,
            operationKey: _screeningDecisionOperationKey ??=
                ApplicationsRepository.newOperationKey(),
            decision: input.decision,
            consumerReportUsed: input.consumerReportUsed,
            reason: input.reason,
          );
      _screeningDecisionOperationKey = null;
      ref.invalidate(applicationScreeningProvider(_id));
      _snack('Screening decision recorded.');
    } on ApiException catch (e) {
      _snack(e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _updateScreeningAgency(ApplicantScreening screening) async {
    final input = await showDialog<_CraContactInput>(
      context: context,
      builder: (_) => _CraContactDialog(initial: screening),
    );
    if (input == null || !mounted) return;

    setState(() => _busy = true);
    try {
      await ref
          .read(applicationsRepositoryProvider)
          .updateExternalScreeningAgency(
            _id,
            screening.id,
            operationKey: _updateScreeningAgencyOperationKey ??=
                ApplicationsRepository.newOperationKey(),
            creditReportingAgencyName: input.name,
            creditReportingAgencyAddress: input.address,
            creditReportingAgencyPhone: input.phone,
          );
      _updateScreeningAgencyOperationKey = null;
      ref.invalidate(applicationScreeningProvider(_id));
      _snack('Consumer reporting agency contact saved.');
    } on ApiException catch (e) {
      _snack(e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _openProvider(String rawUrl) async {
    final uri = Uri.tryParse(rawUrl);
    if (uri == null ||
        (uri.scheme != 'https' && uri.scheme != 'http') ||
        !await launchUrl(uri, mode: LaunchMode.externalApplication)) {
      _snack("Couldn't open the screening provider.");
    }
  }

  Future<void> _generateAdverseAction(RentalApplication app) async {
    final result = await showDialog<_AdverseActionInput>(
      context: context,
      builder: (_) =>
          _AdverseActionDialog(initialReason: app.decisionReason ?? ''),
    );
    if (result == null || !mounted) return;

    setState(() => _busy = true);
    try {
      final notice = await ref
          .read(applicationsRepositoryProvider)
          .adverseAction(
            _id,
            reason: result.reason.isEmpty ? null : result.reason,
            sendToApplicant: result.sendToApplicant,
          );
      if (!mounted) return;
      setState(() => _adverseAction = notice);
      _snack(
        notice.sentAtUtc != null
            ? 'Adverse-action notice generated and sent.'
            : 'Adverse-action notice generated.',
      );
    } on ApiException catch (e) {
      _snack(e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// Fetches the notice PDF bytes (authed) and hands them to the OS viewer via
  /// [DocumentOpener] (FileProvider `content://` URI, share-sheet fallback).
  Future<void> _viewNotice(int storedFileId) async {
    final messenger = ScaffoldMessenger.of(context);
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(const SnackBar(content: Text('Opening notice…')));
    try {
      final bytes = await ref
          .read(applicationsRepositoryProvider)
          .documentBytes(storedFileId);
      await DocumentOpener.openBytes(
        bytes: bytes,
        fileName: 'adverse-action-$storedFileId.pdf',
      );
    } on ApiException catch (e) {
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  @override
  Widget build(BuildContext context) {
    final detailAsync = ref.watch(applicationDetailProvider(_id));
    final screeningAsync = ref.watch(applicationScreeningProvider(_id));
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Application'),
        actions: [
          ...detailAsync.whenOrNull(
                data: (app) => [
                  if (isApplicationOpen(app.status))
                    IconButton(
                      tooltip: 'Edit application',
                      onPressed: _busy ? null : () => _edit(app),
                      icon: const Icon(Icons.edit_outlined),
                    ),
                  IconButton(
                    tooltip: 'View application activity',
                    onPressed: _busy ? null : () => _showActivityHistory(app),
                    icon: const Icon(Icons.history_outlined),
                  ),
                  IconButton(
                    tooltip: 'Delete application',
                    onPressed: _busy ? null : () => _confirmDelete(app),
                    icon: const Icon(Icons.delete_outline),
                  ),
                  ApplicationStatusChip(status: app.status),
                ],
              ) ??
              [const SizedBox.shrink()],
          const SizedBox(width: 12),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: detailAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(
            message: e is ApiException ? e.message : e.toString(),
            onRetry: _refresh,
          ),
          data: (app) => _DetailBody(
            application: app,
            theme: theme,
            busy: _busy,
            createdTenantId: _createdTenantId ?? app.approvedTenantId,
            screeningAsync: screeningAsync,
            adverseAction: _adverseAction,
            onApprove: _approve,
            onDecline: _decline,
            onWithdraw: _withdraw,
            onRecordFee: _recordFee,
            onRunIntegratedScreening: _runIntegratedScreening,
            onTrackExternalScreening: _trackExternalScreening,
            onMarkExternalComplete: _markExternalScreeningComplete,
            onRecordScreeningDecision: _recordScreeningDecision,
            onUpdateScreeningAgency: _updateScreeningAgency,
            onOpenProvider: _openProvider,
            onGenerateAdverseAction: () => _generateAdverseAction(app),
            onViewNotice: _viewNotice,
          ),
        ),
      ),
    );
  }
}

// ── Detail body ───────────────────────────────────────────────────────────────

class _DetailBody extends StatelessWidget {
  const _DetailBody({
    required this.application,
    required this.theme,
    required this.busy,
    required this.createdTenantId,
    required this.screeningAsync,
    required this.adverseAction,
    required this.onApprove,
    required this.onDecline,
    required this.onWithdraw,
    required this.onRecordFee,
    required this.onRunIntegratedScreening,
    required this.onTrackExternalScreening,
    required this.onMarkExternalComplete,
    required this.onRecordScreeningDecision,
    required this.onUpdateScreeningAgency,
    required this.onOpenProvider,
    required this.onGenerateAdverseAction,
    required this.onViewNotice,
  });

  final RentalApplication application;
  final ThemeData theme;
  final bool busy;
  final int? createdTenantId;
  final AsyncValue<ScreeningWorkspace> screeningAsync;
  final AdverseActionNotice? adverseAction;
  final VoidCallback onApprove;
  final VoidCallback onDecline;
  final VoidCallback onWithdraw;
  final VoidCallback onRecordFee;
  final VoidCallback onRunIntegratedScreening;
  final VoidCallback onTrackExternalScreening;
  final void Function(int screeningId) onMarkExternalComplete;
  final void Function(ApplicantScreening screening) onRecordScreeningDecision;
  final void Function(ApplicantScreening screening) onUpdateScreeningAgency;
  final void Function(String url) onOpenProvider;
  final VoidCallback onGenerateAdverseAction;
  final void Function(int storedFileId) onViewNotice;

  @override
  Widget build(BuildContext context) {
    final app = application;
    final cs = theme.colorScheme;
    final open = isApplicationOpen(app.status);

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        // ── Header card ────────────────────────────────────────────────────
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  app.fullName,
                  style: theme.textTheme.titleLarge?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  'Property #${app.propertyId}'
                  '${app.unitId != null ? '  ·  Unit #${app.unitId}' : ''}',
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: cs.onSurfaceVariant,
                  ),
                ),
                if (app.submittedAtUtc != null) ...[
                  const SizedBox(height: 4),
                  Text(
                    'Submitted ${formatApplicationDateTime(app.submittedAtUtc!)}',
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: cs.onSurfaceVariant,
                    ),
                  ),
                ],
              ],
            ),
          ),
        ),
        const SizedBox(height: 12),

        // ── Approved → tenant banner ───────────────────────────────────────
        if (app.status == 'Approved' && createdTenantId != null) ...[
          Card(
            color: cs.tertiaryContainer,
            child: Padding(
              padding: const EdgeInsets.all(14),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Icon(
                        Icons.how_to_reg_outlined,
                        color: cs.onTertiaryContainer,
                      ),
                      const SizedBox(width: 10),
                      Expanded(
                        child: Text(
                          'Approved — tenant #$createdTenantId created. '
                          'Open the tenant to set up a lease.',
                          style: theme.textTheme.bodyMedium?.copyWith(
                            color: cs.onTertiaryContainer,
                          ),
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 10),
                  Align(
                    alignment: Alignment.centerLeft,
                    child: FilledButton.tonalIcon(
                      onPressed: () {
                        if (app.unitId != null) {
                          openUnitCommandCenter(
                            context,
                            unitId: app.unitId!,
                            initialTab: UnitCommandCenterTab.tenants,
                            application: app,
                            tenantId: createdTenantId,
                          );
                          return;
                        }

                        Widget detailBuilder(BuildContext _) =>
                            TenantDetailLoaderScreen(
                              tenantId: createdTenantId!,
                            );
                        final shellNavigator = mobileShellNavigatorOf(context);
                        if (shellNavigator != null) {
                          shellNavigator.openTab(
                            MobileShellTabId.rentals,
                            destination: MobileDestinationId.tenants,
                            detailBuilder: detailBuilder,
                          );
                          revealMobileShellIfDetached(context);
                          return;
                        }

                        final domainNavigator = MobileDomainNavigation.maybeOf(
                          context,
                        );
                        if (domainNavigator != null) {
                          domainNavigator.openDestination(
                            MobileDestinationId.tenants,
                            detailBuilder: detailBuilder,
                          );
                          return;
                        }

                        Navigator.of(context).push<void>(
                          MaterialPageRoute<void>(builder: detailBuilder),
                        );
                      },
                      icon: const Icon(Icons.person_outline, size: 18),
                      label: const Text('View tenant'),
                    ),
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(height: 12),
        ],

        // ── Decision reason (declined) ─────────────────────────────────────
        if (app.status == 'Declined' &&
            app.decisionReason != null &&
            app.decisionReason!.isNotEmpty) ...[
          _SectionCard(
            title: 'Decline reason',
            child: Text(app.decisionReason!, style: theme.textTheme.bodyMedium),
          ),
          const SizedBox(height: 12),
        ],

        // ── Contact ────────────────────────────────────────────────────────
        _SectionCard(
          title: 'Contact',
          child: Column(
            children: [
              if (app.email != null)
                _DetailRow(label: 'Email', value: app.email!),
              if (app.phone != null)
                _DetailRow(label: 'Phone', value: app.phone!),
              if (app.dateOfBirth != null)
                _DetailRow(
                  label: 'Date of birth',
                  value: formatApplicationDate(app.dateOfBirth!.toLocal()),
                ),
              if (app.currentAddress != null)
                _DetailRow(
                  label: 'Current address',
                  value: app.currentAddress!,
                ),
              if (app.email == null &&
                  app.phone == null &&
                  app.dateOfBirth == null &&
                  app.currentAddress == null)
                _EmptyHint(text: 'No contact details provided.'),
            ],
          ),
        ),
        const SizedBox(height: 12),

        // ── Application details ────────────────────────────────────────────
        _SectionCard(
          title: 'Application',
          child: Column(
            children: [
              if (app.employer != null)
                _DetailRow(label: 'Employer', value: app.employer!),
              if (app.monthlyIncome != null)
                _DetailRow(
                  label: 'Monthly income',
                  value: formatMonthlyIncome(app.monthlyIncome!),
                ),
              if (app.desiredMoveInDate != null)
                _DetailRow(
                  label: 'Desired move-in',
                  value: formatApplicationDate(
                    app.desiredMoveInDate!.toLocal(),
                  ),
                ),
              _DetailRow(
                label: 'Status',
                value: friendlyApplicationStatus(app.status),
              ),
              if (app.reviewedAtUtc != null)
                _DetailRow(
                  label: 'Reviewed',
                  value: formatApplicationDateTime(app.reviewedAtUtc!),
                ),
            ],
          ),
        ),
        const SizedBox(height: 12),

        // ── Notes ──────────────────────────────────────────────────────────
        if (app.notes != null && app.notes!.isNotEmpty) ...[
          _SectionCard(
            title: 'Notes',
            child: Text(app.notes!, style: theme.textTheme.bodyMedium),
          ),
          const SizedBox(height: 12),
        ],

        // ── Consent ────────────────────────────────────────────────────────
        _SectionCard(
          title: 'Consent',
          child: Row(
            children: [
              Icon(
                app.consentGiven
                    ? Icons.verified_outlined
                    : Icons.gpp_maybe_outlined,
                size: 18,
                color: app.consentGiven ? cs.primary : cs.error,
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  app.consentGiven
                      ? (app.consentAtUtc != null
                            ? 'Consent given ${formatApplicationDateTime(app.consentAtUtc!)}'
                            : 'Consent given')
                      : 'No consent on file',
                  style: theme.textTheme.bodyMedium,
                ),
              ),
            ],
          ),
        ),
        const SizedBox(height: 12),

        // ── Screening ──────────────────────────────────────────────────────
        _ScreeningSection(
          application: app,
          busy: busy,
          screeningAsync: screeningAsync,
          adverseAction: adverseAction,
          onRunIntegratedScreening: onRunIntegratedScreening,
          onTrackExternalScreening: onTrackExternalScreening,
          onMarkExternalComplete: onMarkExternalComplete,
          onRecordScreeningDecision: onRecordScreeningDecision,
          onUpdateScreeningAgency: onUpdateScreeningAgency,
          onOpenProvider: onOpenProvider,
          onGenerateAdverseAction: onGenerateAdverseAction,
          onViewNotice: onViewNotice,
        ),

        // ── Actions ────────────────────────────────────────────────────────
        if (open) ...[
          const SizedBox(height: 20),
          Text(
            'Decision',
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w700,
            ),
          ),
          const SizedBox(height: 8),
          Row(
            children: [
              Expanded(
                child: FilledButton.icon(
                  onPressed: busy ? null : onApprove,
                  icon: const Icon(Icons.check_circle_outline, size: 18),
                  label: const Text('Approve'),
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: busy ? null : onDecline,
                  icon: const Icon(Icons.do_not_disturb_alt_outlined, size: 18),
                  label: const Text('Decline'),
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          SizedBox(
            width: double.infinity,
            child: TextButton.icon(
              onPressed: busy ? null : onWithdraw,
              icon: const Icon(Icons.archive_outlined, size: 18),
              label: const Text('Withdraw application'),
            ),
          ),
          const SizedBox(height: 8),
          SizedBox(
            width: double.infinity,
            child: OutlinedButton.icon(
              onPressed: busy ? null : onRecordFee,
              icon: const Icon(Icons.attach_money, size: 18),
              label: const Text('Record application fee'),
            ),
          ),
        ],
        const SizedBox(height: 24),
      ],
    );
  }
}

// ── Edit application dialog ───────────────────────────────────────────────────

class _EditApplicationDialog extends StatefulWidget {
  const _EditApplicationDialog({required this.application});

  final RentalApplication application;

  @override
  State<_EditApplicationDialog> createState() => _EditApplicationDialogState();
}

class _EditApplicationDialogState extends State<_EditApplicationDialog> {
  late final TextEditingController _firstName;
  late final TextEditingController _lastName;
  late final TextEditingController _email;
  late final TextEditingController _phone;
  late final TextEditingController _dateOfBirth;
  late final TextEditingController _currentAddress;
  late final TextEditingController _employer;
  late final TextEditingController _monthlyIncome;
  late final TextEditingController _desiredMoveInDate;
  late final TextEditingController _notes;
  String? _error;

  @override
  void initState() {
    super.initState();
    final app = widget.application;
    _firstName = TextEditingController(text: app.firstName);
    _lastName = TextEditingController(text: app.lastName);
    _email = TextEditingController(text: app.email ?? '');
    _phone = TextEditingController(text: app.phone ?? '');
    _dateOfBirth = TextEditingController(
      text: _dateInputValue(app.dateOfBirth),
    );
    _currentAddress = TextEditingController(text: app.currentAddress ?? '');
    _employer = TextEditingController(text: app.employer ?? '');
    _monthlyIncome = TextEditingController(
      text: app.monthlyIncome == null
          ? ''
          : _numberInputValue(app.monthlyIncome!),
    );
    _desiredMoveInDate = TextEditingController(
      text: _dateInputValue(app.desiredMoveInDate),
    );
    _notes = TextEditingController(text: app.notes ?? '');
  }

  @override
  void dispose() {
    _firstName.dispose();
    _lastName.dispose();
    _email.dispose();
    _phone.dispose();
    _dateOfBirth.dispose();
    _currentAddress.dispose();
    _employer.dispose();
    _monthlyIncome.dispose();
    _desiredMoveInDate.dispose();
    _notes.dispose();
    super.dispose();
  }

  void _submit() {
    final firstName = _firstName.text.trim();
    final lastName = _lastName.text.trim();
    if (firstName.isEmpty || lastName.isEmpty) {
      setState(() => _error = 'First and last name are required.');
      return;
    }

    final incomeText = _monthlyIncome.text.trim();
    final income = incomeText.isEmpty ? null : double.tryParse(incomeText);
    if (incomeText.isNotEmpty && (income == null || income < 0)) {
      setState(() => _error = 'Enter a valid monthly income.');
      return;
    }

    Navigator.of(context).pop(
      UpdateApplicationInput(
        firstName: firstName,
        lastName: lastName,
        email: _email.text.trim(),
        phone: _phone.text.trim(),
        dateOfBirth: _dateOfBirth.text.trim().isEmpty
            ? null
            : _dateOfBirth.text.trim(),
        clearDateOfBirth: _dateOfBirth.text.trim().isEmpty,
        currentAddress: _currentAddress.text.trim(),
        employer: _employer.text.trim(),
        monthlyIncome: income,
        clearMonthlyIncome: income == null,
        desiredMoveInDate: _desiredMoveInDate.text.trim().isEmpty
            ? null
            : _desiredMoveInDate.text.trim(),
        clearDesiredMoveInDate: _desiredMoveInDate.text.trim().isEmpty,
        notes: _notes.text.trim(),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return AlertDialog(
      title: const Text('Edit application'),
      content: SizedBox(
        width: 520,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (_error != null) ...[
                Text(_error!, style: TextStyle(color: theme.colorScheme.error)),
                const SizedBox(height: 12),
              ],
              Row(
                children: [
                  Expanded(
                    child: TextField(
                      controller: _firstName,
                      textCapitalization: TextCapitalization.words,
                      decoration: const InputDecoration(
                        labelText: 'First name',
                        border: OutlineInputBorder(),
                      ),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: TextField(
                      controller: _lastName,
                      textCapitalization: TextCapitalization.words,
                      decoration: const InputDecoration(
                        labelText: 'Last name',
                        border: OutlineInputBorder(),
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 10),
              TextField(
                controller: _email,
                keyboardType: TextInputType.emailAddress,
                decoration: const InputDecoration(
                  labelText: 'Email',
                  border: OutlineInputBorder(),
                ),
              ),
              const SizedBox(height: 10),
              TextField(
                controller: _phone,
                keyboardType: TextInputType.phone,
                decoration: const InputDecoration(
                  labelText: 'Phone',
                  border: OutlineInputBorder(),
                ),
              ),
              const SizedBox(height: 10),
              Row(
                children: [
                  Expanded(
                    child: TextField(
                      controller: _dateOfBirth,
                      keyboardType: TextInputType.datetime,
                      decoration: const InputDecoration(
                        labelText: 'Date of birth',
                        hintText: 'YYYY-MM-DD',
                        border: OutlineInputBorder(),
                      ),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: TextField(
                      controller: _desiredMoveInDate,
                      keyboardType: TextInputType.datetime,
                      decoration: const InputDecoration(
                        labelText: 'Desired move-in',
                        hintText: 'YYYY-MM-DD',
                        border: OutlineInputBorder(),
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 10),
              TextField(
                controller: _currentAddress,
                textCapitalization: TextCapitalization.words,
                decoration: const InputDecoration(
                  labelText: 'Current address',
                  border: OutlineInputBorder(),
                ),
              ),
              const SizedBox(height: 10),
              Row(
                children: [
                  Expanded(
                    child: TextField(
                      controller: _employer,
                      textCapitalization: TextCapitalization.words,
                      decoration: const InputDecoration(
                        labelText: 'Employer',
                        border: OutlineInputBorder(),
                      ),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: TextField(
                      controller: _monthlyIncome,
                      keyboardType: const TextInputType.numberWithOptions(
                        decimal: true,
                      ),
                      decoration: const InputDecoration(
                        labelText: 'Monthly income',
                        border: OutlineInputBorder(),
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 10),
              TextField(
                controller: _notes,
                minLines: 2,
                maxLines: 4,
                textCapitalization: TextCapitalization.sentences,
                decoration: const InputDecoration(
                  labelText: 'Notes',
                  border: OutlineInputBorder(),
                ),
              ),
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(onPressed: _submit, child: const Text('Save')),
      ],
    );
  }
}

String _dateInputValue(DateTime? value) {
  if (value == null) return '';
  final local = value.toLocal();
  return '${local.year.toString().padLeft(4, '0')}-'
      '${local.month.toString().padLeft(2, '0')}-'
      '${local.day.toString().padLeft(2, '0')}';
}

String _numberInputValue(double value) {
  if (value == value.truncateToDouble()) return value.toInt().toString();
  return value.toString();
}

// ── Decline-with-reason dialog ──────────────────────────────────────────────────

class _DeclineDialog extends StatefulWidget {
  const _DeclineDialog();

  @override
  State<_DeclineDialog> createState() => _DeclineDialogState();
}

class _DeclineDialogState extends State<_DeclineDialog> {
  final _controller = TextEditingController();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Decline application'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Optionally add a reason. This may be shared with the applicant.',
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
              color: Theme.of(context).colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _controller,
            autofocus: true,
            minLines: 2,
            maxLines: 4,
            textCapitalization: TextCapitalization.sentences,
            decoration: const InputDecoration(
              labelText: 'Reason (optional)',
              border: OutlineInputBorder(),
            ),
          ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () => Navigator.of(context).pop(_controller.text.trim()),
          child: const Text('Decline'),
        ),
      ],
    );
  }
}

// ── Record application fee dialog ─────────────────────────────────────────────

class _RecordFeeInput {
  const _RecordFeeInput({
    required this.operationKey,
    required this.amount,
    this.method,
    this.effectiveOn,
  });
  final String operationKey;
  final double amount;
  final String? method;
  final DateTime? effectiveOn;
}

class _RecordFeeDialog extends StatefulWidget {
  const _RecordFeeDialog();

  @override
  State<_RecordFeeDialog> createState() => _RecordFeeDialogState();
}

class _RecordFeeDialogState extends State<_RecordFeeDialog> {
  final _amountController = TextEditingController();
  final _methodController = TextEditingController();
  DateTime? _effectiveOn;
  String? _amountError;

  @override
  void dispose() {
    _amountController.dispose();
    _methodController.dispose();
    super.dispose();
  }

  Future<void> _pickDate() async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _effectiveOn ?? now,
      firstDate: DateTime(now.year - 5),
      lastDate: DateTime(now.year + 1),
    );
    if (picked != null) setState(() => _effectiveOn = picked);
  }

  void _submit() {
    final amount = double.tryParse(_amountController.text.trim());
    if (amount == null || amount <= 0) {
      setState(() => _amountError = 'Enter an amount greater than zero.');
      return;
    }
    final method = _methodController.text.trim();
    Navigator.of(context).pop(
      _RecordFeeInput(
        operationKey: ApplicationsRepository.newOperationKey(),
        amount: amount,
        method: method.isEmpty ? null : method,
        effectiveOn: _effectiveOn,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return AlertDialog(
      title: const Text('Record application fee'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Records a collected application or screening fee in this '
            "application's financial account.",
            style: Theme.of(
              context,
            ).textTheme.bodySmall?.copyWith(color: cs.onSurfaceVariant),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _amountController,
            autofocus: true,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            decoration: InputDecoration(
              labelText: 'Amount',
              prefixText: '\$ ',
              border: const OutlineInputBorder(),
              errorText: _amountError,
            ),
            onChanged: (_) {
              if (_amountError != null) setState(() => _amountError = null);
            },
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _methodController,
            textCapitalization: TextCapitalization.words,
            decoration: const InputDecoration(
              labelText: 'Method (optional)',
              hintText: 'e.g. Card, Cash, Check',
              border: OutlineInputBorder(),
            ),
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              Expanded(
                child: Text(
                  _effectiveOn == null
                      ? 'Received date: today'
                      : 'Received date: ${_effectiveOn!.toIso8601String().split('T').first}',
                  style: Theme.of(context).textTheme.bodyMedium,
                ),
              ),
              TextButton(onPressed: _pickDate, child: const Text('Change')),
            ],
          ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(onPressed: _submit, child: const Text('Record fee')),
      ],
    );
  }
}

// ── Screening section ───────────────────────────────────────────────────────────

class _ScreeningSection extends StatelessWidget {
  const _ScreeningSection({
    required this.application,
    required this.busy,
    required this.screeningAsync,
    required this.adverseAction,
    required this.onRunIntegratedScreening,
    required this.onTrackExternalScreening,
    required this.onMarkExternalComplete,
    required this.onRecordScreeningDecision,
    required this.onUpdateScreeningAgency,
    required this.onOpenProvider,
    required this.onGenerateAdverseAction,
    required this.onViewNotice,
  });

  final RentalApplication application;
  final bool busy;
  final AsyncValue<ScreeningWorkspace> screeningAsync;
  final AdverseActionNotice? adverseAction;
  final VoidCallback onRunIntegratedScreening;
  final VoidCallback onTrackExternalScreening;
  final void Function(int screeningId) onMarkExternalComplete;
  final void Function(ApplicantScreening screening) onRecordScreeningDecision;
  final void Function(ApplicantScreening screening) onUpdateScreeningAgency;
  final void Function(String url) onOpenProvider;
  final VoidCallback onGenerateAdverseAction;
  final void Function(int storedFileId) onViewNotice;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final app = application;
    final hasConsent = app.consentGiven;
    final applicationOpen = isApplicationOpen(app.status);

    final latestScreening = screeningAsync.maybeWhen<ApplicantScreening?>(
      data: (workspace) =>
          workspace.screenings.isEmpty ? null : workspace.screenings.first,
      orElse: () => null,
    );

    return _SectionCard(
      title: 'Screening',
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Choose how this applicant is screened. Sensitive identity details and report contents stay on the provider’s secure site.',
            style: theme.textTheme.bodySmall?.copyWith(
              color: cs.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 16),
          screeningAsync.when(
            loading: () => const Padding(
              padding: EdgeInsets.symmetric(vertical: 4),
              child: Align(
                alignment: Alignment.centerLeft,
                child: SizedBox(
                  width: 18,
                  height: 18,
                  child: CircularProgressIndicator(strokeWidth: 2),
                ),
              ),
            ),
            error: (e, _) => _EmptyHint(
              text: e is ApiException ? e.message : "Couldn't load screening.",
            ),
            data: (workspace) {
              return Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  _ScreeningPathCard(
                    icon: Icons.verified_user_outlined,
                    title: 'Screen through Rental Command',
                    body: workspace.integratedProvider.isConfigured
                        ? 'Create a secure invitation for the applicant. They enter sensitive information on the provider’s site.'
                        : 'Provider selection is still being finalized. Use an outside screening for now.',
                    actionLabel: workspace.integratedProvider.isConfigured
                        ? 'Invite applicant'
                        : 'Not configured yet',
                    onPressed:
                        busy ||
                            !applicationOpen ||
                            !hasConsent ||
                            !workspace.integratedProvider.isConfigured
                        ? null
                        : onRunIntegratedScreening,
                  ),
                  const SizedBox(height: 10),
                  _ScreeningPathCard(
                    icon: Icons.open_in_new_outlined,
                    title: 'Screened elsewhere',
                    body:
                        'Track a Zillow or other outside screening without copying Social Security numbers or report contents into Rental Command.',
                    actionLabel: 'Add outside screening',
                    onPressed: busy || !applicationOpen
                        ? null
                        : onTrackExternalScreening,
                  ),
                  if (!hasConsent) ...[
                    const SizedBox(height: 8),
                    const _EmptyHint(
                      text:
                          'Applicant consent is required before an integrated screening can start.',
                    ),
                  ],
                  const Divider(height: 28),
                  if (workspace.screenings.isEmpty)
                    const _EmptyHint(
                      text:
                          'No screening has been added yet. Choose either path above.',
                    )
                  else
                    _ScreeningResultView(
                      result: workspace.screenings.first,
                      busy: busy,
                      onMarkExternalComplete: onMarkExternalComplete,
                      onRecordScreeningDecision: onRecordScreeningDecision,
                      onUpdateScreeningAgency: onUpdateScreeningAgency,
                      onOpenProvider: onOpenProvider,
                    ),
                ],
              );
            },
          ),

          // ── Adverse-action (declined + screened) ───────────────────────
          if (app.status == 'Declined' && latestScreening != null) ...[
            const Divider(height: 28),
            Text(
              'Adverse action',
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 6),
            Text(
              latestScreening.consumerReportUsedForDecision
                  ? 'When a consumer report influences a decline, the FCRA requires giving the applicant an adverse-action notice naming the consumer reporting agency and their right to dispute it.'
                  : 'No adverse-action notice is offered unless you record that a consumer report influenced the decline.',
              style: theme.textTheme.bodySmall?.copyWith(
                color: cs.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 12),
            if (adverseAction != null) ...[
              _AdverseActionView(notice: adverseAction!),
              const SizedBox(height: 10),
              if (adverseAction!.storedFileId != null)
                SizedBox(
                  width: double.infinity,
                  child: FilledButton.tonalIcon(
                    onPressed: () => onViewNotice(adverseAction!.storedFileId!),
                    icon: const Icon(Icons.picture_as_pdf_outlined, size: 18),
                    label: const Text('View notice (PDF)'),
                  ),
                ),
            ] else if (latestScreening.canGenerateAdverseAction)
              SizedBox(
                width: double.infinity,
                child: OutlinedButton.icon(
                  onPressed: busy ? null : onGenerateAdverseAction,
                  icon: const Icon(Icons.gavel_outlined, size: 18),
                  label: const Text('Generate adverse-action notice'),
                ),
              ),
          ],
        ],
      ),
    );
  }
}

/// Content-free summary of one screening workflow and the landlord's decision.
class _ScreeningResultView extends StatelessWidget {
  const _ScreeningResultView({
    required this.result,
    required this.busy,
    required this.onMarkExternalComplete,
    required this.onRecordScreeningDecision,
    required this.onUpdateScreeningAgency,
    required this.onOpenProvider,
  });

  final ApplicantScreening result;
  final bool busy;
  final void Function(int screeningId) onMarkExternalComplete;
  final void Function(ApplicantScreening screening) onRecordScreeningDecision;
  final void Function(ApplicantScreening screening) onUpdateScreeningAgency;
  final void Function(String url) onOpenProvider;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final r = result;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Text(
                friendlyScreeningStatus(r.status),
                style: theme.textTheme.bodyMedium?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
              ),
            ),
            if (r.decision != null)
              _RecommendationChip(recommendation: r.decision!),
          ],
        ),
        const SizedBox(height: 8),
        if (r.statusSummary.isNotEmpty || r.nextAction.isNotEmpty) ...[
          Container(
            width: double.infinity,
            padding: const EdgeInsets.all(12),
            decoration: BoxDecoration(
              color: cs.surfaceContainerHighest.withValues(alpha: 0.45),
              borderRadius: BorderRadius.circular(10),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                if (r.statusSummary.isNotEmpty)
                  Text(r.statusSummary, style: theme.textTheme.bodyMedium),
                if (r.nextAction.isNotEmpty) ...[
                  const SizedBox(height: 4),
                  Text(
                    'Next: ${r.nextAction}',
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: cs.onSurfaceVariant,
                    ),
                  ),
                ],
              ],
            ),
          ),
          const SizedBox(height: 8),
        ],
        _DetailRow(
          label: 'Path',
          value: r.mode == 'Integrated' ? 'Rental Command' : 'Outside service',
        ),
        _DetailRow(label: 'Provider', value: r.providerDisplayName),
        if (r.providerReference != null)
          _DetailRow(label: 'Reference', value: r.providerReference!),
        if (r.completedAtUtc != null)
          _DetailRow(
            label: 'Completed',
            value: formatApplicationDateTime(r.completedAtUtc!),
          )
        else if (r.invitedAtUtc != null)
          _DetailRow(
            label: 'Invited',
            value: formatApplicationDateTime(r.invitedAtUtc!),
          ),
        _DetailRow(
          label: 'Updated',
          value: formatApplicationDateTime(r.lastStatusAtUtc),
        ),
        _DetailRow(
          label: 'Consumer report used',
          value: r.decision == null
              ? 'Not recorded yet'
              : (r.consumerReportUsedForDecision ? 'Yes' : 'No'),
        ),
        _DetailRow(
          label: 'Agency contact',
          value: r.hasCompleteCreditReportingAgencyContact
              ? 'Complete'
              : (r.decision != null && !r.consumerReportUsedForDecision
                    ? 'Not required for this decision'
                    : 'Missing'),
        ),
        if (r.decisionReason != null)
          _DetailRow(label: 'Decision reason', value: r.decisionReason!),
        if (r.providerHostedUrl != null) ...[
          const SizedBox(height: 8),
          OutlinedButton.icon(
            onPressed: busy ? null : () => onOpenProvider(r.providerHostedUrl!),
            icon: const Icon(Icons.open_in_new, size: 18),
            label: const Text('Open provider'),
          ),
        ],
        if (r.canMarkExternalComplete) ...[
          const SizedBox(height: 8),
          OutlinedButton.icon(
            onPressed: busy ? null : () => onMarkExternalComplete(r.id),
            icon: const Icon(Icons.check_circle_outline, size: 18),
            label: const Text('Mark outside screening complete'),
          ),
        ],
        if (r.mode == 'External') ...[
          const SizedBox(height: 8),
          OutlinedButton.icon(
            onPressed: busy ? null : () => onUpdateScreeningAgency(r),
            icon: const Icon(Icons.contact_phone_outlined, size: 18),
            label: Text(
              r.hasCompleteCreditReportingAgencyContact
                  ? 'Update agency contact'
                  : 'Add agency contact',
            ),
          ),
        ],
        if (r.isCompleted) ...[
          const SizedBox(height: 8),
          OutlinedButton.icon(
            onPressed: busy ? null : () => onRecordScreeningDecision(r),
            icon: const Icon(Icons.how_to_reg_outlined, size: 18),
            label: Text(
              r.decision == null ? 'Record decision' : 'Update decision',
            ),
          ),
        ],
        if (r.status == 'Failed') ...[
          const SizedBox(height: 4),
          Text(
            'This screening request failed. Try running it again.',
            style: theme.textTheme.bodySmall?.copyWith(color: cs.error),
          ),
        ],
        if (r.decision != null &&
            r.consumerReportUsedForDecision &&
            !r.hasCompleteCreditReportingAgencyContact) ...[
          const SizedBox(height: 6),
          Text(
            'Agency name, mailing address, and phone are required for a report-based decision.',
            style: theme.textTheme.bodySmall?.copyWith(color: cs.error),
          ),
        ],
      ],
    );
  }
}

class _ScreeningPathCard extends StatelessWidget {
  const _ScreeningPathCard({
    required this.icon,
    required this.title,
    required this.body,
    required this.actionLabel,
    required this.onPressed,
  });

  final IconData icon;
  final String title;
  final String body;
  final String actionLabel;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card.outlined(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(icon, size: 20),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    title,
                    style: theme.textTheme.titleSmall?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 6),
            Text(body, style: theme.textTheme.bodySmall),
            const SizedBox(height: 10),
            OutlinedButton(onPressed: onPressed, child: Text(actionLabel)),
          ],
        ),
      ),
    );
  }
}

class _ExternalScreeningInput {
  const _ExternalScreeningInput({
    required this.provider,
    this.reference,
    this.url,
    this.agencyName,
    this.agencyAddress,
    this.agencyPhone,
  });

  final String provider;
  final String? reference;
  final String? url;
  final String? agencyName;
  final String? agencyAddress;
  final String? agencyPhone;
}

class _ScreeningDecisionInput {
  const _ScreeningDecisionInput({
    required this.decision,
    required this.consumerReportUsed,
    this.reason,
  });

  final String decision;
  final bool consumerReportUsed;
  final String? reason;
}

class _CraContactInput {
  const _CraContactInput({
    required this.name,
    required this.address,
    required this.phone,
  });

  final String name;
  final String address;
  final String phone;
}

class _CraContactDialog extends StatefulWidget {
  const _CraContactDialog({required this.initial});

  final ApplicantScreening initial;

  @override
  State<_CraContactDialog> createState() => _CraContactDialogState();
}

class _CraContactDialogState extends State<_CraContactDialog> {
  late final TextEditingController _name;
  late final TextEditingController _address;
  late final TextEditingController _phone;
  String? _error;

  @override
  void initState() {
    super.initState();
    _name = TextEditingController(
      text: widget.initial.creditReportingAgencyName,
    );
    _address = TextEditingController(
      text: widget.initial.creditReportingAgencyAddress,
    );
    _phone = TextEditingController(
      text: widget.initial.creditReportingAgencyPhone,
    );
  }

  @override
  void dispose() {
    _name.dispose();
    _address.dispose();
    _phone.dispose();
    super.dispose();
  }

  void _submit() {
    final name = _name.text.trim();
    final address = _address.text.trim();
    final phone = _phone.text.trim();
    if (name.isEmpty || address.isEmpty || phone.isEmpty) {
      setState(
        () => _error =
            'Enter the agency name, mailing address, and phone number.',
      );
      return;
    }
    Navigator.of(
      context,
    ).pop(_CraContactInput(name: name, address: address, phone: phone));
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Consumer reporting agency contact'),
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text(
              'Required only when a consumer report influences the decision. Saving here updates this screening; it does not create another one.',
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _name,
              decoration: const InputDecoration(labelText: 'Agency name'),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _address,
              decoration: const InputDecoration(labelText: 'Mailing address'),
              maxLines: 2,
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _phone,
              decoration: const InputDecoration(labelText: 'Phone'),
              keyboardType: TextInputType.phone,
            ),
            if (_error != null) ...[
              const SizedBox(height: 8),
              Text(
                _error!,
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: Theme.of(context).colorScheme.error,
                ),
              ),
            ],
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(onPressed: _submit, child: const Text('Save contact')),
      ],
    );
  }
}

class _ScreeningDecisionDialog extends StatefulWidget {
  const _ScreeningDecisionDialog({required this.screening});

  final ApplicantScreening screening;

  @override
  State<_ScreeningDecisionDialog> createState() =>
      _ScreeningDecisionDialogState();
}

class _ScreeningDecisionDialogState extends State<_ScreeningDecisionDialog> {
  late String _decision;
  late bool _consumerReportUsed;
  final _reason = TextEditingController();
  String? _error;

  @override
  void initState() {
    super.initState();
    _decision = widget.screening.decision ?? 'Accept';
    _consumerReportUsed = widget.screening.consumerReportUsedForDecision;
    _reason.text = widget.screening.decisionReason ?? '';
  }

  @override
  void dispose() {
    _reason.dispose();
    super.dispose();
  }

  void _submit() {
    final reason = _reason.text.trim();
    if (_consumerReportUsed && reason.isEmpty) {
      setState(() => _error = 'Enter the principal decision reason.');
      return;
    }
    if (_consumerReportUsed &&
        !widget.screening.hasCompleteCreditReportingAgencyContact) {
      setState(
        () => _error =
            'Save the agency name, mailing address, and phone before recording a report-based decision.',
      );
      return;
    }
    Navigator.of(context).pop(
      _ScreeningDecisionInput(
        decision: _decision,
        consumerReportUsed: _consumerReportUsed,
        reason: reason.isEmpty ? null : reason,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Record screening decision'),
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            DropdownButtonFormField<String>(
              initialValue: _decision,
              decoration: const InputDecoration(labelText: 'Decision'),
              items: const [
                DropdownMenuItem(value: 'Accept', child: Text('Accept')),
                DropdownMenuItem(
                  value: 'Conditional',
                  child: Text('Conditional'),
                ),
                DropdownMenuItem(value: 'Decline', child: Text('Decline')),
              ],
              onChanged: (value) {
                if (value != null) setState(() => _decision = value);
              },
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _reason,
              decoration: const InputDecoration(labelText: 'Reason (optional)'),
              maxLines: 3,
            ),
            const SizedBox(height: 8),
            SwitchListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Consumer report influenced this decision'),
              subtitle: const Text(
                'Turn this on when the outside or integrated report affected the decision. Rental Command can then guide adverse-action steps.',
              ),
              value: _consumerReportUsed,
              onChanged: (value) => setState(() => _consumerReportUsed = value),
            ),
            if (_error != null) ...[
              const SizedBox(height: 8),
              Text(
                _error!,
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: Theme.of(context).colorScheme.error,
                ),
              ),
            ],
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(onPressed: _submit, child: const Text('Save decision')),
      ],
    );
  }
}

class _ExternalScreeningDialog extends StatefulWidget {
  const _ExternalScreeningDialog();

  @override
  State<_ExternalScreeningDialog> createState() =>
      _ExternalScreeningDialogState();
}

class _ExternalScreeningDialogState extends State<_ExternalScreeningDialog> {
  final _provider = TextEditingController(text: 'Zillow');
  final _reference = TextEditingController();
  final _url = TextEditingController();
  final _agencyName = TextEditingController();
  final _agencyAddress = TextEditingController();
  final _agencyPhone = TextEditingController();
  String? _error;

  @override
  void dispose() {
    _provider.dispose();
    _reference.dispose();
    _url.dispose();
    _agencyName.dispose();
    _agencyAddress.dispose();
    _agencyPhone.dispose();
    super.dispose();
  }

  String? _optional(TextEditingController controller) {
    final value = controller.text.trim();
    return value.isEmpty ? null : value;
  }

  void _submit() {
    final provider = _provider.text.trim();
    if (provider.isEmpty) {
      setState(() => _error = 'Enter the screening service.');
      return;
    }
    final url = _url.text.trim();
    if (url.isNotEmpty) {
      final uri = Uri.tryParse(url);
      if (uri == null ||
          (uri.scheme != 'https' && uri.scheme != 'http') ||
          !uri.hasAuthority) {
        setState(() => _error = 'Enter a complete provider link.');
        return;
      }
    }
    Navigator.of(context).pop(
      _ExternalScreeningInput(
        provider: provider,
        reference: _optional(_reference),
        url: _optional(_url),
        agencyName: _optional(_agencyName),
        agencyAddress: _optional(_agencyAddress),
        agencyPhone: _optional(_agencyPhone),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Add outside screening'),
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text(
              'Track progress only. Do not enter Social Security numbers, identity answers, or report contents.',
            ),
            const SizedBox(height: 16),
            TextField(
              controller: _provider,
              decoration: const InputDecoration(labelText: 'Screening service'),
              textInputAction: TextInputAction.next,
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _reference,
              decoration: const InputDecoration(
                labelText: 'Reference (optional)',
              ),
              textInputAction: TextInputAction.next,
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _url,
              decoration: const InputDecoration(
                labelText: 'Provider link (optional)',
                hintText: 'https://…',
              ),
              keyboardType: TextInputType.url,
              textInputAction: TextInputAction.next,
            ),
            const SizedBox(height: 16),
            Text(
              'Consumer reporting agency contact',
              style: Theme.of(context).textTheme.titleSmall,
            ),
            const SizedBox(height: 4),
            Text(
              'Add the agency name, mailing address, and phone if its report may influence your decision. These are not required when no consumer report is used.',
              style: Theme.of(context).textTheme.bodySmall,
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _agencyName,
              decoration: const InputDecoration(labelText: 'Agency name'),
              textInputAction: TextInputAction.next,
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _agencyAddress,
              decoration: const InputDecoration(labelText: 'Agency address'),
              textInputAction: TextInputAction.next,
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _agencyPhone,
              decoration: const InputDecoration(labelText: 'Agency phone'),
              keyboardType: TextInputType.phone,
            ),
            if (_error != null) ...[
              const SizedBox(height: 8),
              Text(
                _error!,
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              ),
            ],
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(onPressed: _submit, child: const Text('Add screening')),
      ],
    );
  }
}

/// Accept / Conditional / Decline chip for the landlord's recorded decision.
class _RecommendationChip extends StatelessWidget {
  const _RecommendationChip({required this.recommendation});

  final String recommendation;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    Color bg;
    Color fg;
    switch (recommendation) {
      case 'Accept':
        bg = cs.tertiaryContainer;
        fg = cs.onTertiaryContainer;
        break;
      case 'Conditional':
        bg = cs.secondaryContainer;
        fg = cs.onSecondaryContainer;
        break;
      case 'Decline':
        bg = cs.errorContainer;
        fg = cs.onErrorContainer;
        break;
      default:
        bg = cs.surfaceContainerHighest;
        fg = cs.onSurfaceVariant;
    }
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        recommendation,
        style: TextStyle(fontSize: 11, fontWeight: FontWeight.w700, color: fg),
      ),
    );
  }
}

/// Inline summary of a generated adverse-action notice.
class _AdverseActionView extends StatelessWidget {
  const _AdverseActionView({required this.notice});

  final AdverseActionNotice notice;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return Card(
      margin: EdgeInsets.zero,
      color: cs.surfaceContainerHighest,
      elevation: 0,
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(Icons.description_outlined, size: 18, color: cs.primary),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    'Adverse-action notice generated',
                    style: theme.textTheme.bodyMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            if (notice.reason != null && notice.reason!.isNotEmpty)
              _DetailRow(label: 'Reason', value: notice.reason!),
            if (notice.creditReportingAgency != null)
              _DetailRow(label: 'Agency', value: notice.creditReportingAgency!),
            if (notice.generatedAtUtc != null)
              _DetailRow(
                label: 'Generated',
                value: formatApplicationDateTime(notice.generatedAtUtc!),
              ),
            _DetailRow(
              label: 'Sent',
              value: notice.sentAtUtc != null
                  ? formatApplicationDateTime(notice.sentAtUtc!)
                  : 'Not sent to applicant',
            ),
          ],
        ),
      ),
    );
  }
}

// ── Adverse-action dialog ───────────────────────────────────────────────────────

class _AdverseActionInput {
  const _AdverseActionInput({
    required this.reason,
    required this.sendToApplicant,
  });

  final String reason;
  final bool sendToApplicant;
}

class _AdverseActionDialog extends StatefulWidget {
  const _AdverseActionDialog({required this.initialReason});

  final String initialReason;

  @override
  State<_AdverseActionDialog> createState() => _AdverseActionDialogState();
}

class _AdverseActionDialogState extends State<_AdverseActionDialog> {
  late final TextEditingController _controller = TextEditingController(
    text: widget.initialReason,
  );
  bool _sendToApplicant = false;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return AlertDialog(
      title: const Text('Adverse-action notice'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'This generates an FCRA-compliant notice for the declined applicant. '
            'Add or adjust the reason below.',
            style: theme.textTheme.bodySmall?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _controller,
            autofocus: true,
            minLines: 2,
            maxLines: 4,
            textCapitalization: TextCapitalization.sentences,
            decoration: const InputDecoration(
              labelText: 'Reason',
              border: OutlineInputBorder(),
            ),
          ),
          const SizedBox(height: 4),
          CheckboxListTile(
            value: _sendToApplicant,
            onChanged: (v) => setState(() => _sendToApplicant = v ?? false),
            contentPadding: EdgeInsets.zero,
            controlAffinity: ListTileControlAffinity.leading,
            title: const Text('Send to applicant'),
            subtitle: const Text('Email the notice to the applicant now.'),
          ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () => Navigator.of(context).pop(
            _AdverseActionInput(
              reason: _controller.text.trim(),
              sendToApplicant: _sendToApplicant,
            ),
          ),
          child: const Text('Generate'),
        ),
      ],
    );
  }
}

// ── Small reusable widgets ──────────────────────────────────────────────────────

class _SectionCard extends StatelessWidget {
  const _SectionCard({required this.title, required this.child});

  final String title;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              title,
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 12),
            child,
          ],
        ),
      ),
    );
  }
}

class _DetailRow extends StatelessWidget {
  const _DetailRow({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 120,
            child: Text(
              label,
              style: TextStyle(
                fontSize: 12,
                color: cs.onSurfaceVariant,
                fontWeight: FontWeight.w500,
              ),
            ),
          ),
          Expanded(
            child: Text(
              value,
              style: theme.textTheme.bodySmall?.copyWith(
                fontWeight: FontWeight.w600,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _EmptyHint extends StatelessWidget {
  const _EmptyHint({required this.text});

  final String text;

  @override
  Widget build(BuildContext context) {
    return Align(
      alignment: Alignment.centerLeft,
      child: Text(
        text,
        style: TextStyle(
          fontSize: 13,
          color: Theme.of(context).colorScheme.onSurfaceVariant,
        ),
      ),
    );
  }
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.message, required this.onRetry});

  final String message;
  final Future<void> Function() onRetry;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.error_outline, size: 40, color: cs.error),
            const SizedBox(height: 12),
            Text(
              message,
              textAlign: TextAlign.center,
              style: TextStyle(color: cs.error),
            ),
            const SizedBox(height: 16),
            FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}
