import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_exception.dart';
import '../activity/activity_history_screen.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'rate_vendor_sheet.dart';
import 'star_rating.dart';
import 'vendor_form_sheet.dart';
import 'vendors_models.dart';
import 'vendors_repository.dart';

/// Vendor detail with a performance scorecard (average rating, rating count,
/// jobs completed, average DONE response time), a tax / W-9 section (text a
/// W-9 request + a "W-9 on file" toggle), and a "Rate vendor" action.
class VendorDetailScreen extends ConsumerStatefulWidget {
  const VendorDetailScreen({super.key, required this.vendor});

  final Vendor vendor;

  @override
  ConsumerState<VendorDetailScreen> createState() => _VendorDetailScreenState();
}

class _VendorDetailScreenState extends ConsumerState<VendorDetailScreen> {
  late Vendor _vendor;
  bool _requestingW9 = false;
  bool _savingW9OnFile = false;
  bool _deleting = false;

  @override
  void initState() {
    super.initState();
    _vendor = widget.vendor;
  }

  /// Texts the vendor a W-9 request; snackbars the result or the 400 reason
  /// (e.g. no phone on file).
  Future<void> _requestW9() async {
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _requestingW9 = true);
    try {
      final result = await ref
          .read(vendorsRepositoryProvider)
          .requestW9(_vendor.id);
      if (!mounted) return;
      final to = result.sentTo;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(
              to != null
                  ? 'W-9 request texted to $to.'
                  : 'W-9 request texted to the vendor.',
            ),
          ),
        );
    } on ApiException catch (e) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    } finally {
      if (mounted) setState(() => _requestingW9 = false);
    }
  }

  /// Optimistically flips the W-9-on-file flag and PATCHes the vendor, reverting
  /// on error.
  Future<void> _setW9OnFile(bool value) async {
    final messenger = ScaffoldMessenger.of(context);
    final previous = _vendor.w9OnFile;
    setState(() {
      _vendor = _vendor.copyWith(w9OnFile: value);
      _savingW9OnFile = true;
    });
    try {
      await ref.read(vendorsRepositoryProvider).setW9OnFile(_vendor.id, value);
      if (mounted) ref.invalidate(vendorsProvider);
    } on ApiException catch (e) {
      if (!mounted) return;
      setState(() => _vendor = _vendor.copyWith(w9OnFile: previous));
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    } finally {
      if (mounted) setState(() => _savingW9OnFile = false);
    }
  }

  /// Opens an external app for the given [uri] (tel:/sms:/mailto:); snackbars if nothing handles it.
  Future<void> _launch(Uri uri, String failureMessage) async {
    final messenger = ScaffoldMessenger.of(context);
    try {
      final ok = await launchUrl(uri, mode: LaunchMode.externalApplication);
      if (!ok && mounted) {
        messenger
          ..hideCurrentSnackBar()
          ..showSnackBar(SnackBar(content: Text(failureMessage)));
      }
    } catch (_) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(failureMessage)));
    }
  }

  void _callVendor() {
    _launch(
      Uri(scheme: 'tel', path: _vendor.phone!.trim()),
      'Could not start a call.',
    );
  }

  void _textVendor() {
    _launch(
      Uri(scheme: 'sms', path: _vendor.phone!.trim()),
      'Could not open messaging.',
    );
  }

  void _emailVendor() {
    _launch(
      Uri(scheme: 'mailto', path: _vendor.email!.trim()),
      'Could not open email.',
    );
  }

  void _openWebsite() {
    final raw = _vendor.website!.trim();
    final uri = Uri.tryParse(raw.contains('://') ? raw : 'https://$raw');
    if (uri == null) return;
    _launch(uri, 'Could not open website.');
  }

  void _showActivityHistory() {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => ActivityHistoryScreen(
          entityType: 'Vendor',
          entityId: _vendor.id,
          title: 'Vendor activity',
          subtitle: _vendor.name,
        ),
      ),
    );
  }

  void _refreshVendorLists() {
    ref.invalidate(vendorsProvider);
    ref.invalidate(vendorsPageProvider);
  }

  void _showEditSheet() {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      useRootNavigator: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => VendorFormSheet(
        existing: _vendor,
        onSaved: _refreshVendorLists,
        onVendorSaved: (updated) {
          if (mounted) setState(() => _vendor = updated);
        },
      ),
    );
  }

  Future<void> _confirmDelete() async {
    final messenger = ScaffoldMessenger.of(context);
    final navigator = Navigator.of(context);
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (_) => AlertDialog(
        title: const Text('Delete vendor?'),
        content: Text('Delete ${_vendor.name}? This cannot be undone.'),
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

    setState(() => _deleting = true);
    try {
      await ref.read(vendorsRepositoryProvider).deleteVendor(_vendor.id);
      _refreshVendorLists();
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Vendor deleted.')));
      navigator.pop();
    } on ApiException catch (e) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    } finally {
      if (mounted) setState(() => _deleting = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final vendor = _vendor;
    final scorecardAsync = ref.watch(vendorScorecardProvider(vendor.id));

    return Scaffold(
      appBar: AppBar(
        title: Text(vendor.name),
        actions: [
          IconButton(
            icon: const Icon(Icons.edit_outlined),
            tooltip: 'Edit vendor',
            onPressed: _deleting ? null : _showEditSheet,
          ),
          IconButton(
            icon: const Icon(Icons.history_outlined),
            tooltip: 'View vendor activity',
            onPressed: _deleting ? null : _showActivityHistory,
          ),
          IconButton(
            icon: const Icon(Icons.delete_outline),
            tooltip: 'Delete vendor',
            onPressed: _deleting ? null : _confirmDelete,
          ),
        ],
      ),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'vendor-detail-fab',
        primaryAction: MobileQuickAction(
          label: 'Rate vendor',
          icon: Icons.star_rounded,
          onPressed: () => showRateVendorSheet(
            context,
            vendorId: vendor.id,
            vendorName: vendor.name,
          ),
        ),
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: RefreshIndicator(
        onRefresh: () async =>
            ref.invalidate(vendorScorecardProvider(vendor.id)),
        child: ListView(
          physics: const AlwaysScrollableScrollPhysics(),
          padding: const EdgeInsets.fromLTRB(16, 16, 16, 96),
          children: [
            // ── Header ────────────────────────────────────────────────────
            Row(
              children: [
                Container(
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: cs.tertiary.withValues(alpha: 0.12),
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: Icon(
                    Icons.handyman_outlined,
                    color: cs.tertiary,
                    size: 26,
                  ),
                ),
                const SizedBox(width: 14),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          Flexible(
                            child: Text(
                              vendor.name,
                              style: theme.textTheme.titleLarge?.copyWith(
                                fontWeight: FontWeight.w700,
                              ),
                            ),
                          ),
                          if (vendor.preferred) ...[
                            const SizedBox(width: 6),
                            Icon(
                              Icons.star_rounded,
                              size: 18,
                              color: Colors.amber.shade600,
                            ),
                          ],
                        ],
                      ),
                      Text(
                        vendor.serviceType,
                        style: theme.textTheme.bodyMedium?.copyWith(
                          color: cs.onSurfaceVariant,
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
            if (vendor.hasPhone || vendor.hasEmail || vendor.hasWebsite) ...[
              const SizedBox(height: 14),
              // Phone → tap to call; trailing icon texts. Hidden when no phone.
              if (vendor.hasPhone)
                _ContactRow(
                  icon: Icons.phone_outlined,
                  value: vendor.phone!,
                  cs: cs,
                  theme: theme,
                  onTap: _callVendor,
                  trailing: IconButton(
                    icon: Icon(Icons.sms_outlined, color: cs.primary),
                    tooltip: 'Text vendor',
                    onPressed: _textVendor,
                  ),
                ),
              // Email → tap to open the mail app. Hidden when no email.
              if (vendor.hasEmail)
                _ContactRow(
                  icon: Icons.email_outlined,
                  value: vendor.email!,
                  cs: cs,
                  theme: theme,
                  onTap: _emailVendor,
                ),
              if (vendor.hasWebsite)
                _ContactRow(
                  icon: Icons.public_outlined,
                  value: vendor.website!,
                  cs: cs,
                  theme: theme,
                  onTap: _openWebsite,
                ),
            ],

            const SizedBox(height: 20),
            Text(
              'Scorecard',
              style: theme.textTheme.labelLarge?.copyWith(
                fontWeight: FontWeight.w700,
                color: cs.onSurfaceVariant,
                letterSpacing: 0.5,
              ),
            ),
            const SizedBox(height: 10),
            scorecardAsync.when(
              loading: () => const Padding(
                padding: EdgeInsets.all(24),
                child: Center(child: CircularProgressIndicator()),
              ),
              error: (e, _) => _ScorecardError(
                message: e is ApiException ? e.message : e.toString(),
                onRetry: () =>
                    ref.invalidate(vendorScorecardProvider(vendor.id)),
              ),
              data: (card) => _ScorecardCard(card: card),
            ),

            // ── Tax / W-9 ─────────────────────────────────────────────────
            const SizedBox(height: 24),
            Text(
              'Tax / W-9',
              style: theme.textTheme.labelLarge?.copyWith(
                fontWeight: FontWeight.w700,
                color: cs.onSurfaceVariant,
                letterSpacing: 0.5,
              ),
            ),
            const SizedBox(height: 10),
            Container(
              decoration: BoxDecoration(
                color: cs.surfaceContainerLowest,
                borderRadius: BorderRadius.circular(12),
                border: Border.all(color: cs.outlineVariant),
              ),
              clipBehavior: Clip.antiAlias,
              child: Material(
                color: Colors.transparent,
                child: Column(
                  children: [
                    SwitchListTile.adaptive(
                      contentPadding: const EdgeInsets.symmetric(
                        horizontal: 16,
                      ),
                      title: const Text('W-9 on file'),
                      subtitle: Text(
                        vendor.w9OnFile
                            ? 'A signed W-9 has been collected.'
                            : 'No W-9 collected yet.',
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: cs.onSurfaceVariant,
                        ),
                      ),
                      value: vendor.w9OnFile,
                      onChanged: _savingW9OnFile
                          ? null
                          : (v) => _setW9OnFile(v),
                    ),
                    Divider(height: 1, color: cs.outlineVariant),
                    ListTile(
                      titleAlignment: ListTileTitleAlignment.center,
                      contentPadding: const EdgeInsets.symmetric(
                        horizontal: 16,
                      ),
                      leading: Icon(Icons.sms_outlined, color: cs.primary),
                      title: const Text('Text W-9 request'),
                      subtitle: Text(
                        'Send the vendor a text asking for their W-9.',
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: cs.onSurfaceVariant,
                        ),
                      ),
                      trailing: _requestingW9
                          ? const SizedBox(
                              width: 20,
                              height: 20,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : const Icon(Icons.chevron_right),
                      onTap: _requestingW9 ? null : _requestW9,
                    ),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _ScorecardCard extends StatelessWidget {
  const _ScorecardCard({required this.card});

  final VendorScorecard card;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final avg = card.averageRating;

    return Container(
      decoration: BoxDecoration(
        color: cs.surfaceContainerLowest,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: cs.outlineVariant),
      ),
      child: Column(
        children: [
          // Big average rating row.
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 12),
            child: Row(
              children: [
                Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      avg == null ? '—' : avg.toStringAsFixed(1),
                      style: theme.textTheme.displaySmall?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    if (avg != null)
                      StarRatingDisplay(rating: avg, size: 18)
                    else
                      Text(
                        'Not rated yet',
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: cs.onSurfaceVariant,
                        ),
                      ),
                  ],
                ),
                const SizedBox(width: 16),
                Expanded(
                  child: Text(
                    card.ratingCount == 1
                        ? '1 rating'
                        : '${card.ratingCount} ratings',
                    textAlign: TextAlign.right,
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: cs.onSurfaceVariant,
                    ),
                  ),
                ),
              ],
            ),
          ),
          Divider(height: 1, color: cs.outlineVariant),
          _StatRow(
            label: 'Jobs completed',
            value: '${card.jobsCompleted}',
            theme: theme,
            cs: cs,
          ),
          _StatRow(
            label: 'Avg. response time',
            value: card.avgResponseHours == null
                ? 'No data yet'
                : _formatHours(card.avgResponseHours!),
            theme: theme,
            cs: cs,
            isLast: true,
          ),
        ],
      ),
    );
  }

  static String _formatHours(double hours) {
    if (hours < 1) {
      final mins = (hours * 60).round();
      return '$mins min';
    }
    if (hours < 48) {
      return '${hours.toStringAsFixed(1)} hr';
    }
    final days = (hours / 24).toStringAsFixed(1);
    return '$days days';
  }
}

class _StatRow extends StatelessWidget {
  const _StatRow({
    required this.label,
    required this.value,
    required this.theme,
    required this.cs,
    this.isLast = false,
  });

  final String label;
  final String value;
  final ThemeData theme;
  final ColorScheme cs;
  final bool isLast;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
      decoration: BoxDecoration(
        border: isLast
            ? null
            : Border(bottom: BorderSide(color: cs.outlineVariant)),
      ),
      child: Row(
        children: [
          Expanded(
            child: Text(
              label,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: cs.onSurfaceVariant,
              ),
            ),
          ),
          Text(
            value,
            style: theme.textTheme.bodyMedium?.copyWith(
              fontWeight: FontWeight.w600,
            ),
          ),
        ],
      ),
    );
  }
}

class _ContactRow extends StatelessWidget {
  const _ContactRow({
    required this.icon,
    required this.value,
    required this.cs,
    required this.theme,
    this.onTap,
    this.trailing,
  });

  final IconData icon;
  final String value;
  final ColorScheme cs;
  final ThemeData theme;

  /// Tapping the row's value (e.g. call the phone / open the email). Null = non-interactive.
  final VoidCallback? onTap;

  /// Optional trailing action (e.g. a Text button next to a phone number).
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    final interactive = onTap != null;
    return Padding(
      padding: const EdgeInsets.only(top: 6),
      child: Row(
        children: [
          Icon(icon, size: 16, color: cs.onSurfaceVariant),
          const SizedBox(width: 8),
          Expanded(
            child: InkWell(
              onTap: onTap,
              borderRadius: BorderRadius.circular(6),
              child: Padding(
                padding: const EdgeInsets.symmetric(vertical: 4),
                child: Text(
                  value,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: interactive ? cs.primary : null,
                  ),
                ),
              ),
            ),
          ),
          ?trailing,
        ],
      ),
    );
  }
}

class _ScorecardError extends StatelessWidget {
  const _ScorecardError({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        color: cs.surfaceContainerLowest,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: cs.outlineVariant),
      ),
      child: Column(
        children: [
          Icon(Icons.error_outline, size: 32, color: cs.error),
          const SizedBox(height: 8),
          Text(
            message,
            textAlign: TextAlign.center,
            style: TextStyle(color: cs.error),
          ),
          const SizedBox(height: 12),
          FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
        ],
      ),
    );
  }
}
