import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/api/api_exception.dart';
import '../applications/application_detail_screen.dart';
import '../home/mobile_quick_action_helpers.dart';
import '../home/mobile_shell_actions.dart';
import '../messages/message_detail_screen.dart';
import 'leasing_detail_screens.dart';
import 'leasing_workspace_repository.dart';

enum LeasingArea { today, pipeline, rentals, calendar, inbox }

/// A purpose-built Leasing Agent shell. It only reads `/leasing/*` projections;
/// management accounting, reports, owners, team, and workspace settings never
/// enter this navigation tree.
class LeasingLandingScreen extends ConsumerStatefulWidget {
  const LeasingLandingScreen({super.key});

  @override
  ConsumerState<LeasingLandingScreen> createState() =>
      _LeasingLandingScreenState();
}

class _LeasingLandingScreenState extends ConsumerState<LeasingLandingScreen> {
  int _selectedIndex = 0;

  static const _titles = [
    'Today',
    'Pipeline',
    'Rentals & listings',
    'Calendar',
    'Inbox',
  ];

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: Text(_titles[_selectedIndex]),
        actions: const [MobileNotificationBell(), MobileAccountMenu()],
      ),
      body: IndexedStack(
        index: _selectedIndex,
        children: const [
          _LeasingTodayTab(),
          _LeasingListTab(area: LeasingArea.pipeline),
          _LeasingListTab(area: LeasingArea.rentals),
          _LeasingListTab(area: LeasingArea.calendar),
          _LeasingListTab(area: LeasingArea.inbox),
        ],
      ),
      floatingActionButton: FloatingActionButton.extended(
        heroTag: 'leasing-scan',
        onPressed: () => openAuthorizedMobileScan(context, ref),
        icon: const Icon(Symbols.document_scanner_rounded),
        label: const Text('Scan'),
      ),
      bottomNavigationBar: NavigationBar(
        selectedIndex: _selectedIndex,
        onDestinationSelected: (index) =>
            setState(() => _selectedIndex = index),
        destinations: const [
          NavigationDestination(
            icon: Icon(Symbols.today_rounded),
            label: 'Today',
          ),
          NavigationDestination(
            icon: Icon(Symbols.assignment_rounded),
            label: 'Pipeline',
          ),
          NavigationDestination(
            icon: Icon(Symbols.home_rounded),
            label: 'Rentals',
          ),
          NavigationDestination(
            icon: Icon(Symbols.event_rounded),
            label: 'Calendar',
          ),
          NavigationDestination(
            icon: Icon(Symbols.forum_rounded),
            label: 'Inbox',
          ),
        ],
      ),
    );
  }
}

class _LeasingTodayTab extends ConsumerStatefulWidget {
  const _LeasingTodayTab();

  @override
  ConsumerState<_LeasingTodayTab> createState() => _LeasingTodayTabState();
}

class _LeasingTodayTabState extends ConsumerState<_LeasingTodayTab> {
  late Future<LeasingToday> _future;

  @override
  void initState() {
    super.initState();
    _future = ref.read(leasingWorkspaceRepositoryProvider).today();
  }

  Future<void> _refresh() async {
    final next = ref.read(leasingWorkspaceRepositoryProvider).today();
    setState(() => _future = next);
    await next;
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<LeasingToday>(
      future: _future,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return const Center(child: CircularProgressIndicator());
        }
        if (snapshot.hasError || snapshot.data == null) {
          return _LeasingError(
            message: _error(snapshot.error),
            onRetry: _refresh,
          );
        }
        final today = snapshot.data!;
        return RefreshIndicator(
          onRefresh: _refresh,
          child: ListView(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 96),
            children: [
              Text(
                'What needs attention',
                style: Theme.of(context).textTheme.headlineSmall,
              ),
              const SizedBox(height: 6),
              Text(
                'Only leasing work for rentals assigned to you is shown.',
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: Theme.of(context).colorScheme.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 18),
              _LeasingMetric(
                icon: Symbols.assignment_rounded,
                label: 'Applications to review',
                value: today.applicationsToReview,
              ),
              _LeasingMetric(
                icon: Symbols.campaign_rounded,
                label: 'Listings need attention',
                value: today.listingsNeedingAttention,
              ),
              _LeasingMetric(
                icon: Symbols.event_rounded,
                label: 'Showings today',
                value: today.showingsToday,
              ),
              _LeasingMetric(
                icon: Symbols.key_rounded,
                label: 'Upcoming move-ins',
                value: today.upcomingMoveIns,
              ),
              _LeasingMetric(
                icon: Symbols.forum_rounded,
                label: 'Unread conversations',
                value: today.unreadConversations,
              ),
              const SizedBox(height: 8),
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      const Icon(Symbols.document_scanner_rounded),
                      const SizedBox(width: 12),
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              'Paper or PDF in hand?',
                              style: Theme.of(context).textTheme.titleMedium,
                            ),
                            const SizedBox(height: 4),
                            const Text(
                              'Scan an application or signed lease. Review the extracted draft before anything is saved.',
                            ),
                          ],
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ],
          ),
        );
      },
    );
  }
}

class _LeasingListTab extends ConsumerStatefulWidget {
  const _LeasingListTab({required this.area});

  final LeasingArea area;

  @override
  ConsumerState<_LeasingListTab> createState() => _LeasingListTabState();
}

class _LeasingListTabState extends ConsumerState<_LeasingListTab> {
  static const _take = 20;
  final _searchController = TextEditingController();
  late Future<LeasingPage<Object>> _future;
  int _skip = 0;
  String? _search;

  @override
  void initState() {
    super.initState();
    _future = _load();
  }

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  Future<LeasingPage<Object>> _load() async {
    final repository = ref.read(leasingWorkspaceRepositoryProvider);
    switch (widget.area) {
      case LeasingArea.pipeline:
        return _asObjects(
          await repository.pipelinePage(
            skip: _skip,
            take: _take,
            search: _search,
          ),
        );
      case LeasingArea.rentals:
        return _asObjects(
          await repository.rentalsPage(
            skip: _skip,
            take: _take,
            search: _search,
          ),
        );
      case LeasingArea.calendar:
        return _asObjects(
          await repository.calendarPage(
            skip: _skip,
            take: _take,
            search: _search,
          ),
        );
      case LeasingArea.inbox:
        return _asObjects(
          await repository.inboxPage(skip: _skip, take: _take, search: _search),
        );
      case LeasingArea.today:
        throw StateError('Today is not a paged leasing area.');
    }
  }

  Future<void> _reload({int? skip, String? search}) async {
    _skip = skip ?? _skip;
    _search = search ?? _search;
    final next = _load();
    setState(() => _future = next);
    await next;
  }

  void _submitSearch(String _) {
    _reload(skip: 0, search: _searchController.text.trim());
  }

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 12, 16, 8),
          child: SearchBar(
            controller: _searchController,
            leading: const Icon(Symbols.search_rounded),
            hintText: 'Search ${_areaName(widget.area).toLowerCase()}',
            trailing: [
              IconButton(
                tooltip: 'Search',
                onPressed: () => _submitSearch(_searchController.text),
                icon: const Icon(Symbols.arrow_forward_rounded),
              ),
            ],
            onSubmitted: _submitSearch,
          ),
        ),
        Expanded(
          child: FutureBuilder<LeasingPage<Object>>(
            future: _future,
            builder: (context, snapshot) {
              if (snapshot.connectionState != ConnectionState.done) {
                return const Center(child: CircularProgressIndicator());
              }
              if (snapshot.hasError || snapshot.data == null) {
                return _LeasingError(
                  message: _error(snapshot.error),
                  onRetry: _reload,
                );
              }
              final page = snapshot.data!;
              if (page.items.isEmpty) {
                return RefreshIndicator(
                  onRefresh: _reload,
                  child: ListView(
                    physics: const AlwaysScrollableScrollPhysics(),
                    padding: const EdgeInsets.all(32),
                    children: const [
                      Icon(Symbols.task_alt_rounded, size: 38),
                      SizedBox(height: 12),
                      Text(
                        'Nothing needs attention here.',
                        textAlign: TextAlign.center,
                      ),
                    ],
                  ),
                );
              }
              return RefreshIndicator(
                onRefresh: _reload,
                child: ListView.separated(
                  physics: const AlwaysScrollableScrollPhysics(),
                  padding: const EdgeInsets.fromLTRB(16, 8, 16, 96),
                  itemCount: page.items.length + 1,
                  separatorBuilder: (_, index) => index == page.items.length - 1
                      ? const SizedBox(height: 12)
                      : const SizedBox(height: 8),
                  itemBuilder: (context, index) {
                    if (index == page.items.length) {
                      return _PageControls(
                        page: page,
                        onPrevious: page.skip == 0
                            ? null
                            : () => _reload(
                                skip: page.skip > page.take
                                    ? page.skip - page.take
                                    : 0,
                              ),
                        onNext: page.skip + page.items.length >= page.totalCount
                            ? null
                            : () => _reload(skip: page.skip + page.take),
                      );
                    }
                    return _itemCard(context, page.items[index]);
                  },
                ),
              );
            },
          ),
        ),
      ],
    );
  }

  Widget _itemCard(BuildContext context, Object item) {
    if (item is LeasingPipelineItem) {
      return ListTile(
        tileColor: Theme.of(context).colorScheme.surfaceContainer,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
        leading: const Icon(Symbols.assignment_rounded),
        title: Text(item.title),
        subtitle: Text(
          '${item.stage}\n${_location(item.propertyName, item.unitNumber)}',
        ),
        isThreeLine: true,
        trailing: const Icon(Symbols.chevron_right_rounded),
        onTap: item.kind == 'Application'
            ? () => Navigator.of(context).push<void>(
                MaterialPageRoute<void>(
                  builder: (_) =>
                      ApplicationDetailScreen(applicationId: item.recordId),
                ),
              )
            : item.kind == 'MoveIn'
            ? () => Navigator.of(context).push<void>(
                MaterialPageRoute<void>(
                  builder: (_) => LeasingMoveInDetailScreen(
                    leaseManagementId: item.recordId,
                  ),
                ),
              )
            : item.unitId == null
            ? null
            : () => _openRental(item.unitId!),
      );
    }
    if (item is LeasingRental) {
      final rent = item.askingRent == null
          ? 'Rent not set'
          : '\$${item.askingRent!.toStringAsFixed(0)}/mo';
      return ListTile(
        tileColor: Theme.of(context).colorScheme.surfaceContainer,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
        leading: const Icon(Symbols.home_rounded),
        title: Text('${item.propertyName} · Unit ${item.unitNumber}'),
        subtitle: Text(
          '${item.listingHeadline ?? 'No listing started'} · $rent\n${item.openApplicationCount} open applications',
        ),
        isThreeLine: true,
        trailing: const Icon(Symbols.chevron_right_rounded),
        onTap: () => _openRental(item.unitId),
      );
    }
    if (item is LeasingCalendarItem) {
      return ListTile(
        tileColor: Theme.of(context).colorScheme.surfaceContainer,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
        leading: const Icon(Symbols.event_rounded),
        title: Text(item.title),
        subtitle: Text(
          '${_date(item.scheduledStart)}\n${_location(item.propertyName, item.unitNumber)}',
        ),
        isThreeLine: true,
        trailing: Text(item.status),
        onTap: () => Navigator.of(context).push<void>(
          MaterialPageRoute<void>(
            builder: (_) =>
                LeasingAppointmentDetailScreen(appointmentId: item.id),
          ),
        ),
      );
    }
    final message = item as LeasingInboxItem;
    return ListTile(
      tileColor: Theme.of(context).colorScheme.surfaceContainer,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      leading: Badge(
        isLabelVisible: message.unreadCount > 0,
        label: Text('${message.unreadCount}'),
        child: const Icon(Symbols.forum_rounded),
      ),
      title: Text(message.tenantName),
      subtitle: Text(
        '${message.subject}\n${message.lastMessagePreview ?? 'No message preview'}',
        maxLines: 3,
        overflow: TextOverflow.ellipsis,
      ),
      isThreeLine: true,
      trailing: const Icon(Symbols.chevron_right_rounded),
      onTap: () => Navigator.of(context).push<void>(
        MaterialPageRoute<void>(
          builder: (_) => MessageDetailScreen(
            conversationId: message.id,
            title: message.tenantName,
            subtitle: message.subject,
          ),
        ),
      ),
    );
  }

  void _openRental(int unitId) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => LeasingRentalDetailScreen(unitId: unitId),
      ),
    );
  }
}

class _LeasingMetric extends StatelessWidget {
  const _LeasingMetric({
    required this.icon,
    required this.label,
    required this.value,
  });

  final IconData icon;
  final String label;
  final int value;

  @override
  Widget build(BuildContext context) {
    return Card(
      margin: const EdgeInsets.only(bottom: 10),
      child: ListTile(
        leading: Icon(icon),
        title: Text(label),
        trailing: Text(
          '$value',
          style: Theme.of(context).textTheme.headlineSmall,
        ),
      ),
    );
  }
}

class _PageControls extends StatelessWidget {
  const _PageControls({
    required this.page,
    required this.onPrevious,
    required this.onNext,
  });

  final LeasingPage<Object> page;
  final VoidCallback? onPrevious;
  final VoidCallback? onNext;

  @override
  Widget build(BuildContext context) {
    final first = page.totalCount == 0 ? 0 : page.skip + 1;
    final last = (page.skip + page.items.length).clamp(0, page.totalCount);
    return Row(
      children: [
        Expanded(child: Text('$first–$last of ${page.totalCount}')),
        IconButton(
          tooltip: 'Previous page',
          onPressed: onPrevious,
          icon: const Icon(Symbols.chevron_left_rounded),
        ),
        IconButton(
          tooltip: 'Next page',
          onPressed: onNext,
          icon: const Icon(Symbols.chevron_right_rounded),
        ),
      ],
    );
  }
}

class _LeasingError extends StatelessWidget {
  const _LeasingError({required this.message, required this.onRetry});

  final String message;
  final Future<void> Function() onRetry;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(Symbols.error_rounded, size: 36),
            const SizedBox(height: 12),
            Text(message, textAlign: TextAlign.center),
            const SizedBox(height: 12),
            FilledButton(onPressed: onRetry, child: const Text('Try again')),
          ],
        ),
      ),
    );
  }
}

LeasingPage<Object> _asObjects<T>(LeasingPage<T> page) => LeasingPage<Object>(
  items: page.items.cast<Object>(),
  totalCount: page.totalCount,
  skip: page.skip,
  take: page.take,
);

String _error(Object? error) => error is ApiException
    ? error.message
    : 'We could not load this leasing area.';

String _areaName(LeasingArea area) => switch (area) {
  LeasingArea.pipeline => 'Pipeline',
  LeasingArea.rentals => 'Rentals & listings',
  LeasingArea.calendar => 'Calendar',
  LeasingArea.inbox => 'Inbox',
  LeasingArea.today => 'Today',
};

String _location(String? property, String? unit) => [
  if (property != null && property.isNotEmpty) property,
  if (unit != null && unit.isNotEmpty) 'Unit $unit',
].join(' · ');

String _date(DateTime value) {
  final local = value.toLocal();
  final minute = local.minute.toString().padLeft(2, '0');
  return '${local.month}/${local.day}/${local.year} ${local.hour}:$minute';
}
