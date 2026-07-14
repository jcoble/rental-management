import 'dart:async';

import 'package:flutter/material.dart';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/theme/app_tokens.dart';
import '../../core/widgets/mobile_grid_controls.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../home/mobile_domain_chrome.dart';
import 'activity_repository.dart';

class ActivityHistoryScreen extends ConsumerStatefulWidget {
  const ActivityHistoryScreen({
    super.key,
    this.entityType,
    this.entityId,
    this.title,
    this.subtitle,
    this.embedded = false,
  });

  final String? entityType;
  final int? entityId;
  final String? title;
  final String? subtitle;
  final bool embedded;

  @override
  ConsumerState<ActivityHistoryScreen> createState() =>
      _ActivityHistoryScreenState();
}

class _ActivityHistoryScreenState extends ConsumerState<ActivityHistoryScreen> {
  final _scrollController = ScrollController();
  final _searchController = TextEditingController();
  ActivityEntry? _focusedEntry;

  ActivityHistoryScope? get _scope {
    final entityType = widget.entityType;
    final entityId = widget.entityId;
    if (entityType == null || entityType.isEmpty || entityId == null) {
      return null;
    }
    return ActivityHistoryScope(entityType: entityType, entityId: entityId);
  }

  @override
  void initState() {
    super.initState();
    final scope = _scope;
    _searchController.text = _readState(scope).filter.search ?? '';
    _scrollController.addListener(_onScroll);
    Future.microtask(() {
      if (!mounted) return;
      _readNotifier(scope).refresh();
    });
  }

  @override
  void dispose() {
    _scrollController.removeListener(_onScroll);
    _scrollController.dispose();
    _searchController.dispose();
    super.dispose();
  }

  void _onScroll() {
    if (_scrollController.position.pixels >=
        _scrollController.position.maxScrollExtent - 240) {
      _readNotifier(_scope).loadMore();
    }
  }

  ActivityHistoryState _readState(ActivityHistoryScope? scope) {
    if (scope == null) return ref.read(activityHistoryProvider);
    return ref.read(activityHistoryScopedProvider(scope));
  }

  ActivityHistoryState _watchState(ActivityHistoryScope? scope) {
    if (scope == null) return ref.watch(activityHistoryProvider);
    return ref.watch(activityHistoryScopedProvider(scope));
  }

  ActivityHistoryNotifier _readNotifier(ActivityHistoryScope? scope) {
    if (scope == null) return ref.read(activityHistoryProvider.notifier);
    return ref.read(activityHistoryScopedProvider(scope).notifier);
  }

  void _listenForSearch(ActivityHistoryScope? scope) {
    if (scope == null) {
      ref.listen<String?>(
        activityHistoryProvider.select((value) => value.filter.search),
        (_, next) => _syncSearchText(next ?? ''),
      );
      return;
    }

    ref.listen<String?>(
      activityHistoryScopedProvider(
        scope,
      ).select((value) => value.filter.search),
      (_, next) => _syncSearchText(next ?? ''),
    );
  }

  Future<void> _refresh() => _readNotifier(_scope).refresh();

  void _openActivityDetail(ActivityEntry entry) =>
      setState(() => _focusedEntry = entry);

  void _closeActivityDetail() => setState(() => _focusedEntry = null);

  @override
  Widget build(BuildContext context) {
    final scope = _scope;
    final state = _watchState(scope);
    _listenForSearch(scope);
    final title = widget.title ?? 'Activity history';

    final body = Column(
      children: [
        _ActivityFilters(
          searchController: _searchController,
          selectedSort: state.filter.sort,
          selectedOperation: state.filter.operation,
          scopeTitle: scope == null ? null : title,
          scopeSubtitle: widget.subtitle,
          onSearch: (value) => _readNotifier(scope).setSearch(value),
          onSortChanged: (value) {
            if (value == null) return;
            _readNotifier(scope).setSort(value);
          },
          onOperationChanged: (value) =>
              _readNotifier(scope).setOperation(value),
          onRefresh: _refresh,
        ),
        if (state.error != null && state.items.isNotEmpty)
          _InlineError(message: state.error!),
        Expanded(
          child: RefreshIndicator(
            onRefresh: _refresh,
            child: _ActivityBody(
              state: state,
              controller: _scrollController,
              focusedEntryId: _focusedEntry?.id,
              onOpenEntry: _openActivityDetail,
              onCloseEntry: _closeActivityDetail,
            ),
          ),
        ),
      ],
    );

    return PopScope<void>(
      canPop: _focusedEntry == null,
      onPopInvokedWithResult: (didPop, _) {
        if (didPop || _focusedEntry == null) return;
        _closeActivityDetail();
      },
      child: widget.embedded
          ? body
          : Scaffold(
              appBar: mobileDomainRootAppBar(context, title: Text(title)),
              body: body,
            ),
    );
  }

  void _syncSearchText(String value) {
    if (_searchController.text == value) return;
    _searchController.value = TextEditingValue(
      text: value,
      selection: TextSelection.collapsed(offset: value.length),
    );
  }
}

class _ActivityFilters extends StatelessWidget {
  const _ActivityFilters({
    required this.searchController,
    required this.selectedSort,
    required this.selectedOperation,
    this.scopeTitle,
    this.scopeSubtitle,
    required this.onSearch,
    required this.onSortChanged,
    required this.onOperationChanged,
    required this.onRefresh,
  });

  final TextEditingController searchController;
  final String selectedSort;
  final String? selectedOperation;
  final String? scopeTitle;
  final String? scopeSubtitle;
  final ValueChanged<String> onSearch;
  final ValueChanged<String?> onSortChanged;
  final ValueChanged<String?> onOperationChanged;
  final VoidCallback onRefresh;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Material(
      color: colorScheme.surface,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
        child: Column(
          children: [
            if (scopeTitle != null) ...[
              Container(
                key: const Key('activity-scope-banner'),
                width: double.infinity,
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: colorScheme.surfaceContainerHighest,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Row(
                  children: [
                    Icon(
                      Icons.history_outlined,
                      color: colorScheme.onSurfaceVariant,
                    ),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            scopeTitle!,
                            style: theme.textTheme.labelLarge?.copyWith(
                              fontWeight: FontWeight.w700,
                            ),
                          ),
                          if ((scopeSubtitle ?? '').trim().isNotEmpty)
                            Text(
                              scopeSubtitle!.trim(),
                              style: theme.textTheme.bodySmall?.copyWith(
                                color: colorScheme.onSurfaceVariant,
                              ),
                            ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 10),
            ],
            MobileGridControlsBar(
              keyPrefix: 'activity',
              padding: EdgeInsets.zero,
              searchController: searchController,
              searchLabel: 'Search audit activity',
              onSearch: onSearch,
              onClearSearch: () {
                searchController.clear();
                onSearch('');
              },
              sort: selectedSort,
              defaultSort: '-timestamp',
              sortOptions: const [
                MobileGridControlOption(
                  value: '-timestamp',
                  label: 'Newest first',
                ),
                MobileGridControlOption(
                  value: 'timestamp',
                  label: 'Oldest first',
                ),
              ],
              onSortChanged: onSortChanged,
              filters: [
                MobileGridChoiceFilter(
                  id: 'operation',
                  label: 'Action',
                  value: selectedOperation,
                  allLabel: 'All actions',
                  options: const [
                    MobileGridControlOption(value: 'Created', label: 'Created'),
                    MobileGridControlOption(value: 'Updated', label: 'Updated'),
                    MobileGridControlOption(value: 'Deleted', label: 'Deleted'),
                    MobileGridControlOption(
                      value: 'Approved',
                      label: 'Approved',
                    ),
                    MobileGridControlOption(
                      value: 'Rejected',
                      label: 'Rejected',
                    ),
                  ],
                  onChanged: onOperationChanged,
                ),
              ],
              trailingActions: [
                IconButton(
                  icon: const Icon(Icons.refresh),
                  tooltip: 'Refresh activity',
                  onPressed: onRefresh,
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _ActivityBody extends StatelessWidget {
  const _ActivityBody({
    required this.state,
    required this.controller,
    required this.focusedEntryId,
    required this.onOpenEntry,
    required this.onCloseEntry,
  });

  final ActivityHistoryState state;
  final ScrollController controller;
  final int? focusedEntryId;
  final ValueChanged<ActivityEntry> onOpenEntry;
  final VoidCallback onCloseEntry;

  @override
  Widget build(BuildContext context) {
    if (state.loading && state.items.isEmpty) {
      return const Center(child: CircularProgressIndicator());
    }

    if (state.error != null && state.items.isEmpty) {
      return _ErrorBody(message: state.error!);
    }

    if (state.items.isEmpty) {
      return const _EmptyBody();
    }

    final itemCount =
        state.items.length + (state.hasMore || state.loadingMore ? 1 : 0);

    return ListView.separated(
      controller: controller,
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 16),
      itemCount: itemCount,
      separatorBuilder: (context, index) {
        if (index < state.items.length - 1) {
          return const MobileM3ListDivider();
        }
        return const SizedBox(height: 16);
      },
      itemBuilder: (context, index) {
        if (index >= state.items.length) {
          return const Padding(
            padding: EdgeInsets.all(16),
            child: Center(child: CircularProgressIndicator()),
          );
        }
        final entry = state.items[index];
        return _ActivityTile(
          entry: entry,
          position: MobileM3ListItemPositionForIndex.forIndex(
            index,
            state.items.length,
          ),
          expanded: entry.id == focusedEntryId,
          onOpen: () => onOpenEntry(entry),
          onClose: onCloseEntry,
        );
      },
    );
  }
}

class _ActivityTile extends StatelessWidget {
  const _ActivityTile({
    required this.entry,
    required this.position,
    required this.expanded,
    required this.onOpen,
    required this.onClose,
  });

  final ActivityEntry entry;
  final MobileM3ListItemPosition position;
  final bool expanded;
  final VoidCallback onOpen;
  final VoidCallback onClose;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final (icon, tint) = _operationVisual(entry.operationName, colorScheme);
    final hasChanges = entry.changes.isNotEmpty;
    final radius = expanded
        ? BorderRadius.circular(M3Shape.extraLarge)
        : _activityRowRadius(position);

    return AnimatedPadding(
      duration: M3Motion.medium3,
      curve: M3Motion.emphasizedDecelerate,
      padding: EdgeInsets.symmetric(vertical: expanded ? 8 : 0),
      child: AnimatedSize(
        duration: M3Motion.medium4,
        curve: M3Motion.emphasizedDecelerate,
        reverseDuration: M3Motion.medium3,
        alignment: Alignment.topCenter,
        child: AnimatedContainer(
          key: Key('activity-entry-${entry.id}'),
          duration: M3Motion.medium3,
          curve: M3Motion.emphasizedDecelerate,
          clipBehavior: Clip.antiAlias,
          padding: expanded
              ? const EdgeInsets.fromLTRB(12, 10, 12, 14)
              : const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
          decoration: BoxDecoration(
            color: expanded
                ? colorScheme.surfaceContainerHighest
                : colorScheme.surfaceContainerHigh,
            borderRadius: radius,
            border: Border.all(
              color: expanded
                  ? colorScheme.outlineVariant.withValues(alpha: 0.58)
                  : Colors.transparent,
            ),
          ),
          child: Material(
            color: Colors.transparent,
            child: InkWell(
              onTap: hasChanges && !expanded ? onOpen : null,
              borderRadius: radius,
              child: AnimatedSwitcher(
                duration: M3Motion.medium3,
                switchInCurve: M3Motion.emphasizedDecelerate,
                switchOutCurve: M3Motion.emphasizedAccelerate,
                transitionBuilder: (child, animation) {
                  final curved = CurvedAnimation(
                    parent: animation,
                    curve: M3Motion.emphasizedDecelerate,
                    reverseCurve: M3Motion.emphasizedAccelerate,
                  );
                  return FadeTransition(
                    opacity: curved,
                    child: ScaleTransition(
                      scale: Tween<double>(
                        begin: 0.985,
                        end: 1,
                      ).animate(curved),
                      child: SlideTransition(
                        position: Tween<Offset>(
                          begin: const Offset(0, 0.018),
                          end: Offset.zero,
                        ).animate(curved),
                        child: child,
                      ),
                    ),
                  );
                },
                child: expanded && hasChanges
                    ? _ActivityInlineCardContent(
                        key: Key('activity-detail-card-${entry.id}'),
                        entry: entry,
                        icon: icon,
                        tint: tint,
                        onBack: onClose,
                      )
                    : _ActivityCompactRowContent(
                        key: Key('activity-row-content-${entry.id}'),
                        entry: entry,
                        icon: icon,
                        tint: tint,
                        navigates: hasChanges,
                      ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _ActivityCompactRowContent extends StatelessWidget {
  const _ActivityCompactRowContent({
    super.key,
    required this.entry,
    required this.icon,
    required this.tint,
    required this.navigates,
  });

  final ActivityEntry entry;
  final IconData icon;
  final Color tint;
  final bool navigates;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final subtitleStyle = theme.textTheme.bodySmall?.copyWith(
      color: colorScheme.onSurfaceVariant,
    );

    return Row(
      crossAxisAlignment: CrossAxisAlignment.center,
      children: [
        SizedBox.square(
          dimension: 48,
          child: Center(
            child: MobileM3LeadingIcon(
              icon: icon,
              backgroundColor: tint.withValues(alpha: 0.16),
              foregroundColor: tint,
            ),
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                entry.description,
                style: theme.textTheme.titleSmall?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
              const SizedBox(height: 2),
              Text(
                '${entry.operationName} · ${entry.actor} · ${entry.entityType} #${entry.entityId}',
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: subtitleStyle,
              ),
            ],
          ),
        ),
        const SizedBox(width: 8),
        _ActivityRowTrailing(
          label: _relativeTime(entry.timestamp),
          navigates: navigates,
        ),
      ],
    );
  }
}

class _ActivityInlineCardContent extends StatelessWidget {
  const _ActivityInlineCardContent({
    super.key,
    required this.entry,
    required this.icon,
    required this.tint,
    required this.onBack,
  });

  final ActivityEntry entry;
  final IconData icon;
  final Color tint;
  final VoidCallback onBack;

  @override
  Widget build(BuildContext context) {
    final targetLabel = _activityTargetLabel(entry);
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final subtitleStyle = theme.textTheme.bodySmall?.copyWith(
      color: colorScheme.onSurfaceVariant,
    );
    final visibleChanges = entry.changes.take(3).toList(growable: false);
    final hiddenChangeCount = entry.changes.length - visibleChanges.length;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            BackButton(
              key: Key('activity-detail-back-${entry.id}'),
              onPressed: onBack,
            ),
            const SizedBox(width: 2),
            Expanded(
              child: Text(
                targetLabel,
                style: theme.textTheme.titleMedium?.copyWith(
                  fontWeight: FontWeight.w800,
                ),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
            ),
          ],
        ),
        const SizedBox(height: 6),
        _ActivityInlineDivider(),
        Padding(
          padding: const EdgeInsets.fromLTRB(6, 12, 6, 12),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              MobileM3LeadingIcon(
                icon: icon,
                backgroundColor: tint.withValues(alpha: 0.18),
                foregroundColor: tint,
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      entry.description,
                      style: theme.textTheme.titleSmall?.copyWith(
                        fontWeight: FontWeight.w800,
                      ),
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                    ),
                    const SizedBox(height: 3),
                    Text(
                      '${entry.operationName} · ${_relativeTime(entry.timestamp)} · ${entry.actor}',
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: subtitleStyle,
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
        _ActivityInlineDivider(),
        Padding(
          padding: const EdgeInsets.fromLTRB(6, 14, 6, 4),
          child: Text(
            'Changed fields',
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w800,
            ),
          ),
        ),
        for (final change in visibleChanges)
          Padding(
            padding: const EdgeInsets.fromLTRB(6, 8, 6, 10),
            child: _ActivityChangeRow(change: change),
          ),
        if (hiddenChangeCount > 0)
          Padding(
            padding: const EdgeInsets.fromLTRB(6, 0, 6, 2),
            child: Text(
              '+$hiddenChangeCount more changed fields',
              style: theme.textTheme.labelMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
                fontWeight: FontWeight.w700,
              ),
            ),
          ),
      ],
    );
  }
}

class _ActivityInlineDivider extends StatelessWidget {
  const _ActivityInlineDivider();

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Container(
      height: 1,
      color: colorScheme.outlineVariant.withValues(alpha: 0.32),
    );
  }
}

class _ActivityRowTrailing extends StatelessWidget {
  const _ActivityRowTrailing({required this.label, required this.navigates});

  final String label;
  final bool navigates;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          label,
          style: theme.textTheme.bodySmall?.copyWith(
            color: colorScheme.onSurfaceVariant,
          ),
        ),
        if (navigates) ...[
          const SizedBox(width: 2),
          Icon(Icons.chevron_right, color: colorScheme.onSurfaceVariant),
        ],
      ],
    );
  }
}

class _ActivityChangeRow extends StatelessWidget {
  const _ActivityChangeRow({required this.change});

  final ActivityChange change;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final valueStyle = theme.textTheme.bodySmall?.copyWith(
      color: colorScheme.onSurfaceVariant,
    );

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          change.field,
          style: theme.textTheme.labelLarge?.copyWith(
            fontWeight: FontWeight.w800,
          ),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
        const SizedBox(height: 6),
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(
              child: Text(
                change.oldValue,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: valueStyle,
              ),
            ),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 10),
              child: Icon(
                Icons.arrow_forward,
                size: 16,
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            Expanded(
              child: Text(
                change.newValue,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: valueStyle,
              ),
            ),
          ],
        ),
      ],
    );
  }
}

class _InlineError extends StatelessWidget {
  const _InlineError({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Container(
      width: double.infinity,
      margin: const EdgeInsets.fromLTRB(16, 0, 16, 8),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: cs.errorContainer,
        borderRadius: BorderRadius.circular(8),
      ),
      child: Text(message, style: TextStyle(color: cs.onErrorContainer)),
    );
  }
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.all(24),
      children: [
        const SizedBox(height: 80),
        Icon(
          Icons.history_toggle_off,
          size: 48,
          color: Theme.of(context).colorScheme.error,
        ),
        const SizedBox(height: 16),
        Text(
          message,
          textAlign: TextAlign.center,
          style: TextStyle(color: Theme.of(context).colorScheme.error),
        ),
      ],
    );
  }
}

class _EmptyBody extends StatelessWidget {
  const _EmptyBody();

  @override
  Widget build(BuildContext context) {
    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.all(24),
      children: [
        const SizedBox(height: 80),
        Icon(
          Icons.history_toggle_off,
          size: 48,
          color: Theme.of(context).colorScheme.onSurfaceVariant,
        ),
        const SizedBox(height: 16),
        Text(
          'No activity yet.',
          textAlign: TextAlign.center,
          style: TextStyle(
            color: Theme.of(context).colorScheme.onSurfaceVariant,
          ),
        ),
      ],
    );
  }
}

(IconData, Color) _operationVisual(String operation, ColorScheme cs) {
  switch (operation) {
    case 'Created':
      return (Icons.add_circle_outline, cs.primary);
    case 'Updated':
      return (Icons.edit_outlined, cs.secondary);
    case 'Deleted':
      return (Icons.delete_outline, cs.error);
    case 'Approved':
      return (Icons.check_circle_outline, cs.tertiary);
    case 'Rejected':
      return (Icons.cancel_outlined, cs.error);
    default:
      return (Icons.bolt_outlined, cs.onSurfaceVariant);
  }
}

BorderRadius _activityRowRadius(MobileM3ListItemPosition position) {
  const radius = Radius.circular(M3Shape.largeIncreased);
  return switch (position) {
    MobileM3ListItemPosition.single => const BorderRadius.all(radius),
    MobileM3ListItemPosition.first => const BorderRadius.vertical(top: radius),
    MobileM3ListItemPosition.middle => BorderRadius.zero,
    MobileM3ListItemPosition.last => const BorderRadius.vertical(
      bottom: radius,
    ),
  };
}

String _activityTargetLabel(ActivityEntry entry) {
  final entity = entry.entityType.trim();
  if (entity.isEmpty) return 'Activity detail';
  return '$entity #${entry.entityId}';
}

String _relativeTime(DateTime when) {
  final now = DateTime.now();
  final local = when.toLocal();
  final diff = now.difference(local);
  if (diff.inMinutes < 1) return 'Just now';
  if (diff.inMinutes < 60) return '${diff.inMinutes}m ago';
  if (diff.inHours < 24) return '${diff.inHours}h ago';
  if (diff.inDays < 7) return '${diff.inDays}d ago';
  final m = local.month.toString().padLeft(2, '0');
  final d = local.day.toString().padLeft(2, '0');
  return '${local.year}-$m-$d';
}
