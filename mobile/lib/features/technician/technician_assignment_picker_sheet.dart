import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/api/api_exception.dart';
import 'technician_repository.dart';

/// Server-paged assignment selector used by technician Scan / Add.
///
/// Never reads the management work-order collection and never loads a large
/// page to filter on-device. Search, status, assignment authorization and
/// paging all remain on `/technician/assignments`.
class TechnicianAssignmentPickerSheet extends ConsumerStatefulWidget {
  const TechnicianAssignmentPickerSheet({super.key});

  @override
  ConsumerState<TechnicianAssignmentPickerSheet> createState() =>
      _TechnicianAssignmentPickerSheetState();
}

class _TechnicianAssignmentPickerSheetState
    extends ConsumerState<TechnicianAssignmentPickerSheet> {
  static const _take = 20;
  final _searchController = TextEditingController();
  late Future<TechnicianAssignmentPage> _future;
  int _skip = 0;
  String? _status;

  @override
  void initState() {
    super.initState();
    _future = _load();
  }

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  Future<TechnicianAssignmentPage> _load() => ref
      .read(technicianRepositoryProvider)
      .list(
        area: 'assignments',
        search: _searchController.text.trim(),
        status: _status,
        skip: _skip,
        take: _take,
      );

  void _reload({int? skip}) {
    _skip = skip ?? 0;
    setState(() => _future = _load());
  }

  @override
  Widget build(BuildContext context) => SafeArea(
    child: Padding(
      padding: EdgeInsets.only(
        left: 16,
        right: 16,
        top: 12,
        bottom: MediaQuery.viewInsetsOf(context).bottom + 16,
      ),
      child: Column(
        children: [
          Text(
            'Choose assigned work',
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 4),
          Text(
            'The scan will stay attached to this assignment.',
            style: Theme.of(context).textTheme.bodyMedium,
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              Expanded(
                child: SearchBar(
                  controller: _searchController,
                  leading: const Icon(Symbols.search_rounded),
                  hintText: 'Search assigned work',
                  onSubmitted: (_) => _reload(),
                ),
              ),
              const SizedBox(width: 8),
              PopupMenuButton<String?>(
                tooltip: 'Filter by status',
                icon: const Icon(Symbols.filter_list_rounded),
                initialValue: _status,
                onSelected: (value) {
                  _status = value;
                  _reload();
                },
                itemBuilder: (_) => const [
                  PopupMenuItem(value: null, child: Text('All open')),
                  PopupMenuItem(value: 'New', child: Text('New')),
                  PopupMenuItem(value: 'Scheduled', child: Text('Scheduled')),
                  PopupMenuItem(
                    value: 'InProgress',
                    child: Text('In progress'),
                  ),
                  PopupMenuItem(
                    value: 'WaitingParts',
                    child: Text('Waiting for parts'),
                  ),
                ],
              ),
            ],
          ),
          const SizedBox(height: 8),
          Expanded(
            child: FutureBuilder<TechnicianAssignmentPage>(
              future: _future,
              builder: (context, snapshot) {
                if (snapshot.connectionState != ConnectionState.done) {
                  return const Center(child: CircularProgressIndicator());
                }
                if (snapshot.hasError || snapshot.data == null) {
                  final message = snapshot.error is ApiException
                      ? (snapshot.error! as ApiException).message
                      : 'Assigned work is unavailable.';
                  return Center(
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(message, textAlign: TextAlign.center),
                        const SizedBox(height: 12),
                        FilledButton(
                          onPressed: _reload,
                          child: const Text('Try again'),
                        ),
                      ],
                    ),
                  );
                }

                final page = snapshot.data!;
                if (page.items.isEmpty) {
                  return const Center(
                    child: Text('No matching assigned repairs.'),
                  );
                }
                return ListView(
                  children: [
                    for (final assignment in page.items)
                      ListTile(
                        leading: const Icon(Symbols.build_rounded),
                        title: Text(assignment.title),
                        subtitle: Text(
                          [assignment.address, assignment.unit]
                              .whereType<String>()
                              .where((value) => value.isNotEmpty)
                              .join(' · '),
                        ),
                        trailing: const Icon(Symbols.chevron_right_rounded),
                        onTap: () => Navigator.pop(context, assignment),
                      ),
                    if (page.totalCount > page.take)
                      Padding(
                        padding: const EdgeInsets.only(top: 8),
                        child: Row(
                          mainAxisAlignment: MainAxisAlignment.end,
                          children: [
                            OutlinedButton(
                              onPressed: page.skip == 0
                                  ? null
                                  : () => _reload(
                                      skip: page.skip > page.take
                                          ? page.skip - page.take
                                          : 0,
                                    ),
                              child: const Text('Previous'),
                            ),
                            const SizedBox(width: 8),
                            OutlinedButton(
                              onPressed:
                                  page.skip + page.items.length >=
                                      page.totalCount
                                  ? null
                                  : () => _reload(skip: page.skip + page.take),
                              child: const Text('Next'),
                            ),
                          ],
                        ),
                      ),
                  ],
                );
              },
            ),
          ),
        ],
      ),
    ),
  );
}

Future<TechnicianAssignmentSummary?> showTechnicianAssignmentPicker(
  BuildContext context,
) => showModalBottomSheet<TechnicianAssignmentSummary>(
  context: context,
  isScrollControlled: true,
  useSafeArea: true,
  builder: (_) => const FractionallySizedBox(
    heightFactor: 0.85,
    child: TechnicianAssignmentPickerSheet(),
  ),
);
