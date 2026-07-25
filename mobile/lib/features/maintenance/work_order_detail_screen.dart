import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:url_launcher/url_launcher.dart';
import 'package:uuid/uuid.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../../core/models/models.dart' hide Vendor;
import '../../core/utils/date_wire.dart';
import '../activity/activity_history_screen.dart';
import '../properties/properties_repository.dart';
import '../tenants/tenants_repository.dart';
import '../vendors/dispatch_vendor_sheet.dart';
import '../vendors/rate_vendor_sheet.dart';
import '../vendors/vendors_repository.dart';
import 'work_order_form_shell.dart';
import 'work_order_timeline.dart';
import 'work_orders_repository.dart';

// ── Helpers ───────────────────────────────────────────────────────────────────

const _months = [
  '',
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec',
];

String _fmtDate(DateTime d) => '${_months[d.month]} ${d.day}, ${d.year}';

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

/// Manager-visible statuses, in display order.
const _allStatuses = [
  'New',
  'Scheduled',
  'InProgress',
  'WaitingParts',
  'Completed',
  'Cancelled',
];

const _assignedTechnicianStatusTargets = <String, List<String>>{
  'New': ['Scheduled', 'InProgress', 'OnHold', 'Cancelled', 'Completed'],
  'Scheduled': [
    'InProgress',
    'WaitingParts',
    'OnHold',
    'Cancelled',
    'Completed',
  ],
  'InProgress': ['WaitingParts', 'OnHold', 'Completed'],
  'WaitingParts': ['InProgress', 'OnHold', 'Completed'],
  'OnHold': [
    'Scheduled',
    'InProgress',
    'WaitingParts',
    'Cancelled',
    'Completed',
  ],
  'Completed': [],
  'Cancelled': [],
};

List<String> _statusActionTargets(
  String currentStatus, {
  required bool restrictToAssignedTechnicianTransitions,
}) {
  if (restrictToAssignedTechnicianTransitions) {
    return _assignedTechnicianStatusTargets[currentStatus] ?? const [];
  }
  return _allStatuses.where((status) => status != currentStatus).toList();
}

/// Human-readable labels for status values (delegates to the shared helper).
String _statusLabel(String s) => workOrderStatusLabel(s);

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

String _mimeFromExtension(String filename) {
  final lower = filename.toLowerCase();
  if (lower.endsWith('.png')) return 'image/png';
  if (lower.endsWith('.webp')) return 'image/webp';
  if (lower.endsWith('.heic')) return 'image/heic';
  return 'image/jpeg';
}

// ── Entry widget ──────────────────────────────────────────────────────────────

/// Detail screen for a single work order.
///
/// Shows full details, a live status timeline, a before/after photo strip with
/// camera/gallery capture, a tap-to-navigate action, and status transitions
/// (each able to record an optional note on the timeline).
class WorkOrderDetailScreen extends ConsumerStatefulWidget {
  const WorkOrderDetailScreen({super.key, required this.workOrderId});

  final int workOrderId;

  @override
  ConsumerState<WorkOrderDetailScreen> createState() =>
      _WorkOrderDetailScreenState();
}

class _WorkOrderDetailScreenState extends ConsumerState<WorkOrderDetailScreen> {
  bool _statusUpdating = false;
  bool _uploadingPhoto = false;

  Future<void> _refresh() =>
      ref.read(workOrderDetailProvider(widget.workOrderId).notifier).refresh();

  void _showError(Object error) {
    if (!mounted) return;
    final message = error is ApiException ? error.message : error.toString();
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(message)));
  }

  Future<void> _changeStatus(String status) async {
    final note = await showModalBottomSheet<String?>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _StatusNoteSheet(status: status),
    );
    // A null result means the sheet was dismissed without confirming.
    if (note == null || !mounted) return;

    setState(() => _statusUpdating = true);
    try {
      await ref
          .read(workOrderDetailProvider(widget.workOrderId).notifier)
          .updateStatus(status, note: note.isEmpty ? null : note);
    } finally {
      if (mounted) setState(() => _statusUpdating = false);
    }
  }

  Future<void> _showResponsibilitySheet() async {
    final repository = ref.read(workOrdersRepositoryProvider);
    try {
      final results = await Future.wait([
        repository.listResponsibilities(widget.workOrderId),
        repository.listResponsibilityCandidates(widget.workOrderId),
      ]);
      if (!mounted) return;
      final responsibilities = results[0];
      final candidates = results[1];
      Map<String, dynamic>? currentMatch;
      for (final item in responsibilities) {
        if (item['kind'] == 'Primary' && item['effectiveToUtc'] == null) {
          currentMatch = item;
          break;
        }
      }
      final current = currentMatch;
      int? selectedAssignmentId;
      var reason = 'Assigned by property manager';
      await showModalBottomSheet<void>(
        context: context,
        isScrollControlled: true,
        builder: (sheetContext) => StatefulBuilder(
          builder: (context, setSheetState) => Padding(
            padding: EdgeInsets.fromLTRB(
              24,
              24,
              24,
              MediaQuery.viewInsetsOf(context).bottom + 24,
            ),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text('Technician responsibility', style: Theme.of(context).textTheme.titleLarge),
                const SizedBox(height: 8),
                Text(current == null
                    ? 'No technician is currently assigned.'
                    : '${current['memberDisplayName']} is currently responsible.'),
                const SizedBox(height: 16),
                DropdownButtonFormField<int>(
                  initialValue: selectedAssignmentId,
                  decoration: const InputDecoration(labelText: 'Technician'),
                  items: candidates.map((candidate) => DropdownMenuItem<int>(
                    value: (candidate['membershipRoleAssignmentId'] as num).toInt(),
                    child: Text(candidate['memberDisplayName'] as String),
                  )).toList(growable: false),
                  onChanged: (value) => setSheetState(() => selectedAssignmentId = value),
                ),
                const SizedBox(height: 12),
                TextFormField(
                  initialValue: reason,
                  maxLength: 1000,
                  decoration: const InputDecoration(labelText: 'Reason'),
                  onChanged: (value) => reason = value,
                ),
                const SizedBox(height: 12),
                FilledButton(
                  onPressed: selectedAssignmentId == null ? null : () async {
                    final selected = candidates.firstWhere((candidate) =>
                        (candidate['membershipRoleAssignmentId'] as num).toInt() == selectedAssignmentId);
                    final contextIds = <int>{(selected['accessContextId'] as num).toInt()};
                    if (current?['accessContextId'] case final num currentId) {
                      contextIds.add(currentId.toInt());
                    }
                    final expectations = contextIds.map((contextId) {
                      final candidate = candidates.firstWhere((item) =>
                          (item['accessContextId'] as num).toInt() == contextId);
                      return {
                        'accessContextId': contextId,
                        'expectedRevision': (candidate['accessRevision'] as num).toInt(),
                      };
                    }).toList(growable: false);
                    await repository.assignResponsibility(widget.workOrderId, {
                      'workspaceMembershipId': (selected['workspaceMembershipId'] as num).toInt(),
                      'membershipRoleAssignmentId': selectedAssignmentId,
                      'kind': 'Primary',
                      'expectedCurrentPrimaryResponsibilityId': current?['id'],
                      'accessRevisionExpectations': expectations,
                      'reason': reason.trim(),
                    });
                    if (sheetContext.mounted) Navigator.pop(sheetContext);
                    await _refresh();
                  },
                  child: Text(current == null ? 'Assign' : 'Reassign'),
                ),
                if (current != null) ...[
                  const SizedBox(height: 8),
                  OutlinedButton(
                    onPressed: () async {
                      final contextId = (current['accessContextId'] as num).toInt();
                      final candidate = candidates.firstWhere((item) =>
                          (item['accessContextId'] as num).toInt() == contextId);
                      await repository.closeResponsibility(widget.workOrderId, current['id'] as String, {
                        'accessRevisionExpectations': [{
                          'accessContextId': contextId,
                          'expectedRevision': (candidate['accessRevision'] as num).toInt(),
                        }],
                        'reason': 'Unassigned by property manager',
                      });
                      if (sheetContext.mounted) Navigator.pop(sheetContext);
                      await _refresh();
                    },
                    child: const Text('Unassign'),
                  ),
                ],
              ],
            ),
          ),
        ),
      );
    } catch (error) {
      _showError(error);
    }
  }

  Future<void> _addPhoto(ImageSource source) async {
    final picked = await ImagePicker().pickImage(
      source: source,
      imageQuality: 80,
      maxWidth: 1600,
      maxHeight: 1600,
    );
    if (picked == null || !mounted) return;

    setState(() => _uploadingPhoto = true);
    try {
      final bytes = Uint8List.fromList(await picked.readAsBytes());
      await ref
          .read(workOrdersRepositoryProvider)
          .uploadWorkOrderPhoto(
            workOrderId: widget.workOrderId,
            bytes: bytes,
            fileName: picked.name,
            contentType: _mimeFromExtension(picked.name),
            clientOperationId: const Uuid().v4(),
          );
      if (!mounted) return;
      ref.invalidate(workOrderDocumentsProvider(widget.workOrderId));
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Photo added.')));
    } on ApiException catch (e) {
      _showError(e);
    } finally {
      if (mounted) setState(() => _uploadingPhoto = false);
    }
  }

  void _showPhotoSourceSheet() {
    showModalBottomSheet<void>(
      context: context,
      builder: (sheetCtx) => SafeArea(
        child: Wrap(
          children: [
            ListTile(
              leading: const Icon(Icons.camera_alt_outlined),
              title: const Text('Take photo'),
              onTap: () {
                Navigator.of(sheetCtx).pop();
                _addPhoto(ImageSource.camera);
              },
            ),
            ListTile(
              leading: const Icon(Icons.photo_library_outlined),
              title: const Text('Choose from gallery'),
              onTap: () {
                Navigator.of(sheetCtx).pop();
                _addPhoto(ImageSource.gallery);
              },
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _navigateToProperty(WorkOrder wo) async {
    var query = [
      if (wo.propertyName != null && wo.propertyName!.isNotEmpty)
        wo.propertyName!,
    ].join(', ');

    // The work-order payload has no address, so fall back to the property
    // record for a real street address when we can fetch it.
    try {
      final property = await ref
          .read(propertiesRepositoryProvider)
          .getProperty(wo.propertyId);
      final addressParts = [
        property.addressLine1,
        if (property.addressLine2 != null && property.addressLine2!.isNotEmpty)
          property.addressLine2!,
        property.city,
        property.state,
        property.postalCode,
      ].where((p) => p.trim().isNotEmpty).toList();
      if (addressParts.isNotEmpty) {
        query = addressParts.join(', ');
      } else if (property.name.isNotEmpty) {
        query = property.name;
      }
    } on ApiException {
      // Keep the property-name query we already have.
    }

    if (query.isEmpty) {
      _showError('No address available for this work order.');
      return;
    }

    final uri = Uri.parse(
      'https://www.google.com/maps/search/?api=1&query=${Uri.encodeComponent(query)}',
    );
    final launched = await launchUrl(uri, mode: LaunchMode.externalApplication);
    if (!launched && mounted) {
      _showError('Could not open Maps.');
    }
  }

  Future<void> _callVendor() async {
    final vendor = await showSelectVendorSheet(context);
    if (vendor == null || !mounted) return;

    final phone = vendor.phone?.trim() ?? '';
    if (phone.isEmpty) {
      _showError('${vendor.name} has no phone number on file.');
      return;
    }

    final launched = await launchUrl(
      Uri(scheme: 'tel', path: phone),
      mode: LaunchMode.externalApplication,
    );
    if (!launched && mounted) {
      _showError('Could not start a call.');
    }
  }

  Future<void> _dispatchVendor() async {
    final vendor = await showDispatchVendorSheet(
      context,
      workOrderId: widget.workOrderId,
    );
    if (vendor == null || !mounted) return;

    // Reload so the assigned vendor + any timeline entry show up.
    await ref
        .read(workOrderDetailProvider(widget.workOrderId).notifier)
        .refresh();
    if (!mounted) return;
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(
        SnackBar(
          content: Text(
            'Texted ${vendor.name}. They reply DONE to close this out.',
          ),
        ),
      );
  }

  Future<void> _rateVendor(WorkOrder wo) async {
    final vendorId = wo.vendorId;
    if (vendorId == null) return;
    final rated = await showRateVendorSheet(
      context,
      vendorId: vendorId,
      vendorName: wo.vendorName ?? 'this vendor',
      workOrderId: wo.id,
    );
    if (rated == true && mounted) {
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Rating saved. Thanks!')));
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

  void _showActivityHistory({String? subtitle}) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => ActivityHistoryScreen(
          entityType: 'WorkOrder',
          entityId: widget.workOrderId,
          title: 'Work order activity',
          subtitle: subtitle,
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final detailAsync = ref.watch(workOrderDetailProvider(widget.workOrderId));
    final detail = detailAsync.asData?.value;
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final auth = ref.watch(authControllerProvider);
    final canManageWork = auth is AuthStateAuthenticated &&
        auth.capabilities.contains('work.manage');

    return Scaffold(
      appBar: AppBar(
        title: const Text('Work Order'),
        actions: [
          if (auth is AuthStateAuthenticated &&
              auth.capabilities.contains('responsibility.assign-existing-member'))
            IconButton(
              icon: const Icon(Icons.engineering_outlined),
              tooltip: 'Assign technician',
              onPressed: _showResponsibilitySheet,
            ),
          IconButton(
            icon: const Icon(Icons.history_outlined),
            tooltip: 'View work order activity',
            onPressed: () =>
                _showActivityHistory(subtitle: detail?.workOrder.title),
          ),
          if (canManageWork) detailAsync.whenOrNull(
                data: (detail) => IconButton(
                  icon: const Icon(Icons.edit_outlined),
                  tooltip: 'Edit',
                  onPressed: () => _showEditSheet(context, detail.workOrder),
                ),
              ) ??
              const SizedBox.shrink(),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: detailAsync.when(
          loading: () => const _WorkOrderDetailLoading(),
          error: (e, _) => Center(
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(Icons.error_outline, size: 40, color: colorScheme.error),
                  const SizedBox(height: 12),
                  Text(
                    e is ApiException ? e.message : e.toString(),
                    textAlign: TextAlign.center,
                    style: TextStyle(color: colorScheme.error),
                  ),
                  const SizedBox(height: 16),
                  FilledButton.tonal(
                    onPressed: _refresh,
                    child: const Text('Retry'),
                  ),
                ],
              ),
            ),
          ),
          data: (detail) => _DetailBody(
            detail: detail,
            colorScheme: colorScheme,
            theme: theme,
            canManageWork: canManageWork,
            statusUpdating: _statusUpdating,
            uploadingPhoto: _uploadingPhoto,
            onTransition: _changeStatus,
            onAddPhoto: _showPhotoSourceSheet,
            onNavigate: () => _navigateToProperty(detail.workOrder),
            onDispatchVendor: _dispatchVendor,
            onCallVendor: _callVendor,
            onRateVendor: () => _rateVendor(detail.workOrder),
          ),
        ),
      ),
    );
  }
}

class _WorkOrderDetailLoading extends StatelessWidget {
  const _WorkOrderDetailLoading();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colors = theme.colorScheme;

    return ListView(
      key: const Key('work-order-detail-loading'),
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      children: [
        Card.filled(
          margin: EdgeInsets.zero,
          color: colors.surfaceContainerHigh,
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Row(
              children: [
                Container(
                  width: 44,
                  height: 44,
                  decoration: BoxDecoration(
                    color: colors.primaryContainer,
                    borderRadius: BorderRadius.circular(16),
                  ),
                  child: Icon(
                    Icons.home_repair_service_outlined,
                    color: colors.onPrimaryContainer,
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Loading work order',
                        style: theme.textTheme.titleMedium?.copyWith(
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        'Details, schedule, and activity',
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: colors.onSurfaceVariant,
                        ),
                      ),
                    ],
                  ),
                ),
                const SizedBox(width: 12),
                const SizedBox.square(
                  dimension: 22,
                  child: CircularProgressIndicator(strokeWidth: 2),
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 16),
        Card.filled(
          margin: EdgeInsets.zero,
          color: colors.surfaceContainer,
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Work order details',
                  style: theme.textTheme.labelLarge?.copyWith(
                    color: colors.onSurfaceVariant,
                  ),
                ),
                const SizedBox(height: 14),
                Row(
                  children: [
                    Icon(
                      Icons.location_on_outlined,
                      size: 20,
                      color: colors.onSurfaceVariant,
                    ),
                    const SizedBox(width: 10),
                    const Expanded(child: LinearProgressIndicator()),
                  ],
                ),
                const SizedBox(height: 16),
                Row(
                  children: [
                    Icon(
                      Icons.schedule_outlined,
                      size: 20,
                      color: colors.onSurfaceVariant,
                    ),
                    const SizedBox(width: 10),
                    const Expanded(child: LinearProgressIndicator()),
                  ],
                ),
              ],
            ),
          ),
        ),
      ],
    );
  }
}

// ── Detail body ───────────────────────────────────────────────────────────────

class _DetailBody extends StatelessWidget {
  const _DetailBody({
    required this.detail,
    required this.colorScheme,
    required this.theme,
    required this.canManageWork,
    required this.statusUpdating,
    required this.uploadingPhoto,
    required this.onTransition,
    required this.onAddPhoto,
    required this.onNavigate,
    required this.onDispatchVendor,
    required this.onCallVendor,
    required this.onRateVendor,
  });

  final WorkOrderDetail detail;
  final ColorScheme colorScheme;
  final ThemeData theme;
  final bool canManageWork;
  final bool statusUpdating;
  final bool uploadingPhoto;
  final void Function(String) onTransition;
  final VoidCallback onAddPhoto;
  final VoidCallback onNavigate;
  final VoidCallback onDispatchVendor;
  final VoidCallback onCallVendor;
  final VoidCallback onRateVendor;

  @override
  Widget build(BuildContext context) {
    final workOrder = detail.workOrder;
    final otherStatuses = _statusActionTargets(
      workOrder.status,
      restrictToAssignedTechnicianTransitions: !canManageWork,
    );
    final isCompleted = workOrder.status.toLowerCase() == 'completed';
    final hasVendor = workOrder.vendorId != null;

    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      children: [
        // ── Title + chips ──────────────────────────────────────────────
        Text(
          workOrder.title,
          style: theme.textTheme.headlineSmall?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        const SizedBox(height: 8),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            _PriorityChip(
              priority: workOrder.priority,
              colorScheme: colorScheme,
            ),
            _StatusChip(status: workOrder.status, colorScheme: colorScheme),
            _CategoryChip(
              category: workOrder.category,
              colorScheme: colorScheme,
            ),
          ],
        ),

        const SizedBox(height: 16),

        // ── Actions ────────────────────────────────────────────────────
        Wrap(
          spacing: 12,
          runSpacing: 8,
          children: [
            FilledButton.tonalIcon(
              onPressed: onNavigate,
              icon: const Icon(Icons.directions_outlined),
              label: const Text('Open in Maps'),
            ),
            // Dispatch is hidden once the job is closed out.
            if (canManageWork &&
                !isCompleted &&
                workOrder.status.toLowerCase() != 'cancelled')
              FilledButton.icon(
                onPressed: onDispatchVendor,
                icon: const Icon(Icons.sms_outlined),
                label: const Text('Text a vendor'),
              ),
            // Call works regardless of status (e.g. follow-up on a completed job).
            if (canManageWork)
              FilledButton.tonalIcon(
                onPressed: onCallVendor,
                icon: const Icon(Icons.call_outlined),
                label: const Text('Call a vendor'),
              ),
            if (canManageWork && isCompleted && hasVendor)
              OutlinedButton.icon(
                onPressed: onRateVendor,
                icon: const Icon(Icons.star_outline_rounded),
                label: const Text('Rate this vendor'),
              ),
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
        _DetailGrid(
          workOrder: workOrder,
          colorScheme: colorScheme,
          theme: theme,
        ),

        const SizedBox(height: 20),

        // ── Photos ─────────────────────────────────────────────────────
        Row(
          children: [
            Expanded(
              child: _SectionLabel(label: 'Photos', theme: theme),
            ),
            TextButton.icon(
              onPressed: uploadingPhoto ? null : onAddPhoto,
              icon: uploadingPhoto
                  ? const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.add_a_photo_outlined, size: 18),
              label: Text(uploadingPhoto ? 'Uploading...' : 'Add photo'),
            ),
          ],
        ),
        const SizedBox(height: 8),
        _PhotoStrip(workOrderId: workOrder.id),

        const SizedBox(height: 20),

        // ── Timeline ───────────────────────────────────────────────────
        _SectionLabel(label: 'Status timeline', theme: theme),
        const SizedBox(height: 8),
        WorkOrderTimeline(events: detail.timeline),

        const SizedBox(height: 24),

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
                        horizontal: 14,
                        vertical: 8,
                      ),
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

// ── Photo strip ───────────────────────────────────────────────────────────────

class _PhotoStrip extends ConsumerWidget {
  const _PhotoStrip({required this.workOrderId});

  final int workOrderId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final docsAsync = ref.watch(workOrderDocumentsProvider(workOrderId));
    final cs = Theme.of(context).colorScheme;

    return docsAsync.when(
      loading: () => const SizedBox(
        height: 96,
        child: Center(child: CircularProgressIndicator()),
      ),
      error: (_, _) => _PhotoStripMessage(
        icon: Icons.broken_image_outlined,
        text: "Couldn't load photos.",
      ),
      data: (docs) {
        final images = docs.where((d) => d.isImage).toList();
        if (images.isEmpty) {
          return const _PhotoStripMessage(
            icon: Icons.photo_outlined,
            text: 'No photos yet — add before/after evidence.',
          );
        }
        return SizedBox(
          height: 96,
          child: ListView.separated(
            scrollDirection: Axis.horizontal,
            itemCount: images.length,
            separatorBuilder: (_, _) => const SizedBox(width: 8),
            itemBuilder: (_, i) => _PhotoThumb(document: images[i], cs: cs),
          ),
        );
      },
    );
  }
}

class _PhotoStripMessage extends StatelessWidget {
  const _PhotoStripMessage({required this.icon, required this.text});

  final IconData icon;
  final String text;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: cs.surfaceContainerLowest,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: cs.outlineVariant),
      ),
      child: Row(
        children: [
          Icon(icon, color: cs.onSurfaceVariant, size: 20),
          const SizedBox(width: 12),
          Expanded(
            child: Text(text, style: TextStyle(color: cs.onSurfaceVariant)),
          ),
        ],
      ),
    );
  }
}

class _PhotoThumb extends ConsumerWidget {
  const _PhotoThumb({required this.document, required this.cs});

  final Document document;
  final ColorScheme cs;

  void _openFullScreen(BuildContext context, Uint8List bytes) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => Scaffold(
          backgroundColor: Colors.black,
          appBar: AppBar(
            backgroundColor: Colors.black,
            foregroundColor: Colors.white,
          ),
          body: Center(child: InteractiveViewer(child: Image.memory(bytes))),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final bytesAsync = ref.watch(documentBytesProvider(document.id));

    return bytesAsync.when(
      loading: () => Container(
        width: 96,
        height: 96,
        decoration: BoxDecoration(
          color: cs.surfaceContainerHighest,
          borderRadius: BorderRadius.circular(12),
        ),
        child: const Center(
          child: SizedBox(
            width: 18,
            height: 18,
            child: CircularProgressIndicator(strokeWidth: 2),
          ),
        ),
      ),
      error: (_, _) => Container(
        width: 96,
        height: 96,
        decoration: BoxDecoration(
          color: cs.surfaceContainerHighest,
          borderRadius: BorderRadius.circular(12),
        ),
        child: Icon(Icons.broken_image_outlined, color: cs.onSurfaceVariant),
      ),
      data: (bytes) => GestureDetector(
        onTap: () => _openFullScreen(context, bytes),
        child: ClipRRect(
          borderRadius: BorderRadius.circular(12),
          child: Image.memory(bytes, width: 96, height: 96, fit: BoxFit.cover),
        ),
      ),
    );
  }
}

// ── Status-note bottom sheet ──────────────────────────────────────────────────

/// Pops `null` if dismissed, or the (possibly empty) note string on confirm.
class _StatusNoteSheet extends StatefulWidget {
  const _StatusNoteSheet({required this.status});

  final String status;

  @override
  State<_StatusNoteSheet> createState() => _StatusNoteSheetState();
}

class _StatusNoteSheetState extends State<_StatusNoteSheet> {
  final _noteCtrl = TextEditingController();

  @override
  void dispose() {
    _noteCtrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottomPadding),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            'Set status: ${_statusLabel(widget.status)}',
            style: theme.textTheme.titleLarge?.copyWith(
              fontWeight: FontWeight.w700,
            ),
          ),
          const SizedBox(height: 4),
          Text(
            'Add an optional note for the timeline.',
            style: theme.textTheme.bodyMedium?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 16),
          TextField(
            controller: _noteCtrl,
            maxLines: 3,
            autofocus: true,
            textCapitalization: TextCapitalization.sentences,
            decoration: const InputDecoration(
              labelText: 'Note (optional)',
              hintText: 'e.g. Parts ordered, tenant let us in...',
            ),
          ),
          const SizedBox(height: 20),
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
                  onPressed: () =>
                      Navigator.of(context).pop(_noteCtrl.text.trim()),
                  child: const Text('Update'),
                ),
              ),
            ],
          ),
        ],
      ),
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
            value:
                workOrder.propertyName ?? 'Property #${workOrder.propertyId}',
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
            : Border(bottom: BorderSide(color: colorScheme.outlineVariant)),
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
              style: theme.textTheme.bodyMedium?.copyWith(
                fontWeight: FontWeight.w500,
              ),
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
  const _PriorityChip({required this.priority, required this.colorScheme});

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
  const _StatusChip({required this.status, required this.colorScheme});

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
  const _CategoryChip({required this.category, required this.colorScheme});

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
        style: TextStyle(fontSize: 12, color: colorScheme.onSurfaceVariant),
      ),
    );
  }
}

// ── Edit Work Order Bottom Sheet ──────────────────────────────────────────────

class _EditWorkOrderSheet extends ConsumerStatefulWidget {
  const _EditWorkOrderSheet({required this.workOrder, required this.onSaved});

  final WorkOrder workOrder;
  final VoidCallback onSaved;

  @override
  ConsumerState<_EditWorkOrderSheet> createState() =>
      _EditWorkOrderSheetState();
}

class _EditWorkOrderSheetState extends ConsumerState<_EditWorkOrderSheet> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _titleCtrl;
  late final TextEditingController _descCtrl;
  late final TextEditingController _technicianAccessCtrl;
  late final TextEditingController _estCostCtrl;
  late final TextEditingController _actualCostCtrl;
  late int _selectedPropertyId;
  int? _selectedUnitId;
  int? _selectedTenantId;
  int? _selectedVendorId;
  DateTime? _scheduledDate;
  TimeOfDay? _startTime;
  TimeOfDay? _windowEndTime;
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
    _titleCtrl = TextEditingController(text: widget.workOrder.title);
    _descCtrl = TextEditingController(text: widget.workOrder.description);
    _technicianAccessCtrl = TextEditingController(
      text: widget.workOrder.technicianAccessInstructions ?? '',
    );
    _estCostCtrl = TextEditingController(
      text: widget.workOrder.estimatedCost?.toStringAsFixed(2) ?? '',
    );
    _actualCostCtrl = TextEditingController(
      text: widget.workOrder.actualCost?.toStringAsFixed(2) ?? '',
    );
    _selectedPropertyId = widget.workOrder.propertyId;
    _selectedUnitId = widget.workOrder.unitId;
    _selectedTenantId = widget.workOrder.tenantId;
    _selectedVendorId = widget.workOrder.vendorId;

    final scheduledFor = widget.workOrder.scheduledFor?.toLocal();
    final scheduledWindowEnd = widget.workOrder.scheduledWindowEnd?.toLocal();
    if (scheduledFor != null) {
      _scheduledDate = DateTime(
        scheduledFor.year,
        scheduledFor.month,
        scheduledFor.day,
      );
      _startTime = TimeOfDay.fromDateTime(scheduledFor);
    } else if (scheduledWindowEnd != null) {
      _scheduledDate = DateTime(
        scheduledWindowEnd.year,
        scheduledWindowEnd.month,
        scheduledWindowEnd.day,
      );
    }
    if (scheduledWindowEnd != null) {
      _windowEndTime = TimeOfDay.fromDateTime(scheduledWindowEnd);
    }

    _priority = _priorities.contains(widget.workOrder.priority)
        ? widget.workOrder.priority
        : 'Normal';
    _category = _categories.contains(widget.workOrder.category)
        ? widget.workOrder.category
        : 'General';
    Future.microtask(() {
      ref.read(propertiesForWoProvider.notifier).load();
    });
  }

  @override
  void dispose() {
    _titleCtrl.dispose();
    _descCtrl.dispose();
    _technicianAccessCtrl.dispose();
    _estCostCtrl.dispose();
    _actualCostCtrl.dispose();
    super.dispose();
  }

  DateTime? _combine(DateTime? date, TimeOfDay? time) {
    if (date == null || time == null) return null;
    return DateTime(date.year, date.month, date.day, time.hour, time.minute);
  }

  DateTime? _scheduledStart(DateTime? date, TimeOfDay? time) {
    if (date == null) return null;
    return _combine(date, time) ?? DateTime(date.year, date.month, date.day);
  }

  static String _fmtEditDate(DateTime d) {
    return '${_months[d.month]} ${d.day}, ${d.year}';
  }

  Future<void> _pickScheduledDate() async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _scheduledDate ?? now,
      firstDate: DateTime(now.year - 1),
      lastDate: DateTime(now.year + 5),
    );
    if (picked == null || !mounted) return;
    setState(() {
      _scheduledDate = DateTime(picked.year, picked.month, picked.day);
    });
  }

  Future<void> _pickTime({required bool isStart}) async {
    final picked = await showTimePicker(
      context: context,
      initialTime: (isStart ? _startTime : _windowEndTime) ?? TimeOfDay.now(),
    );
    if (picked == null || !mounted) return;
    setState(() {
      if (isStart) {
        _startTime = picked;
      } else {
        _windowEndTime = picked;
      }
    });
  }

  String _tenantLabel(Tenant tenant) {
    final fullName = tenant.fullName?.trim();
    if (fullName != null && fullName.isNotEmpty) return fullName;
    final name = '${tenant.firstName} ${tenant.lastName}'.trim();
    return name.isEmpty ? 'Tenant #${tenant.id}' : name;
  }

  TenantListQuery get _tenantQuery => TenantListQuery(
    take: 200,
    sort: 'name',
    propertyId: _selectedPropertyId,
    unitId: _selectedUnitId,
  );

  void _clearSelectedTenantIfMissing(List<Tenant> tenants) {
    final tenantId = _selectedTenantId;
    if (tenantId == null || tenants.any((tenant) => tenant.id == tenantId)) {
      return;
    }

    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && _selectedTenantId == tenantId) {
        setState(() => _selectedTenantId = null);
      }
    });
  }

  double? _parseOptionalAmount(TextEditingController controller) {
    final trimmed = controller.text.trim();
    if (trimmed.isEmpty) return null;
    return double.tryParse(trimmed);
  }

  bool _validateArrivalWindow() {
    final scheduledFor = _scheduledStart(_scheduledDate, _startTime);
    final scheduledWindowEnd = _combine(_scheduledDate, _windowEndTime);
    if (scheduledFor != null &&
        scheduledWindowEnd != null &&
        !scheduledWindowEnd.isAfter(scheduledFor)) {
      setState(
        () => _error = 'Arrival window end must be after the start time.',
      );
      return false;
    }
    if (_error == 'Arrival window end must be after the start time.') {
      setState(() => _error = null);
    }
    return true;
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;

    final scheduledFor = _scheduledStart(_scheduledDate, _startTime);
    final scheduledWindowEnd = _combine(_scheduledDate, _windowEndTime);
    if (!_validateArrivalWindow()) return;

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final estimatedCost = _parseOptionalAmount(_estCostCtrl);
      final actualCost = _parseOptionalAmount(_actualCostCtrl);
      final clearUnit =
          _selectedUnitId == null && widget.workOrder.unitId != null;
      final clearTenant =
          _selectedTenantId == null && widget.workOrder.tenantId != null;
      final clearLease =
          _selectedUnitId != widget.workOrder.unitId &&
          widget.workOrder.leaseId != null;
      await ref
          .read(workOrdersRepositoryProvider)
          .updateWorkOrder(widget.workOrder.id, {
            'title': _titleCtrl.text.trim(),
            'description': _descCtrl.text.trim(),
            'technicianAccessInstructions': _technicianAccessCtrl.text.trim(),
            'priority': _priority,
            'category': _category,
            'unitId': ?_selectedUnitId,
            'clearUnit': clearUnit,
            'tenantId': ?_selectedTenantId,
            'clearTenant': clearTenant,
            'clearLease': clearLease,
            'vendorId': ?_selectedVendorId,
            if (scheduledFor != null)
              'scheduledFor': localToWireIso(scheduledFor),
            if (scheduledWindowEnd != null)
              'scheduledWindowEnd': localToWireIso(scheduledWindowEnd),
            'estimatedCost': ?estimatedCost,
            'actualCost': ?actualCost,
          });

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
    final colorScheme = Theme.of(context).colorScheme;
    final propertiesAsync = ref.watch(propertiesForWoProvider);
    final tenantsAsync = ref.watch(tenantsPageProvider(_tenantQuery));
    final vendorsAsync = ref.watch(vendorsProvider);
    final unitsAsync = ref.watch(unitsProvider(_selectedPropertyId));
    const gap = SizedBox(height: 12);

    final propertyField = propertiesAsync.when(
      loading: () => const Padding(
        padding: EdgeInsets.symmetric(vertical: 8),
        child: LinearProgressIndicator(),
      ),
      error: (e, _) => Text(
        'Could not load properties: ${e is ApiException ? e.message : e}',
        style: TextStyle(color: colorScheme.error, fontSize: 13),
      ),
      data: (properties) {
        var propertyName =
            widget.workOrder.propertyName ?? 'Property #$_selectedPropertyId';
        for (final property in properties) {
          if (property.id == _selectedPropertyId) {
            propertyName = property.name;
            break;
          }
        }
        return TextFormField(
          key: const Key('work-order-property-field'),
          initialValue: propertyName,
          readOnly: true,
          decoration: const InputDecoration(labelText: 'Property'),
        );
      },
    );

    final unitField = unitsAsync.when(
      loading: () => const Padding(
        padding: EdgeInsets.symmetric(vertical: 8),
        child: LinearProgressIndicator(),
      ),
      error: (e, _) => Text(
        'Could not load units: ${e is ApiException ? e.message : e}',
        style: TextStyle(color: colorScheme.error, fontSize: 13),
      ),
      data: (units) {
        final hasSelectedUnit =
            _selectedUnitId == null ||
            units.any((unit) => unit.id == _selectedUnitId);
        return DropdownButtonFormField<int?>(
          initialValue: _selectedUnitId,
          decoration: const InputDecoration(labelText: 'Unit (optional)'),
          items: [
            const DropdownMenuItem<int?>(
              value: null,
              child: Text('No specific unit'),
            ),
            if (!hasSelectedUnit)
              DropdownMenuItem<int?>(
                value: _selectedUnitId,
                child: Text(
                  'Unit ${widget.workOrder.unitNumber ?? _selectedUnitId}',
                  overflow: TextOverflow.ellipsis,
                ),
              ),
            ...units.map(
              (unit) => DropdownMenuItem<int?>(
                value: unit.id,
                child: Text(
                  'Unit ${unit.unitNumber}',
                  overflow: TextOverflow.ellipsis,
                ),
              ),
            ),
          ],
          onChanged: (value) => setState(() {
            _selectedUnitId = value;
            _selectedTenantId = null;
          }),
        );
      },
    );

    final tenantField = tenantsAsync.when(
      loading: () => const Padding(
        padding: EdgeInsets.symmetric(vertical: 8),
        child: LinearProgressIndicator(),
      ),
      error: (e, _) => Text(
        'Could not load tenants: ${e is ApiException ? e.message : e}',
        style: TextStyle(color: colorScheme.error, fontSize: 13),
      ),
      data: (page) {
        final tenants = page.items;
        _clearSelectedTenantIfMissing(tenants);
        final hasSelectedTenant =
            _selectedTenantId == null ||
            tenants.any((tenant) => tenant.id == _selectedTenantId);
        return DropdownButtonFormField<int?>(
          initialValue: hasSelectedTenant ? _selectedTenantId : null,
          decoration: const InputDecoration(labelText: 'Tenant (optional)'),
          items: [
            const DropdownMenuItem<int?>(value: null, child: Text('No tenant')),
            ...tenants.map(
              (tenant) => DropdownMenuItem<int?>(
                value: tenant.id,
                child: Text(
                  _tenantLabel(tenant),
                  overflow: TextOverflow.ellipsis,
                ),
              ),
            ),
          ],
          onChanged: (value) => setState(() => _selectedTenantId = value),
        );
      },
    );

    final vendorField = vendorsAsync.when(
      loading: () => const Padding(
        padding: EdgeInsets.symmetric(vertical: 8),
        child: LinearProgressIndicator(),
      ),
      error: (e, _) => Text(
        'Could not load vendors: ${e is ApiException ? e.message : e}',
        style: TextStyle(color: colorScheme.error, fontSize: 13),
      ),
      data: (vendors) {
        final hasSelectedVendor =
            _selectedVendorId == null ||
            vendors.any((vendor) => vendor.id == _selectedVendorId);
        return DropdownButtonFormField<int?>(
          initialValue: _selectedVendorId,
          decoration: const InputDecoration(labelText: 'Vendor (optional)'),
          items: [
            const DropdownMenuItem<int?>(value: null, child: Text('No vendor')),
            if (!hasSelectedVendor)
              DropdownMenuItem<int?>(
                value: _selectedVendorId,
                child: Text(
                  widget.workOrder.vendorName ??
                      'Vendor #${widget.workOrder.vendorId}',
                  overflow: TextOverflow.ellipsis,
                ),
              ),
            ...vendors.map(
              (vendor) => DropdownMenuItem<int?>(
                value: vendor.id,
                child: Text(vendor.name, overflow: TextOverflow.ellipsis),
              ),
            ),
          ],
          onChanged: (value) => setState(() => _selectedVendorId = value),
        );
      },
    );

    return Form(
      key: _formKey,
      child: WorkOrderFormShell(
        title: 'Edit Work Order',
        saveLabel: 'Save Changes',
        saving: _saving,
        error: _error,
        onSave: _submit,
        tabs: [
          WorkOrderFormTabSpec(
            label: 'Details',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                propertyField,
                gap,
                unitField,
                gap,
                TextFormField(
                  key: const Key('work-order-title-field'),
                  controller: _titleCtrl,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(labelText: 'Title'),
                  validator: (value) => (value == null || value.trim().isEmpty)
                      ? 'Title is required'
                      : null,
                ),
                gap,
                TextFormField(
                  key: const Key('work-order-description-field'),
                  controller: _descCtrl,
                  maxLines: 3,
                  textInputAction: TextInputAction.newline,
                  decoration: const InputDecoration(labelText: 'Description'),
                  validator: (value) => (value == null || value.trim().isEmpty)
                      ? 'Description is required'
                      : null,
                ),
                gap,
                TextFormField(
                  key: const Key('work-order-technician-access-field'),
                  controller: _technicianAccessCtrl,
                  maxLines: 3,
                  maxLength: 2000,
                  textInputAction: TextInputAction.newline,
                  decoration: const InputDecoration(
                    labelText: 'Safe technician access (optional)',
                    helperText:
                        'Entry, lockbox, pet, or contact guidance safe for the assigned technician.',
                  ),
                ),
              ],
            ),
          ),
          WorkOrderFormTabSpec(
            label: 'Schedule',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: DropdownButtonFormField<String>(
                        initialValue: _priority,
                        decoration: const InputDecoration(
                          labelText: 'Priority',
                        ),
                        items: _priorities
                            .map(
                              (priority) => DropdownMenuItem(
                                value: priority,
                                child: Text(priority),
                              ),
                            )
                            .toList(),
                        onChanged: (value) {
                          if (value != null) {
                            setState(() => _priority = value);
                          }
                        },
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: DropdownButtonFormField<String>(
                        initialValue: _category,
                        decoration: const InputDecoration(
                          labelText: 'Category',
                        ),
                        items: _categories
                            .map(
                              (category) => DropdownMenuItem(
                                value: category,
                                child: Text(category),
                              ),
                            )
                            .toList(),
                        onChanged: (value) {
                          if (value != null) {
                            setState(() => _category = value);
                          }
                        },
                      ),
                    ),
                  ],
                ),
                gap,
                InkWell(
                  onTap: _pickScheduledDate,
                  child: InputDecorator(
                    decoration: const InputDecoration(
                      labelText: 'Scheduled date (optional)',
                      suffixIcon: Icon(Icons.calendar_today_outlined),
                    ),
                    child: Text(
                      _scheduledDate == null
                          ? 'Not scheduled'
                          : _fmtEditDate(_scheduledDate!),
                      style: TextStyle(
                        color: _scheduledDate == null
                            ? colorScheme.onSurfaceVariant
                            : colorScheme.onSurface,
                      ),
                    ),
                  ),
                ),
                gap,
                Row(
                  children: [
                    Expanded(
                      child: InkWell(
                        onTap: _scheduledDate == null
                            ? null
                            : () => _pickTime(isStart: true),
                        child: InputDecorator(
                          decoration: const InputDecoration(
                            labelText: 'Start time',
                            suffixIcon: Icon(Icons.schedule_outlined),
                          ),
                          child: Text(
                            _startTime == null
                                ? '--:--'
                                : _startTime!.format(context),
                            style: TextStyle(
                              color: _startTime == null
                                  ? colorScheme.onSurfaceVariant
                                  : colorScheme.onSurface,
                            ),
                          ),
                        ),
                      ),
                    ),
                    const Spacer(),
                  ],
                ),
              ],
            ),
          ),
          WorkOrderFormTabSpec(
            label: 'Costs',
            validate: _validateArrivalWindow,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                InkWell(
                  onTap: _scheduledDate == null
                      ? null
                      : () => _pickTime(isStart: false),
                  child: InputDecorator(
                    decoration: const InputDecoration(
                      labelText: 'Arrival window end',
                      suffixIcon: Icon(Icons.schedule_outlined),
                    ),
                    child: Text(
                      _windowEndTime == null
                          ? '--:--'
                          : _windowEndTime!.format(context),
                      style: TextStyle(
                        color: _windowEndTime == null
                            ? colorScheme.onSurfaceVariant
                            : colorScheme.onSurface,
                      ),
                    ),
                  ),
                ),
                gap,
                TextFormField(
                  key: const Key('work-order-estimated-cost-field'),
                  controller: _estCostCtrl,
                  keyboardType: const TextInputType.numberWithOptions(
                    decimal: true,
                  ),
                  decoration: const InputDecoration(
                    labelText: 'Estimated cost (optional)',
                    prefixText: '\$ ',
                  ),
                  validator: (value) {
                    final text = value?.trim() ?? '';
                    if (text.isEmpty) return null;
                    final parsed = double.tryParse(text);
                    if (parsed == null) return 'Enter a valid amount';
                    if (parsed < 0) return 'Cannot be negative';
                    return null;
                  },
                ),
                gap,
                TextFormField(
                  key: const Key('work-order-actual-cost-field'),
                  controller: _actualCostCtrl,
                  keyboardType: const TextInputType.numberWithOptions(
                    decimal: true,
                  ),
                  decoration: const InputDecoration(
                    labelText: 'Actual cost (optional)',
                    prefixText: '\$ ',
                  ),
                  validator: (value) {
                    final text = value?.trim() ?? '';
                    if (text.isEmpty) return null;
                    final parsed = double.tryParse(text);
                    if (parsed == null) return 'Enter a valid amount';
                    if (parsed < 0) return 'Cannot be negative';
                    return null;
                  },
                ),
              ],
            ),
          ),
          WorkOrderFormTabSpec(
            label: 'Assign',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [tenantField, gap, vendorField],
            ),
          ),
        ],
      ),
    );
  }
}
