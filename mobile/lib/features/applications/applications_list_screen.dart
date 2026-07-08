import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/widgets/mobile_grid_controls.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../home/mobile_domain_chrome.dart';
import '../units/unit_command_center_screen.dart';
import '../units/unit_navigation.dart';
import 'application_detail_screen.dart';
import 'applications_models.dart';
import 'applications_repository.dart';
import 'applications_shared.dart';

/// Landlord-facing list of rental applications, filterable by status, with a
/// "Share application link" action that mints and copies the apply URL.
class ApplicationsListScreen extends ConsumerStatefulWidget {
  const ApplicationsListScreen({super.key});

  @override
  ConsumerState<ApplicationsListScreen> createState() =>
      _ApplicationsListScreenState();
}

class _ApplicationsListScreenState
    extends ConsumerState<ApplicationsListScreen> {
  static const _pageSize = 20;

  // null = "All".
  String? _statusFilter;
  final _searchCtrl = TextEditingController();
  String? _search;
  String _sort = '-submittedAt';
  int _skip = 0;
  bool _sharing = false;

  ApplicationListQuery get _query => ApplicationListQuery(
    skip: _skip,
    take: _pageSize,
    status: _statusFilter,
    search: _search,
    sort: _sort,
  );

  @override
  void dispose() {
    _searchCtrl.dispose();
    super.dispose();
  }

  Future<void> _refresh() async {
    ref.invalidate(applicationsPageProvider);
    ref.invalidate(applicationsProvider(_statusFilter));
  }

  void _setStatusFilter(String? value) {
    setState(() {
      _statusFilter = value;
      _skip = 0;
    });
  }

  void _submitSearch([String? value]) {
    final next = (value ?? _searchCtrl.text).trim();
    setState(() {
      _search = next.isEmpty ? null : next;
      _skip = 0;
    });
  }

  void _clearSearch() {
    _searchCtrl.clear();
    _submitSearch('');
  }

  void _setSort(String? value) {
    if (value == null || value == _sort) return;
    setState(() {
      _sort = value;
      _skip = 0;
    });
  }

  void _openDetail(RentalApplication app) {
    final unitId = app.unitId;
    if (unitId != null) {
      openUnitCommandCenter(
        context,
        unitId: unitId,
        initialTab: UnitCommandCenterTab.applications,
        application: app,
      );
      return;
    }

    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => ApplicationDetailScreen(applicationId: app.id),
      ),
    );
  }

  Future<void> _shareLink() async {
    if (_sharing) return;
    setState(() => _sharing = true);
    try {
      final link = await ref.read(applicationsRepositoryProvider).createLink();
      if (!mounted) return;
      await showDialog<void>(
        context: context,
        builder: (_) => _ShareLinkDialog(link: link),
      );
    } on ApiException catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text(e.message)));
    } finally {
      if (mounted) setState(() => _sharing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final applicationsAsync = ref.watch(applicationsPageProvider(_query));
    final shareButton = IconButton(
      tooltip: 'Share application link',
      icon: _sharing
          ? const SizedBox(
              width: 18,
              height: 18,
              child: CircularProgressIndicator(strokeWidth: 2),
            )
          : const Icon(Icons.ios_share),
      onPressed: _sharing ? null : _shareLink,
    );

    return Scaffold(
      appBar: mobileDomainRootAppBar(
        context,
        title: const Text('Applications'),
      ),
      body: Column(
        children: [
          MobileGridControlsBar(
            keyPrefix: 'applications',
            searchController: _searchCtrl,
            searchLabel: 'Search applications',
            onSearch: _submitSearch,
            onClearSearch: _clearSearch,
            sort: _sort,
            defaultSort: '-submittedAt',
            sortOptions: const [
              MobileGridControlOption(
                value: '-submittedAt',
                label: 'Newest submitted',
              ),
              MobileGridControlOption(
                value: 'submittedAt',
                label: 'Oldest submitted',
              ),
              MobileGridControlOption(value: 'name', label: 'Applicant name'),
              MobileGridControlOption(value: 'status', label: 'Status'),
            ],
            onSortChanged: _setSort,
            trailingActions: [shareButton],
            filters: [
              MobileGridChoiceFilter(
                id: 'status',
                label: 'Status',
                value: _statusFilter,
                allLabel: 'All statuses',
                options: [
                  for (final status in applicationStatuses)
                    MobileGridControlOption(
                      value: status,
                      label: friendlyApplicationStatus(status),
                    ),
                ],
                onChanged: _setStatusFilter,
              ),
            ],
          ),
          Expanded(
            child: RefreshIndicator(
              onRefresh: _refresh,
              child: applicationsAsync.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (e, _) => _ErrorBody(
                  message: e is ApiException ? e.message : e.toString(),
                  onRetry: _refresh,
                ),
                data: (page) {
                  final apps = page.items;
                  if (apps.isEmpty) {
                    if (_search == null && _skip == 0) {
                      return _EmptyBody(filter: _statusFilter);
                    }
                    return ListView(
                      physics: const AlwaysScrollableScrollPhysics(),
                      children: [
                        Padding(
                          padding: const EdgeInsets.all(24),
                          child: Text(
                            _search == null
                                ? 'No applications on this page.'
                                : 'No applications match "$_search".',
                            textAlign: TextAlign.center,
                          ),
                        ),
                      ],
                    );
                  }
                  return ListView.separated(
                    physics: const AlwaysScrollableScrollPhysics(),
                    padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
                    itemCount: apps.length + 1,
                    separatorBuilder: (_, index) => index < apps.length - 1
                        ? const MobileM3ListDivider()
                        : const SizedBox(height: 16),
                    itemBuilder: (_, i) {
                      if (i == apps.length) {
                        return MobileGridPagingBar(
                          totalCount: page.totalCount,
                          skip: page.skip,
                          itemCount: page.items.length,
                          onPrevious: page.hasPrevious
                              ? () => setState(() {
                                  _skip = _skip <= _pageSize
                                      ? 0
                                      : _skip - _pageSize;
                                })
                              : null,
                          onNext: page.hasNext
                              ? () => setState(() => _skip += _pageSize)
                              : null,
                        );
                      }
                      final app = apps[i];
                      return _ApplicationCard(
                        application: app,
                        position: MobileM3ListItemPositionForIndex.forIndex(
                          i,
                          apps.length,
                        ),
                        onTap: () => _openDetail(app),
                      );
                    },
                  );
                },
              ),
            ),
          ),
        ],
      ),
    );
  }
}

// ── Application card ───────────────────────────────────────────────────────────

class _ApplicationCard extends StatelessWidget {
  const _ApplicationCard({
    required this.application,
    required this.position,
    required this.onTap,
  });

  final RentalApplication application;
  final MobileM3ListItemPosition position;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final app = application;

    final appliedFor = StringBuffer('Property #${app.propertyId}');
    if (app.unitId != null) appliedFor.write('  ·  Unit #${app.unitId}');
    final submittedLabel = app.submittedAtUtc != null
        ? 'Submitted ${formatApplicationDate(app.submittedAtUtc!.toLocal())}'
        : 'Not yet submitted';
    final incomeLabel = app.monthlyIncome != null
        ? ' · ${formatMonthlyIncome(app.monthlyIncome!)}/mo'
        : '';

    return MobileM3ListItem(
      position: position,
      leading: MobileM3LeadingIcon(
        icon: Icons.assignment_ind_outlined,
        backgroundColor: cs.primaryContainer,
        foregroundColor: cs.onPrimaryContainer,
      ),
      title: Text(
        app.fullName,
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w700,
        ),
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
      ),
      supporting: [
        Text(
          appliedFor.toString(),
          style: theme.textTheme.bodySmall?.copyWith(
            color: cs.onSurfaceVariant,
          ),
        ),
        Text(
          '$submittedLabel$incomeLabel',
          style: theme.textTheme.bodySmall?.copyWith(
            color: cs.onSurfaceVariant,
          ),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
      ],
      trailing: ApplicationStatusChip(status: app.status),
      onTap: onTap,
    );
  }
}

// ── Share link dialog ──────────────────────────────────────────────────────────

class _ShareLinkDialog extends StatelessWidget {
  const _ShareLinkDialog({required this.link});

  final ApplicationLink link;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final url = buildApplyUrl(link.applyPath);

    return AlertDialog(
      title: const Text('Application link'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Share this link with applicants. They can fill out the '
            'application on the web.',
            style: theme.textTheme.bodySmall?.copyWith(
              color: cs.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 12),
          Container(
            width: double.infinity,
            padding: const EdgeInsets.all(12),
            decoration: BoxDecoration(
              color: cs.surfaceContainerHighest,
              borderRadius: BorderRadius.circular(8),
            ),
            child: SelectableText(
              url,
              style: theme.textTheme.bodySmall?.copyWith(
                fontFamily: 'monospace',
              ),
            ),
          ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Close'),
        ),
        FilledButton.icon(
          onPressed: () async {
            await Clipboard.setData(ClipboardData(text: url));
            if (!context.mounted) return;
            Navigator.of(context).pop();
            ScaffoldMessenger.of(context).showSnackBar(
              const SnackBar(content: Text('Link copied to clipboard')),
            );
          },
          icon: const Icon(Icons.copy, size: 16),
          label: const Text('Copy'),
        ),
      ],
    );
  }
}

// ── Empty / error states ───────────────────────────────────────────────────────

class _EmptyBody extends StatelessWidget {
  const _EmptyBody({required this.filter});

  final String? filter;

  @override
  Widget build(BuildContext context) {
    final label = filter == null
        ? ''
        : ' with status "${friendlyApplicationStatus(filter!)}"';
    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(24, 80, 24, 24),
          child: Column(
            children: [
              Icon(
                Icons.inbox_outlined,
                size: 40,
                color: Theme.of(context).colorScheme.onSurfaceVariant,
              ),
              const SizedBox(height: 12),
              Text(
                'No applications$label yet.',
                textAlign: TextAlign.center,
                style: TextStyle(
                  color: Theme.of(context).colorScheme.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 6),
              Text(
                'Share the application link to invite applicants.',
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: Theme.of(context).colorScheme.onSurfaceVariant,
                ),
              ),
            ],
          ),
        ),
      ],
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
    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(24, 80, 24, 24),
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
              FilledButton.tonal(
                onPressed: onRetry,
                child: const Text('Retry'),
              ),
            ],
          ),
        ),
      ],
    );
  }
}
