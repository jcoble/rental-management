import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/models.dart';
import '../../core/api/api_exception.dart';
import 'work_orders_repository.dart';

// ── Helpers ───────────────────────────────────────────────────────────────────

const _months = [
  '', 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
  'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
];

String _fmtDate(DateTime d) =>
    '${_months[d.month]} ${d.day}, ${d.year}';

String _fmtCost(double cost) {
  final parts = cost.toStringAsFixed(2).split('.');
  final intPart = parts[0];
  final buf = StringBuffer();
  final len = intPart.length;
  for (var i = 0; i < len; i++) {
    if (i > 0 && (len - i) % 3 == 0) buf.write(',');
    buf.write(intPart[i]);
  }
  return '\$$buf.${parts[1]}';
}

/// All valid status transitions, in display order.
///
/// WorkOrderStatus values: New | Scheduled | InProgress | WaitingParts |
/// Completed | Cancelled
const _allStatuses = [
  'New',
  'Scheduled',
  'InProgress',
  'WaitingParts',
  'Completed',
  'Cancelled',
];

/// Human-readable labels for status values.
String _statusLabel(String s) {
  switch (s) {
    case 'InProgress':
      return 'In Progress';
    case 'WaitingParts':
      return 'Waiting Parts';
    default:
      return s;
  }
}

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

/// Detail screen for a single work order.
///
/// Shows full details, an edit bottom sheet, and status-transition buttons
/// for all valid [_allStatuses] except the current one.
class WorkOrderDetailScreen extends ConsumerStatefulWidget {
  const WorkOrderDetailScreen({super.key, required this.workOrderId});

  final int workOrderId;

  @override
  ConsumerState<WorkOrderDetailScreen> createState() =>
      _WorkOrderDetailScreenState();
}

class _WorkOrderDetailScreenState
    extends ConsumerState<WorkOrderDetailScreen> {
  bool _statusUpdating = false;

  Future<void> _transitionStatus(String status) async {
    setState(() => _statusUpdating = true);
    try {
      await ref
          .read(workOrderDetailProvider(widget.workOrderId).notifier)
          .updateStatus(status);
    } finally {
      if (mounted) setState(() => _statusUpdating = false);
    }
  }

  void _showEditSheet(BuildContext context, WorkOrder wo) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _EditWorkOrderSheet(
        workOrder: wo,
        onSaved: () {
          ref
              .read(workOrderDetailProvider(widget.workOrderId).notifier)
              .refresh();
        },
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final woAsync =
        ref.watch(workOrderDetailProvider(widget.workOrderId));
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Work Order'),
        actions: [
          woAsync.whenOrNull(
                data: (wo) => IconButton(
                  icon: const Icon(Icons.edit_outlined),
                  tooltip: 'Edit',
                  onPressed: () => _showEditSheet(context, wo),
                ),
              ) ??
              const SizedBox.shrink(),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: () => ref
            .read(workOrderDetailProvider(widget.workOrderId).notifier)
            .refresh(),
        child: woAsync.when(
          loading: () =>
              const Center(child: CircularProgressIndicator()),
          error: (e, _) => Center(
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(Icons.error_outline,
                      size: 40, color: colorScheme.error),
                  const SizedBox(height: 12),
                  Text(
                    e is ApiException ? e.message : e.toString(),
                    textAlign: TextAlign.center,
                    style: TextStyle(color: colorScheme.error),
                  ),
                  const SizedBox(height: 16),
                  FilledButton.tonal(
                    onPressed: () => ref
                        .read(workOrderDetailProvider(widget.workOrderId)
                            .notifier)
                        .refresh(),
                    child: const Text('Retry'),
                  ),
                ],
              ),
            ),
          ),
          data: (wo) => _DetailBody(
            workOrder: wo,
            colorScheme: colorScheme,
            theme: theme,
            statusUpdating: _statusUpdating,
            onTransition: _transitionStatus,
          ),
        ),
      ),
    );
  }
}

// ── Detail body ───────────────────────────────────────────────────────────────

class _DetailBody extends StatelessWidget {
  const _DetailBody({
    required this.workOrder,
    required this.colorScheme,
    required this.theme,
    required this.statusUpdating,
    required this.onTransition,
  });

  final WorkOrder workOrder;
  final ColorScheme colorScheme;
  final ThemeData theme;
  final bool statusUpdating;
  final void Function(String) onTransition;

  @override
  Widget build(BuildContext context) {
    final otherStatuses =
        _allStatuses.where((s) => s != workOrder.status).toList();

    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      children: [
        // ── Title + chips ──────────────────────────────────────────────
        Text(
          workOrder.title,
          style: theme.textTheme.headlineSmall
              ?.copyWith(fontWeight: FontWeight.w700),
        ),
        const SizedBox(height: 8),
        Row(
          children: [
            _PriorityChip(
                priority: workOrder.priority, colorScheme: colorScheme),
            const SizedBox(width: 8),
            _StatusChip(
                status: workOrder.status, colorScheme: colorScheme),
            const SizedBox(width: 8),
            _CategoryChip(
                category: workOrder.category, colorScheme: colorScheme),
          ],
        ),

        const SizedBox(height: 20),

        // ── Description ────────────────────────────────────────────────
        _SectionLabel(label: 'Description', theme: theme),
        const SizedBox(height: 4),
        Text(workOrder.description, style: theme.textTheme.bodyMedium),

        const SizedBox(height: 20),

        // ── Details grid ───────────────────────────────────────────────
        _SectionLabel(label: 'Details', theme: theme),
        const SizedBox(height: 8),
        _DetailGrid(workOrder: workOrder, colorScheme: colorScheme, theme: theme),

        const SizedBox(height: 20),

        // ── Status transitions ─────────────────────────────────────────
        _SectionLabel(label: 'Update Status', theme: theme),
        const SizedBox(height: 8),
        if (statusUpdating)
          const Center(child: CircularProgressIndicator())
        else
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: otherStatuses
                .map(
                  (s) => OutlinedButton(
                    onPressed: () => onTransition(s),
                    style: OutlinedButton.styleFrom(
                      padding: const EdgeInsets.symmetric(
                          horizontal: 14, vertical: 8),
                      minimumSize: Size.zero,
                      tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                    ),
                    child: Text(_statusLabel(s)),
                  ),
                )
                .toList(),
          ),
      ],
    );
  }
}

class _DetailGrid extends StatelessWidget {
  const _DetailGrid({
    required this.workOrder,
    required this.colorScheme,
    required this.theme,
  });

  final WorkOrder workOrder;
  final ColorScheme colorScheme;
  final ThemeData theme;

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: BoxDecoration(
        color: colorScheme.surfaceContainerLowest,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: colorScheme.outlineVariant),
      ),
      child: Column(
        children: [
          _DetailRow(
            label: 'Property',
            value: workOrder.propertyName ?? 'Property #${workOrder.propertyId}',
            theme: theme,
            colorScheme: colorScheme,
          ),
          if (workOrder.unitNumber != null)
            _DetailRow(
              label: 'Unit',
              value: workOrder.unitNumber!,
              theme: theme,
              colorScheme: colorScheme,
            ),
          if (workOrder.tenantName != null)
            _DetailRow(
              label: 'Tenant',
              value: workOrder.tenantName!,
              theme: theme,
              colorScheme: colorScheme,
            ),
          if (workOrder.vendorName != null)
            _DetailRow(
              label: 'Vendor',
              value: workOrder.vendorName!,
              theme: theme,
              colorScheme: colorScheme,
            ),
          _DetailRow(
            label: 'Requested',
            value: _fmtDate(workOrder.requestedAt),
            theme: theme,
            colorScheme: colorScheme,
          ),
          if (workOrder.scheduledFor != null)
            _DetailRow(
              label: 'Scheduled for',
              value: _fmtDate(workOrder.scheduledFor!),
              theme: theme,
              colorScheme: colorScheme,
            ),
          if (workOrder.completedAt != null)
            _DetailRow(
              label: 'Completed',
              value: _fmtDate(workOrder.completedAt!),
              theme: theme,
              colorScheme: colorScheme,
            ),
          if (workOrder.estimatedCost != null)
            _DetailRow(
              label: 'Est. cost',
              value: _fmtCost(workOrder.estimatedCost!),
              theme: theme,
              colorScheme: colorScheme,
            ),
          if (workOrder.actualCost != null)
            _DetailRow(
              label: 'Actual cost',
              value: _fmtCost(workOrder.actualCost!),
              theme: theme,
              colorScheme: colorScheme,
              isLast: true,
            )
          else
            _DetailRow(
              label: 'Last updated',
              value: _fmtDate(workOrder.updatedAt),
              theme: theme,
              colorScheme: colorScheme,
              isLast: true,
            ),
        ],
      ),
    );
  }
}

class _DetailRow extends StatelessWidget {
  const _DetailRow({
    required this.label,
    required this.value,
    required this.theme,
    required this.colorScheme,
    this.isLast = false,
  });

  final String label;
  final String value;
  final ThemeData theme;
  final ColorScheme colorScheme;
  final bool isLast;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
      decoration: BoxDecoration(
        border: isLast
            ? null
            : Border(
                bottom: BorderSide(color: colorScheme.outlineVariant),
              ),
      ),
      child: Row(
        children: [
          SizedBox(
            width: 110,
            child: Text(
              label,
              style: theme.textTheme.bodySmall?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
          ),
          Expanded(
            child: Text(
              value,
              style: theme.textTheme.bodyMedium
                  ?.copyWith(fontWeight: FontWeight.w500),
            ),
          ),
        ],
      ),
    );
  }
}

class _SectionLabel extends StatelessWidget {
  const _SectionLabel({required this.label, required this.theme});

  final String label;
  final ThemeData theme;

  @override
  Widget build(BuildContext context) {
    return Text(
      label,
      style: theme.textTheme.labelLarge?.copyWith(
        fontWeight: FontWeight.w700,
        color: Theme.of(context).colorScheme.onSurfaceVariant,
        letterSpacing: 0.5,
      ),
    );
  }
}

// ── Chip helpers (same as list screen, kept local) ────────────────────────────

class _PriorityChip extends StatelessWidget {
  const _PriorityChip(
      {required this.priority, required this.colorScheme});

  final String priority;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      decoration: BoxDecoration(
        color: _priorityColor(priority, colorScheme),
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        priority,
        style: TextStyle(
          fontSize: 12,
          fontWeight: FontWeight.w700,
          color: _priorityTextColor(priority, colorScheme),
        ),
      ),
    );
  }
}

class _StatusChip extends StatelessWidget {
  const _StatusChip(
      {required this.status, required this.colorScheme});

  final String status;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      decoration: BoxDecoration(
        color: _statusColor(status, colorScheme),
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        _statusLabel(status),
        style: TextStyle(
          fontSize: 12,
          fontWeight: FontWeight.w600,
          color: _statusTextColor(status, colorScheme),
        ),
      ),
    );
  }
}

class _CategoryChip extends StatelessWidget {
  const _CategoryChip(
      {required this.category, required this.colorScheme});

  final String category;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      decoration: BoxDecoration(
        color: colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        category,
        style: TextStyle(
          fontSize: 12,
          color: colorScheme.onSurfaceVariant,
        ),
      ),
    );
  }
}

// ── Edit Work Order Bottom Sheet ──────────────────────────────────────────────

class _EditWorkOrderSheet extends ConsumerStatefulWidget {
  const _EditWorkOrderSheet({
    required this.workOrder,
    required this.onSaved,
  });

  final WorkOrder workOrder;
  final VoidCallback onSaved;

  @override
  ConsumerState<_EditWorkOrderSheet> createState() =>
      _EditWorkOrderSheetState();
}

class _EditWorkOrderSheetState
    extends ConsumerState<_EditWorkOrderSheet> {
  late final TextEditingController _titleCtrl;
  late final TextEditingController _descCtrl;
  late String _priority;
  late String _category;

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
  void initState() {
    super.initState();
    _titleCtrl =
        TextEditingController(text: widget.workOrder.title);
    _descCtrl =
        TextEditingController(text: widget.workOrder.description);
    _priority = _priorities.contains(widget.workOrder.priority)
        ? widget.workOrder.priority
        : 'Normal';
    _category = _categories.contains(widget.workOrder.category)
        ? widget.workOrder.category
        : 'General';
  }

  @override
  void dispose() {
    _titleCtrl.dispose();
    _descCtrl.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (_titleCtrl.text.trim().isEmpty ||
        _descCtrl.text.trim().isEmpty) {
      setState(() => _error = 'Title and description are required.');
      return;
    }

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      await ref.read(workOrdersRepositoryProvider).updateWorkOrder(
        widget.workOrder.id,
        {
          'title': _titleCtrl.text.trim(),
          'description': _descCtrl.text.trim(),
          'priority': _priority,
          'category': _category,
        },
      );

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
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottomPadding),
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
                    'Edit Work Order',
                    style: theme.textTheme.titleLarge
                        ?.copyWith(fontWeight: FontWeight.w700),
                  ),
                ),
                IconButton(
                  icon: const Icon(Icons.close),
                  onPressed: () => Navigator.of(context).pop(),
                ),
              ],
            ),
            const SizedBox(height: 16),

            // Title
            TextFormField(
              controller: _titleCtrl,
              textInputAction: TextInputAction.next,
              decoration: const InputDecoration(labelText: 'Title'),
            ),
            const SizedBox(height: 12),

            // Description
            TextFormField(
              controller: _descCtrl,
              maxLines: 3,
              textInputAction: TextInputAction.newline,
              decoration: const InputDecoration(labelText: 'Description'),
            ),
            const SizedBox(height: 12),

            // Priority + Category row
            Row(
              children: [
                Expanded(
                  child: DropdownButtonFormField<String>(
                    initialValue: _priority,
                    decoration:
                        const InputDecoration(labelText: 'Priority'),
                    items: _priorities
                        .map((p) => DropdownMenuItem(
                            value: p, child: Text(p)))
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
                    decoration:
                        const InputDecoration(labelText: 'Category'),
                    items: _categories
                        .map((c) => DropdownMenuItem(
                            value: c, child: Text(c)))
                        .toList(),
                    onChanged: (v) {
                      if (v != null) setState(() => _category = v);
                    },
                  ),
                ),
              ],
            ),

            if (_error != null) ...[
              const SizedBox(height: 12),
              Container(
                padding: const EdgeInsets.symmetric(
                    horizontal: 12, vertical: 10),
                decoration: BoxDecoration(
                  color: colorScheme.errorContainer,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Text(
                  _error!,
                  style: TextStyle(
                      color: colorScheme.onErrorContainer, fontSize: 13),
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
                  : const Text('Save Changes'),
            ),
          ],
        ),
      ),
    );
  }
}
