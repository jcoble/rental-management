import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import 'lease_ledger_view.dart';
import 'leases_list_screen.dart';
import 'leases_repository.dart';

const _monthNames = [
  '', 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
  'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
];

String _fmt(DateTime d) => '${_monthNames[d.month]} ${d.day}, ${d.year}';

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

/// Detail screen for a single lease.
///
/// Shows full lease information with edit support and status action buttons.
class LeaseDetailScreen extends ConsumerStatefulWidget {
  const LeaseDetailScreen({super.key, required this.lease});

  final Lease lease;

  @override
  ConsumerState<LeaseDetailScreen> createState() =>
      _LeaseDetailScreenState();
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
    await ref.read(leaseDetailProvider(_lease.id).notifier).refresh();
    final updated = ref.read(leaseDetailProvider(_lease.id));
    updated.whenData((l) {
      if (mounted) setState(() => _lease = l);
    });
  }

  void _showEditSheet(BuildContext context) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => LeaseFormSheet(
        existing: _lease,
        onSaved: _refresh,
      ),
    );
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

  Future<void> _askLease() async {
    final question = _questionController.text.trim();
    if (question.isEmpty) return;

    setState(() {
      _askingLease = true;
      _leaseAnswer = null;
      _leaseAskError = null;
    });

    try {
      final response = await ref.read(leasesRepositoryProvider).ask(_lease.id, question);
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

  /// Fetches the generated PDF bytes (authed), writes them to a temp file, and
  /// hands the file off to the platform viewer via url_launcher.
  Future<void> _openDocument() async {
    if (_openingDoc) return;
    setState(() {
      _openingDoc = true;
      _docError = null;
    });
    final messenger = ScaffoldMessenger.of(context);
    try {
      final bytes =
          await ref.read(leasesRepositoryProvider).documentBytes(_lease.id);
      final path =
          '${Directory.systemTemp.path}/lease-${_lease.id}-agreement.pdf';
      final file = File(path);
      await file.writeAsBytes(bytes, flush: true);
      final ok = await launchUrl(
        Uri.file(path),
        mode: LaunchMode.externalApplication,
      );
      if (!mounted) return;
      if (!ok) {
        messenger
          ..hideCurrentSnackBar()
          ..showSnackBar(
            const SnackBar(content: Text('No app available to open the PDF.')),
          );
      }
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
      final status =
          await ref.read(leasesRepositoryProvider).signatureStatus(_lease.id);
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
      final status =
          await ref.read(leasesRepositoryProvider).sendForSignature(_lease.id);
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
            const SnackBar(
              content: Text('E-signature isn’t set up yet.'),
            ),
          );
      } else {
        setState(() => _signatureError = e.message);
      }
    } finally {
      if (mounted) setState(() => _sendingSignature = false);
    }
  }

  /// Fetches the signed lease PDF bytes (authed), writes them to a temp file,
  /// and hands the file off to the platform viewer via url_launcher.
  Future<void> _openSignedDocument() async {
    if (_openingSignedDoc) return;
    setState(() {
      _openingSignedDoc = true;
      _signatureError = null;
    });
    final messenger = ScaffoldMessenger.of(context);
    try {
      final bytes = await ref
          .read(leasesRepositoryProvider)
          .signedDocumentBytes(_lease.id);
      final path =
          '${Directory.systemTemp.path}/lease-${_lease.id}-signed.pdf';
      final file = File(path);
      await file.writeAsBytes(bytes, flush: true);
      final ok = await launchUrl(
        Uri.file(path),
        mode: LaunchMode.externalApplication,
      );
      if (!mounted) return;
      if (!ok) {
        messenger
          ..hideCurrentSnackBar()
          ..showSnackBar(
            const SnackBar(content: Text('No app available to open the PDF.')),
          );
      }
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

    return Scaffold(
      appBar: AppBar(
        title: Text(
          'Lease #${_lease.leaseNumber}',
          overflow: TextOverflow.ellipsis,
        ),
        actions: [
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
                _RowKV(
                  label: 'Start date',
                  value: _fmt(_lease.startDate),
                ),
                _RowKV(
                  label: 'End date',
                  value: _fmt(_lease.endDate),
                ),
                if (_lease.moveInDate != null)
                  _RowKV(
                    label: 'Move-in',
                    value: _fmt(_lease.moveInDate!),
                  ),
                if (_lease.moveOutDate != null)
                  _RowKV(
                    label: 'Move-out',
                    value: _fmt(_lease.moveOutDate!),
                  ),
              ],
            ),
            const SizedBox(height: 16),

            // ── Status actions ─────────────────────────────────────────────
            _StatusActions(
              currentStatus: _lease.status,
              loading: _updatingStatus,
              error: _statusError,
              onSetStatus: _updateStatus,
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
              style: theme.textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w700),
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
              style:
                  theme.textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w700),
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
                    style: theme.textTheme.titleSmall
                        ?.copyWith(fontWeight: FontWeight.w700),
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
                        horizontal: 10, vertical: 4),
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
                      Text(
                        lease.tenantName ?? 'Lease #${lease.leaseNumber}',
                        style: theme.textTheme.titleLarge
                            ?.copyWith(fontWeight: FontWeight.w700),
                      ),
                      if (lease.propertyName != null) ...[
                        const SizedBox(height: 2),
                        Text(
                          '${lease.propertyName}'
                          '${lease.unitNumber != null ? ' · Unit ${lease.unitNumber}' : ''}',
                          style: theme.textTheme.bodyMedium?.copyWith(
                              color: colorScheme.onSurfaceVariant),
                        ),
                      ],
                    ],
                  ),
                ),
                const SizedBox(width: 8),
                _StatusBadge(
                    status: lease.status, colorScheme: colorScheme),
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
              style: theme.textTheme.titleSmall
                  ?.copyWith(fontWeight: FontWeight.w700),
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
                  fontSize: 13, color: colorScheme.onSurfaceVariant),
            ),
          ),
          Expanded(
            child: Text(
              value,
              style: theme.textTheme.bodyMedium
                  ?.copyWith(fontWeight: FontWeight.w600),
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
    required this.theme,
    required this.colorScheme,
    this.error,
  });

  final String currentStatus;
  final bool loading;
  final String? error;
  final Future<void> Function(String) onSetStatus;
  final ThemeData theme;
  final ColorScheme colorScheme;

  static const _transitions = {
    'Draft': ['Active'],
    'Active': ['NoticeGiven', 'Terminated'],
    'NoticeGiven': ['Expired', 'Terminated'],
    'Expired': <String>[],
    'Terminated': <String>[],
  };

  @override
  Widget build(BuildContext context) {
    final available =
        _transitions[currentStatus] ?? <String>[];

    if (available.isEmpty && error == null) {
      return const SizedBox.shrink();
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          'Status Actions',
          style: theme.textTheme.titleSmall
              ?.copyWith(fontWeight: FontWeight.w700),
        ),
        const SizedBox(height: 8),
        if (available.isNotEmpty)
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: available.map((status) {
              return OutlinedButton(
                onPressed: loading ? null : () => onSetStatus(status),
                child: loading
                    ? const SizedBox(
                        height: 16,
                        width: 16,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : Text('Mark $status'),
              );
            }).toList(),
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
}
