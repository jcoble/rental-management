import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';

import '../../core/models/models.dart';
import '../../core/api/api_exception.dart';
import 'work_orders_repository.dart';
import 'work_order_detail_screen.dart';

// ── Helpers ───────────────────────────────────────────────────────────────────

Color _priorityColor(String priority, ColorScheme cs) {
  switch (priority.toLowerCase()) {
    case 'emergency':
      return cs.error;
    case 'high':
      return cs.errorContainer;
    case 'normal':
      return cs.secondaryContainer;
    default:
      return cs.surfaceContainerHighest;
  }
}

Color _priorityTextColor(String priority, ColorScheme cs) {
  switch (priority.toLowerCase()) {
    case 'emergency':
      return cs.onError;
    case 'high':
      return cs.onErrorContainer;
    case 'normal':
      return cs.onSecondaryContainer;
    default:
      return cs.onSurfaceVariant;
  }
}

Color _statusColor(String status, ColorScheme cs) {
  switch (status.toLowerCase()) {
    case 'completed':
      return cs.primaryContainer;
    case 'inprogress':
      return cs.tertiaryContainer;
    case 'cancelled':
      return cs.surfaceContainerHighest;
    case 'new':
      return cs.secondaryContainer;
    default:
      return cs.surfaceContainerHighest;
  }
}

Color _statusTextColor(String status, ColorScheme cs) {
  switch (status.toLowerCase()) {
    case 'completed':
      return cs.onPrimaryContainer;
    case 'inprogress':
      return cs.onTertiaryContainer;
    case 'cancelled':
      return cs.onSurfaceVariant;
    case 'new':
      return cs.onSecondaryContainer;
    default:
      return cs.onSurfaceVariant;
  }
}

// ── Entry widget ──────────────────────────────────────────────────────────────

/// Work orders list screen with open/all filter and create FAB.
class WorkOrdersScreen extends ConsumerStatefulWidget {
  const WorkOrdersScreen({super.key});

  @override
  ConsumerState<WorkOrdersScreen> createState() => _WorkOrdersScreenState();
}

class _WorkOrdersScreenState extends ConsumerState<WorkOrdersScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(workOrdersProvider.notifier).load());
  }

  Future<void> _refresh() => ref.read(workOrdersProvider.notifier).refresh();

  void _openDetail(BuildContext context, WorkOrder wo) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => WorkOrderDetailScreen(workOrderId: wo.id),
      ),
    );
  }

  void _showCreateSheet(BuildContext context) {
    ref.read(propertiesForWoProvider.notifier).load();
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _CreateWorkOrderSheet(
        onSaved: () => ref.read(workOrdersProvider.notifier).refresh(),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final workOrdersAsync = ref.watch(workOrdersProvider);
    final notifier = ref.read(workOrdersProvider.notifier);
    final currentFilter = notifier.filter;
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Maintenance'),
        actions: [
          // Open / All toggle
          Padding(
            padding: const EdgeInsets.only(right: 8),
            child: SegmentedButton<WorkOrderFilter>(
              segments: const [
                ButtonSegment(value: WorkOrderFilter.open, label: Text('Open')),
                ButtonSegment(value: WorkOrderFilter.all, label: Text('All')),
              ],
              selected: {currentFilter},
              onSelectionChanged: (s) {
                notifier.setFilter(s.first);
              },
              style: ButtonStyle(
                tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                visualDensity: VisualDensity.compact,
              ),
            ),
          ),
        ],
      ),
      floatingActionButton: FloatingActionButton(
        heroTag: 'work-orders-fab',
        onPressed: () => _showCreateSheet(context),
        tooltip: 'New work order',
        child: const Icon(Icons.add),
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: workOrdersAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(
            message: e is ApiException ? e.message : e.toString(),
            onRetry: _refresh,
          ),
          data: (list) {
            if (list.isEmpty) {
              return _EmptyBody(filter: currentFilter);
            }
            return ListView.separated(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 88),
              itemCount: list.length,
              separatorBuilder: (context, index) => const SizedBox(height: 8),
              itemBuilder: (ctx, i) => _WorkOrderCard(
                workOrder: list[i],
                colorScheme: colorScheme,
                theme: theme,
                onTap: () => _openDetail(ctx, list[i]),
              ),
            );
          },
        ),
      ),
    );
  }
}

// ── Work Order Card ───────────────────────────────────────────────────────────

class _WorkOrderCard extends StatelessWidget {
  const _WorkOrderCard({
    required this.workOrder,
    required this.colorScheme,
    required this.theme,
    required this.onTap,
  });

  final WorkOrder workOrder;
  final ColorScheme colorScheme;
  final ThemeData theme;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(14),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: Text(
                      workOrder.title,
                      style: theme.textTheme.titleSmall?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  const SizedBox(width: 8),
                  _PriorityChip(
                    priority: workOrder.priority,
                    colorScheme: colorScheme,
                  ),
                ],
              ),
              const SizedBox(height: 4),
              if (workOrder.propertyName != null)
                Text(
                  workOrder.propertyName!,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: colorScheme.onSurfaceVariant,
                  ),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
              const SizedBox(height: 8),
              Row(
                children: [
                  _StatusChip(
                    status: workOrder.status,
                    colorScheme: colorScheme,
                  ),
                  const SizedBox(width: 8),
                  _CategoryChip(
                    category: workOrder.category,
                    colorScheme: colorScheme,
                  ),
                  const Spacer(),
                  Icon(
                    Icons.chevron_right,
                    size: 18,
                    color: colorScheme.onSurfaceVariant,
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

class _PriorityChip extends StatelessWidget {
  const _PriorityChip({required this.priority, required this.colorScheme});

  final String priority;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
      decoration: BoxDecoration(
        color: _priorityColor(priority, colorScheme),
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        priority,
        style: TextStyle(
          fontSize: 11,
          fontWeight: FontWeight.w700,
          color: _priorityTextColor(priority, colorScheme),
        ),
      ),
    );
  }
}

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.status, required this.colorScheme});

  final String status;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
      decoration: BoxDecoration(
        color: _statusColor(status, colorScheme),
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        status,
        style: TextStyle(
          fontSize: 11,
          fontWeight: FontWeight.w600,
          color: _statusTextColor(status, colorScheme),
        ),
      ),
    );
  }
}

class _CategoryChip extends StatelessWidget {
  const _CategoryChip({required this.category, required this.colorScheme});

  final String category;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
      decoration: BoxDecoration(
        color: colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        category,
        style: TextStyle(fontSize: 11, color: colorScheme.onSurfaceVariant),
      ),
    );
  }
}

// ── Empty / Error ─────────────────────────────────────────────────────────────

class _EmptyBody extends StatelessWidget {
  const _EmptyBody({required this.filter});

  final WorkOrderFilter filter;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    final msg = filter == WorkOrderFilter.open
        ? 'No open work orders'
        : 'No work orders yet';
    final sub = filter == WorkOrderFilter.open
        ? 'All caught up! Tap + to create one.'
        : 'Tap + to create a work order.';
    return ListView(
      children: [
        SizedBox(
          height: 300,
          child: Center(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(
                  Icons.build_outlined,
                  size: 48,
                  color: colorScheme.onSurfaceVariant,
                ),
                const SizedBox(height: 12),
                Text(
                  msg,
                  style: Theme.of(context).textTheme.titleMedium?.copyWith(
                    color: colorScheme.onSurfaceVariant,
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  sub,
                  style: TextStyle(color: colorScheme.onSurfaceVariant),
                ),
              ],
            ),
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
    final colorScheme = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.error_outline, size: 40, color: colorScheme.error),
            const SizedBox(height: 12),
            Text(
              message,
              textAlign: TextAlign.center,
              style: TextStyle(color: colorScheme.error),
            ),
            const SizedBox(height: 16),
            FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}

// ── Create Work Order Bottom Sheet ────────────────────────────────────────────

class _CreateWorkOrderSheet extends ConsumerStatefulWidget {
  const _CreateWorkOrderSheet({required this.onSaved});

  final VoidCallback onSaved;

  @override
  ConsumerState<_CreateWorkOrderSheet> createState() =>
      _CreateWorkOrderSheetState();
}

class _CreateWorkOrderSheetState extends ConsumerState<_CreateWorkOrderSheet> {
  final _formKey = GlobalKey<FormState>();
  final _titleCtrl = TextEditingController();
  final _descCtrl = TextEditingController();

  int? _selectedPropertyId;
  String _priority = 'Normal';
  String _category = 'General';
  Uint8List? _photoBytes;
  String? _photoName;
  String? _photoContentType;

  bool _saving = false;
  String? _error;

  static const _priorities = ['Low', 'Normal', 'High', 'Emergency'];
  static const _categories = [
    'General',
    'Plumbing',
    'Electrical',
    'HVAC',
    'Appliance',
    'Structural',
    'Exterior',
    'Landscaping',
    'Cleaning',
    'Safety',
    'Other',
  ];

  @override
  void dispose() {
    _titleCtrl.dispose();
    _descCtrl.dispose();
    super.dispose();
  }

  Future<void> _pickPhoto(ImageSource source) async {
    final picked = await ImagePicker().pickImage(
      source: source,
      imageQuality: 80,
      maxWidth: 1600,
      maxHeight: 1600,
    );
    if (picked == null) return;
    final bytes = Uint8List.fromList(await picked.readAsBytes());
    if (!mounted) return;
    setState(() {
      _photoBytes = bytes;
      _photoName = picked.name;
      _photoContentType = _mimeFromExtension(picked.name);
    });
  }

  String _mimeFromExtension(String filename) {
    final lower = filename.toLowerCase();
    if (lower.endsWith('.png')) return 'image/png';
    if (lower.endsWith('.webp')) return 'image/webp';
    if (lower.endsWith('.heic')) return 'image/heic';
    return 'image/jpeg';
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final repo = ref.read(workOrdersRepositoryProvider);
      final created = await repo.createWorkOrder({
        'propertyId': _selectedPropertyId,
        'title': _titleCtrl.text.trim(),
        'description': _descCtrl.text.trim(),
        'priority': _priority,
        'category': _category,
      });
      final photoBytes = _photoBytes;
      final photoName = _photoName;
      final photoContentType = _photoContentType;
      if (photoBytes != null && photoName != null && photoContentType != null) {
        await repo.uploadWorkOrderPhoto(
          workOrderId: created.id,
          bytes: photoBytes,
          fileName: photoName,
          contentType: photoContentType,
        );
      }

      widget.onSaved();
      if (mounted) Navigator.of(context).pop();
    } on ApiException catch (e) {
      setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final propertiesAsync = ref.watch(propertiesForWoProvider);
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottomPadding),
      child: Form(
        key: _formKey,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // Header
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'New Work Order',
                      style: theme.textTheme.titleLarge?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                  IconButton(
                    icon: const Icon(Icons.close),
                    onPressed: () => Navigator.of(context).pop(),
                  ),
                ],
              ),
              const SizedBox(height: 16),

              // Property dropdown
              propertiesAsync.when(
                loading: () => const Center(
                  child: Padding(
                    padding: EdgeInsets.symmetric(vertical: 12),
                    child: CircularProgressIndicator(),
                  ),
                ),
                error: (e, _) => Text(
                  'Could not load properties: ${e is ApiException ? e.message : e}',
                  style: TextStyle(color: colorScheme.error, fontSize: 13),
                ),
                data: (properties) => DropdownButtonFormField<int>(
                  initialValue: _selectedPropertyId,
                  decoration: const InputDecoration(labelText: 'Property'),
                  items: properties
                      .map(
                        (p) => DropdownMenuItem(
                          value: p.id,
                          child: Text(p.name, overflow: TextOverflow.ellipsis),
                        ),
                      )
                      .toList(),
                  onChanged: (v) => setState(() => _selectedPropertyId = v),
                  validator: (v) =>
                      v == null ? 'Please select a property' : null,
                ),
              ),
              const SizedBox(height: 12),

              // Title
              TextFormField(
                controller: _titleCtrl,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(labelText: 'Title'),
                validator: (v) => (v == null || v.trim().isEmpty)
                    ? 'Title is required'
                    : null,
              ),
              const SizedBox(height: 12),

              // Description
              TextFormField(
                controller: _descCtrl,
                maxLines: 3,
                textInputAction: TextInputAction.newline,
                decoration: const InputDecoration(labelText: 'Description'),
                validator: (v) => (v == null || v.trim().isEmpty)
                    ? 'Description is required'
                    : null,
              ),
              const SizedBox(height: 12),

              // Priority + Category row
              Row(
                children: [
                  Expanded(
                    child: DropdownButtonFormField<String>(
                      initialValue: _priority,
                      decoration: const InputDecoration(labelText: 'Priority'),
                      items: _priorities
                          .map(
                            (p) => DropdownMenuItem(value: p, child: Text(p)),
                          )
                          .toList(),
                      onChanged: (v) {
                        if (v != null) setState(() => _priority = v);
                      },
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: DropdownButtonFormField<String>(
                      initialValue: _category,
                      decoration: const InputDecoration(labelText: 'Category'),
                      items: _categories
                          .map(
                            (c) => DropdownMenuItem(value: c, child: Text(c)),
                          )
                          .toList(),
                      onChanged: (v) {
                        if (v != null) setState(() => _category = v);
                      },
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 12),

              if (_photoBytes != null) ...[
                ClipRRect(
                  borderRadius: BorderRadius.circular(12),
                  child: Image.memory(
                    _photoBytes!,
                    height: 140,
                    fit: BoxFit.cover,
                    width: double.infinity,
                  ),
                ),
                const SizedBox(height: 8),
              ],
              Row(
                children: [
                  Expanded(
                    child: OutlinedButton.icon(
                      onPressed: _saving
                          ? null
                          : () => _pickPhoto(ImageSource.camera),
                      icon: const Icon(Icons.camera_alt_outlined),
                      label: Text(
                        _photoBytes == null ? 'Take photo' : 'Retake',
                      ),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: OutlinedButton.icon(
                      onPressed: _saving
                          ? null
                          : () => _pickPhoto(ImageSource.gallery),
                      icon: const Icon(Icons.photo_library_outlined),
                      label: const Text('Choose'),
                    ),
                  ),
                ],
              ),

              if (_error != null) ...[
                const SizedBox(height: 12),
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 12,
                    vertical: 10,
                  ),
                  decoration: BoxDecoration(
                    color: colorScheme.errorContainer,
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Text(
                    _error!,
                    style: TextStyle(
                      color: colorScheme.onErrorContainer,
                      fontSize: 13,
                    ),
                  ),
                ),
              ],

              const SizedBox(height: 20),

              FilledButton(
                onPressed: _saving ? null : _submit,
                child: _saving
                    ? const SizedBox(
                        height: 20,
                        width: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Text('Save Work Order'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
