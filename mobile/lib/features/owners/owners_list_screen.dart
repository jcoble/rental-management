import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'owner_detail_screen.dart';
import 'owner_form_sheet.dart';
import 'owners_models.dart';
import 'owners_repository.dart';

class OwnersListScreen extends ConsumerStatefulWidget {
  const OwnersListScreen({super.key});

  @override
  ConsumerState<OwnersListScreen> createState() => _OwnersListScreenState();
}

class _OwnersListScreenState extends ConsumerState<OwnersListScreen> {
  static const _pageSize = 20;

  final _searchCtrl = TextEditingController();
  String? _search;
  int _skip = 0;

  OwnerListQuery get _query => OwnerListQuery(
    skip: _skip,
    take: _pageSize,
    search: _search,
    sort: 'name',
  );

  @override
  void dispose() {
    _searchCtrl.dispose();
    super.dispose();
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

  void _showOwnerForm({OwnerEntity? owner}) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => OwnerFormSheet(
        existing: owner,
        onSaved: () => ref.invalidate(ownersPageProvider),
      ),
    );
  }

  Future<void> _confirmDelete(OwnerEntity owner) async {
    final messenger = ScaffoldMessenger.of(context);
    final hasAssignments = owner.assignedPropertyCount > 0;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (_) => AlertDialog(
        title: const Text('Delete owner?'),
        content: Text(
          hasAssignments
              ? '${owner.name} is assigned to ${owner.assignedPropertyCount} '
                    'properties. Delete this owner and clear those property '
                    'assignments?'
              : 'Delete ${owner.name}? This cannot be undone.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: Text(hasAssignments ? 'Clear and delete' : 'Delete'),
          ),
        ],
      ),
    );

    if (confirmed != true || !mounted) return;

    try {
      await ref
          .read(ownersRepositoryProvider)
          .deleteOwner(owner.id, clearPropertyAssignments: hasAssignments);
      ref.invalidate(ownersPageProvider);
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Owner deleted.')));
    } on ApiException catch (e) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  void _openDetail(OwnerEntity owner) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => OwnerDetailScreen(
          owner: owner,
          onChanged: () => ref.invalidate(ownersPageProvider),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final ownersAsync = ref.watch(ownersPageProvider(_query));

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Owners')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'owners-fab',
        primaryAction: MobileQuickAction(
          label: 'Add owner',
          icon: Icons.add,
          onPressed: () => _showOwnerForm(),
        ),
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
            child: TextField(
              controller: _searchCtrl,
              textInputAction: TextInputAction.search,
              onSubmitted: _submitSearch,
              decoration: InputDecoration(
                labelText: 'Search owners',
                prefixIcon: const Icon(Icons.search),
                suffixIcon: (_search ?? '').isEmpty
                    ? IconButton(
                        tooltip: 'Search owners',
                        icon: const Icon(Icons.arrow_forward),
                        onPressed: _submitSearch,
                      )
                    : IconButton(
                        tooltip: 'Clear owner search',
                        icon: const Icon(Icons.close),
                        onPressed: _clearSearch,
                      ),
              ),
            ),
          ),
          Expanded(
            child: RefreshIndicator(
              onRefresh: () async => ref.invalidate(ownersPageProvider),
              child: ownersAsync.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (error, _) => _ErrorBody(
                  message: error is ApiException
                      ? error.message
                      : error.toString(),
                  onRetry: () => ref.invalidate(ownersPageProvider),
                ),
                data: (page) {
                  if (page.items.isEmpty) {
                    return _EmptyBody(
                      hasSearch: (_search ?? '').isNotEmpty,
                      onAdd: () => _showOwnerForm(),
                    );
                  }

                  return ListView.separated(
                    physics: const AlwaysScrollableScrollPhysics(),
                    padding: const EdgeInsets.fromLTRB(16, 12, 16, 96),
                    itemCount: page.items.length + 1,
                    separatorBuilder: (_, index) =>
                        index == page.items.length - 1
                        ? const SizedBox(height: 14)
                        : const SizedBox(height: 8),
                    itemBuilder: (_, index) {
                      if (index == page.items.length) {
                        return _PagingBar(
                          page: page,
                          onPrevious: page.hasPrevious
                              ? () => setState(() {
                                  _skip = (_skip - _pageSize).clamp(0, _skip);
                                })
                              : null,
                          onNext: page.hasNext
                              ? () => setState(() => _skip += _pageSize)
                              : null,
                        );
                      }

                      final owner = page.items[index];
                      return _OwnerCard(
                        owner: owner,
                        onTap: () => _openDetail(owner),
                        onEdit: () => _showOwnerForm(owner: owner),
                        onDelete: () => _confirmDelete(owner),
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

class _OwnerCard extends StatelessWidget {
  const _OwnerCard({
    required this.owner,
    required this.onTap,
    required this.onEdit,
    required this.onDelete,
  });

  final OwnerEntity owner;
  final VoidCallback onTap;
  final VoidCallback onEdit;
  final VoidCallback onDelete;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final assignmentLabel = owner.assignedPropertyCount == 1
        ? '1 property'
        : '${owner.assignedPropertyCount} properties';

    return Card(
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(8),
        child: Padding(
          padding: const EdgeInsets.all(14),
          child: Row(
            children: [
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: cs.primaryContainer,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Icon(
                  Icons.account_balance_outlined,
                  color: cs.onPrimaryContainer,
                  size: 22,
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
                            owner.name,
                            style: theme.textTheme.titleSmall?.copyWith(
                              fontWeight: FontWeight.w700,
                            ),
                          ),
                        ),
                        if (owner.isPrimary) ...[
                          const SizedBox(width: 6),
                          Icon(
                            Icons.verified_outlined,
                            size: 16,
                            color: cs.primary,
                          ),
                        ],
                      ],
                    ),
                    const SizedBox(height: 2),
                    Text(
                      owner.typeLabel,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: cs.onSurfaceVariant,
                      ),
                    ),
                    const SizedBox(height: 6),
                    Wrap(
                      spacing: 10,
                      runSpacing: 4,
                      children: [
                        _MetaChip(
                          icon: Icons.apartment_outlined,
                          label: assignmentLabel,
                        ),
                        if (owner.hasPhone)
                          _MetaChip(
                            icon: Icons.phone_outlined,
                            label: owner.phone!,
                          ),
                        if (owner.hasEmail)
                          _MetaChip(
                            icon: Icons.email_outlined,
                            label: owner.email!,
                          ),
                      ],
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 8),
              Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  IconButton(
                    icon: const Icon(Icons.edit_outlined),
                    tooltip: 'Edit owner',
                    onPressed: onEdit,
                  ),
                  IconButton(
                    icon: const Icon(Icons.delete_outline),
                    tooltip: 'Delete owner',
                    onPressed: onDelete,
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

class _MetaChip extends StatelessWidget {
  const _MetaChip({required this.icon, required this.label});

  final IconData icon;
  final String label;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(icon, size: 14, color: cs.onSurfaceVariant),
        const SizedBox(width: 4),
        Flexible(
          child: Text(
            label,
            overflow: TextOverflow.ellipsis,
            style: theme.textTheme.bodySmall?.copyWith(
              color: cs.onSurfaceVariant,
            ),
          ),
        ),
      ],
    );
  }
}

class _PagingBar extends StatelessWidget {
  const _PagingBar({
    required this.page,
    required this.onPrevious,
    required this.onNext,
  });

  final OwnerEntityPage page;
  final VoidCallback? onPrevious;
  final VoidCallback? onNext;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final start = page.totalCount == 0 ? 0 : page.skip + 1;
    final end = page.skip + page.items.length;

    return Row(
      children: [
        Expanded(
          child: Text(
            '$start-$end of ${page.totalCount}',
            style: theme.textTheme.bodySmall,
          ),
        ),
        IconButton(
          tooltip: 'Previous owners page',
          icon: const Icon(Icons.chevron_left),
          onPressed: onPrevious,
        ),
        IconButton(
          tooltip: 'Next owners page',
          icon: const Icon(Icons.chevron_right),
          onPressed: onNext,
        ),
      ],
    );
  }
}

class _EmptyBody extends StatelessWidget {
  const _EmptyBody({required this.hasSearch, required this.onAdd});

  final bool hasSearch;
  final VoidCallback onAdd;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(24, 80, 24, 24),
          child: Column(
            children: [
              Icon(
                Icons.account_balance_outlined,
                size: 40,
                color: cs.onSurfaceVariant,
              ),
              const SizedBox(height: 12),
              Text(
                hasSearch ? 'No matching owners.' : 'No owners yet.',
                textAlign: TextAlign.center,
                style: TextStyle(color: cs.onSurfaceVariant),
              ),
              const SizedBox(height: 6),
              Text(
                hasSearch
                    ? 'Try another name, email, phone or tax ID.'
                    : 'Add owners here, then assign them to properties.',
                textAlign: TextAlign.center,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              ),
              if (!hasSearch) ...[
                const SizedBox(height: 18),
                FilledButton.icon(
                  onPressed: onAdd,
                  icon: const Icon(Icons.add),
                  label: const Text('Add your first owner'),
                ),
              ],
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
  final VoidCallback onRetry;

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
