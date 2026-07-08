import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'guided_rental_flow.dart';
import 'scan_capture.dart';
import 'scan_models.dart';
import 'scan_repository.dart';
import 'scan_review_screen.dart';

// ---------------------------------------------------------------------------
// Providers
// ---------------------------------------------------------------------------

/// Which tab the user has selected. `null` = All.
///
/// Note this is the *UI* filter, which is not 1:1 with the server's `status`
/// query. The "In progress" tab covers two server statuses (Pending and
/// Processing), so it is filtered client-side rather than by a single server
/// status — see [_serverStatusFor] / [_clientFilterFor].
class _ScanFilterNotifier extends Notifier<_ScanFilter?> {
  @override
  _ScanFilter? build() => null;
  void setFilter(_ScanFilter? v) => state = v;
}

final _scanFilterProvider = NotifierProvider<_ScanFilterNotifier, _ScanFilter?>(
  _ScanFilterNotifier.new,
);

// Re-export the public provider under a local alias for readability.
final _scanListProvider = scanListFamilyProvider;

/// UI filter tabs. Distinct from server status so "In progress" can span both
/// Pending and Processing without dropping a draft mid-extraction.
enum _ScanFilter { inProgress, reviewing, confirmed }

/// The server-side `status` query for a given tab, or null to fetch everything
/// and filter client-side. "In progress" fetches all and filters locally so a
/// draft stays visible across the Pending → Processing transition.
String? _serverStatusFor(_ScanFilter? filter) {
  switch (filter) {
    case _ScanFilter.reviewing:
      return 'Reviewing';
    case _ScanFilter.confirmed:
      return 'Confirmed';
    case _ScanFilter.inProgress:
    case null:
      return null;
  }
}

/// Client-side predicate applied on top of whatever the server returned. Only
/// the "In progress" tab needs one (Pending OR Processing); the rest already
/// fetch exactly what they show.
bool Function(ScanDraft)? _clientFilterFor(_ScanFilter? filter) {
  switch (filter) {
    case _ScanFilter.inProgress:
      return (d) => d.isInFlight;
    case _ScanFilter.reviewing:
    case _ScanFilter.confirmed:
    case null:
      return null;
  }
}

String _labelFor(_ScanFilter? filter) {
  switch (filter) {
    case _ScanFilter.inProgress:
      return 'In progress';
    case _ScanFilter.reviewing:
      return 'Reviewing';
    case _ScanFilter.confirmed:
      return 'Confirmed';
    case null:
      return 'All';
  }
}

// ---------------------------------------------------------------------------
// ScanListScreen
// ---------------------------------------------------------------------------

/// Lists all scan drafts with status chips and a FAB to capture a new scan.
class ScanListScreen extends ConsumerWidget {
  const ScanListScreen({super.key});

  static const _filterTabs = <_ScanFilter?>[
    null, // All
    _ScanFilter.inProgress,
    _ScanFilter.reviewing,
    _ScanFilter.confirmed,
  ];

  Future<void> _startCapture(BuildContext context, WidgetRef ref) async {
    final draftId = await showScanCaptureSheet(context);
    if (draftId == null) return;
    if (!context.mounted) return;

    await Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => ScanReviewScreen(draftId: draftId),
      ),
    );

    // Refresh list after returning from the review screen.
    final status = _serverStatusFor(ref.read(_scanFilterProvider));
    ref.invalidate(_scanListProvider(status));
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final activeFilter = ref.watch(_scanFilterProvider);
    final status = _serverStatusFor(activeFilter);
    final clientFilter = _clientFilterFor(activeFilter);
    final draftsAsync = ref.watch(_scanListProvider(status));

    return Scaffold(
      appBar: AppBar(
        title: const Text('Scan / Add'),
        actions: [
          IconButton(
            icon: const Icon(Icons.refresh),
            tooltip: 'Refresh',
            onPressed: () => ref.invalidate(_scanListProvider(status)),
          ),
        ],
      ),
      body: Column(
        children: [
          // Filter chips
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Row(
                children: _filterTabs.map((filterValue) {
                  final selected = activeFilter == filterValue;
                  return Padding(
                    padding: const EdgeInsets.only(right: 8),
                    child: FilterChip(
                      label: Text(_labelFor(filterValue)),
                      selected: selected,
                      onSelected: (_) {
                        ref
                            .read(_scanFilterProvider.notifier)
                            .setFilter(filterValue);
                      },
                    ),
                  );
                }).toList(),
              ),
            ),
          ),
          const Divider(height: 1),
          // List
          Expanded(
            child: draftsAsync.when(
              loading: () => const Center(child: CircularProgressIndicator()),
              error: (e, _) => _ErrorView(
                message: e is ApiException ? e.message : e.toString(),
                onRetry: () => ref.invalidate(_scanListProvider(status)),
              ),
              data: (allDrafts) {
                final drafts = clientFilter == null
                    ? allDrafts
                    : allDrafts.where(clientFilter).toList();
                return drafts.isEmpty
                    ? _EmptyView(onCapture: () => _startCapture(context, ref))
                    : RefreshIndicator(
                        onRefresh: () async =>
                            ref.invalidate(_scanListProvider(status)),
                        child: ListView.separated(
                          padding: const EdgeInsets.only(bottom: 96),
                          itemCount: drafts.length,
                          separatorBuilder: (context, index) =>
                              const Divider(height: 1),
                          itemBuilder: (context, index) {
                            final draft = drafts[index];
                            return _DraftTile(
                              draft: draft,
                              onTap: () async {
                                // Lease drafts open the guided New-rental flow;
                                // everything else uses the standard review screen.
                                if (draft.isLease) {
                                  await GuidedRentalFlow.open(
                                    context,
                                    draft.id,
                                  );
                                } else {
                                  await Navigator.of(context).push(
                                    MaterialPageRoute<void>(
                                      builder: (_) =>
                                          ScanReviewScreen(draftId: draft.id),
                                    ),
                                  );
                                }
                                ref.invalidate(_scanListProvider(status));
                              },
                            );
                          },
                        ),
                      );
              },
            ),
          ),
        ],
      ),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'scan-list-quick-action-fab',
        primaryAction: MobileQuickAction(
          label: 'New scan',
          icon: Icons.document_scanner_outlined,
          onPressed: () => _startCapture(context, ref),
        ),
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => _startCapture(context, ref),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// Widgets
// ---------------------------------------------------------------------------

class _DraftTile extends StatelessWidget {
  const _DraftTile({required this.draft, required this.onTap});

  final ScanDraft draft;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return ListTile(
      titleAlignment: ListTileTitleAlignment.center,
      onTap: onTap,
      leading: CircleAvatar(
        backgroundColor: _statusColor(
          draft.status,
          colorScheme,
        ).withValues(alpha: 0.12),
        child: Icon(
          _statusIcon(draft.status),
          color: _statusColor(draft.status, colorScheme),
          size: 20,
        ),
      ),
      title: Text(
        'Scan #${draft.id}',
        style: theme.textTheme.bodyLarge?.copyWith(fontWeight: FontWeight.w600),
      ),
      subtitle: Text(
        _formatDate(draft.createdAt),
        style: theme.textTheme.bodySmall?.copyWith(
          color: colorScheme.onSurfaceVariant,
        ),
      ),
      trailing: Row(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.center,
        children: [
          _StatusChip(status: draft.status),
          const SizedBox(width: 4),
          const Icon(Icons.chevron_right),
        ],
      ),
    );
  }

  Color _statusColor(String status, ColorScheme cs) {
    switch (status) {
      case 'Confirmed':
        return Colors.green;
      case 'Reviewing':
        return Colors.blue;
      case 'Processing':
        return Colors.indigo;
      case 'Failed':
      case 'Rejected':
        return cs.error;
      case 'Pending':
      default:
        return Colors.amber.shade700;
    }
  }

  IconData _statusIcon(String status) {
    switch (status) {
      case 'Confirmed':
        return Icons.check_circle_outline;
      case 'Reviewing':
        return Icons.rate_review_outlined;
      case 'Processing':
        return Icons.auto_awesome_outlined;
      case 'Failed':
      case 'Rejected':
        return Icons.cancel_outlined;
      case 'Pending':
      default:
        return Icons.hourglass_empty;
    }
  }

  String _formatDate(DateTime dt) {
    return '${dt.month}/${dt.day}/${dt.year}';
  }
}

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.status});

  final String status;

  @override
  Widget build(BuildContext context) {
    final (label, bg, fg) = _style(status, Theme.of(context).colorScheme);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Text(
        label,
        style: TextStyle(fontSize: 11, fontWeight: FontWeight.w600, color: fg),
      ),
    );
  }

  (String, Color, Color) _style(String status, ColorScheme cs) {
    switch (status) {
      case 'Pending':
        return ('Queued', Colors.amber.shade100, Colors.amber.shade900);
      case 'Processing':
        return ('Reading…', Colors.indigo.shade100, Colors.indigo.shade900);
      case 'Reviewing':
        return ('Review', Colors.blue.shade100, Colors.blue.shade900);
      case 'Confirmed':
        return ('Done', Colors.green.shade100, Colors.green.shade900);
      case 'Failed':
        return ('Failed', cs.errorContainer, cs.onErrorContainer);
      case 'Rejected':
        return ('Rejected', cs.errorContainer, cs.onErrorContainer);
      default:
        return (status, cs.surfaceContainerHighest, cs.onSurface);
    }
  }
}

class _EmptyView extends StatelessWidget {
  const _EmptyView({required this.onCapture});

  final VoidCallback onCapture;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              Icons.document_scanner_outlined,
              size: 64,
              color: theme.colorScheme.onSurfaceVariant,
            ),
            const SizedBox(height: 16),
            Text(
              'No scans yet',
              style: theme.textTheme.titleMedium?.copyWith(
                fontWeight: FontWeight.w600,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              'Snap a photo of a receipt, bill, or check\nand the app will read the details for you.',
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 24),
            FilledButton.icon(
              icon: const Icon(Icons.camera_alt_outlined),
              label: const Text('Scan a document'),
              onPressed: onCapture,
            ),
          ],
        ),
      ),
    );
  }
}

class _ErrorView extends StatelessWidget {
  const _ErrorView({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              Icons.error_outline,
              color: Theme.of(context).colorScheme.error,
              size: 48,
            ),
            const SizedBox(height: 12),
            Text(message, textAlign: TextAlign.center),
            const SizedBox(height: 16),
            OutlinedButton(onPressed: onRetry, child: const Text('Try again')),
          ],
        ),
      ),
    );
  }
}
