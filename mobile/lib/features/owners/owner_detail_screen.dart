import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_exception.dart';
import '../activity/activity_history_screen.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_domain_navigation.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'owner_form_sheet.dart';
import 'owners_models.dart';
import 'owners_repository.dart';

class OwnerDetailScreen extends ConsumerStatefulWidget {
  const OwnerDetailScreen({super.key, required this.owner, this.onChanged});

  final OwnerEntity owner;
  final VoidCallback? onChanged;

  @override
  ConsumerState<OwnerDetailScreen> createState() => _OwnerDetailScreenState();
}

class _OwnerDetailScreenState extends ConsumerState<OwnerDetailScreen> {
  late OwnerEntity _owner;
  bool _refreshing = false;

  @override
  void initState() {
    super.initState();
    _owner = widget.owner;
  }

  Future<void> _refreshOwner() async {
    setState(() => _refreshing = true);
    try {
      final next = await ref.read(ownersRepositoryProvider).getOwner(_owner.id);
      if (mounted) setState(() => _owner = next);
    } on ApiException catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  void _showEditForm() {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      useRootNavigator: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => OwnerFormSheet(
        existing: _owner,
        onSaved: () {
          widget.onChanged?.call();
          _refreshOwner();
        },
      ),
    );
  }

  void _showActivityHistory() {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => ActivityHistoryScreen(
          entityType: 'OwnerEntity',
          entityId: _owner.id,
          title: 'Owner activity',
          subtitle: _owner.name,
        ),
      ),
    );
  }

  Future<void> _confirmDelete() async {
    final messenger = ScaffoldMessenger.of(context);
    final hasAssignments = _owner.assignedPropertyCount > 0;
    if (hasAssignments) {
      await showDialog<void>(
        context: context,
        builder: (_) => AlertDialog(
          title: const Text('Owner cannot be deleted'),
          content: Text(
            '${_owner.name} is assigned to ${_owner.assignedPropertyCount} '
            'properties. Reassign or clear those properties first.',
          ),
          actions: [
            FilledButton(
              onPressed: () => Navigator.of(context).pop(),
              child: const Text('Close'),
            ),
          ],
        ),
      );
      return;
    }
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (_) => AlertDialog(
        title: const Text('Delete owner?'),
        content: Text(
          'Delete ${_owner.name}? This cannot be undone.',
        ),
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

    try {
      await ref
          .read(ownersRepositoryProvider)
          .deleteOwner(_owner.id);
      widget.onChanged?.call();
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Owner deleted.')));
      Navigator.of(context).pop();
    } on ApiException catch (e) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

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

  void _callOwner() {
    _launch(
      Uri(scheme: 'tel', path: _owner.phone!.trim()),
      'Could not start a call.',
    );
  }

  void _textOwner() {
    _launch(
      Uri(scheme: 'sms', path: _owner.phone!.trim()),
      'Could not open messaging.',
    );
  }

  void _emailOwner() {
    _launch(
      Uri(scheme: 'mailto', path: _owner.email!.trim()),
      'Could not open email.',
    );
  }

  @override
  Widget build(BuildContext context) {
    final owner = _owner;
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final embedded = MobileDomainChromeScope.isEmbedded(context);

    Widget editButton() {
      return IconButton(
        icon: const Icon(Icons.edit_outlined),
        tooltip: 'Edit owner',
        onPressed: _showEditForm,
      );
    }

    Widget activityButton() {
      return IconButton(
        icon: const Icon(Icons.history_outlined),
        tooltip: 'View owner activity',
        onPressed: _showActivityHistory,
      );
    }

    Widget deleteButton() {
      return IconButton(
        icon: const Icon(Icons.delete_outline),
        tooltip: 'Delete owner',
        onPressed: _confirmDelete,
      );
    }

    return MobileDomainDetailHeader(
      title: owner.name,
      subtitle: '${owner.typeLabel} owner',
      child: Scaffold(
        appBar: mobileDomainRootAppBar(
          context,
          title: Text(owner.name),
          actions: [editButton(), activityButton(), deleteButton()],
        ),
        floatingActionButton: MobileQuickActionFab(
          heroTag: 'owner-detail-fab',
          primaryAction: MobileQuickAction(
            label: 'Edit owner',
            icon: Icons.edit_outlined,
            onPressed: _showEditForm,
          ),
          onChat: () => openMobileAssistant(context),
          onRecord: () => openMobileRecord(context),
          onScan: () => openMobileScan(context),
        ),
        body: RefreshIndicator(
          onRefresh: _refreshOwner,
          child: ListView(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 96),
            children: [
              if (embedded) ...[
                MobileDomainEmbeddedToolbar(
                  padding: EdgeInsets.zero,
                  children: [editButton(), activityButton(), deleteButton()],
                ),
                const SizedBox(height: 8),
              ],
              Row(
                children: [
                  Container(
                    padding: const EdgeInsets.all(12),
                    decoration: BoxDecoration(
                      color: cs.primaryContainer,
                      borderRadius: BorderRadius.circular(8),
                    ),
                    child: Icon(
                      Icons.account_balance_outlined,
                      color: cs.onPrimaryContainer,
                      size: 26,
                    ),
                  ),
                  const SizedBox(width: 14),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          owner.name,
                          style: theme.textTheme.titleLarge?.copyWith(
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                        Text(
                          owner.typeLabel,
                          style: theme.textTheme.bodyMedium?.copyWith(
                            color: cs.onSurfaceVariant,
                          ),
                        ),
                      ],
                    ),
                  ),
                  if (_refreshing)
                    const SizedBox.square(
                      dimension: 20,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    ),
                ],
              ),
              const SizedBox(height: 18),
              if (owner.hasPhone)
                _ContactRow(
                  icon: Icons.phone_outlined,
                  value: owner.phone!,
                  onTap: _callOwner,
                  trailing: IconButton(
                    icon: Icon(Icons.sms_outlined, color: cs.primary),
                    tooltip: 'Text owner',
                    onPressed: _textOwner,
                  ),
                ),
              if (owner.hasEmail)
                _ContactRow(
                  icon: Icons.email_outlined,
                  value: owner.email!,
                  onTap: _emailOwner,
                ),
              if (!owner.hasPhone && !owner.hasEmail)
                Text(
                  'No contact details on file.',
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: cs.onSurfaceVariant,
                  ),
                ),
              const SizedBox(height: 20),
              _InfoCard(
                title: 'Your rentals',
                rows: [
                  _InfoRow(
                    label: 'Assigned properties',
                    value: owner.assignedPropertyCount.toString(),
                  ),
                  _InfoRow(
                    label: 'Primary owner',
                    value: owner.isPrimary ? 'Yes' : 'No',
                  ),
                  if ((owner.taxId ?? '').trim().isNotEmpty)
                    _InfoRow(label: 'Tax ID', value: owner.taxId!),
                ],
              ),
              if (owner.displayAddress != null) ...[
                const SizedBox(height: 12),
                _InfoCard(
                  title: 'Address',
                  rows: [
                    _InfoRow(label: 'Mailing', value: owner.displayAddress!),
                  ],
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

class _ContactRow extends StatelessWidget {
  const _ContactRow({
    required this.icon,
    required this.value,
    required this.onTap,
    this.trailing,
  });

  final IconData icon;
  final String value;
  final VoidCallback onTap;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return Card(
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
      child: ListTile(
        titleAlignment: ListTileTitleAlignment.center,
        leading: Icon(icon, color: cs.primary),
        title: Text(value),
        trailing: trailing,
        onTap: onTap,
        titleTextStyle: theme.textTheme.bodyMedium?.copyWith(
          color: cs.onSurface,
          fontWeight: FontWeight.w600,
        ),
      ),
    );
  }
}

class _InfoCard extends StatelessWidget {
  const _InfoCard({required this.title, required this.rows});

  final String title;
  final List<_InfoRow> rows;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return Card(
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              title,
              style: theme.textTheme.labelLarge?.copyWith(
                fontWeight: FontWeight.w700,
                color: cs.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 10),
            for (final row in rows) ...[
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  SizedBox(
                    width: 132,
                    child: Text(
                      row.label,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: cs.onSurfaceVariant,
                      ),
                    ),
                  ),
                  Expanded(
                    child: Text(
                      row.value,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                  ),
                ],
              ),
              if (row != rows.last) const SizedBox(height: 8),
            ],
          ],
        ),
      ),
    );
  }
}

class _InfoRow {
  const _InfoRow({required this.label, required this.value});

  final String label;
  final String value;
}
