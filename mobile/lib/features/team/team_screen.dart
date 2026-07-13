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

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(teamProvider);
    final auth = ref.watch(authControllerProvider);
    final currentUserId = auth is AuthStateAuthenticated ? auth.user.id : null;

    return Scaffold(
      appBar: AppBar(title: const Text('Team')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'team-fab',
        primaryAction: MobileQuickAction(
          label: 'Add team member',
          icon: Icons.person_add_outlined,
          onPressed: _showCreateSheet,
        ),
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
  const _MemberCard({required this.member, required this.isCurrentUser});

  final TeamMember member;
  final bool isCurrentUser;

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
              child: TextButton(
                onPressed: widget.isCurrentUser || _saving
                    ? null
                    : _changeStatus,
                child: _saving
                    ? const SizedBox.square(
                        dimension: 16,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : Text(member.isActive ? 'Suspend access' : 'Reactivate'),
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
                  ? 'Team member added. Their secure activation email is queued.'
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
  'AssignedWorkOrders' => 'Only assigned work orders',
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
