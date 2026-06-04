import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
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

  int get _id => widget.applicationId;

  Future<void> _refresh() async =>
      ref.invalidate(applicationDetailProvider(_id));

  void _snack(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(message)),
    );
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
      final result = await ref.read(applicationsRepositoryProvider).approve(_id);
      if (!mounted) return;
      setState(() => _createdTenantId = result.tenantId);
      await _refresh();
      _snack(result.tenantId != null
          ? 'Approved — tenant #${result.tenantId} created.'
          : 'Application approved.');
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

  @override
  Widget build(BuildContext context) {
    final detailAsync = ref.watch(applicationDetailProvider(_id));
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Application'),
        actions: [
          detailAsync.whenOrNull(
                data: (app) => ApplicationStatusChip(status: app.status),
              ) ??
              const SizedBox.shrink(),
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
            onApprove: _approve,
            onDecline: _decline,
            onWithdraw: _withdraw,
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
    required this.onApprove,
    required this.onDecline,
    required this.onWithdraw,
  });

  final RentalApplication application;
  final ThemeData theme;
  final bool busy;
  final int? createdTenantId;
  final VoidCallback onApprove;
  final VoidCallback onDecline;
  final VoidCallback onWithdraw;

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
                  style: theme.textTheme.titleLarge
                      ?.copyWith(fontWeight: FontWeight.w700),
                ),
                const SizedBox(height: 4),
                Text(
                  'Property #${app.propertyId}'
                  '${app.unitId != null ? '  ·  Unit #${app.unitId}' : ''}',
                  style: theme.textTheme.bodyMedium
                      ?.copyWith(color: cs.onSurfaceVariant),
                ),
                if (app.submittedAtUtc != null) ...[
                  const SizedBox(height: 4),
                  Text(
                    'Submitted ${formatApplicationDateTime(app.submittedAtUtc!)}',
                    style: theme.textTheme.bodySmall
                        ?.copyWith(color: cs.onSurfaceVariant),
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
              child: Row(
                children: [
                  Icon(Icons.how_to_reg_outlined,
                      color: cs.onTertiaryContainer),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      'Approved — tenant #$createdTenantId created. '
                      'Find them in Tenants to set up a lease.',
                      style: theme.textTheme.bodyMedium
                          ?.copyWith(color: cs.onTertiaryContainer),
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
              if (app.email != null) _DetailRow(label: 'Email', value: app.email!),
              if (app.phone != null) _DetailRow(label: 'Phone', value: app.phone!),
              if (app.dateOfBirth != null)
                _DetailRow(
                  label: 'Date of birth',
                  value: formatApplicationDate(app.dateOfBirth!.toLocal()),
                ),
              if (app.currentAddress != null)
                _DetailRow(label: 'Current address', value: app.currentAddress!),
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
                  value: formatApplicationDate(app.desiredMoveInDate!.toLocal()),
                ),
              _DetailRow(label: 'Status', value: friendlyApplicationStatus(app.status)),
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

        // ── Actions ────────────────────────────────────────────────────────
        if (open) ...[
          const SizedBox(height: 20),
          Text(
            'Decision',
            style: theme.textTheme.titleSmall
                ?.copyWith(fontWeight: FontWeight.w700),
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
        ],
        const SizedBox(height: 24),
      ],
    );
  }
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
                color: Theme.of(context).colorScheme.onSurfaceVariant),
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
          onPressed: () =>
              Navigator.of(context).pop(_controller.text.trim()),
          child: const Text('Decline'),
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
              style: theme.textTheme.titleSmall
                  ?.copyWith(fontWeight: FontWeight.w700),
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
              style: theme.textTheme.bodySmall
                  ?.copyWith(fontWeight: FontWeight.w600),
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
            Text(message,
                textAlign: TextAlign.center,
                style: TextStyle(color: cs.error)),
            const SizedBox(height: 16),
            FilledButton.tonal(
              onPressed: onRetry,
              child: const Text('Retry'),
            ),
          ],
        ),
      ),
    );
  }
}
