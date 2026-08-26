import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/api/api_exception.dart';
import '../owner_portal/owner_portal_repository.dart';
import '../owner_reports/owner_reports_repository.dart';
import 'mobile_role_shell.dart';
import 'mobile_shell_actions.dart';
import '../../core/presentation/formatting.dart';

String _ownerDate(DateTime value) {
  final local = value.toLocal();
  return '${local.month}/${local.day}/${local.year}';
}

String _ownerError(Object error) =>
    error is ApiException ? error.message : 'Something went wrong.';

/// Relationship-scoped Owner shell. It intentionally does not reuse management
/// hubs: every tab calls only `/owner/*` projections anchored to OwnerUserAccess.
class OwnerLandingScreen extends ConsumerStatefulWidget {
  const OwnerLandingScreen({super.key});

  @override
  ConsumerState<OwnerLandingScreen> createState() => _OwnerLandingScreenState();
}

class _OwnerLandingScreenState extends ConsumerState<OwnerLandingScreen> {
  @override
  Widget build(BuildContext context) {
    return const MobileRoleShell(
      actions: [MobileNotificationBell(), MobileAccountMenu()],
      destinations: [
        MobileRoleDestination(
          label: 'Overview',
          icon: Symbols.home_rounded,
          builder: _ownerOverviewBuilder,
        ),
        MobileRoleDestination(
          label: 'Properties',
          icon: Symbols.apartment_rounded,
          builder: _ownerPropertiesBuilder,
        ),
        MobileRoleDestination(
          label: 'Statements',
          icon: Symbols.description_rounded,
          builder: _ownerStatementsBuilder,
        ),
        MobileRoleDestination(
          label: 'Approvals',
          icon: Symbols.task_alt_rounded,
          builder: _ownerApprovalsBuilder,
        ),
        MobileRoleDestination(
          label: 'Messages',
          icon: Symbols.forum_rounded,
          builder: _ownerMessagesBuilder,
        ),
      ],
    );
  }
}

Widget _ownerOverviewBuilder(BuildContext context) => const _OwnerOverviewTab();
Widget _ownerPropertiesBuilder(BuildContext context) =>
    const _OwnerPropertiesTab();
Widget _ownerStatementsBuilder(BuildContext context) =>
    const _OwnerStatementsTab();
Widget _ownerApprovalsBuilder(BuildContext context) =>
    const _OwnerItemsTab(kind: _OwnerItemKind.approvals);
Widget _ownerMessagesBuilder(BuildContext context) =>
    const _OwnerItemsTab(kind: _OwnerItemKind.messages);

class _OwnerOverviewTab extends ConsumerStatefulWidget {
  const _OwnerOverviewTab();

  @override
  ConsumerState<_OwnerOverviewTab> createState() => _OwnerOverviewTabState();
}

class _OwnerOverviewTabState extends ConsumerState<_OwnerOverviewTab> {
  late Future<OwnerPortalOverview> _future;

  @override
  void initState() {
    super.initState();
    _future = ref.read(ownerPortalRepositoryProvider).overview();
  }

  Future<void> _refresh() async {
    final next = ref.read(ownerPortalRepositoryProvider).overview();
    setState(() => _future = next);
    await next;
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<OwnerPortalOverview>(
      future: _future,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return const Center(child: CircularProgressIndicator());
        }
        if (snapshot.hasError || snapshot.data == null) {
          return _OwnerErrorBody(
            message: _ownerError(snapshot.error ?? 'Unable to load overview.'),
            onRetry: _refresh,
          );
        }
        final overview = snapshot.data!;
        return RefreshIndicator(
          onRefresh: _refresh,
          child: ListView(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
            children: [
              Text(
                'Your ownership overview',
                style: Theme.of(context).textTheme.headlineSmall,
              ),
              const SizedBox(height: 6),
              Text(
                'Only information connected to your ownership is shown.',
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: Theme.of(context).colorScheme.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 18),
              _OwnerMetricCard(
                icon: Symbols.apartment_rounded,
                label: 'Properties',
                value: '${overview.propertyCount}',
                supporting: '${overview.unitCount} rental units',
              ),
              const SizedBox(height: 12),
              _OwnerMetricCard(
                icon: Symbols.payments_rounded,
                label: 'Distributed in ${overview.currentYear}',
                value: moneyFmt(overview.distributedThisYear),
              ),
              const SizedBox(height: 12),
              _OwnerMetricCard(
                icon: Symbols.task_alt_rounded,
                label: 'Pending approvals',
                value: '${overview.pendingApprovalCount}',
              ),
              const SizedBox(height: 12),
              _OwnerMetricCard(
                icon: Symbols.forum_rounded,
                label: 'Unread messages',
                value: '${overview.unreadMessageCount}',
              ),
              const SizedBox(height: 18),
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Text(
                    'Team, tenant, banking, billing, security, and workspace settings remain in the management experience and are not available here.',
                    style: Theme.of(context).textTheme.bodySmall,
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

class _OwnerPropertiesTab extends ConsumerStatefulWidget {
  const _OwnerPropertiesTab();

  @override
  ConsumerState<_OwnerPropertiesTab> createState() =>
      _OwnerPropertiesTabState();
}

class _OwnerPropertiesTabState extends ConsumerState<_OwnerPropertiesTab> {
  static const _pageSize = 20;
  final _searchController = TextEditingController();
  String? _search;
  int _skip = 0;
  late Future<OwnerPortalPage<OwnerPortalProperty>> _future;

  @override
  void initState() {
    super.initState();
    _future = _load();
  }

  Future<OwnerPortalPage<OwnerPortalProperty>> _load() => ref
      .read(ownerPortalRepositoryProvider)
      .propertiesPage(skip: _skip, take: _pageSize, search: _search);

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  void _submitSearch(String value) {
    final normalized = value.trim();
    setState(() {
      _search = normalized.isEmpty ? null : normalized;
      _skip = 0;
      _future = _load();
    });
  }

  Future<void> _refresh() async {
    final next = _load();
    setState(() => _future = next);
    await next;
  }

  void _page(int skip) {
    setState(() {
      _skip = skip;
      _future = _load();
    });
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<OwnerPortalPage<OwnerPortalProperty>>(
      future: _future,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return const Center(child: CircularProgressIndicator());
        }
        if (snapshot.hasError || snapshot.data == null) {
          return _OwnerErrorBody(
            message: _ownerError(
              snapshot.error ?? 'Unable to load properties.',
            ),
            onRetry: _refresh,
          );
        }
        final page = snapshot.data!;
        return RefreshIndicator(
          onRefresh: _refresh,
          child: ListView.separated(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
            itemCount: page.items.isEmpty ? 2 : page.items.length + 2,
            separatorBuilder: (_, _) => const SizedBox(height: 10),
            itemBuilder: (context, index) {
              if (index == 0) {
                return SearchBar(
                  controller: _searchController,
                  leading: const Icon(Symbols.search_rounded),
                  hintText: 'Search your properties',
                  onSubmitted: _submitSearch,
                  trailing: [
                    if (_search != null)
                      IconButton(
                        tooltip: 'Clear search',
                        icon: const Icon(Symbols.close_rounded),
                        onPressed: () {
                          _searchController.clear();
                          _submitSearch('');
                        },
                      ),
                  ],
                );
              }
              if (page.items.isEmpty) {
                return const _OwnerEmptyBody(
                  icon: Symbols.apartment_rounded,
                  title: 'No connected properties',
                  message: 'No properties are connected to this owner account.',
                );
              }
              if (index == page.items.length + 1) {
                return page.totalCount > page.take
                    ? _OwnerPager(
                        skip: page.skip,
                        take: page.take,
                        totalCount: page.totalCount,
                        onPrevious: page.skip > 0
                            ? () => _page(
                                (page.skip - page.take)
                                    .clamp(0, page.totalCount)
                                    .toInt(),
                              )
                            : null,
                        onNext: page.skip + page.items.length < page.totalCount
                            ? () => _page(page.skip + page.take)
                            : null,
                      )
                    : const SizedBox.shrink();
              }
              final property = page.items[index - 1];
              return Card(
                child: ListTile(
                  leading: const Icon(Symbols.apartment_rounded),
                  title: Text(property.name),
                  subtitle: Text(
                    '${property.address}\n${property.unitCount} ${property.unitCount == 1 ? 'unit' : 'units'}',
                  ),
                  isThreeLine: true,
                ),
              );
            },
          ),
        );
      },
    );
  }
}

class _OwnerStatementsTab extends ConsumerStatefulWidget {
  const _OwnerStatementsTab();

  @override
  ConsumerState<_OwnerStatementsTab> createState() =>
      _OwnerStatementsTabState();
}

class _OwnerStatementsTabState extends ConsumerState<_OwnerStatementsTab> {
  static const _pageSize = 20;
  final _statementSearchController = TextEditingController();
  String? _statementSearch;
  int _year = DateTime.now().year;
  int _statementSkip = 0;
  int _distributionSkip = 0;
  late Future<
    ({
      OwnerPortalPage<OwnerSummary> summaries,
      OwnerPortalPage<OwnerDistribution> distributions,
    })
  >
  _future;

  @override
  void initState() {
    super.initState();
    _future = _load();
  }

  @override
  void dispose() {
    _statementSearchController.dispose();
    super.dispose();
  }

  Future<
    ({
      OwnerPortalPage<OwnerSummary> summaries,
      OwnerPortalPage<OwnerDistribution> distributions,
    })
  >
  _load() async {
    final repository = ref.read(ownerPortalRepositoryProvider);
    final values = await Future.wait<Object>([
      repository.statementsPage(
        year: _year,
        skip: _statementSkip,
        take: _pageSize,
        search: _statementSearch,
      ),
      repository.distributionsPage(
        year: _year,
        skip: _distributionSkip,
        take: _pageSize,
      ),
    ]);
    return (
      summaries: values[0] as OwnerPortalPage<OwnerSummary>,
      distributions: values[1] as OwnerPortalPage<OwnerDistribution>,
    );
  }

  Future<void> _refresh() async {
    final next = _load();
    setState(() => _future = next);
    await next;
  }

  void _changeYear(int year) {
    setState(() {
      _year = year;
      _statementSkip = 0;
      _distributionSkip = 0;
      _future = _load();
    });
  }

  void _searchStatements(String value) {
    final normalized = value.trim();
    setState(() {
      _statementSearch = normalized.isEmpty ? null : normalized;
      _statementSkip = 0;
      _future = _load();
    });
  }

  void _pageStatements(int skip) {
    setState(() {
      _statementSkip = skip;
      _future = _load();
    });
  }

  void _pageDistributions(int skip) {
    setState(() {
      _distributionSkip = skip;
      _future = _load();
    });
  }

  Future<void> _showStatement(OwnerSummary owner) async {
    final future = ref
        .read(ownerPortalRepositoryProvider)
        .statement(owner.ownerId, _year);
    await showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      builder: (context) => FutureBuilder<OwnerStatement>(
        future: future,
        builder: (context, snapshot) {
          if (snapshot.connectionState != ConnectionState.done) {
            return const SizedBox(
              height: 280,
              child: Center(child: CircularProgressIndicator()),
            );
          }
          if (snapshot.hasError || snapshot.data == null) {
            return SizedBox(
              height: 280,
              child: _OwnerErrorBody(
                message: _ownerError(
                  snapshot.error ?? 'Unable to load statement.',
                ),
                onRetry: () => Navigator.of(context).pop(),
              ),
            );
          }
          return _OwnerStatementSheet(statement: snapshot.data!);
        },
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final years = List.generate(5, (index) => DateTime.now().year - index);
    return FutureBuilder<
      ({
        OwnerPortalPage<OwnerSummary> summaries,
        OwnerPortalPage<OwnerDistribution> distributions,
      })
    >(
      future: _future,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return const Center(child: CircularProgressIndicator());
        }
        if (snapshot.hasError || snapshot.data == null) {
          return _OwnerErrorBody(
            message: _ownerError(
              snapshot.error ?? 'Unable to load statements.',
            ),
            onRetry: _refresh,
          );
        }
        final data = snapshot.data!;
        return RefreshIndicator(
          onRefresh: _refresh,
          child: ListView(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
            children: [
              Align(
                alignment: Alignment.centerRight,
                child: DropdownButton<int>(
                  value: _year,
                  items: years
                      .map(
                        (year) =>
                            DropdownMenuItem(value: year, child: Text('$year')),
                      )
                      .toList(growable: false),
                  onChanged: (year) {
                    if (year != null) _changeYear(year);
                  },
                ),
              ),
              const SizedBox(height: 8),
              SearchBar(
                controller: _statementSearchController,
                leading: const Icon(Symbols.search_rounded),
                hintText: 'Search owner statements',
                onSubmitted: _searchStatements,
                trailing: [
                  if (_statementSearch != null)
                    IconButton(
                      tooltip: 'Clear statement search',
                      icon: const Icon(Symbols.close_rounded),
                      onPressed: () {
                        _statementSearchController.clear();
                        _searchStatements('');
                      },
                    ),
                ],
              ),
              const SizedBox(height: 12),
              if (data.summaries.items.isEmpty)
                _OwnerEmptyBody(
                  icon: Symbols.description_rounded,
                  title: 'No statement data for $_year',
                  message:
                      'Statement totals appear after property income or expenses are recorded.',
                )
              else
                ...data.summaries.items.map(
                  (owner) => Card(
                    child: ListTile(
                      title: Text(owner.ownerName),
                      subtitle: Text(
                        'Net ${moneyFmt(owner.netToOwner)} · Distributed ${moneyFmt(owner.totalDistributed)}',
                      ),
                      trailing: const Icon(Icons.chevron_right),
                      onTap: () => _showStatement(owner),
                    ),
                  ),
                ),
              if (data.summaries.totalCount > data.summaries.take)
                _OwnerPager(
                  skip: data.summaries.skip,
                  take: data.summaries.take,
                  totalCount: data.summaries.totalCount,
                  onPrevious: data.summaries.skip > 0
                      ? () => _pageStatements(
                          (data.summaries.skip - data.summaries.take)
                              .clamp(0, data.summaries.totalCount)
                              .toInt(),
                        )
                      : null,
                  onNext:
                      data.summaries.skip + data.summaries.items.length <
                          data.summaries.totalCount
                      ? () => _pageStatements(
                          data.summaries.skip + data.summaries.take,
                        )
                      : null,
                ),
              const SizedBox(height: 20),
              Text(
                'Recorded distributions',
                style: Theme.of(context).textTheme.titleMedium,
              ),
              const SizedBox(height: 8),
              if (data.distributions.items.isEmpty)
                Text(
                  'No distributions are recorded for $_year.',
                  style: Theme.of(context).textTheme.bodyMedium,
                )
              else
                ...data.distributions.items.map(
                  (distribution) => ListTile(
                    contentPadding: EdgeInsets.zero,
                    title: Text(
                      distribution.propertyName ?? distribution.ownerName,
                    ),
                    subtitle: Text(
                      '${_ownerDate(distribution.date)} · ${distribution.method.label}',
                    ),
                    trailing: Text(
                      moneyFmt(distribution.amount),
                      style: const TextStyle(fontWeight: FontWeight.w600),
                    ),
                  ),
                ),
              if (data.distributions.totalCount > data.distributions.take)
                _OwnerPager(
                  skip: data.distributions.skip,
                  take: data.distributions.take,
                  totalCount: data.distributions.totalCount,
                  onPrevious: data.distributions.skip > 0
                      ? () => _pageDistributions(
                          (data.distributions.skip - data.distributions.take)
                              .clamp(0, data.distributions.totalCount)
                              .toInt(),
                        )
                      : null,
                  onNext:
                      data.distributions.skip +
                              data.distributions.items.length <
                          data.distributions.totalCount
                      ? () => _pageDistributions(
                          data.distributions.skip + data.distributions.take,
                        )
                      : null,
                ),
              const SizedBox(height: 16),
              Text(
                'For reference only. Totals use collected rent allocations and paid expenses for the selected year.',
                style: Theme.of(context).textTheme.bodySmall,
              ),
            ],
          ),
        );
      },
    );
  }
}

enum _OwnerItemKind { approvals, messages }

class _OwnerItemsTab extends ConsumerStatefulWidget {
  const _OwnerItemsTab({required this.kind});

  final _OwnerItemKind kind;

  @override
  ConsumerState<_OwnerItemsTab> createState() => _OwnerItemsTabState();
}

class _OwnerItemsTabState extends ConsumerState<_OwnerItemsTab> {
  static const _pageSize = 20;
  int _skip = 0;
  late Future<OwnerPortalPage<OwnerPortalItem>> _future;

  @override
  void initState() {
    super.initState();
    _future = _load();
  }

  Future<OwnerPortalPage<OwnerPortalItem>> _load() {
    final repository = ref.read(ownerPortalRepositoryProvider);
    return widget.kind == _OwnerItemKind.approvals
        ? repository.approvalsPage(skip: _skip, take: _pageSize)
        : repository.messagesPage(skip: _skip, take: _pageSize);
  }

  Future<void> _refresh() async {
    final next = _load();
    setState(() => _future = next);
    await next;
  }

  void _page(int skip) {
    setState(() {
      _skip = skip;
      _future = _load();
    });
  }

  @override
  Widget build(BuildContext context) {
    final approvals = widget.kind == _OwnerItemKind.approvals;
    return FutureBuilder<OwnerPortalPage<OwnerPortalItem>>(
      future: _future,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return const Center(child: CircularProgressIndicator());
        }
        if (snapshot.hasError || snapshot.data == null) {
          return _OwnerErrorBody(
            message: _ownerError(snapshot.error ?? 'Unable to load items.'),
            onRetry: _refresh,
          );
        }
        final page = snapshot.data!;
        final items = page.items;
        return RefreshIndicator(
          onRefresh: _refresh,
          child: ListView.separated(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
            itemCount: items.isEmpty ? 1 : items.length + 1,
            separatorBuilder: (_, _) => const SizedBox(height: 10),
            itemBuilder: (context, index) {
              if (items.isEmpty) {
                return _OwnerEmptyBody(
                  icon: approvals
                      ? Symbols.task_alt_rounded
                      : Symbols.forum_rounded,
                  title: approvals
                      ? 'No decisions waiting'
                      : 'No owner messages yet',
                  message: approvals
                      ? 'Your property manager has not routed any approval items to you.'
                      : 'Only messages approved for your owner account will appear here.',
                );
              }
              if (index == items.length) {
                return page.totalCount > page.take
                    ? _OwnerPager(
                        skip: page.skip,
                        take: page.take,
                        totalCount: page.totalCount,
                        onPrevious: page.skip > 0
                            ? () => _page(
                                (page.skip - page.take)
                                    .clamp(0, page.totalCount)
                                    .toInt(),
                              )
                            : null,
                        onNext: page.skip + items.length < page.totalCount
                            ? () => _page(page.skip + page.take)
                            : null,
                      )
                    : const SizedBox.shrink();
              }
              final item = items[index];
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
                              item.title,
                              style: Theme.of(context).textTheme.titleSmall,
                            ),
                          ),
                          if (!item.isRead) const Badge(label: Text('New')),
                        ],
                      ),
                      const SizedBox(height: 8),
                      Text(item.message),
                      const SizedBox(height: 10),
                      Text(
                        _ownerDate(item.createdAt),
                        style: Theme.of(context).textTheme.bodySmall,
                      ),
                    ],
                  ),
                ),
              );
            },
          ),
        );
      },
    );
  }
}

class _OwnerMetricCard extends StatelessWidget {
  const _OwnerMetricCard({
    required this.icon,
    required this.label,
    required this.value,
    this.supporting,
  });

  final IconData icon;
  final String label;
  final String value;
  final String? supporting;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(icon, color: Theme.of(context).colorScheme.primary),
            const SizedBox(width: 14),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(label, style: Theme.of(context).textTheme.bodySmall),
                  Text(value, style: Theme.of(context).textTheme.headlineSmall),
                  if (supporting != null)
                    Text(
                      supporting!,
                      style: Theme.of(context).textTheme.bodySmall,
                    ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _OwnerStatementSheet extends StatelessWidget {
  const _OwnerStatementSheet({required this.statement});

  final OwnerStatement statement;

  @override
  Widget build(BuildContext context) {
    return SafeArea(
      child: DraggableScrollableSheet(
        expand: false,
        initialChildSize: .8,
        maxChildSize: .95,
        builder: (context, controller) => ListView(
          controller: controller,
          padding: const EdgeInsets.all(20),
          children: [
            Text(
              '${statement.ownerName} · ${statement.year}',
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: 16),
            _OwnerMetricCard(
              icon: Symbols.payments_rounded,
              label: 'Net to owner',
              value: moneyFmt(statement.totalNetToOwner),
              supporting:
                  'Distributed ${moneyFmt(statement.totalDistributed)} · Undistributed ${moneyFmt(statement.undistributed)}',
            ),
            const SizedBox(height: 16),
            ...statement.properties.map(
              (property) => ListTile(
                contentPadding: EdgeInsets.zero,
                title: Text(property.propertyName),
                subtitle: Text(
                  'Income ${moneyFmt(property.rentalIncome)} · Expenses and fee ${moneyFmt(property.expenses + property.managementFee)}',
                ),
                trailing: Text(moneyFmt(property.netToOwner)),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _OwnerPager extends StatelessWidget {
  const _OwnerPager({
    required this.skip,
    required this.take,
    required this.totalCount,
    required this.onPrevious,
    required this.onNext,
  });

  final int skip;
  final int take;
  final int totalCount;
  final VoidCallback? onPrevious;
  final VoidCallback? onNext;

  @override
  Widget build(BuildContext context) {
    final first = totalCount == 0 ? 0 : skip + 1;
    final last = (skip + take).clamp(0, totalCount);
    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        IconButton(
          onPressed: onPrevious,
          icon: const Icon(Icons.chevron_left),
          tooltip: 'Previous page',
        ),
        Text('$first–$last of $totalCount'),
        IconButton(
          onPressed: onNext,
          icon: const Icon(Icons.chevron_right),
          tooltip: 'Next page',
        ),
      ],
    );
  }
}

class _OwnerEmptyBody extends StatelessWidget {
  const _OwnerEmptyBody({
    required this.icon,
    required this.title,
    required this.message,
  });

  final IconData icon;
  final String title;
  final String message;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 64, horizontal: 24),
      child: Column(
        children: [
          Icon(icon, size: 42, color: Theme.of(context).colorScheme.outline),
          const SizedBox(height: 12),
          Text(title, style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 6),
          Text(message, textAlign: TextAlign.center),
        ],
      ),
    );
  }
}

class _OwnerErrorBody extends StatelessWidget {
  const _OwnerErrorBody({required this.message, required this.onRetry});

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
            Text(message, textAlign: TextAlign.center),
            const SizedBox(height: 12),
            OutlinedButton(onPressed: onRetry, child: const Text('Try again')),
          ],
        ),
      ),
    );
  }
}
