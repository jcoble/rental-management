import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'team_repository.dart';

// ── Screen ────────────────────────────────────────────────────────────────────

/// Team management screen — list of members with role + active controls.
///
/// FAB opens the invite dialog which reveals a generated password if the API
/// returns one.
class TeamScreen extends ConsumerStatefulWidget {
  const TeamScreen({super.key});

  @override
  ConsumerState<TeamScreen> createState() => _TeamScreenState();
}

class _TeamScreenState extends ConsumerState<TeamScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(teamProvider.notifier).load());
  }

  Future<void> _refresh() => ref.read(teamProvider.notifier).refresh();

  void _showInviteDialog() {
    showDialog<void>(context: context, builder: (_) => const _InviteDialog());
  }

  @override
  Widget build(BuildContext context) {
    final asyncState = ref.watch(teamProvider);

    return Scaffold(
      appBar: AppBar(title: const Text('Team')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'team-fab',
        primaryAction: MobileQuickAction(
          label: 'Invite member',
          icon: Icons.person_add_outlined,
          onPressed: _showInviteDialog,
        ),
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: asyncState.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(
            message: e is ApiException ? e.message : e.toString(),
            onRetry: () => ref.read(teamProvider.notifier).refresh(),
          ),
          data: (list) {
            if (list.isEmpty) {
              return CustomScrollView(
                physics: const AlwaysScrollableScrollPhysics(),
                slivers: [const SliverFillRemaining(child: _EmptyBody())],
              );
            }
            return ListView.separated(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 88),
              itemCount: list.length,
              separatorBuilder: (context, index) => const SizedBox(height: 8),
              itemBuilder: (_, i) => _MemberCard(member: list[i]),
            );
          },
        ),
      ),
    );
  }
}

// ── Member Card ───────────────────────────────────────────────────────────────

const _roles = ['Admin', 'Manager', 'Owner', 'Viewer'];

class _MemberCard extends ConsumerWidget {
  const _MemberCard({required this.member});

  final TeamMember member;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final notifier = ref.read(teamProvider.notifier);

    return Card(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(14, 12, 14, 12),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.center,
          children: [
            // Avatar
            CircleAvatar(
              backgroundColor: member.isActive
                  ? cs.primaryContainer
                  : cs.surfaceContainerHighest,
              child: Text(
                member.label.isNotEmpty ? member.label[0].toUpperCase() : '?',
                style: TextStyle(
                  color: member.isActive
                      ? cs.onPrimaryContainer
                      : cs.onSurfaceVariant,
                  fontWeight: FontWeight.w700,
                ),
              ),
            ),
            const SizedBox(width: 12),

            // Name + email
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    member.label,
                    style: theme.textTheme.titleSmall?.copyWith(
                      fontWeight: FontWeight.w600,
                      color: member.isActive ? null : cs.onSurfaceVariant,
                    ),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                  if (member.displayName != null)
                    Text(
                      member.email,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: cs.onSurfaceVariant,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                  if (!member.isActive)
                    Text(
                      'Inactive',
                      style: theme.textTheme.labelSmall?.copyWith(
                        color: cs.error,
                      ),
                    ),
                ],
              ),
            ),
            const SizedBox(width: 8),

            // Role dropdown
            DropdownButton<String>(
              value: _roles.contains(member.role) ? member.role : null,
              hint: Text(member.role),
              underline: const SizedBox.shrink(),
              style: theme.textTheme.bodySmall?.copyWith(
                fontWeight: FontWeight.w600,
                color: cs.primary,
              ),
              items: _roles
                  .map((r) => DropdownMenuItem(value: r, child: Text(r)))
                  .toList(),
              onChanged: (v) {
                if (v != null && v != member.role) {
                  notifier.updateRole(member.id, v);
                }
              },
            ),
            const SizedBox(width: 4),

            // Active toggle
            Switch(
              value: member.isActive,
              materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
              onChanged: (v) => notifier.setActive(member.id, isActive: v),
            ),
          ],
        ),
      ),
    );
  }
}

// ── Invite Dialog ─────────────────────────────────────────────────────────────

class _InviteDialog extends ConsumerStatefulWidget {
  const _InviteDialog();

  @override
  ConsumerState<_InviteDialog> createState() => _InviteDialogState();
}

class _InviteDialogState extends ConsumerState<_InviteDialog> {
  final _formKey = GlobalKey<FormState>();
  final _emailCtrl = TextEditingController();
  final _nameCtrl = TextEditingController();
  final _pwCtrl = TextEditingController();
  String _role = 'Viewer';
  bool _saving = false;
  String? _error;
  String? _generatedPassword;

  @override
  void dispose() {
    _emailCtrl.dispose();
    _nameCtrl.dispose();
    _pwCtrl.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final result = await ref
          .read(teamProvider.notifier)
          .invite(
            email: _emailCtrl.text.trim(),
            displayName: _nameCtrl.text.trim().isEmpty
                ? null
                : _nameCtrl.text.trim(),
            role: _role,
            temporaryPassword: _pwCtrl.text.trim().isEmpty
                ? null
                : _pwCtrl.text.trim(),
          );
      if (result.generatedPassword != null) {
        setState(() => _generatedPassword = result.generatedPassword);
      } else {
        if (mounted) Navigator.of(context).pop();
      }
    } on ApiException catch (e) {
      setState(() => _error = e.message);
    } catch (e) {
      setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final theme = Theme.of(context);

    // Show generated password reveal view
    if (_generatedPassword != null) {
      return AlertDialog(
        title: const Text('Member Invited'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text(
              'The account was created. Share this one-time password with the new member — it will not be shown again.',
            ),
            const SizedBox(height: 16),
            Container(
              width: double.infinity,
              padding: const EdgeInsets.all(14),
              decoration: BoxDecoration(
                color: cs.primaryContainer,
                borderRadius: BorderRadius.circular(8),
              ),
              child: SelectableText(
                _generatedPassword!,
                style: theme.textTheme.titleMedium?.copyWith(
                  fontWeight: FontWeight.w700,
                  color: cs.onPrimaryContainer,
                  fontFamily: 'monospace',
                ),
              ),
            ),
          ],
        ),
        actions: [
          FilledButton(
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('Done'),
          ),
        ],
      );
    }

    return AlertDialog(
      title: const Text('Invite Team Member'),
      content: Form(
        key: _formKey,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            TextFormField(
              controller: _emailCtrl,
              keyboardType: TextInputType.emailAddress,
              textInputAction: TextInputAction.next,
              decoration: const InputDecoration(labelText: 'Email'),
              validator: (v) {
                if (v == null || v.trim().isEmpty) return 'Required';
                if (!v.contains('@')) return 'Enter a valid email';
                return null;
              },
            ),
            const SizedBox(height: 10),
            TextFormField(
              controller: _nameCtrl,
              textInputAction: TextInputAction.next,
              decoration: const InputDecoration(
                labelText: 'Display name (optional)',
              ),
            ),
            const SizedBox(height: 10),
            DropdownButtonFormField<String>(
              initialValue: _role,
              decoration: const InputDecoration(labelText: 'Role'),
              items: _roles
                  .map((r) => DropdownMenuItem(value: r, child: Text(r)))
                  .toList(),
              onChanged: (v) {
                if (v != null) setState(() => _role = v);
              },
            ),
            const SizedBox(height: 10),
            TextFormField(
              controller: _pwCtrl,
              textInputAction: TextInputAction.done,
              decoration: const InputDecoration(
                labelText: 'Temporary password (optional)',
                helperText: 'Leave blank to auto-generate',
              ),
            ),
            if (_error != null) ...[
              const SizedBox(height: 8),
              Text(_error!, style: TextStyle(color: cs.error, fontSize: 13)),
            ],
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: _saving ? null : () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: _saving ? null : _submit,
          child: _saving
              ? const SizedBox(
                  width: 16,
                  height: 16,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : const Text('Send Invite'),
        ),
      ],
    );
  }
}

// ── Empty / Error ─────────────────────────────────────────────────────────────

class _EmptyBody extends StatelessWidget {
  const _EmptyBody();

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.group_outlined, size: 48, color: cs.onSurfaceVariant),
          const SizedBox(height: 12),
          Text(
            'No team members yet',
            style: Theme.of(
              context,
            ).textTheme.titleMedium?.copyWith(color: cs.onSurfaceVariant),
          ),
          const SizedBox(height: 4),
          Text(
            'Tap + to invite someone.',
            style: TextStyle(color: cs.onSurfaceVariant),
          ),
        ],
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
    final cs = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
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
            FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}
