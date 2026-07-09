import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/files/document_opener.dart';
import '../../core/models/models.dart';
import '../activity/activity_history_screen.dart';
import '../home/mobile_domain_navigation.dart';
import '../notices/create_tenant_notice.dart';
import '../payments/payment_detail_screen.dart';
import '../payments/payments_repository.dart';
import '../payments/record_payment_sheet.dart';
import '../properties/property_detail_screen.dart';
import '../units/unit_command_center_screen.dart';
import '../units/unit_navigation.dart';
import 'eviction_cases_repository.dart';
import 'lease_eviction_case_form_sheet.dart';
import 'lease_ledger_view.dart';
import 'leases_list_screen.dart';
import 'leases_repository.dart';

const _monthNames = [
  '',
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec',
];

String _fmt(DateTime d) => '${_monthNames[d.month]} ${d.day}, ${d.year}';

String _fmtNullable(DateTime? d) {
  if (d == null || d.year <= 1) return '-';
  return _fmt(d);
}

String _formatCurrency(double amount) {
  final rounded = amount.round();
  final s = rounded.toString();
  final buf = StringBuffer(r'$');
  final start = s.length % 3;
  if (start > 0) buf.write(s.substring(0, start));
  for (var i = start; i < s.length; i += 3) {
    if (i > 0) buf.write(',');
    buf.write(s.substring(i, i + 3));
  }
  return buf.toString();
}

void _openTenantDetail(BuildContext context, Lease lease) {
  _openTenantIdDetail(context, lease, lease.tenantId);
}

void _openTenantIdDetail(BuildContext context, Lease lease, int tenantId) {
  openUnitCommandCenter(
    context,
    unitId: lease.unitId,
    initialTab: UnitCommandCenterTab.tenants,
    lease: lease,
    tenantId: tenantId,
  );
}

void _openPropertyDetail(BuildContext context, int propertyId) {
  final shellNavigator = mobileShellNavigatorOf(context);
  if (shellNavigator != null) {
    shellNavigator.openTab(
      MobileShellTabId.rentals,
      destination: MobileDestinationId.properties,
      detailBuilder: (_) => PropertyDetailLoaderScreen(propertyId: propertyId),
    );
    revealMobileShellIfDetached(context);
    return;
  }

  final domainNavigator = MobileDomainNavigation.maybeOf(context);
  if (domainNavigator != null) {
    domainNavigator.openDestination(
      MobileDestinationId.properties,
      detailBuilder: (_) => PropertyDetailLoaderScreen(propertyId: propertyId),
    );
    return;
  }

  Navigator.of(context).push<void>(
    MaterialPageRoute<void>(
      builder: (_) => PropertyDetailLoaderScreen(propertyId: propertyId),
    ),
  );
}

/// Loads a lease by id, then lands in the Unit command center's Lease tab. Use
/// this when the caller only has a lease id (e.g. a payment, which carries
/// `leaseId` but not the full model).
class LeaseDetailLoaderScreen extends ConsumerWidget {
  const LeaseDetailLoaderScreen({super.key, required this.leaseId});

  final int leaseId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(leaseDetailProvider(leaseId));
    return async.when(
      loading: () =>
          const Scaffold(body: Center(child: CircularProgressIndicator())),
      error: (e, _) => Scaffold(
        appBar: AppBar(title: const Text('Lease')),
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
      data: (lease) => UnitCommandCenterLoaderScreen(
        unitId: lease.unitId,
        initialTab: UnitCommandCenterTab.lease,
        initialLease: lease,
      ),
    );
  }
}

/// Detail screen for a single lease.
///
/// Shows full lease information with edit support and status action buttons.
class LeaseDetailScreen extends ConsumerStatefulWidget {
  const LeaseDetailScreen({
    super.key,
    required this.lease,
    this.leadingContent,
  });

  final Lease lease;
  final Widget? leadingContent;

  @override
  ConsumerState<LeaseDetailScreen> createState() => _LeaseDetailScreenState();
}

class _LeaseDetailScreenState extends ConsumerState<LeaseDetailScreen> {
  late Lease _lease;
  bool _updatingStatus = false;
  String? _statusError;
  final _questionController = TextEditingController();
  bool _askingLease = false;
  String? _leaseAnswer;
  String? _leaseAskError;

  // Lease-agreement PDF actions.
  bool _generatingDoc = false;
  bool _openingDoc = false;
  String? _docError;

  // E-signature workflow.
  LeaseSignatureStatus? _signature;
  bool _loadingSignature = false;
  bool _sendingSignature = false;
  bool _openingSignedDoc = false;
  String? _signatureError;

  @override
  void initState() {
    super.initState();
    _lease = widget.lease;
    _loadSignatureStatus();
  }

  @override
  void dispose() {
    _questionController.dispose();
    super.dispose();
  }

  Future<void> _refresh() async {
    await Future.wait<void>([
      ref.read(leaseDetailProvider(_lease.id).notifier).refresh(),
      ref.read(leaseEvictionCasesProvider(_lease.id).notifier).refresh(),
    ]);
    final updated = ref.read(leaseDetailProvider(_lease.id));
    updated.whenData((l) {
      if (mounted) setState(() => _lease = l);
    });
  }

  void _showEditSheet(BuildContext context) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      isDismissible: false,
      enableDrag: false,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => LeaseFormSheet(existing: _lease, onSaved: _refresh),
    );
  }

  void _showActivityHistory() {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => ActivityHistoryScreen(
          entityType: 'Lease',
          entityId: _lease.id,
          title: 'Lease activity',
          subtitle: 'Lease #${_lease.leaseNumber}',
        ),
      ),
    );
  }

  Future<void> _showAddEvictionCaseSheet(BuildContext context) async {
    final saved = await showEvictionCaseFormSheet(context, leaseId: _lease.id);
    if (saved && mounted) await _refreshAfterEvictionChange();
  }

  Future<void> _showEditEvictionCaseSheet(
    BuildContext context,
    EvictionCase evictionCase,
  ) async {
    final saved = await showEvictionCaseFormSheet(
      context,
      leaseId: _lease.id,
      evictionCase: evictionCase,
    );
    if (saved && mounted) await _refreshAfterEvictionChange();
  }

  Future<void> _showAddEvictionEventSheet(
    BuildContext context,
    EvictionCase evictionCase,
  ) async {
    final saved = await showEvictionEventFormSheet(
      context,
      evictionCase: evictionCase,
    );
    if (saved && mounted) await _refreshAfterEvictionChange();
  }

  Future<void> _refreshAfterEvictionChange() async {
    await Future.wait<void>([
      ref.read(leaseEvictionCasesProvider(_lease.id).notifier).refresh(),
      ref.read(leaseDetailProvider(_lease.id).notifier).refresh(),
      ref.read(leasesProvider.notifier).refresh(),
    ]);
    final updated = ref.read(leaseDetailProvider(_lease.id));
    updated.whenData((l) {
      if (mounted) setState(() => _lease = l);
    });
  }

  Future<void> _confirmDeleteEvictionCase(
    BuildContext context,
    EvictionCase evictionCase,
  ) async {
    final label = evictionCase.caseNumber?.trim().isNotEmpty == true
        ? evictionCase.caseNumber!.trim()
        : '#${evictionCase.id}';
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text('Remove eviction case $label?'),
        content: const Text(
          'This removes the eviction case and its event timeline.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
    if (confirmed != true || !context.mounted) return;

    final messenger = ScaffoldMessenger.of(context);
    try {
      await ref
          .read(evictionCasesRepositoryProvider)
          .deleteCase(evictionCase.id);
      await ref.read(leaseEvictionCasesProvider(_lease.id).notifier).refresh();
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Eviction case removed.')));
    } on ApiException catch (e) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    } catch (_) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          const SnackBar(content: Text('Could not remove eviction case.')),
        );
    }
  }

  Future<void> _updateStatus(String newStatus) async {
    setState(() {
      _updatingStatus = true;
      _statusError = null;
    });
    try {
      final updated = await ref
          .read(leasesRepositoryProvider)
          .updateStatus(_lease.id, newStatus);
      if (mounted) setState(() => _lease = updated);
      // Also refresh the list provider so it stays consistent.
      ref.read(leasesProvider.notifier).refresh();
    } on ApiException catch (e) {
      if (mounted) setState(() => _statusError = e.message);
    } finally {
      if (mounted) setState(() => _updatingStatus = false);
    }
  }

  Future<void> _createNotice() async {
    if (_lease.tenantId <= 0) {
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          const SnackBar(content: Text('This lease has no tenant to notify.')),
        );
      return;
    }

    final tenantName = (_lease.tenantName ?? '').trim();
    await showCreateTenantNoticeFlow(
      context,
      ref,
      tenantId: _lease.tenantId,
      leaseId: _lease.id,
      tenantName: tenantName.isEmpty
          ? 'tenant #${_lease.tenantId}'
          : tenantName,
    );
  }

  Future<void> _askLease() async {
    final question = _questionController.text.trim();
    if (question.isEmpty) return;

    setState(() {
      _askingLease = true;
      _leaseAnswer = null;
      _leaseAskError = null;
    });

    try {
      final response = await ref
          .read(leasesRepositoryProvider)
          .ask(_lease.id, question);
      if (mounted) setState(() => _leaseAnswer = response.answer);
    } on ApiException catch (e) {
      if (mounted) setState(() => _leaseAskError = e.message);
    } finally {
      if (mounted) setState(() => _askingLease = false);
    }
  }

  /// Builds (or rebuilds) the standard lease-agreement PDF from the lease's
  /// captured terms, then opens it for the landlord to review.
  Future<void> _generateDocument() async {
    if (_generatingDoc) return;
    setState(() {
      _generatingDoc = true;
      _docError = null;
    });
    final messenger = ScaffoldMessenger.of(context);
    try {
      await ref.read(leasesRepositoryProvider).generateDocument(_lease.id);
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          const SnackBar(content: Text('Lease agreement generated.')),
        );
      await _openDocument();
    } on ApiException catch (e) {
      if (mounted) setState(() => _docError = e.message);
    } finally {
      if (mounted) setState(() => _generatingDoc = false);
    }
  }

  /// Fetches the generated PDF bytes (authed) and hands them to the OS viewer
  /// via [DocumentOpener] (FileProvider `content://` URI, share-sheet fallback).
  Future<void> _openDocument() async {
    if (_openingDoc) return;
    setState(() {
      _openingDoc = true;
      _docError = null;
    });
    try {
      final bytes = await ref
          .read(leasesRepositoryProvider)
          .documentBytes(_lease.id);
      await DocumentOpener.openBytes(
        bytes: bytes,
        fileName: 'lease-${_lease.id}-agreement.pdf',
      );
    } on ApiException catch (e) {
      if (mounted) setState(() => _docError = e.message);
    } finally {
      if (mounted) setState(() => _openingDoc = false);
    }
  }

  /// Loads the current e-signature status for the lease. Silently ignores a
  /// 503 (provider not configured) — the card stays in its default state and
  /// the action handles the gentle messaging when the user actually tries.
  Future<void> _loadSignatureStatus() async {
    if (_loadingSignature) return;
    setState(() {
      _loadingSignature = true;
      _signatureError = null;
    });
    try {
      final status = await ref
          .read(leasesRepositoryProvider)
          .signatureStatus(_lease.id);
      if (mounted) setState(() => _signature = status);
    } on ApiException catch (e) {
      // 503 = gated/not configured; leave the card in its neutral state.
      if (e.statusCode != 503 && mounted) {
        setState(() => _signatureError = e.message);
      }
    } finally {
      if (mounted) setState(() => _loadingSignature = false);
    }
  }

  /// Sends the lease to the e-sign provider, then refreshes the status. On a
  /// 503 (not configured) shows a gentle snackbar instead of an error.
  Future<void> _sendForSignature() async {
    if (_sendingSignature) return;
    setState(() {
      _sendingSignature = true;
      _signatureError = null;
    });
    final messenger = ScaffoldMessenger.of(context);
    try {
      final status = await ref
          .read(leasesRepositoryProvider)
          .sendForSignature(_lease.id);
      if (!mounted) return;
      setState(() => _signature = status);
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          const SnackBar(content: Text('Lease sent for signature.')),
        );
    } on ApiException catch (e) {
      if (!mounted) return;
      if (e.statusCode == 503) {
        messenger
          ..hideCurrentSnackBar()
          ..showSnackBar(
            const SnackBar(content: Text('E-signature isn’t set up yet.')),
          );
      } else {
        setState(() => _signatureError = e.message);
      }
    } finally {
      if (mounted) setState(() => _sendingSignature = false);
    }
  }

  /// Fetches the signed lease PDF bytes (authed) and hands them to the OS viewer
  /// via [DocumentOpener] (FileProvider `content://` URI, share-sheet fallback).
  Future<void> _openSignedDocument() async {
    if (_openingSignedDoc) return;
    setState(() {
      _openingSignedDoc = true;
      _signatureError = null;
    });
    try {
      final bytes = await ref
          .read(leasesRepositoryProvider)
          .signedDocumentBytes(_lease.id);
      await DocumentOpener.openBytes(
        bytes: bytes,
        fileName: 'lease-${_lease.id}-signed.pdf',
      );
    } on ApiException catch (e) {
      if (mounted) setState(() => _signatureError = e.message);
    } finally {
      if (mounted) setState(() => _openingSignedDoc = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final evictionCasesAsync = ref.watch(leaseEvictionCasesProvider(_lease.id));

    return Scaffold(
      appBar: AppBar(
        title: Text(
          'Lease #${_lease.leaseNumber}',
          overflow: TextOverflow.ellipsis,
        ),
        actions: [
          IconButton(
            icon: const Icon(Icons.history_outlined),
            tooltip: 'View lease activity',
            onPressed: _showActivityHistory,
          ),
          IconButton(
            icon: const Icon(Icons.edit_outlined),
            tooltip: 'Edit lease',
            onPressed: () => _showEditSheet(context),
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            if (widget.leadingContent != null) ...[
              widget.leadingContent!,
              const SizedBox(height: 12),
            ],
            // ── Header card ────────────────────────────────────────────────
            _LeaseHeaderCard(
              lease: _lease,
              theme: theme,
              colorScheme: colorScheme,
            ),
            const SizedBox(height: 16),

            // ── Financial details ──────────────────────────────────────────
            _SectionCard(
              title: 'Financials',
              theme: theme,
              colorScheme: colorScheme,
              children: [
                _RowKV(
                  label: 'Monthly rent',
                  value: _formatCurrency(_lease.monthlyRent),
                ),
                _RowKV(
                  label: 'Security deposit',
                  value: _formatCurrency(_lease.securityDeposit),
                ),
                _RowKV(
                  label: 'Late fee',
                  value: _formatCurrency(_lease.lateFeeAmount),
                ),
                _RowKV(
                  label: 'Rent due day',
                  value: 'Day ${_lease.rentDueDay}',
                ),
              ],
            ),
            const SizedBox(height: 12),

            // ── Account history (transparent ledger) ───────────────────────
            Card(
              child: ListTile(
                leading: const Icon(Icons.receipt_long_outlined),
                title: const Text('Account history'),
                subtitle: const Text('Every charge and payment, explained'),
                trailing: const Icon(Icons.chevron_right),
                onTap: () => Navigator.of(context).push<void>(
                  MaterialPageRoute<void>(
                    builder: (_) => Scaffold(
                      appBar: AppBar(
                        title: Text('Lease #${_lease.leaseNumber} history'),
                      ),
                      body: LeaseLedgerView(leaseId: _lease.id),
                    ),
                  ),
                ),
              ),
            ),
            const SizedBox(height: 12),

            // ── Payments ───────────────────────────────────────────────────
            _LeasePaymentsSection(leaseId: _lease.id),
            const SizedBox(height: 12),

            // ── Lease agreement (PDF) ──────────────────────────────────────
            _LeaseDocumentCard(
              generating: _generatingDoc,
              opening: _openingDoc,
              error: _docError,
              onGenerate: _generateDocument,
              onView: _openDocument,
              theme: theme,
              colorScheme: colorScheme,
            ),
            const SizedBox(height: 12),

            // ── E-signature ────────────────────────────────────────────────
            _LeaseSignatureCard(
              signature: _signature,
              loading: _loadingSignature,
              sending: _sendingSignature,
              openingSigned: _openingSignedDoc,
              error: _signatureError,
              onSend: _sendForSignature,
              onViewSigned: _openSignedDocument,
              theme: theme,
              colorScheme: colorScheme,
            ),
            const SizedBox(height: 12),

            // ── Dates ──────────────────────────────────────────────────────
            _SectionCard(
              title: 'Dates',
              theme: theme,
              colorScheme: colorScheme,
              children: [
                _RowKV(label: 'Start date', value: _fmt(_lease.startDate)),
                _RowKV(label: 'End date', value: _fmt(_lease.endDate)),
                if (_lease.moveInDate != null)
                  _RowKV(label: 'Move-in', value: _fmt(_lease.moveInDate!)),
                if (_lease.moveOutDate != null)
                  _RowKV(label: 'Move-out', value: _fmt(_lease.moveOutDate!)),
              ],
            ),
            const SizedBox(height: 16),

            // ── Status actions ─────────────────────────────────────────────
            _StatusActions(
              currentStatus: _lease.status,
              loading: _updatingStatus,
              error: _statusError,
              onSetStatus: _updateStatus,
              onCreateNotice: _createNotice,
              theme: theme,
              colorScheme: colorScheme,
            ),
            const SizedBox(height: 16),

            _LeaseEvictionCasesCard(
              casesAsync: evictionCasesAsync,
              onAdd: () => _showAddEvictionCaseSheet(context),
              onEdit: (evictionCase) =>
                  _showEditEvictionCaseSheet(context, evictionCase),
              onAddEvent: (evictionCase) =>
                  _showAddEvictionEventSheet(context, evictionCase),
              onDelete: (evictionCase) =>
                  _confirmDeleteEvictionCase(context, evictionCase),
              onLoadMore: () => ref
                  .read(leaseEvictionCasesProvider(_lease.id).notifier)
                  .loadMore(),
              theme: theme,
              colorScheme: colorScheme,
            ),
            const SizedBox(height: 16),

            _LeaseAskCard(
              controller: _questionController,
              loading: _askingLease,
              answer: _leaseAnswer,
              error: _leaseAskError,
              onAsk: _askLease,
              theme: theme,
              colorScheme: colorScheme,
            ),

            const SizedBox(height: 32),
          ],
        ),
      ),
    );
  }
}

enum _EvictionAction { addEvent, edit, delete }

class _LeaseEvictionCasesCard extends StatelessWidget {
  const _LeaseEvictionCasesCard({
    required this.casesAsync,
    required this.onAdd,
    required this.onEdit,
    required this.onAddEvent,
    required this.onDelete,
    required this.onLoadMore,
    required this.theme,
    required this.colorScheme,
  });

  final AsyncValue<LeaseEvictionCasesPage> casesAsync;
  final VoidCallback onAdd;
  final ValueChanged<EvictionCase> onEdit;
  final ValueChanged<EvictionCase> onAddEvent;
  final ValueChanged<EvictionCase> onDelete;
  final VoidCallback onLoadMore;
  final ThemeData theme;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    'Eviction Cases',
                    style: theme.textTheme.titleSmall?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                IconButton(
                  icon: const Icon(Icons.add),
                  tooltip: 'File eviction case',
                  onPressed: onAdd,
                ),
              ],
            ),
            const SizedBox(height: 8),
            casesAsync.when(
              loading: () => const Padding(
                padding: EdgeInsets.symmetric(vertical: 12),
                child: Center(child: CircularProgressIndicator()),
              ),
              error: (e, _) => Text(
                e is ApiException ? e.message : e.toString(),
                style: TextStyle(color: colorScheme.error, fontSize: 13),
              ),
              data: (page) {
                if (page.cases.isEmpty) {
                  return Text(
                    'No eviction case recorded for this lease.',
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: colorScheme.onSurfaceVariant,
                    ),
                  );
                }

                return Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    ...page.cases.map(
                      (evictionCase) => _EvictionCaseTile(
                        evictionCase: evictionCase,
                        onEdit: () => onEdit(evictionCase),
                        onAddEvent: () => onAddEvent(evictionCase),
                        onDelete: () => onDelete(evictionCase),
                      ),
                    ),
                    if (page.loadMoreError != null)
                      Padding(
                        padding: const EdgeInsets.only(top: 4, bottom: 8),
                        child: Text(
                          page.loadMoreError is ApiException
                              ? (page.loadMoreError! as ApiException).message
                              : 'Could not load more eviction cases.',
                          style: TextStyle(
                            color: colorScheme.error,
                            fontSize: 12,
                          ),
                          textAlign: TextAlign.center,
                        ),
                      ),
                    if (page.hasMore || page.isLoadingMore)
                      Padding(
                        padding: const EdgeInsets.only(top: 4),
                        child: OutlinedButton.icon(
                          icon: page.isLoadingMore
                              ? const SizedBox(
                                  width: 16,
                                  height: 16,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 2,
                                  ),
                                )
                              : const Icon(Icons.expand_more),
                          label: Text(
                            page.isLoadingMore
                                ? 'Loading cases...'
                                : 'Load more cases',
                          ),
                          onPressed: page.isLoadingMore ? null : onLoadMore,
                        ),
                      ),
                  ],
                );
              },
            ),
          ],
        ),
      ),
    );
  }
}

class _EvictionCaseTile extends StatelessWidget {
  const _EvictionCaseTile({
    required this.evictionCase,
    required this.onEdit,
    required this.onAddEvent,
    required this.onDelete,
  });

  final EvictionCase evictionCase;
  final VoidCallback onEdit;
  final VoidCallback onAddEvent;
  final VoidCallback onDelete;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final isResolved =
        evictionCase.status == EvictionCaseStatus.dismissed ||
        evictionCase.status == EvictionCaseStatus.settled ||
        evictionCase.status == EvictionCaseStatus.moveOut;

    return Card(
      key: Key('eviction-case-${evictionCase.id}'),
      margin: const EdgeInsets.only(bottom: 8),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(8, 8, 8, 10),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            ListTile(
              contentPadding: const EdgeInsets.only(left: 4),
              leading: CircleAvatar(
                radius: 20,
                backgroundColor: isResolved
                    ? colorScheme.surfaceContainerHighest
                    : colorScheme.tertiaryContainer,
                child: Icon(
                  isResolved ? Icons.fact_check_outlined : Icons.gavel_outlined,
                  size: 18,
                  color: isResolved
                      ? colorScheme.onSurfaceVariant
                      : colorScheme.onTertiaryContainer,
                ),
              ),
              title: Text(
                evictionCase.status.label,
                style: theme.textTheme.bodyMedium?.copyWith(
                  fontWeight: FontWeight.w600,
                ),
              ),
              subtitle: Text(
                evictionCase.caseNumber?.trim().isNotEmpty == true
                    ? 'Case ${evictionCase.caseNumber!.trim()}'
                    : 'Eviction case #${evictionCase.id}',
                style: TextStyle(
                  fontSize: 12,
                  color: colorScheme.onSurfaceVariant,
                ),
              ),
              trailing: PopupMenuButton<_EvictionAction>(
                tooltip: 'Eviction case actions',
                onSelected: (action) {
                  switch (action) {
                    case _EvictionAction.addEvent:
                      onAddEvent();
                    case _EvictionAction.edit:
                      onEdit();
                    case _EvictionAction.delete:
                      onDelete();
                  }
                },
                itemBuilder: (_) => const [
                  PopupMenuItem(
                    value: _EvictionAction.addEvent,
                    child: ListTile(
                      leading: Icon(Icons.add),
                      title: Text('Add event'),
                    ),
                  ),
                  PopupMenuItem(
                    value: _EvictionAction.edit,
                    child: ListTile(
                      leading: Icon(Icons.edit_outlined),
                      title: Text('Edit'),
                    ),
                  ),
                  PopupMenuItem(
                    value: _EvictionAction.delete,
                    child: ListTile(
                      leading: Icon(Icons.delete_outline),
                      title: Text('Delete'),
                    ),
                  ),
                ],
              ),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 4),
              child: Wrap(
                spacing: 16,
                runSpacing: 8,
                children: [
                  _CaseMetric(
                    label: 'Filed',
                    value: _fmtNullable(evictionCase.filedOnDate),
                  ),
                  _CaseMetric(
                    label: 'Hearing',
                    value: _fmtNullable(evictionCase.hearingDate),
                  ),
                  _CaseMetric(
                    label: 'Resolved',
                    value: _fmtNullable(evictionCase.resolvedOnDate),
                  ),
                  _CaseMetric(
                    label: 'Events',
                    value: '${evictionCase.eventCount}',
                  ),
                  _CaseMetric(
                    label: 'Latest event',
                    value: _fmtNullable(evictionCase.latestEventDate),
                  ),
                  if (evictionCase.courtName != null &&
                      evictionCase.courtName!.isNotEmpty)
                    _CaseMetric(label: 'Court', value: evictionCase.courtName!),
                ],
              ),
            ),
            if (evictionCase.resolution != null &&
                evictionCase.resolution!.isNotEmpty)
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 4, 16, 0),
                child: Text(
                  evictionCase.resolution!,
                  style: theme.textTheme.bodySmall?.copyWith(
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ),
            if (evictionCase.notes != null && evictionCase.notes!.isNotEmpty)
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 4, 16, 0),
                child: Text(
                  evictionCase.notes!,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: colorScheme.onSurfaceVariant,
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _CaseMetric extends StatelessWidget {
  const _CaseMetric({required this.label, required this.value});

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
          style: theme.textTheme.labelSmall?.copyWith(
            color: colorScheme.onSurfaceVariant,
          ),
        ),
        Text(
          value,
          style: theme.textTheme.bodySmall?.copyWith(
            fontWeight: FontWeight.w600,
          ),
        ),
      ],
    );
  }
}

class _LeaseAskCard extends StatelessWidget {
  const _LeaseAskCard({
    required this.controller,
    required this.loading,
    required this.onAsk,
    required this.theme,
    required this.colorScheme,
    this.answer,
    this.error,
  });

  final TextEditingController controller;
  final bool loading;
  final String? answer;
  final String? error;
  final VoidCallback onAsk;
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
            Text(
              'Ask This Lease',
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 10),
            TextField(
              controller: controller,
              minLines: 1,
              maxLines: 3,
              textInputAction: TextInputAction.send,
              onSubmitted: (_) => onAsk(),
              decoration: const InputDecoration(
                labelText: 'Question',
                hintText: 'When is rent due? Can I have a pet?',
                border: OutlineInputBorder(),
              ),
            ),
            const SizedBox(height: 10),
            Align(
              alignment: Alignment.centerRight,
              child: FilledButton.tonal(
                onPressed: loading ? null : onAsk,
                child: loading
                    ? const SizedBox(
                        width: 16,
                        height: 16,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Text('Ask'),
              ),
            ),
            if (answer != null && answer!.isNotEmpty) ...[
              const SizedBox(height: 12),
              Text(
                answer!,
                style: theme.textTheme.bodyMedium?.copyWith(height: 1.45),
              ),
            ],
            if (error != null) ...[
              const SizedBox(height: 12),
              Text(error!, style: TextStyle(color: colorScheme.error)),
            ],
          ],
        ),
      ),
    );
  }
}

// ── Lease agreement (PDF) card ──────────────────────────────────────────────────

class _LeaseDocumentCard extends StatelessWidget {
  const _LeaseDocumentCard({
    required this.generating,
    required this.opening,
    required this.onGenerate,
    required this.onView,
    required this.theme,
    required this.colorScheme,
    this.error,
  });

  final bool generating;
  final bool opening;
  final String? error;
  final VoidCallback onGenerate;
  final VoidCallback onView;
  final ThemeData theme;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    final busy = generating || opening;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Lease Agreement',
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 4),
            Text(
              'Create a standard residential lease agreement from this '
              'lease’s terms, then open it to print or share.',
              style: theme.textTheme.bodySmall?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                FilledButton.icon(
                  onPressed: busy ? null : onGenerate,
                  icon: generating
                      ? const SizedBox(
                          width: 16,
                          height: 16,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Icons.description_outlined, size: 18),
                  label: const Text('Generate lease agreement (PDF)'),
                ),
                OutlinedButton.icon(
                  onPressed: busy ? null : onView,
                  icon: opening
                      ? const SizedBox(
                          width: 16,
                          height: 16,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Icons.open_in_new, size: 18),
                  label: const Text('View agreement'),
                ),
              ],
            ),
            if (error != null) ...[
              const SizedBox(height: 10),
              Text(
                error!,
                style: TextStyle(color: colorScheme.error, fontSize: 13),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

// ── E-signature card ────────────────────────────────────────────────────────────

class _LeaseSignatureCard extends StatelessWidget {
  const _LeaseSignatureCard({
    required this.loading,
    required this.sending,
    required this.openingSigned,
    required this.onSend,
    required this.onViewSigned,
    required this.theme,
    required this.colorScheme,
    this.signature,
    this.error,
  });

  final LeaseSignatureStatus? signature;
  final bool loading;
  final bool sending;
  final bool openingSigned;
  final String? error;
  final VoidCallback onSend;
  final VoidCallback onViewSigned;
  final ThemeData theme;
  final ColorScheme colorScheme;

  ({String label, Color bg, Color fg, IconData icon}) _statusChip() {
    final s = signature;
    if (s == null || s.esignStatus.toLowerCase() == 'none') {
      return (
        label: 'Not sent',
        bg: colorScheme.surfaceContainerHighest,
        fg: colorScheme.onSurfaceVariant,
        icon: Icons.drafts_outlined,
      );
    }
    if (s.isSigned) {
      return (
        label: 'Signed',
        bg: colorScheme.primaryContainer,
        fg: colorScheme.onPrimaryContainer,
        icon: Icons.verified_outlined,
      );
    }
    if (s.isDeclined) {
      return (
        label: 'Declined',
        bg: colorScheme.errorContainer,
        fg: colorScheme.onErrorContainer,
        icon: Icons.cancel_outlined,
      );
    }
    // Sent — waiting.
    return (
      label: 'Sent — waiting',
      bg: colorScheme.tertiaryContainer,
      fg: colorScheme.onTertiaryContainer,
      icon: Icons.schedule_outlined,
    );
  }

  @override
  Widget build(BuildContext context) {
    final chip = _statusChip();
    final busy = sending || openingSigned;
    final hasSignedDoc = signature?.hasSignedDocument ?? false;
    final isSigned = signature?.isSigned ?? false;
    // Once signed, "Send for signature" no longer makes sense.
    final canSend = !isSigned;

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
                    'E-Signature',
                    style: theme.textTheme.titleSmall?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                if (loading)
                  const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                else
                  Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 10,
                      vertical: 4,
                    ),
                    decoration: BoxDecoration(
                      color: chip.bg,
                      borderRadius: BorderRadius.circular(20),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Icon(chip.icon, size: 14, color: chip.fg),
                        const SizedBox(width: 4),
                        Text(
                          chip.label,
                          style: TextStyle(
                            fontSize: 12,
                            fontWeight: FontWeight.w600,
                            color: chip.fg,
                          ),
                        ),
                      ],
                    ),
                  ),
              ],
            ),
            const SizedBox(height: 4),
            Text(
              'Send this lease to the tenant for electronic signature, then '
              'download the signed copy once it’s returned.',
              style: theme.textTheme.bodySmall?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                if (canSend)
                  FilledButton.icon(
                    onPressed: busy ? null : onSend,
                    icon: sending
                        ? const SizedBox(
                            width: 16,
                            height: 16,
                            child: CircularProgressIndicator(strokeWidth: 2),
                          )
                        : const Icon(Icons.send_outlined, size: 18),
                    label: const Text('Send for signature'),
                  ),
                if (hasSignedDoc)
                  OutlinedButton.icon(
                    onPressed: busy ? null : onViewSigned,
                    icon: openingSigned
                        ? const SizedBox(
                            width: 16,
                            height: 16,
                            child: CircularProgressIndicator(strokeWidth: 2),
                          )
                        : const Icon(Icons.download_outlined, size: 18),
                    label: const Text('Download signed lease'),
                  ),
              ],
            ),
            if (error != null) ...[
              const SizedBox(height: 10),
              Text(
                error!,
                style: TextStyle(color: colorScheme.error, fontSize: 13),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

// ── Lease header card ─────────────────────────────────────────────────────────

class _LeaseHeaderCard extends StatelessWidget {
  const _LeaseHeaderCard({
    required this.lease,
    required this.theme,
    required this.colorScheme,
  });

  final Lease lease;
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
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      // Tenant — tappable drill-through to the tenant page.
                      InkWell(
                        onTap: () => _openTenantDetail(context, lease),
                        child: Row(
                          children: [
                            Flexible(
                              child: Text(
                                lease.tenantName ??
                                    'Lease #${lease.leaseNumber}',
                                style: theme.textTheme.titleLarge?.copyWith(
                                  fontWeight: FontWeight.w700,
                                  color: colorScheme.primary,
                                ),
                              ),
                            ),
                            Icon(
                              Icons.chevron_right,
                              size: 18,
                              color: colorScheme.onSurfaceVariant,
                            ),
                          ],
                        ),
                      ),
                      if (lease.tenants.length > 1) ...[
                        const SizedBox(height: 8),
                        Wrap(
                          spacing: 8,
                          runSpacing: 8,
                          children: [
                            for (final tenant in lease.tenants)
                              ActionChip(
                                label: Text(
                                  tenant.name.isEmpty
                                      ? 'Tenant #${tenant.id}'
                                      : tenant.name,
                                ),
                                avatar: tenant.isPrimary
                                    ? const Icon(Icons.star_outline, size: 16)
                                    : null,
                                onPressed: () => _openTenantIdDetail(
                                  context,
                                  lease,
                                  tenant.id,
                                ),
                              ),
                          ],
                        ),
                      ],
                      if (lease.propertyName != null) ...[
                        const SizedBox(height: 2),
                        // Property/unit — tappable drill-through to the property.
                        InkWell(
                          onTap: () =>
                              _openPropertyDetail(context, lease.propertyId),
                          child: Row(
                            children: [
                              Flexible(
                                child: Text(
                                  '${lease.propertyName}'
                                  '${lease.unitNumber != null ? ' · Unit ${lease.unitNumber}' : ''}',
                                  style: theme.textTheme.bodyMedium?.copyWith(
                                    color: colorScheme.primary,
                                  ),
                                ),
                              ),
                              Icon(
                                Icons.chevron_right,
                                size: 16,
                                color: colorScheme.onSurfaceVariant,
                              ),
                            ],
                          ),
                        ),
                      ],
                    ],
                  ),
                ),
                const SizedBox(width: 8),
                _StatusBadge(status: lease.status, colorScheme: colorScheme),
              ],
            ),
            const SizedBox(height: 12),
            const Divider(height: 1),
            const SizedBox(height: 12),
            Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Lease #${lease.leaseNumber}',
                        style: TextStyle(
                          fontSize: 11,
                          color: colorScheme.onSurfaceVariant,
                        ),
                      ),
                      Text(
                        _formatCurrency(lease.monthlyRent),
                        style: theme.textTheme.headlineSmall?.copyWith(
                          fontWeight: FontWeight.w700,
                          color: colorScheme.primary,
                        ),
                      ),
                      Text(
                        'per month',
                        style: TextStyle(
                          fontSize: 12,
                          color: colorScheme.onSurfaceVariant,
                        ),
                      ),
                    ],
                  ),
                ),
                Column(
                  crossAxisAlignment: CrossAxisAlignment.end,
                  children: [
                    Text(
                      '${_fmt(lease.startDate)} –',
                      style: TextStyle(
                        fontSize: 12,
                        color: colorScheme.onSurfaceVariant,
                      ),
                    ),
                    Text(
                      _fmt(lease.endDate),
                      style: TextStyle(
                        fontSize: 12,
                        color: colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _StatusBadge extends StatelessWidget {
  const _StatusBadge({required this.status, required this.colorScheme});

  final String status;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    final isActive = status.toLowerCase() == 'active';
    final isNotice = status.toLowerCase() == 'noticegiven';
    Color bgColor;
    Color fgColor;

    if (isActive) {
      bgColor = colorScheme.primaryContainer;
      fgColor = colorScheme.onPrimaryContainer;
    } else if (isNotice) {
      bgColor = colorScheme.tertiaryContainer;
      fgColor = colorScheme.onTertiaryContainer;
    } else {
      bgColor = colorScheme.surfaceContainerHighest;
      fgColor = colorScheme.onSurfaceVariant;
    }

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: bgColor,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        status,
        style: TextStyle(
          fontSize: 12,
          fontWeight: FontWeight.w600,
          color: fgColor,
        ),
      ),
    );
  }
}

// ── Section card ──────────────────────────────────────────────────────────────

class _SectionCard extends StatelessWidget {
  const _SectionCard({
    required this.title,
    required this.children,
    required this.theme,
    required this.colorScheme,
  });

  final String title;
  final List<Widget> children;
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
            Text(
              title,
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 12),
            ...children,
          ],
        ),
      ),
    );
  }
}

class _RowKV extends StatelessWidget {
  const _RowKV({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Row(
        children: [
          SizedBox(
            width: 140,
            child: Text(
              label,
              style: TextStyle(
                fontSize: 13,
                color: colorScheme.onSurfaceVariant,
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

// ── Status actions ────────────────────────────────────────────────────────────

class _StatusActions extends StatelessWidget {
  const _StatusActions({
    required this.currentStatus,
    required this.loading,
    required this.onSetStatus,
    required this.onCreateNotice,
    required this.theme,
    required this.colorScheme,
    this.error,
  });

  final String currentStatus;
  final bool loading;
  final String? error;
  final Future<void> Function(String) onSetStatus;
  final Future<void> Function() onCreateNotice;
  final ThemeData theme;
  final ColorScheme colorScheme;

  static const _transitions = {
    'Draft': ['Active'],
    'Active': ['Terminated'],
    'NoticeGiven': ['Expired', 'Terminated'],
    'Expired': <String>[],
    'Terminated': <String>[],
  };

  @override
  Widget build(BuildContext context) {
    final available = _transitions[currentStatus] ?? <String>[];
    final canCreateNotice = currentStatus == 'Active';

    if (available.isEmpty && !canCreateNotice && error == null) {
      return const SizedBox.shrink();
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          'Status Actions',
          style: theme.textTheme.titleSmall?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        const SizedBox(height: 8),
        if (available.isNotEmpty || canCreateNotice)
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              if (canCreateNotice)
                OutlinedButton.icon(
                  onPressed: loading ? null : onCreateNotice,
                  icon: const Icon(Icons.notifications_active_outlined),
                  label: const Text('Create / Send notice'),
                ),
              ...available.map((status) {
                return OutlinedButton(
                  onPressed: loading ? null : () => onSetStatus(status),
                  child: loading
                      ? const SizedBox(
                          height: 16,
                          width: 16,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : Text(_statusActionLabel(status)),
                );
              }),
            ],
          ),
        if (error != null) ...[
          const SizedBox(height: 8),
          Text(
            error!,
            style: TextStyle(color: colorScheme.error, fontSize: 13),
          ),
        ],
      ],
    );
  }

  String _statusActionLabel(String status) {
    return switch (status) {
      'Active' => 'Set Active',
      'Expired' => 'Mark expired',
      'Terminated' => 'Terminate',
      _ => 'Mark $status',
    };
  }
}

// ── Payments section ──────────────────────────────────────────────────────────

/// Lists this lease's payments (newest first) with drill-through to each payment
/// and an inline "Record" action on unpaid rows. Backs lease → payments
/// drill-through (and, via the property unit tile, unit → payments).
class _LeasePaymentsSection extends ConsumerWidget {
  const _LeasePaymentsSection({required this.leaseId});

  final int leaseId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final async = ref.watch(leasePaymentsProvider(leaseId));

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Payments',
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 8),
            async.when(
              loading: () => const Padding(
                padding: EdgeInsets.symmetric(vertical: 12),
                child: Center(child: CircularProgressIndicator()),
              ),
              error: (e, _) => Padding(
                padding: const EdgeInsets.symmetric(vertical: 8),
                child: Text(
                  e is ApiException ? e.message : "Couldn't load payments.",
                  style: TextStyle(color: cs.error, fontSize: 13),
                ),
              ),
              data: (payments) {
                if (payments.isEmpty) {
                  return Padding(
                    padding: const EdgeInsets.symmetric(vertical: 8),
                    child: Text(
                      'No payments recorded for this lease yet.',
                      style: TextStyle(color: cs.onSurfaceVariant),
                    ),
                  );
                }
                return Column(
                  children: [
                    for (final p in payments)
                      _LeasePaymentTile(payment: p, leaseId: leaseId),
                  ],
                );
              },
            ),
          ],
        ),
      ),
    );
  }
}

class _LeasePaymentTile extends ConsumerWidget {
  const _LeasePaymentTile({required this.payment, required this.leaseId});

  final Payment payment;
  final int leaseId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final isPaid = payment.status.toLowerCase() == 'paid';

    Color statusBg() {
      final lower = payment.status.toLowerCase();
      if (lower == 'paid') return cs.primaryContainer;
      if (lower == 'late' || lower == 'overdue') return cs.errorContainer;
      return cs.surfaceContainerHighest;
    }

    Color statusFg() {
      final lower = payment.status.toLowerCase();
      if (lower == 'paid') return cs.onPrimaryContainer;
      if (lower == 'late' || lower == 'overdue') return cs.onErrorContainer;
      return cs.onSurfaceVariant;
    }

    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      elevation: 0,
      color: cs.surfaceContainerHighest.withValues(alpha: 0.4),
      child: InkWell(
        onTap: () {
          Navigator.of(context).push<void>(
            MaterialPageRoute<void>(
              builder: (_) => PaymentDetailScreen(paymentId: payment.id),
            ),
          );
        },
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Text(
                          _formatCurrency(payment.amount),
                          style: theme.textTheme.bodyMedium?.copyWith(
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                        const SizedBox(width: 8),
                        Container(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 7,
                            vertical: 3,
                          ),
                          decoration: BoxDecoration(
                            color: statusBg(),
                            borderRadius: BorderRadius.circular(20),
                          ),
                          child: Text(
                            payment.status,
                            style: TextStyle(
                              fontSize: 11,
                              fontWeight: FontWeight.w600,
                              color: statusFg(),
                            ),
                          ),
                        ),
                      ],
                    ),
                    const SizedBox(height: 4),
                    Text(
                      '${payment.type.isEmpty ? 'Payment' : payment.type}  ·  '
                      'Due ${_fmt(payment.dueDate)}',
                      style: TextStyle(
                        fontSize: 12,
                        color: cs.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
              ),
              if (!isPaid)
                Padding(
                  padding: const EdgeInsets.only(left: 8),
                  child: FilledButton.tonal(
                    onPressed: () async {
                      final updated = await showRecordPaymentSheet(
                        context,
                        ref,
                        payment: payment,
                      );
                      if (updated != null) {
                        ref.invalidate(leasePaymentsProvider(leaseId));
                      }
                    },
                    style: FilledButton.styleFrom(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 10,
                        vertical: 6,
                      ),
                      minimumSize: Size.zero,
                      tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                      textStyle: const TextStyle(fontSize: 12),
                    ),
                    child: const Text('Record'),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}
