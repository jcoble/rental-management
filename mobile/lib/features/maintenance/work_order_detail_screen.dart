import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../properties/properties_repository.dart';
import '../vendors/dispatch_vendor_sheet.dart';
import '../vendors/rate_vendor_sheet.dart';
import 'work_order_timeline.dart';
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

class _WorkOrderDetailScreenState
    extends ConsumerState<WorkOrderDetailScreen> {
  bool _statusUpdating = false;
  bool _uploadingPhoto = false;

  Future<void> _refresh() => ref
      .read(workOrderDetailProvider(widget.workOrderId).notifier)
      .refresh();

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
      await ref.read(workOrdersRepositoryProvider).uploadWorkOrderPhoto(
            workOrderId: widget.workOrderId,
            bytes: bytes,
            fileName: picked.name,
            contentType: _mimeFromExtension(picked.name),
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

  @override
  Widget build(BuildContext context) {
    final detailAsync =
        ref.watch(workOrderDetailProvider(widget.workOrderId));
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Work Order'),
        actions: [
          detailAsync.whenOrNull(
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
            statusUpdating: _statusUpdating,
            uploadingPhoto: _uploadingPhoto,
            onTransition: _changeStatus,
            onAddPhoto: _showPhotoSourceSheet,
            onNavigate: () => _navigateToProperty(detail.workOrder),
            onDispatchVendor: _dispatchVendor,
            onRateVendor: () => _rateVendor(detail.workOrder),
          ),
        ),
      ),
    );
  }
}

// ── Detail body ───────────────────────────────────────────────────────────────

class _DetailBody extends StatelessWidget {
  const _DetailBody({
    required this.detail,
    required this.colorScheme,
    required this.theme,
    required this.statusUpdating,
    required this.uploadingPhoto,
    required this.onTransition,
    required this.onAddPhoto,
    required this.onNavigate,
    required this.onDispatchVendor,
    required this.onRateVendor,
  });

  final WorkOrderDetail detail;
  final ColorScheme colorScheme;
  final ThemeData theme;
  final bool statusUpdating;
  final bool uploadingPhoto;
  final void Function(String) onTransition;
  final VoidCallback onAddPhoto;
  final VoidCallback onNavigate;
  final VoidCallback onDispatchVendor;
  final VoidCallback onRateVendor;

  @override
  Widget build(BuildContext context) {
    final workOrder = detail.workOrder;
    final otherStatuses =
        _allStatuses.where((s) => s != workOrder.status).toList();
    final isCompleted = workOrder.status.toLowerCase() == 'completed';
    final hasVendor = workOrder.vendorId != null;

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
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            _PriorityChip(
                priority: workOrder.priority, colorScheme: colorScheme),
            _StatusChip(status: workOrder.status, colorScheme: colorScheme),
            _CategoryChip(
                category: workOrder.category, colorScheme: colorScheme),
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
            if (!isCompleted && workOrder.status.toLowerCase() != 'cancelled')
              FilledButton.icon(
                onPressed: onDispatchVendor,
                icon: const Icon(Icons.sms_outlined),
                label: const Text('Text a vendor'),
              ),
            if (isCompleted && hasVendor)
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
            workOrder: workOrder, colorScheme: colorScheme, theme: theme),

        const SizedBox(height: 20),

        // ── Photos ─────────────────────────────────────────────────────
        Row(
          children: [
            Expanded(child: _SectionLabel(label: 'Photos', theme: theme)),
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
            child: Text(
              text,
              style: TextStyle(color: cs.onSurfaceVariant),
            ),
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
          body: Center(
            child: InteractiveViewer(child: Image.memory(bytes)),
          ),
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
          child: Image.memory(
            bytes,
            width: 96,
            height: 96,
            fit: BoxFit.cover,
          ),
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
            style:
                theme.textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w700),
          ),
          const SizedBox(height: 4),
          Text(
            'Add an optional note for the timeline.',
            style: theme.textTheme.bodyMedium
                ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
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
