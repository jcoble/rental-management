import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';

import '../../core/api/api_exception.dart';
import '../../core/files/document_opener.dart';
import '../home/mobile_quick_action_fab.dart';
import 'inspections_list_screen.dart'
    show InspectionStatusChip, fmtInspectionDate;
import 'inspections_models.dart';
import 'inspections_repository.dart';

String _mimeFromExtension(String filename) {
  final lower = filename.toLowerCase();
  if (lower.endsWith('.png')) return 'image/png';
  if (lower.endsWith('.webp')) return 'image/webp';
  if (lower.endsWith('.heic')) return 'image/heic';
  return 'image/jpeg';
}

/// The on-site inspection run screen. Maria walks the unit, marks each item
/// Pass/Fail/N/A, adds a note + photo, then completes — which generates a PDF
/// report and auto-creates a work order per failed item. Read-only once done.
class InspectionRunScreen extends ConsumerStatefulWidget {
  const InspectionRunScreen({super.key, required this.inspectionId});

  final int inspectionId;

  @override
  ConsumerState<InspectionRunScreen> createState() =>
      _InspectionRunScreenState();
}

class _InspectionRunScreenState extends ConsumerState<InspectionRunScreen> {
  bool _completing = false;
  int? _busyItemId;

  InspectionDetailNotifier get _notifier =>
      ref.read(inspectionDetailProvider(widget.inspectionId).notifier);

  void _showSnack(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(message)));
  }

  Future<void> _setResult(InspectionItem item, String result) async {
    // Toggle off back to Pending if the same result is tapped again.
    final next = item.result == result ? InspectionItemResults.pending : result;
    setState(() => _busyItemId = item.id);
    try {
      final updated = await ref
          .read(inspectionsRepositoryProvider)
          .patchItem(widget.inspectionId, item.id, result: next);
      _notifier.replaceItem(updated);
    } on ApiException catch (e) {
      _showSnack(e.message);
    } finally {
      if (mounted) setState(() => _busyItemId = null);
    }
  }

  Future<void> _editNote(InspectionItem item) async {
    final controller = TextEditingController(text: item.note ?? '');
    final note = await showModalBottomSheet<String>(
      context: context,
      isScrollControlled: true,
      builder: (sheetCtx) {
        final inset = MediaQuery.of(sheetCtx).viewInsets.bottom;
        return Padding(
          padding: EdgeInsets.fromLTRB(16, 16, 16, 16 + inset),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(item.label, style: Theme.of(sheetCtx).textTheme.titleMedium),
              const SizedBox(height: 12),
              TextField(
                controller: controller,
                autofocus: true,
                minLines: 2,
                maxLines: 5,
                decoration: const InputDecoration(
                  labelText: 'Note',
                  hintText: 'What did you find?',
                  border: OutlineInputBorder(),
                ),
              ),
              const SizedBox(height: 12),
              FilledButton(
                onPressed: () =>
                    Navigator.of(sheetCtx).pop(controller.text.trim()),
                child: const Text('Save note'),
              ),
            ],
          ),
        );
      },
    );
    controller.dispose();
    if (note == null) return;

    setState(() => _busyItemId = item.id);
    try {
      final updated = await ref
          .read(inspectionsRepositoryProvider)
          .patchItem(widget.inspectionId, item.id, note: note);
      _notifier.replaceItem(updated);
    } on ApiException catch (e) {
      _showSnack(e.message);
    } finally {
      if (mounted) setState(() => _busyItemId = null);
    }
  }

  Future<void> _addPhoto(InspectionItem item, ImageSource source) async {
    final picked = await ImagePicker().pickImage(
      source: source,
      imageQuality: 80,
      maxWidth: 1600,
      maxHeight: 1600,
    );
    if (picked == null || !mounted) return;

    setState(() => _busyItemId = item.id);
    try {
      final repo = ref.read(inspectionsRepositoryProvider);
      final bytes = Uint8List.fromList(await picked.readAsBytes());
      final storedFileId = await repo.uploadInspectionPhoto(
        inspectionId: widget.inspectionId,
        bytes: bytes,
        fileName: picked.name,
        contentType: _mimeFromExtension(picked.name),
      );
      final updated = await repo.attachPhoto(
        widget.inspectionId,
        item.id,
        storedFileId,
      );
      _notifier.replaceItem(updated);
      _showSnack('Photo added.');
    } on ApiException catch (e) {
      _showSnack(e.message);
    } finally {
      if (mounted) setState(() => _busyItemId = null);
    }
  }

  void _showPhotoSheet(InspectionItem item) {
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
                _addPhoto(item, ImageSource.camera);
              },
            ),
            ListTile(
              leading: const Icon(Icons.photo_library_outlined),
              title: const Text('Choose from gallery'),
              onTap: () {
                Navigator.of(sheetCtx).pop();
                _addPhoto(item, ImageSource.gallery);
              },
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _complete(InspectionDetail detail) async {
    final pending = detail.items
        .where((i) => i.result == InspectionItemResults.pending)
        .length;
    final confirm = await showDialog<bool>(
      context: context,
      builder: (dialogCtx) => AlertDialog(
        title: const Text('Complete inspection?'),
        content: Text(
          pending > 0
              ? '$pending item${pending == 1 ? '' : 's'} are still pending. '
                    'Completing now generates the report and creates a work order '
                    'for each failed item. You can\'t edit afterward.'
              : 'This generates the PDF report and creates a work order for each '
                    'failed item. You can\'t edit afterward.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogCtx).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogCtx).pop(true),
            child: const Text('Complete'),
          ),
        ],
      ),
    );
    if (confirm != true) return;

    setState(() => _completing = true);
    try {
      final result = await ref
          .read(inspectionsRepositoryProvider)
          .complete(widget.inspectionId);
      await _notifier.refresh();
      if (!mounted) return;
      await showDialog<void>(
        context: context,
        builder: (_) => _CompleteSummaryDialog(
          result: result,
          inspectionId: widget.inspectionId,
        ),
      );
    } on ApiException catch (e) {
      _showSnack(e.message);
    } finally {
      if (mounted) setState(() => _completing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final detailAsync = ref.watch(
      inspectionDetailProvider(widget.inspectionId),
    );

    return Scaffold(
      appBar: AppBar(
        title: const Text('Inspection'),
        actions: [
          detailAsync.whenOrNull(
                data: (detail) =>
                    detail.isCompleted && detail.reportStoredFileId != null
                    ? IconButton(
                        tooltip: 'View report',
                        icon: const Icon(Icons.picture_as_pdf_outlined),
                        onPressed: () =>
                            _openReport(context, ref, widget.inspectionId),
                      )
                    : const SizedBox.shrink(),
              ) ??
              const SizedBox.shrink(),
        ],
      ),
      body: detailAsync.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => Center(
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(
                  Icons.error_outline,
                  size: 40,
                  color: Theme.of(context).colorScheme.error,
                ),
                const SizedBox(height: 12),
                Text(
                  e is ApiException ? e.message : e.toString(),
                  textAlign: TextAlign.center,
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
                const SizedBox(height: 16),
                FilledButton.tonal(
                  onPressed: _notifier.refresh,
                  child: const Text('Retry'),
                ),
              ],
            ),
          ),
        ),
        data: (detail) {
          final body = _RunBody(
            detail: detail,
            busyItemId: _busyItemId,
            completing: _completing,
            onSetResult: _setResult,
            onEditNote: _editNote,
            onAddPhoto: _showPhotoSheet,
            onComplete: () => _complete(detail),
            onViewReport: () => _openReport(context, ref, widget.inspectionId),
          );
          return detail.isCompleted
              ? body
              : MobileQuickActionHider(child: body);
        },
      ),
    );
  }
}

/// Fetches the PDF report bytes (authed) and hands them to the OS viewer via
/// [DocumentOpener] (FileProvider `content://` URI, share-sheet fallback).
Future<void> _openReport(
  BuildContext context,
  WidgetRef ref,
  int inspectionId,
) async {
  final messenger = ScaffoldMessenger.of(context);
  messenger
    ..hideCurrentSnackBar()
    ..showSnackBar(const SnackBar(content: Text('Opening report…')));
  try {
    final bytes = await ref
        .read(inspectionsRepositoryProvider)
        .reportBytes(inspectionId);
    await DocumentOpener.openBytes(
      bytes: bytes,
      fileName: 'inspection-$inspectionId-report.pdf',
    );
  } on ApiException catch (e) {
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(e.message)));
  }
}

class _RunBody extends StatelessWidget {
  const _RunBody({
    required this.detail,
    required this.busyItemId,
    required this.completing,
    required this.onSetResult,
    required this.onEditNote,
    required this.onAddPhoto,
    required this.onComplete,
    required this.onViewReport,
  });

  final InspectionDetail detail;
  final int? busyItemId;
  final bool completing;
  final void Function(InspectionItem, String) onSetResult;
  final void Function(InspectionItem) onEditNote;
  final void Function(InspectionItem) onAddPhoto;
  final VoidCallback onComplete;
  final VoidCallback onViewReport;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final readOnly = detail.isCompleted;

    // Group items by area, preserving the (sorted) incoming order.
    final groups = <String, List<InspectionItem>>{};
    for (final item in detail.items) {
      final area = item.area.isEmpty ? 'General' : item.area;
      groups.putIfAbsent(area, () => []).add(item);
    }

    final passed = detail.items
        .where((i) => i.result == InspectionItemResults.pass)
        .length;
    final failed = detail.items
        .where((i) => i.result == InspectionItemResults.fail)
        .length;
    final pending = detail.items
        .where((i) => i.result == InspectionItemResults.pending)
        .length;

    return Column(
      children: [
        // Header summary card.
        Container(
          width: double.infinity,
          color: cs.surfaceContainerHighest,
          padding: const EdgeInsets.fromLTRB(16, 12, 16, 12),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      friendlyInspectionType(detail.type),
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                  InspectionStatusChip(status: detail.status),
                ],
              ),
              const SizedBox(height: 4),
              Text(
                'Scheduled ${fmtInspectionDate(detail.scheduledFor.toLocal())}',
                style: theme.textTheme.bodySmall?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              ),
              if (detail.items.isNotEmpty) ...[
                const SizedBox(height: 8),
                Wrap(
                  spacing: 8,
                  children: [
                    _CountPill(label: 'Pass', count: passed, color: cs.primary),
                    _CountPill(label: 'Fail', count: failed, color: cs.error),
                    _CountPill(
                      label: 'Pending',
                      count: pending,
                      color: cs.onSurfaceVariant,
                    ),
                  ],
                ),
              ],
            ],
          ),
        ),
        Expanded(
          child: detail.items.isEmpty
              ? Center(
                  child: Padding(
                    padding: const EdgeInsets.all(24),
                    child: Text(
                      'This inspection has no checklist items.',
                      textAlign: TextAlign.center,
                      style: TextStyle(color: cs.onSurfaceVariant),
                    ),
                  ),
                )
              : ListView(
                  padding: const EdgeInsets.fromLTRB(16, 12, 16, 120),
                  children: [
                    for (final entry in groups.entries) ...[
                      Padding(
                        padding: const EdgeInsets.fromLTRB(4, 12, 4, 8),
                        child: Text(
                          entry.key.toUpperCase(),
                          style: theme.textTheme.labelMedium?.copyWith(
                            color: cs.primary,
                            fontWeight: FontWeight.w700,
                            letterSpacing: 0.5,
                          ),
                        ),
                      ),
                      for (final item in entry.value)
                        _ItemCard(
                          item: item,
                          readOnly: readOnly,
                          busy: busyItemId == item.id,
                          onSetResult: (r) => onSetResult(item, r),
                          onEditNote: () => onEditNote(item),
                          onAddPhoto: () => onAddPhoto(item),
                        ),
                    ],
                  ],
                ),
        ),
        if (!readOnly && detail.items.isNotEmpty)
          SafeArea(
            top: false,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
              child: SizedBox(
                height: 54,
                width: double.infinity,
                child: FilledButton.icon(
                  onPressed: completing ? null : onComplete,
                  icon: completing
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Icons.check_circle_outline),
                  label: Text(completing ? 'Completing…' : 'Complete'),
                ),
              ),
            ),
          ),
        if (readOnly && detail.reportStoredFileId != null)
          SafeArea(
            top: false,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
              child: SizedBox(
                height: 54,
                width: double.infinity,
                child: FilledButton.tonalIcon(
                  onPressed: onViewReport,
                  icon: const Icon(Icons.picture_as_pdf_outlined),
                  label: const Text('View report'),
                ),
              ),
            ),
          ),
      ],
    );
  }
}

class _CountPill extends StatelessWidget {
  const _CountPill({
    required this.label,
    required this.count,
    required this.color,
  });

  final String label;
  final int count;
  final Color color;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.14),
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        '$count $label',
        style: TextStyle(
          color: color,
          fontSize: 12,
          fontWeight: FontWeight.w600,
        ),
      ),
    );
  }
}

class _ItemCard extends StatelessWidget {
  const _ItemCard({
    required this.item,
    required this.readOnly,
    required this.busy,
    required this.onSetResult,
    required this.onEditNote,
    required this.onAddPhoto,
  });

  final InspectionItem item;
  final bool readOnly;
  final bool busy;
  final void Function(String) onSetResult;
  final VoidCallback onEditNote;
  final VoidCallback onAddPhoto;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Card(
      margin: const EdgeInsets.only(bottom: 10),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(14, 12, 14, 12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    item.label,
                    style: theme.textTheme.titleSmall?.copyWith(
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ),
                if (busy)
                  const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  ),
              ],
            ),
            const SizedBox(height: 10),
            // Big Pass / Fail / N/A toggles.
            Row(
              children: [
                Expanded(
                  child: _ResultButton(
                    label: 'Pass',
                    icon: Icons.check,
                    color: cs.primary,
                    selected: item.result == InspectionItemResults.pass,
                    enabled: !readOnly && !busy,
                    onTap: () => onSetResult(InspectionItemResults.pass),
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: _ResultButton(
                    label: 'Fail',
                    icon: Icons.close,
                    color: cs.error,
                    selected: item.result == InspectionItemResults.fail,
                    enabled: !readOnly && !busy,
                    onTap: () => onSetResult(InspectionItemResults.fail),
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: _ResultButton(
                    label: 'N/A',
                    icon: Icons.remove,
                    color: cs.onSurfaceVariant,
                    selected:
                        item.result == InspectionItemResults.notApplicable,
                    enabled: !readOnly && !busy,
                    onTap: () =>
                        onSetResult(InspectionItemResults.notApplicable),
                  ),
                ),
              ],
            ),
            if (item.note != null && item.note!.trim().isNotEmpty) ...[
              const SizedBox(height: 10),
              Container(
                width: double.infinity,
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: cs.surfaceContainerHighest,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Text(item.note!, style: theme.textTheme.bodySmall),
              ),
            ],
            if (item.photoStoredFileId != null) ...[
              const SizedBox(height: 10),
              _ItemPhoto(storedFileId: item.photoStoredFileId!),
            ],
            if (item.spawnedWorkOrderId != null) ...[
              const SizedBox(height: 8),
              Row(
                children: [
                  Icon(Icons.build_outlined, size: 14, color: cs.error),
                  const SizedBox(width: 4),
                  Text(
                    'Created work order #${item.spawnedWorkOrderId}',
                    style: theme.textTheme.bodySmall?.copyWith(color: cs.error),
                  ),
                ],
              ),
            ],
            if (!readOnly) ...[
              const SizedBox(height: 6),
              Row(
                children: [
                  TextButton.icon(
                    onPressed: busy ? null : onEditNote,
                    icon: const Icon(Icons.edit_note, size: 18),
                    label: Text(
                      item.note != null && item.note!.trim().isNotEmpty
                          ? 'Edit note'
                          : 'Add note',
                    ),
                  ),
                  TextButton.icon(
                    onPressed: busy ? null : onAddPhoto,
                    icon: const Icon(Icons.camera_alt_outlined, size: 18),
                    label: Text(
                      item.photoStoredFileId != null
                          ? 'Replace photo'
                          : 'Add photo',
                    ),
                  ),
                ],
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _ResultButton extends StatelessWidget {
  const _ResultButton({
    required this.label,
    required this.icon,
    required this.color,
    required this.selected,
    required this.enabled,
    required this.onTap,
  });

  final String label;
  final IconData icon;
  final Color color;
  final bool selected;
  final bool enabled;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final bg = selected ? color : cs.surfaceContainerHighest;
    final fg = selected ? _onColor(color, cs) : cs.onSurfaceVariant;
    return Material(
      color: bg,
      borderRadius: BorderRadius.circular(12),
      child: InkWell(
        onTap: enabled ? onTap : null,
        borderRadius: BorderRadius.circular(12),
        child: Container(
          height: 56,
          alignment: Alignment.center,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(icon, color: fg, size: 22),
              const SizedBox(height: 2),
              Text(
                label,
                style: TextStyle(
                  color: fg,
                  fontWeight: FontWeight.w700,
                  fontSize: 13,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Color _onColor(Color c, ColorScheme cs) {
    if (c == cs.primary) return cs.onPrimary;
    if (c == cs.error) return cs.onError;
    return cs.onSurface;
  }
}

class _ItemPhoto extends ConsumerWidget {
  const _ItemPhoto({required this.storedFileId});

  final int storedFileId;

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
    final cs = Theme.of(context).colorScheme;
    final bytesAsync = ref.watch(inspectionPhotoBytesProvider(storedFileId));
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

class _CompleteSummaryDialog extends ConsumerWidget {
  const _CompleteSummaryDialog({
    required this.result,
    required this.inspectionId,
  });

  final CompleteInspectionResult result;
  final int inspectionId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final created = result.createdWorkOrderIds;

    return AlertDialog(
      title: const Text('Inspection complete'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              _CountPill(
                label: 'Pass',
                count: result.passCount,
                color: cs.primary,
              ),
              _CountPill(
                label: 'Fail',
                count: result.failCount,
                color: cs.error,
              ),
              if (result.notApplicableCount > 0)
                _CountPill(
                  label: 'N/A',
                  count: result.notApplicableCount,
                  color: cs.onSurfaceVariant,
                ),
              if (result.pendingCount > 0)
                _CountPill(
                  label: 'Pending',
                  count: result.pendingCount,
                  color: cs.onSurfaceVariant,
                ),
            ],
          ),
          if (created.isNotEmpty) ...[
            const SizedBox(height: 16),
            Text(
              'Created ${created.length} work order'
              '${created.length == 1 ? '' : 's'}:',
              style: theme.textTheme.bodyMedium?.copyWith(
                fontWeight: FontWeight.w600,
              ),
            ),
            const SizedBox(height: 4),
            Text(
              created.map((id) => '#$id').join(', '),
              style: theme.textTheme.bodySmall?.copyWith(
                color: cs.onSurfaceVariant,
              ),
            ),
          ] else ...[
            const SizedBox(height: 16),
            Text(
              'No failed items — no work orders created.',
              style: theme.textTheme.bodySmall?.copyWith(
                color: cs.onSurfaceVariant,
              ),
            ),
          ],
        ],
      ),
      actions: [
        if (result.reportStoredFileId != null)
          TextButton.icon(
            onPressed: () {
              Navigator.of(context).pop();
              _openReport(context, ref, inspectionId);
            },
            icon: const Icon(Icons.picture_as_pdf_outlined, size: 18),
            label: const Text('View report'),
          ),
        FilledButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Done'),
        ),
      ],
    );
  }
}
