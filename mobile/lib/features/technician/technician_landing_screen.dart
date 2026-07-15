import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../home/mobile_quick_action_helpers.dart';
import '../home/mobile_role_shell.dart';
import '../home/mobile_shell_actions.dart';
import 'technician_assignment_detail_screen.dart';
import 'technician_repository.dart';

class TechnicianLandingScreen extends ConsumerStatefulWidget {
  const TechnicianLandingScreen({super.key});

  @override
  ConsumerState<TechnicianLandingScreen> createState() =>
      _TechnicianLandingScreenState();
}

class _TechnicianLandingScreenState
    extends ConsumerState<TechnicianLandingScreen> {
  @override
  Widget build(BuildContext context) => MobileRoleShell(
    actions: const [MobileNotificationBell(), MobileAccountMenu()],
    destinations: const [
      MobileRoleDestination(
        label: 'My work',
        icon: Symbols.build_rounded,
        builder: _technicianAssignmentsBuilder,
      ),
      MobileRoleDestination(
        label: 'Schedule',
        icon: Symbols.schedule_rounded,
        builder: _technicianScheduleBuilder,
      ),
      MobileRoleDestination(
        label: 'Inbox',
        icon: Symbols.forum_rounded,
        builder: _technicianInboxBuilder,
      ),
      MobileRoleDestination(
        label: 'Profile',
        icon: Symbols.person_rounded,
        builder: _technicianProfileBuilder,
      ),
    ],
    floatingActionButton: FloatingActionButton.extended(
      heroTag: 'technician-assigned-work-scan',
      onPressed: () => openAuthorizedMobileScan(context, ref),
      icon: const Icon(Symbols.document_scanner_rounded),
      label: const Text('Scan / Add'),
    ),
  );
}

Widget _technicianAssignmentsBuilder(BuildContext context) =>
    const TechnicianAssignmentList(area: 'assignments');
Widget _technicianScheduleBuilder(BuildContext context) =>
    const TechnicianAssignmentList(area: 'schedule');
Widget _technicianInboxBuilder(BuildContext context) =>
    const TechnicianAssignmentList(area: 'inbox');
Widget _technicianProfileBuilder(BuildContext context) =>
    const _TechnicianProfile();

class TechnicianAssignmentList extends ConsumerStatefulWidget {
  const TechnicianAssignmentList({super.key, required this.area});
  final String area;

  @override
  ConsumerState<TechnicianAssignmentList> createState() =>
      _TechnicianAssignmentListState();
}

class _TechnicianAssignmentListState
    extends ConsumerState<TechnicianAssignmentList> {
  static const _take = 20;
  final _searchController = TextEditingController();
  String? _status;
  int _skip = 0;
  late Future<TechnicianAssignmentPage> _future;

  @override
  void initState() {
    super.initState();
    _load();
  }

  void _load() {
    _future = ref
        .read(technicianRepositoryProvider)
        .list(
          area: widget.area,
          search: _searchController.text.trim(),
          status: _status,
          skip: _skip,
          take: _take,
        );
  }

  Future<void> _refresh() async {
    setState(_load);
    await _future;
  }

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Column(
    children: [
      Padding(
        padding: const EdgeInsets.fromLTRB(16, 12, 16, 8),
        child: Row(
          children: [
            Expanded(
              child: SearchBar(
                controller: _searchController,
                hintText: 'Search assigned work',
                leading: const Icon(Symbols.search_rounded),
                trailing: [
                  if (_searchController.text.isNotEmpty)
                    IconButton(
                      icon: const Icon(Symbols.close_rounded),
                      onPressed: () {
                        _searchController.clear();
                        _skip = 0;
                        setState(_load);
                      },
                    ),
                ],
                onSubmitted: (_) {
                  _skip = 0;
                  setState(_load);
                },
              ),
            ),
            const SizedBox(width: 8),
            PopupMenuButton<String?>(
              icon: const Icon(Symbols.filter_list_rounded),
              tooltip: 'Filter by status',
              onSelected: (value) {
                _status = value;
                _skip = 0;
                setState(_load);
              },
              itemBuilder: (_) => const [
                PopupMenuItem(value: null, child: Text('All statuses')),
                PopupMenuItem(value: 'New', child: Text('New')),
                PopupMenuItem(value: 'Scheduled', child: Text('Scheduled')),
                PopupMenuItem(value: 'InProgress', child: Text('In progress')),
                PopupMenuItem(
                  value: 'WaitingParts',
                  child: Text('Waiting for parts'),
                ),
                PopupMenuItem(value: 'Completed', child: Text('Completed')),
              ],
            ),
          ],
        ),
      ),
      Expanded(
        child: FutureBuilder<TechnicianAssignmentPage>(
          future: _future,
          builder: (context, snapshot) {
            if (snapshot.connectionState != ConnectionState.done) {
              return const Center(child: CircularProgressIndicator());
            }
            if (snapshot.hasError) {
              return _ErrorState(
                error: snapshot.error!,
                onRetry: () => setState(_load),
              );
            }
            final page = snapshot.data;
            if (page == null) {
              return _ErrorState(
                error: StateError('Assigned work is unavailable.'),
                onRetry: () => setState(_load),
              );
            }
            final items = page.items;
            if (items.isEmpty) {
              return RefreshIndicator(
                onRefresh: _refresh,
                child: ListView(
                  children: const [
                    SizedBox(height: 140),
                    Icon(Symbols.task_alt_rounded, size: 48),
                    SizedBox(height: 12),
                    Center(child: Text('Nothing assigned right now')),
                  ],
                ),
              );
            }
            return RefreshIndicator(
              onRefresh: _refresh,
              child: ListView.separated(
                padding: const EdgeInsets.fromLTRB(16, 8, 16, 100),
                itemCount: items.length + (page.totalCount > page.take ? 1 : 0),
                separatorBuilder: (_, _) => const SizedBox(height: 10),
                itemBuilder: (context, index) {
                  if (index == items.length) {
                    return Row(
                      mainAxisAlignment: MainAxisAlignment.end,
                      children: [
                        OutlinedButton(
                          onPressed: page.skip == 0
                              ? null
                              : () {
                                  _skip = page.skip > page.take
                                      ? page.skip - page.take
                                      : 0;
                                  setState(_load);
                                },
                          child: const Text('Previous'),
                        ),
                        const SizedBox(width: 8),
                        OutlinedButton(
                          onPressed:
                              page.skip + page.items.length >= page.totalCount
                              ? null
                              : () {
                                  _skip = page.skip + page.take;
                                  setState(_load);
                                },
                          child: const Text('Next'),
                        ),
                      ],
                    );
                  }
                  return _AssignmentCard(
                    assignment: items[index],
                    onTap: () async {
                      await Navigator.of(context).push<void>(
                        MaterialPageRoute(
                          builder: (_) => TechnicianAssignmentDetailScreen(
                            workOrderId: items[index].id,
                          ),
                        ),
                      );
                      if (mounted) setState(_load);
                    },
                  );
                },
              ),
            );
          },
        ),
      ),
    ],
  );
}

class _AssignmentCard extends StatelessWidget {
  const _AssignmentCard({required this.assignment, required this.onTap});
  final TechnicianAssignmentSummary assignment;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    final scheduled = assignment.scheduledForUtc?.toLocal();
    return Card(
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      assignment.title,
                      style: Theme.of(context).textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                  if (assignment.unreadMessageCount > 0)
                    Badge(label: Text('${assignment.unreadMessageCount}')),
                  const SizedBox(width: 8),
                  const Icon(Symbols.chevron_right_rounded),
                ],
              ),
              const SizedBox(height: 8),
              Text(
                assignment.status,
                style: TextStyle(
                  color: colorScheme.primary,
                  fontWeight: FontWeight.w600,
                ),
              ),
              const SizedBox(height: 10),
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Icon(Symbols.location_on_rounded, size: 18),
                  const SizedBox(width: 6),
                  Expanded(
                    child: Text(
                      '${assignment.address}${assignment.unit == null ? '' : ' · Unit ${assignment.unit}'}',
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 6),
              Row(
                children: [
                  const Icon(Symbols.schedule_rounded, size: 18),
                  const SizedBox(width: 6),
                  Text(
                    scheduled == null
                        ? 'Not scheduled'
                        : MaterialLocalizations.of(
                            context,
                          ).formatMediumDate(scheduled),
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _TechnicianProfile extends ConsumerWidget {
  const _TechnicianProfile();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final auth = ref.watch(authControllerProvider);
    final user = auth is AuthStateAuthenticated ? auth.user : null;
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Card(
          child: Padding(
            padding: const EdgeInsets.all(20),
            child: Row(
              children: [
                CircleAvatar(
                  radius: 28,
                  child: Text(
                    (user?.displayName.isNotEmpty ?? false)
                        ? user!.displayName[0].toUpperCase()
                        : 'T',
                  ),
                ),
                const SizedBox(width: 16),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        user?.displayName ?? 'Technician',
                        style: Theme.of(context).textTheme.titleMedium
                            ?.copyWith(fontWeight: FontWeight.w700),
                      ),
                      const SizedBox(height: 4),
                      Text(
                        user?.email ?? '',
                        style: Theme.of(context).textTheme.bodySmall,
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 12),
        const Card(
          child: ListTile(
            leading: Icon(Symbols.shield_rounded),
            title: Text('Sign-in security'),
            subtitle: Text(
              'Use the account menu to manage password and sessions.',
            ),
          ),
        ),
      ],
    );
  }
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
          const Icon(Symbols.error_rounded, size: 42),
          const SizedBox(height: 12),
          Text(
            error is ApiException
                ? (error as ApiException).message
                : 'Assigned work is unavailable.',
          ),
          const SizedBox(height: 12),
          FilledButton(onPressed: onRetry, child: const Text('Try again')),
        ],
      ),
    ),
  );
}
