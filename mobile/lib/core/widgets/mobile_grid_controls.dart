import 'dart:async';

import 'package:flutter/material.dart';

import '../theme/app_tokens.dart';

class MobileGridControlOption {
  const MobileGridControlOption({
    required this.value,
    required this.label,
    this.subtitle,
  });

  final String value;
  final String label;
  final String? subtitle;
}

class MobileGridChoiceFilter {
  const MobileGridChoiceFilter({
    required this.id,
    required this.label,
    required this.value,
    required this.options,
    required this.onChanged,
    this.defaultValue,
    this.allLabel = 'All',
  });

  final String id;
  final String label;
  final String? value;
  final String? defaultValue;
  final String allLabel;
  final List<MobileGridControlOption> options;
  final ValueChanged<String?> onChanged;

  bool get isActive => value != defaultValue;

  String labelFor(String? candidate) {
    if (candidate == defaultValue) return allLabel;
    for (final option in options) {
      if (option.value == candidate) return option.label;
    }
    return candidate ?? allLabel;
  }
}

class MobileGridControlsBar extends StatelessWidget {
  const MobileGridControlsBar({
    super.key,
    required this.searchController,
    required this.searchLabel,
    required this.onSearch,
    required this.onClearSearch,
    required this.sort,
    required this.defaultSort,
    required this.sortOptions,
    required this.onSortChanged,
    this.sortLabel = 'Sort',
    this.period,
    this.defaultPeriod = MobileGridPeriod.all,
    this.periodLabel = 'Period',
    this.onPeriodChanged,
    this.filters = const [],
    this.padding = const EdgeInsets.fromLTRB(16, 8, 16, 4),
    this.filterButtonTooltip = 'Sort and filter',
    this.keyPrefix,
    this.trailingActions = const [],
  });

  final TextEditingController searchController;
  final String searchLabel;
  final ValueChanged<String> onSearch;
  final VoidCallback onClearSearch;
  final String sort;
  final String defaultSort;
  final String sortLabel;
  final List<MobileGridControlOption> sortOptions;
  final ValueChanged<String?> onSortChanged;
  final String? period;
  final String defaultPeriod;
  final String periodLabel;
  final ValueChanged<String?>? onPeriodChanged;
  final List<MobileGridChoiceFilter> filters;
  final EdgeInsetsGeometry padding;
  final String filterButtonTooltip;
  final String? keyPrefix;
  final List<Widget> trailingActions;

  int get _activeCount {
    var count = sort == defaultSort ? 0 : 1;
    if (period != null && period != defaultPeriod) count++;
    count += filters.where((filter) => filter.isActive).length;
    return count;
  }

  List<_ActiveGridControl> get _activeControls {
    final controls = <_ActiveGridControl>[];
    if (sort != defaultSort) {
      controls.add(
        _ActiveGridControl(
          icon: Icons.sort,
          label: '$sortLabel: ${_optionLabel(sortOptions, sort)}',
        ),
      );
    }
    final currentPeriod = period;
    if (currentPeriod != null && currentPeriod != defaultPeriod) {
      controls.add(
        _ActiveGridControl(
          icon: Icons.event_outlined,
          label: '$periodLabel: ${MobileGridPeriod.labelFor(currentPeriod)}',
        ),
      );
    }
    for (final filter in filters) {
      if (!filter.isActive) continue;
      controls.add(
        _ActiveGridControl(
          icon: Icons.filter_list,
          label: '${filter.label}: ${filter.labelFor(filter.value)}',
        ),
      );
    }
    return controls;
  }

  @override
  Widget build(BuildContext context) {
    final activeCount = _activeCount;
    final activeControls = _activeControls;

    return Padding(
      padding: padding,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: MobileGridSearchField(
                  controller: searchController,
                  labelText: searchLabel,
                  onSubmitted: onSearch,
                  onClear: onClearSearch,
                ),
              ),
              const SizedBox(width: 8),
              for (final action in trailingActions) ...[
                SizedBox.square(dimension: 56, child: Center(child: action)),
                const SizedBox(width: 8),
              ],
              _GridControlSheetButton(
                key: _key('controls-button'),
                activeCount: activeCount,
                tooltip: filterButtonTooltip,
                onPressed: () => _showSheet(context),
              ),
            ],
          ),
          if (activeControls.isNotEmpty) ...[
            const SizedBox(height: 8),
            SizedBox(
              height: 36,
              child: ListView.separated(
                scrollDirection: Axis.horizontal,
                itemCount: activeControls.length,
                separatorBuilder: (_, _) => const SizedBox(width: 8),
                itemBuilder: (context, index) {
                  final control = activeControls[index];
                  return ActionChip(
                    avatar: Icon(control.icon, size: 16),
                    label: Text(control.label),
                    visualDensity: VisualDensity.compact,
                    onPressed: () => _showSheet(context),
                  );
                },
              ),
            ),
          ],
        ],
      ),
    );
  }

  void _showSheet(BuildContext context) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      useRootNavigator: true,
      useSafeArea: true,
      builder: (_) => _MobileGridControlsSheet(
        sort: sort,
        defaultSort: defaultSort,
        sortLabel: sortLabel,
        sortOptions: sortOptions,
        onSortChanged: onSortChanged,
        period: period,
        defaultPeriod: defaultPeriod,
        periodLabel: periodLabel,
        onPeriodChanged: onPeriodChanged,
        filters: filters,
        keyPrefix: keyPrefix,
      ),
    );
  }

  Key? _key(String suffix) {
    final prefix = keyPrefix;
    return prefix == null ? null : Key('$prefix-$suffix');
  }
}

class MobileGridSearchField extends StatefulWidget {
  const MobileGridSearchField({
    super.key,
    required this.controller,
    required this.labelText,
    required this.onSubmitted,
    required this.onClear,
    this.searchTooltip,
    this.clearTooltip,
  });

  final TextEditingController controller;
  final String labelText;
  final ValueChanged<String> onSubmitted;
  final VoidCallback onClear;
  final String? searchTooltip;
  final String? clearTooltip;

  @override
  State<MobileGridSearchField> createState() => _MobileGridSearchFieldState();
}

class _MobileGridSearchFieldState extends State<MobileGridSearchField>
    with WidgetsBindingObserver {
  late final FocusNode _focusNode = FocusNode();
  Timer? _searchDebounce;
  double _lastViewInsetBottom = 0;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    widget.controller.addListener(_handleTextChanged);
    _focusNode.addListener(_handleTextChanged);
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _lastViewInsetBottom = _viewInsetBottom;
  }

  @override
  void didUpdateWidget(covariant MobileGridSearchField oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.controller == widget.controller) return;
    oldWidget.controller.removeListener(_handleTextChanged);
    widget.controller.addListener(_handleTextChanged);
  }

  @override
  void dispose() {
    _searchDebounce?.cancel();
    WidgetsBinding.instance.removeObserver(this);
    widget.controller.removeListener(_handleTextChanged);
    _focusNode.removeListener(_handleTextChanged);
    _focusNode.dispose();
    super.dispose();
  }

  @override
  void didChangeMetrics() {
    final viewInsetBottom = _viewInsetBottom;
    final keyboardDismissed = _lastViewInsetBottom > 0 && viewInsetBottom == 0;
    _lastViewInsetBottom = viewInsetBottom;

    if (keyboardDismissed) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _closeSearch();
      });
    }
  }

  double get _viewInsetBottom {
    final view = View.of(context);
    return view.viewInsets.bottom / view.devicePixelRatio;
  }

  void _handleTextChanged() {
    setState(() {});
  }

  void _closeSearch() {
    if (!_focusNode.hasFocus) return;
    _focusNode.unfocus();
    FocusManager.instance.primaryFocus?.unfocus();
  }

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    final hasText = widget.controller.text.trim().isNotEmpty;
    final isActive = _focusNode.hasFocus;

    return PopScope<void>(
      canPop: !isActive,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop) _closeSearch();
      },
      child: SearchBar(
        controller: widget.controller,
        focusNode: _focusNode,
        hintText: widget.labelText,
        leading: _MorphingSearchLeadingIcon(
          active: isActive,
          searchTooltip: widget.searchTooltip ?? 'Search',
          backTooltip: 'Close search',
          onTap: () {
            if (isActive) {
              _closeSearch();
            } else {
              _focusNode.requestFocus();
            }
          },
        ),
        trailing: hasText
            ? [
                IconButton(
                  tooltip: widget.clearTooltip ?? 'Clear search',
                  icon: const Icon(Icons.close),
                  onPressed: widget.onClear,
                ),
              ]
            : null,
        onTapOutside: (_) => _closeSearch(),
        onTap: () {
          if (!_focusNode.hasFocus) {
            _focusNode.requestFocus();
          }
        },
        onChanged: (value) {
          _searchDebounce?.cancel();
          _searchDebounce = Timer(
            const Duration(milliseconds: 300),
            () => widget.onSubmitted(value),
          );
        },
        overlayColor: WidgetStatePropertyAll(
          colorScheme.primary.withValues(alpha: 0.08),
        ),
        onSubmitted: (value) {
          _searchDebounce?.cancel();
          widget.onSubmitted(value);
          _closeSearch();
        },
        constraints: const BoxConstraints(minHeight: 56),
        elevation: WidgetStateProperty.resolveWith((states) {
          if (states.contains(WidgetState.pressed) ||
              states.contains(WidgetState.focused)) {
            return 0;
          }
          return 0;
        }),
        backgroundColor: WidgetStateProperty.resolveWith((states) {
          if (states.contains(WidgetState.pressed) ||
              states.contains(WidgetState.focused)) {
            return colorScheme.surfaceContainerHighest;
          }
          return colorScheme.surfaceContainerHigh;
        }),
        surfaceTintColor: const WidgetStatePropertyAll(Colors.transparent),
        shadowColor: const WidgetStatePropertyAll(Colors.transparent),
        side: const WidgetStatePropertyAll(BorderSide.none),
        shape: const WidgetStatePropertyAll(StadiumBorder()),
        padding: const WidgetStatePropertyAll(
          EdgeInsets.only(left: 4, right: 4),
        ),
        hintStyle: WidgetStatePropertyAll(
          Theme.of(
            context,
          ).textTheme.bodyLarge?.copyWith(color: colorScheme.onSurfaceVariant),
        ),
        textStyle: WidgetStatePropertyAll(
          Theme.of(
            context,
          ).textTheme.bodyLarge?.copyWith(color: colorScheme.onSurface),
        ),
        textInputAction: TextInputAction.search,
      ),
    );
  }
}

class _MorphingSearchLeadingIcon extends StatelessWidget {
  const _MorphingSearchLeadingIcon({
    required this.active,
    required this.onTap,
    required this.searchTooltip,
    required this.backTooltip,
  });

  final bool active;
  final VoidCallback onTap;
  final String searchTooltip;
  final String backTooltip;

  @override
  Widget build(BuildContext context) {
    final color = IconTheme.of(context).color;

    return Semantics(
      button: true,
      label: active ? backTooltip : searchTooltip,
      child: Tooltip(
        message: active ? backTooltip : searchTooltip,
        child: InkResponse(
          onTap: onTap,
          radius: 28,
          containedInkWell: true,
          customBorder: const CircleBorder(),
          child: SizedBox.square(
            dimension: 48,
            child: TweenAnimationBuilder<double>(
              duration: M3Motion.medium2,
              curve: M3Motion.emphasizedDecelerate,
              tween: Tween<double>(end: active ? 1 : 0),
              builder: (context, progress, _) {
                final searchOpacity = 1 - progress;
                final backOpacity = progress;
                return Stack(
                  alignment: Alignment.center,
                  children: [
                    Transform.translate(
                      offset: Offset(-8 * progress, 0),
                      child: Transform.rotate(
                        angle: -0.25 * progress,
                        child: Transform.scale(
                          scale: 1 - (0.12 * progress),
                          child: Opacity(
                            opacity: searchOpacity.clamp(0.0, 1.0).toDouble(),
                            child: Icon(Icons.search, color: color),
                          ),
                        ),
                      ),
                    ),
                    Transform.translate(
                      offset: Offset(8 * (1 - progress), 0),
                      child: Transform.rotate(
                        angle: 0.25 * (1 - progress),
                        child: Transform.scale(
                          scale: 0.88 + (0.12 * progress),
                          child: Opacity(
                            opacity: backOpacity.clamp(0.0, 1.0).toDouble(),
                            child: Icon(Icons.arrow_back, color: color),
                          ),
                        ),
                      ),
                    ),
                  ],
                );
              },
            ),
          ),
        ),
      ),
    );
  }
}

class MobileGridSortField extends StatelessWidget {
  const MobileGridSortField({
    super.key,
    required this.value,
    required this.labelText,
    required this.items,
    required this.onChanged,
  });

  final String value;
  final String labelText;
  final List<DropdownMenuItem<String>> items;
  final ValueChanged<String?> onChanged;

  @override
  Widget build(BuildContext context) {
    return DropdownButtonFormField<String>(
      initialValue: value,
      isExpanded: true,
      decoration: InputDecoration(
        labelText: labelText,
        prefixIcon: const Icon(Icons.sort),
      ),
      items: items,
      onChanged: onChanged,
    );
  }
}

class MobileGridPeriod {
  static const all = 'all';
  static const thisMonth = 'thisMonth';
  static const lastMonth = 'lastMonth';
  static const next30Days = 'next30Days';
  static const thisYear = 'thisYear';

  static const options = <MobileGridControlOption>[
    MobileGridControlOption(value: all, label: 'All dates'),
    MobileGridControlOption(value: thisMonth, label: 'This month'),
    MobileGridControlOption(value: lastMonth, label: 'Last month'),
    MobileGridControlOption(value: next30Days, label: 'Next 30 days'),
    MobileGridControlOption(value: thisYear, label: 'This year'),
  ];

  static const items = <DropdownMenuItem<String>>[
    DropdownMenuItem(value: all, child: Text('All dates')),
    DropdownMenuItem(value: thisMonth, child: Text('This month')),
    DropdownMenuItem(value: lastMonth, child: Text('Last month')),
    DropdownMenuItem(value: next30Days, child: Text('Next 30 days')),
    DropdownMenuItem(value: thisYear, child: Text('This year')),
  ];

  static String labelFor(String value) => _optionLabel(options, value);
}

class MobileGridDateRange {
  const MobileGridDateRange({this.from, this.to});

  final String? from;
  final String? to;
}

MobileGridDateRange mobileGridDateRangeForPeriod(
  String period, {
  DateTime? now,
}) {
  final today = DateUtils.dateOnly(now ?? DateTime.now());

  switch (period) {
    case MobileGridPeriod.thisMonth:
      final start = DateTime(today.year, today.month);
      final end = DateTime(
        today.year,
        today.month + 1,
      ).subtract(const Duration(days: 1));
      return MobileGridDateRange(from: _dateOnly(start), to: _dateOnly(end));
    case MobileGridPeriod.lastMonth:
      final start = DateTime(today.year, today.month - 1);
      final end = DateTime(
        today.year,
        today.month,
      ).subtract(const Duration(days: 1));
      return MobileGridDateRange(from: _dateOnly(start), to: _dateOnly(end));
    case MobileGridPeriod.next30Days:
      final end = today.add(const Duration(days: 30));
      return MobileGridDateRange(from: _dateOnly(today), to: _dateOnly(end));
    case MobileGridPeriod.thisYear:
      final start = DateTime(today.year);
      final end = DateTime(today.year, 12, 31);
      return MobileGridDateRange(from: _dateOnly(start), to: _dateOnly(end));
    case MobileGridPeriod.all:
    default:
      return const MobileGridDateRange();
  }
}

String _dateOnly(DateTime date) {
  final month = date.month.toString().padLeft(2, '0');
  final day = date.day.toString().padLeft(2, '0');
  return '${date.year}-$month-$day';
}

class MobileGridPeriodField extends StatelessWidget {
  const MobileGridPeriodField({
    super.key,
    required this.value,
    required this.labelText,
    required this.onChanged,
  });

  final String value;
  final String labelText;
  final ValueChanged<String?> onChanged;

  @override
  Widget build(BuildContext context) {
    return DropdownButtonFormField<String>(
      initialValue: value,
      isExpanded: true,
      decoration: InputDecoration(
        labelText: labelText,
        prefixIcon: const Icon(Icons.event_outlined),
      ),
      items: MobileGridPeriod.items,
      onChanged: onChanged,
    );
  }
}

class MobileGridPagingBar extends StatelessWidget {
  const MobileGridPagingBar({
    super.key,
    required this.totalCount,
    required this.skip,
    required this.itemCount,
    required this.onPrevious,
    required this.onNext,
    this.label,
    this.previousTooltip = 'Previous page',
    this.nextTooltip = 'Next page',
  });

  final int totalCount;
  final int skip;
  final int itemCount;
  final VoidCallback? onPrevious;
  final VoidCallback? onNext;
  final String? label;
  final String previousTooltip;
  final String nextTooltip;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final start = totalCount == 0 ? 0 : skip + 1;
    final end = skip + itemCount;

    return Row(
      children: [
        Expanded(
          child: Text(
            label ?? '$start-$end of $totalCount',
            style: theme.textTheme.bodySmall,
          ),
        ),
        IconButton(
          tooltip: previousTooltip,
          icon: const Icon(Icons.chevron_left),
          onPressed: onPrevious,
        ),
        IconButton(
          tooltip: nextTooltip,
          icon: const Icon(Icons.chevron_right),
          onPressed: onNext,
        ),
      ],
    );
  }
}

class _GridControlSheetButton extends StatelessWidget {
  const _GridControlSheetButton({
    super.key,
    required this.activeCount,
    required this.tooltip,
    required this.onPressed,
  });

  final int activeCount;
  final String tooltip;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    final button = IconButton(
      tooltip: tooltip,
      icon: const Icon(Icons.tune),
      style: IconButton.styleFrom(
        minimumSize: const Size.square(48),
        backgroundColor: activeCount > 0
            ? colorScheme.secondaryContainer
            : colorScheme.surfaceContainerHigh,
        foregroundColor: activeCount > 0
            ? colorScheme.onSecondaryContainer
            : colorScheme.onSurfaceVariant,
        shape: const RoundedRectangleBorder(borderRadius: M3Shape.radiusFull),
      ),
      onPressed: onPressed,
    );

    return SizedBox.square(
      dimension: 56,
      child: Center(
        child: activeCount > 0
            ? Badge.count(count: activeCount, child: button)
            : button,
      ),
    );
  }
}

class _MobileGridControlsSheet extends StatefulWidget {
  const _MobileGridControlsSheet({
    required this.sort,
    required this.defaultSort,
    required this.sortLabel,
    required this.sortOptions,
    required this.onSortChanged,
    required this.period,
    required this.defaultPeriod,
    required this.periodLabel,
    required this.onPeriodChanged,
    required this.filters,
    required this.keyPrefix,
  });

  final String sort;
  final String defaultSort;
  final String sortLabel;
  final List<MobileGridControlOption> sortOptions;
  final ValueChanged<String?> onSortChanged;
  final String? period;
  final String defaultPeriod;
  final String periodLabel;
  final ValueChanged<String?>? onPeriodChanged;
  final List<MobileGridChoiceFilter> filters;
  final String? keyPrefix;

  @override
  State<_MobileGridControlsSheet> createState() =>
      _MobileGridControlsSheetState();
}

class _MobileGridControlsSheetState extends State<_MobileGridControlsSheet> {
  late String _sort = widget.sort;
  late String? _period = widget.period;
  late final Map<String, String?> _filterValues = {
    for (final filter in widget.filters) filter.id: filter.value,
  };

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final bottomInset = MediaQuery.viewInsetsOf(context).bottom;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 0, 20, 16 + bottomInset),
      child: ConstrainedBox(
        constraints: BoxConstraints(
          maxHeight: MediaQuery.sizeOf(context).height * 0.82,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    'Sort and filter',
                    style: theme.textTheme.titleLarge,
                  ),
                ),
                TextButton(onPressed: _reset, child: const Text('Reset')),
              ],
            ),
            const SizedBox(height: 8),
            Flexible(
              child: ListView(
                shrinkWrap: true,
                children: [
                  _SheetSectionTitle(label: widget.sortLabel),
                  RadioGroup<String>(
                    groupValue: _sort,
                    onChanged: (value) {
                      if (value == null) return;
                      setState(() => _sort = value);
                    },
                    child: Column(
                      children: [
                        for (final option in widget.sortOptions)
                          RadioListTile<String>(
                            key: _key('sort-${option.value}'),
                            value: option.value,
                            title: Text(option.label),
                            subtitle: option.subtitle == null
                                ? null
                                : Text(option.subtitle!),
                            contentPadding: EdgeInsets.zero,
                            shape: const RoundedRectangleBorder(
                              borderRadius: M3Shape.radiusLarge,
                            ),
                          ),
                      ],
                    ),
                  ),
                  if (widget.period != null) ...[
                    const SizedBox(height: 16),
                    _SheetSectionTitle(label: widget.periodLabel),
                    RadioGroup<String>(
                      groupValue: _period,
                      onChanged: (value) {
                        if (value == null) return;
                        setState(() => _period = value);
                      },
                      child: Column(
                        children: [
                          for (final option in MobileGridPeriod.options)
                            RadioListTile<String>(
                              key: _key('period-${option.value}'),
                              value: option.value,
                              title: Text(option.label),
                              contentPadding: EdgeInsets.zero,
                              shape: const RoundedRectangleBorder(
                                borderRadius: M3Shape.radiusLarge,
                              ),
                            ),
                        ],
                      ),
                    ),
                  ],
                  for (final filter in widget.filters) ...[
                    const SizedBox(height: 16),
                    _SheetSectionTitle(label: filter.label),
                    Wrap(
                      spacing: 8,
                      runSpacing: 8,
                      children: [
                        ChoiceChip(
                          key: _key('filter-${filter.id}-default'),
                          label: Text(filter.allLabel),
                          selected:
                              _filterValues[filter.id] == filter.defaultValue,
                          onSelected: (_) => setState(
                            () =>
                                _filterValues[filter.id] = filter.defaultValue,
                          ),
                        ),
                        for (final option in filter.options)
                          ChoiceChip(
                            key: _key('filter-${filter.id}-${option.value}'),
                            label: Text(option.label),
                            selected: _filterValues[filter.id] == option.value,
                            onSelected: (_) => setState(
                              () => _filterValues[filter.id] = option.value,
                            ),
                          ),
                      ],
                    ),
                  ],
                ],
              ),
            ),
            const SizedBox(height: 16),
            Row(
              children: [
                Expanded(
                  child: OutlinedButton(
                    onPressed: () => Navigator.of(context).pop(),
                    child: const Text('Cancel'),
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: FilledButton(
                    key: _key('apply-controls'),
                    onPressed: _apply,
                    child: const Text('Apply'),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  void _reset() {
    setState(() {
      _sort = widget.defaultSort;
      _period = widget.period == null ? null : widget.defaultPeriod;
      for (final filter in widget.filters) {
        _filterValues[filter.id] = filter.defaultValue;
      }
    });
  }

  void _apply() {
    if (_sort != widget.sort) widget.onSortChanged(_sort);
    if (widget.period != null && _period != widget.period) {
      widget.onPeriodChanged?.call(_period);
    }
    for (final filter in widget.filters) {
      final value = _filterValues[filter.id];
      if (value != filter.value) filter.onChanged(value);
    }
    Navigator.of(context).pop();
  }

  Key? _key(String suffix) {
    final prefix = widget.keyPrefix;
    return prefix == null ? null : Key('$prefix-$suffix');
  }
}

class _SheetSectionTitle extends StatelessWidget {
  const _SheetSectionTitle({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.only(bottom: 6),
      child: Text(
        label,
        style: theme.textTheme.titleSmall?.copyWith(
          color: theme.colorScheme.onSurfaceVariant,
        ),
      ),
    );
  }
}

class _ActiveGridControl {
  const _ActiveGridControl({required this.icon, required this.label});

  final IconData icon;
  final String label;
}

String _optionLabel(List<MobileGridControlOption> options, String value) {
  for (final option in options) {
    if (option.value == value) return option.label;
  }
  return value;
}
