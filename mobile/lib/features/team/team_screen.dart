import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import '../properties/properties_repository.dart';
import 'team_repository.dart';

class TeamScreen extends ConsumerStatefulWidget {
  const TeamScreen({super.key});

  @override
  ConsumerState<TeamScreen> createState() => _TeamScreenState();
}

class _TeamScreenState extends ConsumerState<TeamScreen> {
  final _searchController = TextEditingController();
  Timer? _searchDebounce;

  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(teamProvider.notifier).load());
  }

  @override
  void dispose() {
    _searchDebounce?.cancel();
    _searchController.dispose();
    super.dispose();
  }

  void _search(String value) {
    _searchDebounce?.cancel();
    _searchDebounce = Timer(const Duration(milliseconds: 350), () {
      ref.read(teamProvider.notifier).load(search: value);
    });
  }

  Future<void> _showCreateSheet() async {
    await showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => const _CreateTeamMemberSheet(),
    );
  }

  Future<void> _showAssignments(
    TeamMember member, {
    required bool canManageTeam,
    required bool isCurrentUser,
  }) async {
    await showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      showDragHandle: true,
      builder: (_) => _AssignmentsSheet(
        member: member,
        canManageTeam: canManageTeam,
        isCurrentUser: isCurrentUser,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(teamProvider);
    final auth = ref.watch(authControllerProvider);
    final currentUserId = auth is AuthStateAuthenticated ? auth.user.id : null;
    final canManageTeam =
        auth is AuthStateAuthenticated && auth.hasCapability('team.manage');

    return Scaffold(
      appBar: AppBar(title: const Text('Team')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'team-fab',
        primaryAction: canManageTeam
            ? MobileQuickAction(
                label: 'Add team member',
                icon: Icons.person_add_outlined,
                onPressed: _showCreateSheet,
              )
            : null,
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: RefreshIndicator(
        onRefresh: ref.read(teamProvider.notifier).refresh,
        child: CustomScrollView(
          physics: const AlwaysScrollableScrollPhysics(),
          slivers: [
            SliverToBoxAdapter(
              child: Padding(
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 8),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Give each person a clear job and only the rentals or assigned work they need.',
                      style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: Theme.of(context).colorScheme.onSurfaceVariant,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      'Owners and tenants are managed from their own records, not as Team roles.',
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: Theme.of(context).colorScheme.onSurfaceVariant,
                      ),
                    ),
                    const SizedBox(height: 12),
                    SearchBar(
                      controller: _searchController,
                      leading: const Icon(Icons.search),
                      hintText: 'Search by name or email',
                      onChanged: _search,
                      trailing: [
                        if (_searchController.text.isNotEmpty)
                          IconButton(
                            tooltip: 'Clear search',
                            onPressed: () {
                              _searchController.clear();
                              setState(() {});
                              _search('');
                            },
                            icon: const Icon(Icons.close),
                          ),
                      ],
                    ),
                  ],
                ),
              ),
            ),
            ...state.when(
              loading: () => const [
                SliverFillRemaining(
                  hasScrollBody: false,
                  child: Center(child: CircularProgressIndicator()),
                ),
              ],
              error: (error, _) => [
                SliverFillRemaining(
                  hasScrollBody: false,
                  child: _ErrorBody(
                    message: error is ApiException
                        ? error.message
                        : error.toString(),
                    onRetry: ref.read(teamProvider.notifier).refresh,
                  ),
                ),
              ],
              data: (page) {
                if (page.items.isEmpty) {
                  return const [
                    SliverFillRemaining(
                      hasScrollBody: false,
                      child: _EmptyBody(),
                    ),
                  ];
                }
                return [
                  SliverPadding(
                    padding: const EdgeInsets.fromLTRB(16, 8, 16, 8),
                    sliver: SliverList.separated(
                      itemCount: page.items.length,
                      separatorBuilder: (_, _) => const SizedBox(height: 8),
                      itemBuilder: (_, index) => _MemberCard(
                        member: page.items[index],
                        isCurrentUser:
                            page.items[index].userId == currentUserId,
                        canManageTeam: canManageTeam,
                        onViewAssignments: () => _showAssignments(
                          page.items[index],
                          canManageTeam: canManageTeam,
                          isCurrentUser:
                              page.items[index].userId == currentUserId,
                        ),
                      ),
                    ),
                  ),
                  if (page.hasMore)
                    SliverToBoxAdapter(
                      child: Padding(
                        padding: const EdgeInsets.fromLTRB(16, 4, 16, 100),
                        child: OutlinedButton(
                          onPressed: ref.read(teamProvider.notifier).loadMore,
                          child: Text(
                            'Load more (${page.items.length} of ${page.totalCount})',
                          ),
                        ),
                      ),
                    )
                  else
                    const SliverToBoxAdapter(child: SizedBox(height: 100)),
                ];
              },
            ),
          ],
        ),
      ),
    );
  }
}

class _MemberCard extends ConsumerStatefulWidget {
  const _MemberCard({
    required this.member,
    required this.isCurrentUser,
    required this.canManageTeam,
    required this.onViewAssignments,
  });

  final TeamMember member;
  final bool isCurrentUser;
  final bool canManageTeam;
  final VoidCallback onViewAssignments;

  @override
  ConsumerState<_MemberCard> createState() => _MemberCardState();
}

class _MemberCardState extends ConsumerState<_MemberCard> {
  bool _saving = false;

  Future<void> _changeStatus() async {
    setState(() => _saving = true);
    try {
      await ref.read(teamProvider.notifier).changeStatus(widget.member);
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              widget.member.isActive
                  ? 'Team access suspended.'
                  : 'Team access reactivated.',
            ),
          ),
        );
      }
    } on ApiException catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(error.message)));
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final member = widget.member;
    final theme = Theme.of(context);
    final colors = theme.colorScheme;
    final label = member.displayName.isNotEmpty
        ? member.displayName
        : member.email;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                CircleAvatar(
                  backgroundColor: member.isActive
                      ? colors.primaryContainer
                      : colors.surfaceContainerHighest,
                  child: Text(label.isEmpty ? '?' : label[0].toUpperCase()),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        widget.isCurrentUser ? '$label (you)' : label,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: theme.textTheme.titleSmall?.copyWith(
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                      Text(
                        member.email,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: colors.onSurfaceVariant,
                        ),
                      ),
                      if (member.requiresAccountActivation)
                        Text(
                          'Activation required — cannot sign in until the queued email is delivered and used.',
                          style: theme.textTheme.labelSmall?.copyWith(
                            color: colors.tertiary,
                            fontWeight: FontWeight.w600,
                          ),
                        ),
                    ],
                  ),
                ),
                _StatusChip(active: member.isActive),
              ],
            ),
            const SizedBox(height: 12),
            Text(
              member.roleSummary.isEmpty
                  ? 'No active job assignment'
                  : member.roleSummary,
              style: theme.textTheme.bodyMedium?.copyWith(
                fontWeight: FontWeight.w600,
              ),
            ),
            Text(
              '${member.assignmentCount} ${member.assignmentCount == 1 ? 'assignment' : 'assignments'}',
              style: theme.textTheme.bodySmall?.copyWith(
                color: colors.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 10),
            Align(
              alignment: Alignment.centerRight,
              child: Wrap(
                spacing: 8,
                children: [
                  TextButton(
                    onPressed: widget.onViewAssignments,
                    child: const Text('Assignments'),
                  ),
                  if (widget.canManageTeam)
                    TextButton(
                      onPressed: widget.isCurrentUser || _saving
                          ? null
                          : _changeStatus,
                      child: _saving
                          ? const SizedBox.square(
                              dimension: 16,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : Text(
                              member.isActive ? 'Suspend access' : 'Reactivate',
                            ),
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

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.active});

  final bool active;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 4),
      decoration: BoxDecoration(
        color: active ? colors.primaryContainer : colors.surfaceContainerHigh,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Text(
        active ? 'Active' : 'Suspended',
        style: Theme.of(context).textTheme.labelSmall?.copyWith(
          color: active ? colors.onPrimaryContainer : colors.onSurfaceVariant,
        ),
      ),
    );
  }
}

class _AssignmentsSheet extends ConsumerStatefulWidget {
  const _AssignmentsSheet({
    required this.member,
    required this.canManageTeam,
    required this.isCurrentUser,
  });

  final TeamMember member;
  final bool canManageTeam;
  final bool isCurrentUser;

  @override
  ConsumerState<_AssignmentsSheet> createState() => _AssignmentsSheetState();
}

class _AssignmentsSheetState extends ConsumerState<_AssignmentsSheet> {
  static const _pageSize = 50;
  final List<TeamAssignment> _items = [];
  bool _loading = true;
  bool _loadingMore = false;
  int _totalCount = 0;
  late int _accessRevision;
  int? _savingAssignmentId;
  String? _error;

  @override
  void initState() {
    super.initState();
    _accessRevision = widget.member.accessRevision;
    Future.microtask(() => _load(reset: true));
  }

  bool get _hasMore => _items.length < _totalCount;

  Future<void> _load({required bool reset}) async {
    setState(() {
      if (reset) {
        _loading = true;
        _error = null;
      } else {
        _loadingMore = true;
      }
    });
    try {
      final page = await ref
          .read(teamRepositoryProvider)
          .listAssignments(
            widget.member.accessContextId,
            skip: reset ? 0 : _items.length,
            take: _pageSize,
          );
      if (!mounted) return;
      setState(() {
        if (reset) _items.clear();
        _items.addAll(page.items);
        _totalCount = page.totalCount;
      });
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) {
        setState(() {
          _loading = false;
          _loadingMore = false;
        });
      }
    }
  }

  Future<void> _editAssignment({TeamAssignment? replacement}) async {
    final draft = await showModalBottomSheet<_AssignmentDraft>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      showDragHandle: true,
      builder: (_) => _AssignmentEditorSheet(replacement: replacement),
    );
    if (draft == null || !mounted) return;

    setState(() => _savingAssignmentId = replacement?.assignmentId ?? -1);
    try {
      final repository = ref.read(teamRepositoryProvider);
      final result = replacement == null
          ? await repository.addAssignment(
              accessContextId: widget.member.accessContextId,
              expectedAccessRevision: _accessRevision,
              roleProfileKey: draft.roleProfileKey,
              scopeKind: draft.scopeKind,
              selectedPropertyIds: draft.propertyIds,
            )
          : await repository.replaceAssignmentProperties(
              accessContextId: widget.member.accessContextId,
              assignmentId: replacement.assignmentId,
              expectedAccessRevision: _accessRevision,
              propertyIds: draft.propertyIds,
            );
      _accessRevision = result.accessRevision;
      await _load(reset: true);
      await ref.read(teamProvider.notifier).refresh();
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              replacement == null
                  ? 'Assignment added.'
                  : 'Property scope replaced.',
            ),
          ),
        );
      }
    } on ApiException catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(error.message)));
      }
    } finally {
      if (mounted) setState(() => _savingAssignmentId = null);
    }
  }

  Future<void> _endAssignment(TeamAssignment assignment) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('End assignment?'),
        content: Text(
          'End the ${assignment.roleProfileName} assignment now? The member keeps any other active assignments.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: const Text('End assignment'),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;

    setState(() => _savingAssignmentId = assignment.assignmentId);
    try {
      final result = await ref
          .read(teamRepositoryProvider)
          .endAssignment(
            accessContextId: widget.member.accessContextId,
            assignmentId: assignment.assignmentId,
            expectedAccessRevision: _accessRevision,
          );
      _accessRevision = result.accessRevision;
      await _load(reset: true);
      await ref.read(teamProvider.notifier).refresh();
      if (mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(const SnackBar(content: Text('Assignment ended.')));
      }
    } on ApiException catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(error.message)));
      }
    } finally {
      if (mounted) setState(() => _savingAssignmentId = null);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colors = theme.colorScheme;

    return SizedBox(
      height: MediaQuery.sizeOf(context).height * .9,
      child: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 4, 8, 8),
            child: Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        '${widget.member.displayName} assignments',
                        style: theme.textTheme.titleLarge,
                      ),
                      Text(
                        'Property scope is broader access. Assigned-work scope shows only specifically assigned repairs.',
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: colors.onSurfaceVariant,
                        ),
                      ),
                    ],
                  ),
                ),
                IconButton(
                  tooltip: 'Close',
                  onPressed: () => Navigator.pop(context),
                  icon: const Icon(Icons.close),
                ),
              ],
            ),
          ),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16),
            child: Card.filled(
              child: Padding(
                padding: const EdgeInsets.all(12),
                child: Text(
                  'One person can hold several separately scoped assignments. Owners and tenants use their own experiences, not Team jobs.',
                  style: theme.textTheme.bodySmall,
                ),
              ),
            ),
          ),
          Expanded(
            child: _loading
                ? const Center(child: CircularProgressIndicator())
                : _error != null
                ? _ErrorBody(
                    message: _error!,
                    onRetry: () => _load(reset: true),
                  )
                : _items.isEmpty
                ? const Center(child: Text('No assignments yet.'))
                : ListView.separated(
                    padding: const EdgeInsets.all(16),
                    itemCount: _items.length + (_hasMore ? 1 : 0),
                    separatorBuilder: (_, _) => const SizedBox(height: 8),
                    itemBuilder: (context, index) {
                      if (index == _items.length) {
                        return OutlinedButton(
                          onPressed: _loadingMore
                              ? null
                              : () => _load(reset: false),
                          child: _loadingMore
                              ? const SizedBox.square(
                                  dimension: 18,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 2,
                                  ),
                                )
                              : Text(
                                  'Load more (${_items.length} of $_totalCount)',
                                ),
                        );
                      }
                      final assignment = _items[index];
                      final saving =
                          _savingAssignmentId == assignment.assignmentId;
                      final canReplacePropertyScope =
                          assignment.roleProfileKey == 'property-manager' ||
                          assignment.roleProfileKey == 'leasing-agent';
                      return Card(
                        child: Padding(
                          padding: const EdgeInsets.all(14),
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Row(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  Expanded(
                                    child: Column(
                                      crossAxisAlignment:
                                          CrossAxisAlignment.start,
                                      children: [
                                        Text(
                                          assignment.roleProfileName,
                                          style: theme.textTheme.titleSmall,
                                        ),
                                        Text(
                                          _assignmentScopeLabel(assignment),
                                          style: theme.textTheme.bodySmall
                                              ?.copyWith(
                                                color: colors.onSurfaceVariant,
                                              ),
                                        ),
                                        Text(
                                          _assignmentDates(assignment),
                                          style: theme.textTheme.labelSmall
                                              ?.copyWith(
                                                color: colors.onSurfaceVariant,
                                              ),
                                        ),
                                      ],
                                    ),
                                  ),
                                  _AssignmentStatusChip(
                                    status: assignment.status,
                                  ),
                                ],
                              ),
                              if (widget.canManageTeam &&
                                  assignment.isActive) ...[
                                const SizedBox(height: 8),
                                Wrap(
                                  spacing: 8,
                                  children: [
                                    if (canReplacePropertyScope)
                                      OutlinedButton(
                                        onPressed: saving
                                            ? null
                                            : () => _editAssignment(
                                                replacement: assignment,
                                              ),
                                        child: const Text(
                                          'Replace property scope',
                                        ),
                                      ),
                                    OutlinedButton(
                                      onPressed: widget.isCurrentUser || saving
                                          ? null
                                          : () => _endAssignment(assignment),
                                      child: saving
                                          ? const SizedBox.square(
                                              dimension: 16,
                                              child: CircularProgressIndicator(
                                                strokeWidth: 2,
                                              ),
                                            )
                                          : const Text('End assignment'),
                                    ),
                                  ],
                                ),
                              ],
                            ],
                          ),
                        ),
                      );
                    },
                  ),
          ),
          if (widget.canManageTeam && widget.member.isActive)
            SafeArea(
              top: false,
              child: Padding(
                padding: const EdgeInsets.fromLTRB(16, 8, 16, 16),
                child: SizedBox(
                  width: double.infinity,
                  child: FilledButton.icon(
                    onPressed: _savingAssignmentId == null
                        ? () => _editAssignment()
                        : null,
                    icon: const Icon(Icons.add),
                    label: const Text('Add assignment'),
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _AssignmentDraft {
  const _AssignmentDraft({
    required this.roleProfileKey,
    required this.scopeKind,
    required this.propertyIds,
  });

  final String roleProfileKey;
  final String scopeKind;
  final List<int> propertyIds;
}

class _AssignmentEditorSheet extends ConsumerStatefulWidget {
  const _AssignmentEditorSheet({this.replacement});

  final TeamAssignment? replacement;

  @override
  ConsumerState<_AssignmentEditorSheet> createState() =>
      _AssignmentEditorSheetState();
}

class _AssignmentEditorSheetState
    extends ConsumerState<_AssignmentEditorSheet> {
  int _step = 0;
  String? _roleKey;
  String _scopeKind = 'SelectedProperties';
  final Set<int> _propertyIds = {};

  bool get _replacing => widget.replacement != null;
  bool get _scopeValid =>
      _scopeKind != 'SelectedProperties' || _propertyIds.isNotEmpty;

  @override
  void initState() {
    super.initState();
    if (_replacing) {
      _roleKey = widget.replacement!.roleProfileKey;
      _scopeKind = 'SelectedProperties';
    }
    Future.microtask(() => ref.read(propertiesProvider.notifier).load());
  }

  List<String> _scopesFor(String roleKey) {
    if (roleKey == 'workspace-administrator') return const ['AllProperties'];
    if (roleKey == 'maintenance-technician') {
      return const ['AssignedWorkOrders'];
    }
    return const ['SelectedProperties', 'AllProperties'];
  }

  void _chooseRole(TeamRoleProfile role) {
    setState(() {
      _roleKey = role.key;
      _scopeKind = role.defaultScopeKind;
      _propertyIds.clear();
    });
  }

  void _submit() {
    if (_roleKey == null || !_scopeValid) return;
    Navigator.pop(
      context,
      _AssignmentDraft(
        roleProfileKey: _roleKey!,
        scopeKind: _scopeKind,
        propertyIds: _propertyIds.toList()..sort(),
      ),
    );
  }

  Widget _propertySelector(BuildContext context) {
    final properties = ref.watch(propertiesProvider);
    final colors = Theme.of(context).colorScheme;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          _replacing
              ? 'Choose the complete replacement property list. Existing selected properties will be removed.'
              : _scopeDescription(_scopeKind),
          style: Theme.of(
            context,
          ).textTheme.bodySmall?.copyWith(color: colors.onSurfaceVariant),
        ),
        if (_scopeKind == 'SelectedProperties') ...[
          const SizedBox(height: 8),
          properties.when(
            loading: () => const CircularProgressIndicator(),
            error: (error, _) => Text(
              error is ApiException ? error.message : error.toString(),
              style: TextStyle(color: colors.error),
            ),
            data: (items) => Column(
              children: [
                for (final property in items)
                  CheckboxListTile(
                    value: _propertyIds.contains(property.id),
                    onChanged: (checked) => setState(() {
                      if (checked ?? false) {
                        _propertyIds.add(property.id);
                      } else {
                        _propertyIds.remove(property.id);
                      }
                    }),
                    title: Text(property.name),
                    controlAffinity: ListTileControlAffinity.leading,
                    contentPadding: EdgeInsets.zero,
                  ),
              ],
            ),
          ),
        ],
      ],
    );
  }

  @override
  Widget build(BuildContext context) {
    final roles = ref.watch(teamRoleProfilesProvider);
    final colors = Theme.of(context).colorScheme;

    return SizedBox(
      height: MediaQuery.sizeOf(context).height * .86,
      child: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 4, 8, 4),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    _replacing
                        ? 'Replace property scope'
                        : 'Add another assignment',
                    style: Theme.of(context).textTheme.titleLarge,
                  ),
                ),
                IconButton(
                  tooltip: 'Close',
                  onPressed: () => Navigator.pop(context),
                  icon: const Icon(Icons.close),
                ),
              ],
            ),
          ),
          Expanded(
            child: _replacing
                ? ListView(
                    padding: const EdgeInsets.all(20),
                    children: [
                      Text(
                        widget.replacement!.roleProfileName,
                        style: Theme.of(context).textTheme.titleMedium,
                      ),
                      const SizedBox(height: 8),
                      _propertySelector(context),
                      const SizedBox(height: 16),
                      FilledButton(
                        onPressed: _scopeValid ? _submit : null,
                        child: const Text('Replace properties'),
                      ),
                    ],
                  )
                : Stepper(
                    currentStep: _step,
                    onStepTapped: (step) => setState(() => _step = step),
                    controlsBuilder: (context, details) {
                      final canContinue = _step == 0
                          ? _roleKey != null
                          : _scopeValid;
                      return Padding(
                        padding: const EdgeInsets.only(top: 16),
                        child: Row(
                          children: [
                            FilledButton(
                              onPressed: !canContinue
                                  ? null
                                  : _step == 1
                                  ? _submit
                                  : () => setState(() => _step++),
                              child: Text(
                                _step == 1 ? 'Add assignment' : 'Continue',
                              ),
                            ),
                            if (_step > 0) ...[
                              const SizedBox(width: 8),
                              TextButton(
                                onPressed: () => setState(() => _step--),
                                child: const Text('Back'),
                              ),
                            ],
                          ],
                        ),
                      );
                    },
                    steps: [
                      Step(
                        title: const Text('Job'),
                        subtitle: const Text('What will they do?'),
                        isActive: _step >= 0,
                        state: _step > 0
                            ? StepState.complete
                            : StepState.indexed,
                        content: roles.when(
                          loading: () => const CircularProgressIndicator(),
                          error: (error, _) => Text(
                            error is ApiException
                                ? error.message
                                : error.toString(),
                            style: TextStyle(color: colors.error),
                          ),
                          data: (items) => RadioGroup<String>(
                            groupValue: _roleKey,
                            onChanged: (key) {
                              if (key == null) return;
                              _chooseRole(
                                items.firstWhere((role) => role.key == key),
                              );
                            },
                            child: Column(
                              children: [
                                for (final role in items)
                                  RadioListTile<String>(
                                    value: role.key,
                                    title: Text(role.displayName),
                                    subtitle: Text(role.description),
                                    contentPadding: EdgeInsets.zero,
                                  ),
                              ],
                            ),
                          ),
                        ),
                      ),
                      Step(
                        title: const Text('Access'),
                        subtitle: const Text('Where can they work?'),
                        isActive: _step >= 1,
                        content: Column(
                          children: [
                            RadioGroup<String>(
                              groupValue: _scopeKind,
                              onChanged: (value) {
                                if (value == null) return;
                                setState(() {
                                  _scopeKind = value;
                                  _propertyIds.clear();
                                });
                              },
                              child: Column(
                                children: [
                                  for (final scope in _scopesFor(
                                    _roleKey ?? '',
                                  ))
                                    RadioListTile<String>(
                                      value: scope,
                                      title: Text(_scopeLabel(scope)),
                                      subtitle: Text(_scopeDescription(scope)),
                                      contentPadding: EdgeInsets.zero,
                                    ),
                                ],
                              ),
                            ),
                            _propertySelector(context),
                          ],
                        ),
                      ),
                    ],
                  ),
          ),
        ],
      ),
    );
  }
}

class _AssignmentStatusChip extends StatelessWidget {
  const _AssignmentStatusChip({required this.status});

  final String status;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    final active = status.toLowerCase() == 'active';
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 4),
      decoration: BoxDecoration(
        color: active ? colors.primaryContainer : colors.surfaceContainerHigh,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Text(
        status,
        style: Theme.of(context).textTheme.labelSmall?.copyWith(
          color: active ? colors.onPrimaryContainer : colors.onSurfaceVariant,
        ),
      ),
    );
  }
}

String _assignmentScopeLabel(
  TeamAssignment assignment,
) => switch (assignment.scopeKind) {
  'AllProperties' => 'All properties',
  'AssignedWorkOrders' => 'Only assigned repairs',
  _ =>
    '${assignment.selectedPropertyCount} selected ${assignment.selectedPropertyCount == 1 ? 'property' : 'properties'}',
};

String _assignmentDates(TeamAssignment assignment) {
  final started = _shortDate(assignment.effectiveFromUtc);
  final ended = assignment.effectiveToUtc == null
      ? ''
      : ' · ended ${_shortDate(assignment.effectiveToUtc!)}';
  return 'Started $started$ended';
}

String _shortDate(DateTime value) =>
    '${value.toLocal().month}/${value.toLocal().day}/${value.toLocal().year}';

class _CreateTeamMemberSheet extends ConsumerStatefulWidget {
  const _CreateTeamMemberSheet();

  @override
  ConsumerState<_CreateTeamMemberSheet> createState() =>
      _CreateTeamMemberSheetState();
}

class _CreateTeamMemberSheetState
    extends ConsumerState<_CreateTeamMemberSheet> {
  final _emailController = TextEditingController();
  final _nameController = TextEditingController();
  int _step = 0;
  String? _roleKey;
  String _scopeKind = 'SelectedProperties';
  final Set<int> _propertyIds = {};
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(propertiesProvider.notifier).load());
  }

  @override
  void dispose() {
    _emailController.dispose();
    _nameController.dispose();
    super.dispose();
  }

  bool get _personValid =>
      _nameController.text.trim().isNotEmpty &&
      _emailController.text.contains('@');
  bool get _scopeValid =>
      _scopeKind != 'SelectedProperties' || _propertyIds.isNotEmpty;

  List<String> _scopesFor(String roleKey) {
    if (roleKey == 'workspace-administrator') return const ['AllProperties'];
    if (roleKey == 'maintenance-technician') {
      return const ['AssignedWorkOrders'];
    }
    return const ['SelectedProperties', 'AllProperties'];
  }

  void _chooseRole(TeamRoleProfile role) {
    setState(() {
      _roleKey = role.key;
      _scopeKind = role.defaultScopeKind;
      _propertyIds.clear();
    });
  }

  Future<void> _submit() async {
    if (_roleKey == null || !_scopeValid) return;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final result = await ref
          .read(teamProvider.notifier)
          .createMembership(
            email: _emailController.text,
            displayName: _nameController.text,
            roleProfileKey: _roleKey!,
            scopeKind: _scopeKind,
            selectedPropertyIds: _propertyIds.toList(),
          );
      if (mounted) {
        Navigator.of(context).pop();
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              result.requiresAccountActivation
                  ? 'Team member added. Activation is required before sign-in; the secure email is queued.'
                  : 'Team access added to the existing account.',
            ),
          ),
        );
      }
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final roles = ref.watch(teamRoleProfilesProvider);
    final properties = ref.watch(propertiesProvider);
    final colors = Theme.of(context).colorScheme;
    final bottomInset = MediaQuery.viewInsetsOf(context).bottom;

    return Padding(
      padding: EdgeInsets.only(bottom: bottomInset),
      child: SizedBox(
        height: MediaQuery.sizeOf(context).height * .88,
        child: Column(
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 12, 8, 4),
              child: Row(
                children: [
                  Expanded(
                    child: Text(
                      'Add a team member',
                      style: Theme.of(context).textTheme.titleLarge,
                    ),
                  ),
                  IconButton(
                    tooltip: 'Close',
                    onPressed: _saving ? null : () => Navigator.pop(context),
                    icon: const Icon(Icons.close),
                  ),
                ],
              ),
            ),
            Expanded(
              child: Stepper(
                currentStep: _step,
                onStepTapped: _saving
                    ? null
                    : (step) => setState(() => _step = step),
                controlsBuilder: (context, details) {
                  final canContinue = switch (_step) {
                    0 => _personValid,
                    1 => _roleKey != null,
                    _ => _scopeValid,
                  };
                  return Padding(
                    padding: const EdgeInsets.only(top: 16),
                    child: Row(
                      children: [
                        FilledButton(
                          onPressed: !canContinue || _saving
                              ? null
                              : _step == 2
                              ? _submit
                              : () => setState(() => _step++),
                          child: _saving
                              ? const SizedBox.square(
                                  dimension: 16,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 2,
                                  ),
                                )
                              : Text(_step == 2 ? 'Add member' : 'Continue'),
                        ),
                        if (_step > 0) ...[
                          const SizedBox(width: 8),
                          TextButton(
                            onPressed: _saving
                                ? null
                                : () => setState(() => _step--),
                            child: const Text('Back'),
                          ),
                        ],
                      ],
                    ),
                  );
                },
                steps: [
                  Step(
                    title: const Text('Person'),
                    subtitle: const Text('Who are you adding?'),
                    isActive: _step >= 0,
                    state: _step > 0 ? StepState.complete : StepState.indexed,
                    content: Column(
                      children: [
                        TextField(
                          controller: _nameController,
                          textInputAction: TextInputAction.next,
                          onChanged: (_) => setState(() {}),
                          decoration: const InputDecoration(labelText: 'Name'),
                        ),
                        const SizedBox(height: 12),
                        TextField(
                          controller: _emailController,
                          keyboardType: TextInputType.emailAddress,
                          textInputAction: TextInputAction.done,
                          onChanged: (_) => setState(() {}),
                          decoration: const InputDecoration(labelText: 'Email'),
                        ),
                      ],
                    ),
                  ),
                  Step(
                    title: const Text('Job'),
                    subtitle: const Text('What will they do?'),
                    isActive: _step >= 1,
                    state: _step > 1 ? StepState.complete : StepState.indexed,
                    content: roles.when(
                      loading: () => const CircularProgressIndicator(),
                      error: (error, _) => Text(
                        error is ApiException
                            ? error.message
                            : error.toString(),
                        style: TextStyle(color: colors.error),
                      ),
                      data: (items) => RadioGroup<String>(
                        groupValue: _roleKey,
                        onChanged: (key) {
                          if (key == null) return;
                          _chooseRole(
                            items.firstWhere((role) => role.key == key),
                          );
                        },
                        child: Column(
                          children: [
                            for (final role in items)
                              RadioListTile<String>(
                                value: role.key,
                                title: Text(role.displayName),
                                subtitle: Text(role.description),
                                contentPadding: EdgeInsets.zero,
                              ),
                          ],
                        ),
                      ),
                    ),
                  ),
                  Step(
                    title: const Text('Access'),
                    subtitle: const Text('Where can they work?'),
                    isActive: _step >= 2,
                    content: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        RadioGroup<String>(
                          groupValue: _scopeKind,
                          onChanged: (value) {
                            if (value == null) return;
                            setState(() {
                              _scopeKind = value;
                              _propertyIds.clear();
                            });
                          },
                          child: Column(
                            children: [
                              for (final scope in _scopesFor(_roleKey ?? ''))
                                RadioListTile<String>(
                                  value: scope,
                                  contentPadding: EdgeInsets.zero,
                                  title: Text(_scopeLabel(scope)),
                                  subtitle: Text(_scopeDescription(scope)),
                                ),
                            ],
                          ),
                        ),
                        if (_scopeKind == 'SelectedProperties') ...[
                          const Divider(),
                          Text(
                            'Properties',
                            style: Theme.of(context).textTheme.titleSmall,
                          ),
                          const SizedBox(height: 6),
                          properties.when(
                            loading: () => const CircularProgressIndicator(),
                            error: (error, _) => Text(
                              error is ApiException
                                  ? error.message
                                  : error.toString(),
                              style: TextStyle(color: colors.error),
                            ),
                            data: (items) => Column(
                              children: [
                                for (final property in items)
                                  CheckboxListTile(
                                    value: _propertyIds.contains(property.id),
                                    onChanged: (checked) => setState(() {
                                      if (checked ?? false) {
                                        _propertyIds.add(property.id);
                                      } else {
                                        _propertyIds.remove(property.id);
                                      }
                                    }),
                                    title: Text(property.name),
                                    controlAffinity:
                                        ListTileControlAffinity.leading,
                                    contentPadding: EdgeInsets.zero,
                                  ),
                              ],
                            ),
                          ),
                        ],
                        if (_error != null) ...[
                          const SizedBox(height: 8),
                          Text(_error!, style: TextStyle(color: colors.error)),
                        ],
                      ],
                    ),
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

String _scopeLabel(String scope) => switch (scope) {
  'AllProperties' => 'All properties',
  'AssignedWorkOrders' => 'Only assigned repairs',
  _ => 'Selected properties',
};

String _scopeDescription(String scope) => switch (scope) {
  'AllProperties' => 'This job applies across the entire workspace.',
  'AssignedWorkOrders' => 'They only see work specifically assigned to them.',
  _ => 'Choose the rentals this person is responsible for.',
};

class _EmptyBody extends StatelessWidget {
  const _EmptyBody();

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              Icons.group_outlined,
              size: 48,
              color: colors.onSurfaceVariant,
            ),
            const SizedBox(height: 12),
            Text(
              'No matching team members',
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 4),
            Text(
              'Add the people who help run rentals, leasing, or maintenance.',
              textAlign: TextAlign.center,
              style: TextStyle(color: colors.onSurfaceVariant),
            ),
          ],
        ),
      ),
    );
  }
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.error_outline, size: 40, color: colors.error),
            const SizedBox(height: 12),
            Text(message, textAlign: TextAlign.center),
            const SizedBox(height: 16),
            FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}
