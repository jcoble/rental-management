import 'dart:async';

import 'package:flutter/material.dart';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../home/mobile_domain_chrome.dart';
import 'activity_repository.dart';

class ActivityHistoryScreen extends ConsumerStatefulWidget {
  const ActivityHistoryScreen({super.key});

  @override
  ConsumerState<ActivityHistoryScreen> createState() =>
      _ActivityHistoryScreenState();
}

class _ActivityHistoryScreenState extends ConsumerState<ActivityHistoryScreen> {
  final _scrollController = ScrollController();
  final _searchController = TextEditingController();

  @override
  void initState() {
    super.initState();
    _searchController.text =
        ref.read(activityHistoryProvider).filter.search ?? '';
    _scrollController.addListener(_onScroll);
    Future.microtask(() {
      if (!mounted) return;
      ref.read(activityHistoryProvider.notifier).refresh();
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
      ref.read(activityHistoryProvider.notifier).loadMore();
    }
  }

  Future<void> _refresh() =>
      ref.read(activityHistoryProvider.notifier).refresh();

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(activityHistoryProvider);
    ref.listen<String?>(
      activityHistoryProvider.select((value) => value.filter.search),
      (_, next) => _syncSearchText(next ?? ''),
    );
    final refreshButton = IconButton(
      icon: const Icon(Icons.refresh),
      tooltip: 'Refresh activity',
      onPressed: _refresh,
    );

    return Scaffold(
      appBar: mobileDomainRootAppBar(
        context,
        title: const Text('Activity history'),
        actions: [refreshButton],
      ),
      body: Column(
        children: [
          MobileDomainEmbeddedToolbar(children: [refreshButton]),
          _ActivityFilters(
            searchController: _searchController,
            selectedOperation: state.filter.operation,
            onSearch: (value) =>
                ref.read(activityHistoryProvider.notifier).setSearch(value),
            onOperationChanged: (value) =>
                ref.read(activityHistoryProvider.notifier).setOperation(value),
          ),
          if (state.error != null && state.items.isNotEmpty)
            _InlineError(message: state.error!),
          Expanded(
            child: RefreshIndicator(
              onRefresh: _refresh,
              child: _ActivityBody(state: state, controller: _scrollController),
            ),
          ),
        ],
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
    required this.selectedOperation,
    required this.onSearch,
    required this.onOperationChanged,
  });

  final TextEditingController searchController;
  final String? selectedOperation;
  final ValueChanged<String> onSearch;
  final ValueChanged<String?> onOperationChanged;

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
            ValueListenableBuilder<TextEditingValue>(
              valueListenable: searchController,
              builder: (context, value, _) => TextField(
                controller: searchController,
                textInputAction: TextInputAction.search,
                decoration: InputDecoration(
                  prefixIcon: const Icon(Icons.search),
                  suffixIcon: value.text.isEmpty
                      ? null
                      : IconButton(
                          icon: const Icon(Icons.close),
                          tooltip: 'Clear search',
                          onPressed: () {
                            searchController.clear();
                            onSearch('');
                          },
                        ),
                  hintText: 'Search audit activity',
                  border: const OutlineInputBorder(),
                  isDense: true,
                ),
                onSubmitted: onSearch,
              ),
            ),
            const SizedBox(height: 10),
            DropdownButtonFormField<String?>(
              initialValue: selectedOperation,
              isExpanded: true,
              decoration: const InputDecoration(
                labelText: 'Action',
                border: OutlineInputBorder(),
                isDense: true,
              ),
              items: const [
                DropdownMenuItem<String?>(
                  value: null,
                  child: Text('All actions'),
                ),
                DropdownMenuItem(value: 'Created', child: Text('Created')),
                DropdownMenuItem(value: 'Updated', child: Text('Updated')),
                DropdownMenuItem(value: 'Deleted', child: Text('Deleted')),
                DropdownMenuItem(value: 'Approved', child: Text('Approved')),
                DropdownMenuItem(value: 'Rejected', child: Text('Rejected')),
              ],
              onChanged: onOperationChanged,
            ),
          ],
        ),
      ),
    );
  }
}

class _ActivityBody extends StatelessWidget {
  const _ActivityBody({required this.state, required this.controller});

  final ActivityHistoryState state;
  final ScrollController controller;

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
      padding: const EdgeInsets.only(bottom: 16),
      itemCount: itemCount,
      separatorBuilder: (_, _) => const Divider(height: 1),
      itemBuilder: (context, index) {
        if (index >= state.items.length) {
          return const Padding(
            padding: EdgeInsets.all(16),
            child: Center(child: CircularProgressIndicator()),
          );
        }
        return _ActivityTile(entry: state.items[index]);
      },
    );
  }
}

class _ActivityTile extends StatelessWidget {
  const _ActivityTile({required this.entry});

  final ActivityEntry entry;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final (icon, tint) = _operationVisual(entry.operationName, colorScheme);
    final subtitleStyle = theme.textTheme.bodySmall?.copyWith(
      color: colorScheme.onSurfaceVariant,
    );

    final summary = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          entry.description,
          style: theme.textTheme.bodyMedium?.copyWith(
            fontWeight: FontWeight.w600,
          ),
        ),
        const SizedBox(height: 2),
        Text(
          '${entry.operationName} · ${entry.entityType} #${entry.entityId}',
          style: subtitleStyle,
        ),
        Text(
          '${_relativeTime(entry.timestamp)} · ${entry.actor}',
          style: subtitleStyle,
        ),
      ],
    );

    if (entry.changes.isEmpty) {
      return ListTile(
        leading: _ActivityIcon(icon: icon, tint: tint),
        title: summary,
      );
    }

    return ExpansionTile(
      leading: _ActivityIcon(icon: icon, tint: tint),
      tilePadding: const EdgeInsets.symmetric(horizontal: 16),
      childrenPadding: const EdgeInsets.fromLTRB(72, 0, 16, 12),
      title: summary,
      children: [
        for (final change in entry.changes)
          Padding(
            padding: const EdgeInsets.only(bottom: 6),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(
                  flex: 2,
                  child: Text(change.field, style: theme.textTheme.labelMedium),
                ),
                Expanded(
                  flex: 3,
                  child: Text(
                    '${change.oldValue} -> ${change.newValue}',
                    style: subtitleStyle,
                  ),
                ),
              ],
            ),
          ),
      ],
    );
  }
}

class _ActivityIcon extends StatelessWidget {
  const _ActivityIcon({required this.icon, required this.tint});

  final IconData icon;
  final Color tint;

  @override
  Widget build(BuildContext context) {
    return CircleAvatar(
      backgroundColor: tint.withValues(alpha: 0.14),
      foregroundColor: tint,
      child: Icon(icon, size: 20),
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
