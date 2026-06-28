import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/api/api_exception.dart';
import 'unit_navigation.dart';
import 'units_repository.dart';

class UnitsListScreen extends ConsumerStatefulWidget {
  const UnitsListScreen({super.key});

  @override
  ConsumerState<UnitsListScreen> createState() => _UnitsListScreenState();
}

class _UnitsListScreenState extends ConsumerState<UnitsListScreen> {
  static const _pageSize = 100;

  final _searchCtrl = TextEditingController();
  Timer? _searchDebounce;
  String _search = '';

  @override
  void dispose() {
    _searchDebounce?.cancel();
    _searchCtrl.dispose();
    super.dispose();
  }

  void _setSearch(String value) {
    _searchDebounce?.cancel();
    _searchDebounce = Timer(const Duration(milliseconds: 280), () {
      if (mounted) setState(() => _search = value.trim());
    });
  }

  Future<void> _refresh(UnitHealthListArgs args) {
    return ref.refresh(unitHealthPageProvider(args).future);
  }

  void _openUnit(UnitHealth unit) {
    openUnitCommandCenter(context, unitId: unit.id);
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final args = (
      search: _search.isEmpty ? null : _search,
      skip: 0,
      take: _pageSize,
    );
    final unitsAsync = ref.watch(unitHealthPageProvider(args));

    return Scaffold(
      appBar: AppBar(title: const Text('Units')),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 8),
            child: TextField(
              controller: _searchCtrl,
              decoration: InputDecoration(
                hintText: 'Search unit or property',
                prefixIcon: const Icon(Symbols.search_rounded, size: 20),
                suffixIcon: _searchCtrl.text.isNotEmpty
                    ? IconButton(
                        icon: const Icon(Symbols.close_rounded, size: 20),
                        onPressed: () {
                          _searchDebounce?.cancel();
                          _searchCtrl.clear();
                          setState(() => _search = '');
                        },
                      )
                    : null,
                isDense: true,
                contentPadding: const EdgeInsets.symmetric(vertical: 10),
                border: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(10),
                ),
              ),
              onChanged: (value) {
                setState(() {});
                _setSearch(value);
              },
            ),
          ),
          Expanded(
            child: RefreshIndicator(
              onRefresh: () => _refresh(args),
              child: unitsAsync.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (e, _) => _UnitsErrorBody(
                  message: e is ApiException ? e.message : e.toString(),
                  onRetry: () => _refresh(args),
                ),
                data: (page) {
                  if (page.items.isEmpty) {
                    return _UnitsEmptyBody(hasSearch: _search.isNotEmpty);
                  }

                  final bottomInset = MediaQuery.paddingOf(context).bottom;
                  return ListView.separated(
                    padding: EdgeInsets.fromLTRB(
                      16,
                      8,
                      16,
                      160.0 + bottomInset,
                    ),
                    itemCount: page.items.length,
                    separatorBuilder: (_, index) =>
                        _GroupedListDivider(colorScheme: colorScheme),
                    itemBuilder: (context, index) {
                      final unit = page.items[index];
                      return _UnitHealthRow(
                        unit: unit,
                        first: index == 0,
                        last: index == page.items.length - 1,
                        onTap: () => _openUnit(unit),
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

class _UnitHealthRow extends StatelessWidget {
  const _UnitHealthRow({
    required this.unit,
    required this.first,
    required this.last,
    required this.onTap,
  });

  final UnitHealth unit;
  final bool first;
  final bool last;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final borderRadius = BorderRadius.vertical(
      top: first ? const Radius.circular(20) : Radius.zero,
      bottom: last ? const Radius.circular(20) : Radius.zero,
    );

    return Material(
      color: colorScheme.surfaceContainerHigh,
      borderRadius: borderRadius,
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        borderRadius: borderRadius,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 13),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Container(
                width: 42,
                height: 42,
                decoration: BoxDecoration(
                  color: colorScheme.primaryContainer,
                  borderRadius: BorderRadius.circular(14),
                ),
                child: Icon(
                  Symbols.home_work_rounded,
                  color: colorScheme.onPrimaryContainer,
                  size: 22,
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Expanded(
                          child: Text(
                            _unitLabel(unit),
                            style: theme.textTheme.titleSmall?.copyWith(
                              fontWeight: FontWeight.w700,
                            ),
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                        const SizedBox(width: 8),
                        _ToneChip(label: unit.simpleStage),
                      ],
                    ),
                    const SizedBox(height: 2),
                    Text(
                      unit.propertyName,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: colorScheme.onSurfaceVariant,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                    const SizedBox(height: 8),
                    Wrap(
                      spacing: 8,
                      runSpacing: 4,
                      children: [
                        _MetaChip(
                          icon: Symbols.payments_rounded,
                          label: '${_formatCurrency(unit.marketRent)}/mo',
                        ),
                        _MetaChip(
                          icon: Symbols.build_rounded,
                          label: '${unit.openWorkOrderCount} open',
                        ),
                        if (unit.leaseEndsInDays != null)
                          _MetaChip(
                            icon: Symbols.event_rounded,
                            label: _leaseEndsLabel(unit.leaseEndsInDays!),
                          ),
                        if (unit.docsNeedingReviewCount > 0)
                          _MetaChip(
                            icon: Symbols.folder_open_rounded,
                            label: '${unit.docsNeedingReviewCount} docs',
                          ),
                      ],
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 8),
              Icon(
                Symbols.chevron_right_rounded,
                color: colorScheme.onSurfaceVariant,
                size: 20,
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _ToneChip extends StatelessWidget {
  const _ToneChip({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    final (bg, fg) = switch (label) {
      'Active' => (
        colorScheme.primaryContainer,
        colorScheme.onPrimaryContainer,
      ),
      'Renewal' => (
        colorScheme.tertiaryContainer,
        colorScheme.onTertiaryContainer,
      ),
      'Move-Out' => (colorScheme.errorContainer, colorScheme.onErrorContainer),
      'Turnover' => (
        colorScheme.secondaryContainer,
        colorScheme.onSecondaryContainer,
      ),
      _ => (colorScheme.surfaceContainerHighest, colorScheme.onSurfaceVariant),
    };

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Text(
        label.isEmpty ? 'Unit' : label,
        style: TextStyle(fontSize: 11, fontWeight: FontWeight.w700, color: fg),
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
    final color = Theme.of(context).colorScheme.onSurfaceVariant;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(icon, size: 14, color: color),
        const SizedBox(width: 3),
        Text(label, style: TextStyle(fontSize: 12, color: color)),
      ],
    );
  }
}

class _GroupedListDivider extends StatelessWidget {
  const _GroupedListDivider({required this.colorScheme});

  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Divider(
      height: 1,
      thickness: 1,
      indent: 70,
      endIndent: 16,
      color: colorScheme.outlineVariant.withValues(alpha: 0.48),
    );
  }
}

class _UnitsEmptyBody extends StatelessWidget {
  const _UnitsEmptyBody({required this.hasSearch});

  final bool hasSearch;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;

    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.all(32),
      children: [
        const SizedBox(height: 96),
        Icon(
          Symbols.home_work_rounded,
          size: 48,
          color: colorScheme.onSurfaceVariant,
        ),
        const SizedBox(height: 12),
        Text(
          hasSearch ? 'No units match that search.' : 'No units yet',
          textAlign: TextAlign.center,
          style: Theme.of(context).textTheme.titleMedium,
        ),
        const SizedBox(height: 6),
        Text(
          hasSearch
              ? 'Try a different unit number or property name.'
              : 'Units live under a property. Add a property and its units to start managing them here.',
          textAlign: TextAlign.center,
          style: TextStyle(color: colorScheme.onSurfaceVariant),
        ),
      ],
    );
  }
}

class _UnitsErrorBody extends StatelessWidget {
  const _UnitsErrorBody({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;

    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.all(32),
      children: [
        const SizedBox(height: 96),
        Icon(Symbols.error_rounded, size: 40, color: colorScheme.error),
        const SizedBox(height: 12),
        Text(
          message,
          textAlign: TextAlign.center,
          style: TextStyle(color: colorScheme.error),
        ),
        const SizedBox(height: 16),
        Center(
          child: FilledButton.tonal(
            onPressed: onRetry,
            child: const Text('Retry'),
          ),
        ),
      ],
    );
  }
}

String _unitLabel(UnitHealth unit) {
  final value = unit.unitNumber.trim();
  if (value.isEmpty) return 'Unit';
  return value.toLowerCase().startsWith('unit ') ? value : 'Unit $value';
}

String _leaseEndsLabel(int days) {
  if (days == 0) return 'Lease ends today';
  if (days == 1) return 'Lease ends tomorrow';
  return 'Lease ends in ${days}d';
}

String _formatCurrency(double amount) {
  final rounded = amount.round();
  final s = rounded.toString();
  final buf = StringBuffer(r'$');
  final start = s.length % 3;
  if (start > 0) buf.write(s.substring(0, start));
  for (var i = start; i < s.length; i += 3) {
    if (i > 0) buf.write(',');
    buf.write(s.substring(i, i + 3));
  }
  return buf.toString();
}
