import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:uuid/uuid.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../maintenance/work_order_timeline.dart';
import '../maintenance/work_orders_repository.dart';
import 'tenant_portal_repository.dart';

/// Read-only maintenance-request detail for tenants: shows the request summary
/// plus its live status timeline (Received -> ... -> Done) so they can follow
/// progress without messaging the landlord.
class TenantWorkOrderDetailScreen extends ConsumerStatefulWidget {
  const TenantWorkOrderDetailScreen({super.key, required this.workOrderId});

  final int workOrderId;

  @override
  ConsumerState<TenantWorkOrderDetailScreen> createState() =>
      _TenantWorkOrderDetailScreenState();
}

class _TenantWorkOrderDetailScreenState
    extends ConsumerState<TenantWorkOrderDetailScreen> {
  final _commentController = TextEditingController();
  bool _uploadingPhoto = false;
  bool _submittingComment = false;
  bool _savingRequest = false;
  bool _cancellingRequest = false;

  @override
  void dispose() {
    _commentController.dispose();
    super.dispose();
  }

  void _showError(Object error) {
    if (!mounted) return;
    final message = error is ApiException ? error.message : error.toString();
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(message)));
  }

  String _mimeFromExtension(String filename) {
    final lower = filename.toLowerCase();
    if (lower.endsWith('.png')) return 'image/png';
    if (lower.endsWith('.webp')) return 'image/webp';
    if (lower.endsWith('.heic')) return 'image/heic';
    return 'image/jpeg';
  }

  Future<void> _pickAndUploadPhoto(ImageSource source) async {
    final picker = ImagePicker();
    final picked = await picker.pickImage(
      source: source,
      maxWidth: 1800,
      imageQuality: 85,
    );
    if (picked == null || !mounted) return;
    Navigator.of(context).maybePop();
    setState(() => _uploadingPhoto = true);
    try {
      final Uint8List bytes = await picked.readAsBytes();
      await ref
          .read(tenantPortalRepositoryProvider)
          .uploadWorkOrderPhoto(
            workOrderId: widget.workOrderId,
            bytes: bytes,
            fileName: picked.name,
            contentType: picked.mimeType ?? _mimeFromExtension(picked.name),
            clientOperationId: const Uuid().v4(),
          );
      ref.invalidate(tenantWorkOrderDetailProvider(widget.workOrderId));
    } catch (error) {
      _showError(error);
    } finally {
      if (mounted) setState(() => _uploadingPhoto = false);
    }
  }

  Future<void> _showPhotoSourceSheet() async {
    await showModalBottomSheet<void>(
      context: context,
      builder: (context) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            ListTile(
              leading: const Icon(Icons.photo_camera_outlined),
              title: const Text('Take photo'),
              onTap: () => _pickAndUploadPhoto(ImageSource.camera),
            ),
            ListTile(
              leading: const Icon(Icons.photo_library_outlined),
              title: const Text('Choose photo'),
              onTap: () => _pickAndUploadPhoto(ImageSource.gallery),
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _submitComment() async {
    final body = _commentController.text.trim();
    if (body.isEmpty) return;
    setState(() => _submittingComment = true);
    try {
      await ref
          .read(tenantPortalRepositoryProvider)
          .commentWorkOrder(widget.workOrderId, body: body);
      _commentController.clear();
      ref.invalidate(tenantWorkOrderDetailProvider(widget.workOrderId));
    } catch (error) {
      _showError(error);
    } finally {
      if (mounted) setState(() => _submittingComment = false);
    }
  }

  Future<void> _showEditRequestSheet(WorkOrder workOrder) async {
    final result = await showModalBottomSheet<Map<String, dynamic>>(
      context: context,
      isScrollControlled: true,
      builder: (_) => _TenantRequestEditSheet(workOrder: workOrder),
    );
    if (result == null || !mounted) return;
    setState(() => _savingRequest = true);
    try {
      await ref
          .read(tenantPortalRepositoryProvider)
          .updateWorkOrder(widget.workOrderId, result);
      ref.invalidate(tenantWorkOrderDetailProvider(widget.workOrderId));
    } catch (error) {
      _showError(error);
    } finally {
      if (mounted) setState(() => _savingRequest = false);
    }
  }

  Future<void> _showCancelSheet() async {
    final note = await showModalBottomSheet<String?>(
      context: context,
      isScrollControlled: true,
      builder: (_) => const _TenantCancelSheet(),
    );
    if (note == null || !mounted) return;
    setState(() => _cancellingRequest = true);
    try {
      await ref
          .read(tenantPortalRepositoryProvider)
          .cancelWorkOrder(widget.workOrderId, note: note);
      ref.invalidate(tenantWorkOrderDetailProvider(widget.workOrderId));
    } catch (error) {
      _showError(error);
    } finally {
      if (mounted) setState(() => _cancellingRequest = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final detailAsync = ref.watch(
      tenantWorkOrderDetailProvider(widget.workOrderId),
    );
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(title: const Text('Request status')),
      body: RefreshIndicator(
        onRefresh: () async =>
            ref.invalidate(tenantWorkOrderDetailProvider(widget.workOrderId)),
        child: detailAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => ListView(
            padding: const EdgeInsets.all(20),
            children: [
              Icon(Icons.error_outline, color: cs.error, size: 40),
              const SizedBox(height: 12),
              Text(
                e is ApiException ? e.message : 'Could not load this request.',
                style: TextStyle(color: cs.error),
              ),
              const SizedBox(height: 16),
              FilledButton.tonal(
                onPressed: () => ref.invalidate(
                  tenantWorkOrderDetailProvider(widget.workOrderId),
                ),
                child: const Text('Retry'),
              ),
            ],
          ),
          data: (detail) {
            final roleDetail = RoleAwareWorkOrderDetail.fromBase(detail);
            final wo = roleDetail.workOrder;
            final capabilities = roleDetail.capabilities;
            final contextRows = _tenantContextRows(wo, roleDetail);
            return ListView(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.all(20),
              children: [
                Text(
                  wo.title,
                  style: theme.textTheme.headlineSmall?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
                ),
                const SizedBox(height: 8),
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 10,
                    vertical: 6,
                  ),
                  decoration: BoxDecoration(
                    color: cs.secondaryContainer,
                    borderRadius: BorderRadius.circular(20),
                  ),
                  child: Text(
                    workOrderStatusLabel(wo.status),
                    style: TextStyle(
                      fontSize: 12,
                      fontWeight: FontWeight.w600,
                      color: cs.onSecondaryContainer,
                    ),
                  ),
                ),
                if (wo.description.isNotEmpty) ...[
                  const SizedBox(height: 16),
                  Text(wo.description, style: theme.textTheme.bodyMedium),
                ],
                if (contextRows.isNotEmpty) ...[
                  const SizedBox(height: 20),
                  Text(
                    'Request details',
                    style: theme.textTheme.labelLarge?.copyWith(
                      fontWeight: FontWeight.w700,
                      color: cs.onSurfaceVariant,
                    ),
                  ),
                  const SizedBox(height: 8),
                  ...contextRows,
                ],
                if (capabilities.canUploadPhoto ||
                    capabilities.canEditRequestFields ||
                    capabilities.canCancel) ...[
                  const SizedBox(height: 16),
                  Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                      if (capabilities.canUploadPhoto)
                        FilledButton.tonalIcon(
                          onPressed: _uploadingPhoto
                              ? null
                              : _showPhotoSourceSheet,
                          icon: _uploadingPhoto
                              ? const SizedBox(
                                  width: 16,
                                  height: 16,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 2,
                                  ),
                                )
                              : const Icon(Icons.add_a_photo_outlined),
                          label: Text(
                            _uploadingPhoto ? 'Uploading...' : 'Add photo',
                          ),
                        ),
                      if (capabilities.canEditRequestFields)
                        OutlinedButton.icon(
                          onPressed: _savingRequest
                              ? null
                              : () => _showEditRequestSheet(wo),
                          icon: _savingRequest
                              ? const SizedBox(
                                  width: 16,
                                  height: 16,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 2,
                                  ),
                                )
                              : const Icon(Icons.edit_outlined),
                          label: const Text('Update request'),
                        ),
                      if (capabilities.canCancel)
                        OutlinedButton.icon(
                          onPressed: _cancellingRequest
                              ? null
                              : _showCancelSheet,
                          icon: _cancellingRequest
                              ? const SizedBox(
                                  width: 16,
                                  height: 16,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 2,
                                  ),
                                )
                              : const Icon(Icons.cancel_outlined),
                          label: const Text('Cancel request'),
                        ),
                    ],
                  ),
                ],
                if (capabilities.canCommentPublicly) ...[
                  const SizedBox(height: 20),
                  TextField(
                    controller: _commentController,
                    minLines: 2,
                    maxLines: 4,
                    textCapitalization: TextCapitalization.sentences,
                    decoration: const InputDecoration(
                      labelText: 'Add update',
                      hintText: 'Share details for the maintenance team',
                    ),
                  ),
                  const SizedBox(height: 8),
                  Align(
                    alignment: Alignment.centerRight,
                    child: FilledButton.icon(
                      onPressed: _submittingComment ? null : _submitComment,
                      icon: _submittingComment
                          ? const SizedBox(
                              width: 16,
                              height: 16,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : const Icon(Icons.send_outlined),
                      label: const Text('Send update'),
                    ),
                  ),
                ],
                if (roleDetail.activity.isNotEmpty) ...[
                  const SizedBox(height: 24),
                  Text(
                    'Activity',
                    style: theme.textTheme.labelLarge?.copyWith(
                      fontWeight: FontWeight.w700,
                      color: cs.onSurfaceVariant,
                      letterSpacing: 0.5,
                    ),
                  ),
                  const SizedBox(height: 8),
                  ...roleDetail.activity.map(
                    (item) => _ActivityTile(item: item),
                  ),
                ],
                const SizedBox(height: 24),
                Text(
                  'Progress',
                  style: theme.textTheme.labelLarge?.copyWith(
                    fontWeight: FontWeight.w700,
                    color: cs.onSurfaceVariant,
                    letterSpacing: 0.5,
                  ),
                ),
                const SizedBox(height: 8),
                WorkOrderTimeline(events: detail.timeline),
              ],
            );
          },
        ),
      ),
    );
  }
}

List<Widget> _tenantContextRows(
  WorkOrder workOrder,
  RoleAwareWorkOrderDetail detail,
) {
  final capabilities = detail.capabilities;
  final rows = <Widget>[];
  void add(String label, String? value) {
    final text = value?.trim();
    if (text == null || text.isEmpty) return;
    rows.add(_ContextRow(label: label, value: text));
  }

  if (capabilities.canViewTenantContact) {
    add('Requester', workOrder.requesterName ?? workOrder.submittedByLabel);
    add('Phone', workOrder.requesterPhone);
    add('Email', workOrder.requesterEmail);
  }
  if (capabilities.canViewResidents && detail.residentNames.isNotEmpty) {
    add('Residents', detail.residentNames.join(', '));
  }
  if (capabilities.canViewAccessInstructions) {
    if (workOrder.residentMustBePresent != null) {
      add(
        'Presence',
        workOrder.residentMustBePresent!
            ? 'Resident must be present'
            : 'Resident does not need to be present',
      );
    }
    if (workOrder.callBeforeEntry == true) add('Call before entry', 'Yes');
    if (workOrder.callIfNotHome == true) add('Call if not home', 'Yes');
    if (workOrder.permissionToEnter != null) {
      add('Permission to enter', workOrder.permissionToEnter! ? 'Yes' : 'No');
    }
    add('Entry notes', workOrder.entryNotes);
    add('Pets', workOrder.petWarnings);
    add('Access warnings', workOrder.accessWarnings);
  }
  return rows;
}

class _ContextRow extends StatelessWidget {
  const _ContextRow({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colors = theme.colorScheme;
    return Card.filled(
      margin: const EdgeInsets.only(bottom: 8),
      color: colors.surfaceContainerLowest,
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              label,
              style: theme.textTheme.labelSmall?.copyWith(
                color: colors.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 2),
            Text(value, style: theme.textTheme.bodyMedium),
          ],
        ),
      ),
    );
  }
}

class _ActivityTile extends StatelessWidget {
  const _ActivityTile({required this.item});

  final WorkOrderActivityItem item;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colors = theme.colorScheme;
    final title = item.toStatus == null
        ? item.kind
        : '${item.kind}: ${workOrderStatusLabel(item.toStatus!)}';
    return Card.filled(
      margin: const EdgeInsets.only(bottom: 8),
      color: colors.surfaceContainerLowest,
      child: ListTile(
        dense: true,
        title: Text(title),
        subtitle: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (item.note != null && item.note!.trim().isNotEmpty)
              Text(item.note!),
            if (item.actorLabel != null && item.actorLabel!.trim().isNotEmpty)
              Text(
                item.actorLabel!,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: colors.onSurfaceVariant,
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _TenantRequestEditSheet extends StatefulWidget {
  const _TenantRequestEditSheet({required this.workOrder});

  final WorkOrder workOrder;

  @override
  State<_TenantRequestEditSheet> createState() =>
      _TenantRequestEditSheetState();
}

class _TenantRequestEditSheetState extends State<_TenantRequestEditSheet> {
  late final _title = TextEditingController(text: widget.workOrder.title);
  late final _description = TextEditingController(
    text: widget.workOrder.description,
  );
  late final _requesterName = TextEditingController(
    text: widget.workOrder.requesterName ?? '',
  );
  late final _requesterPhone = TextEditingController(
    text: widget.workOrder.requesterPhone ?? '',
  );
  late final _requesterEmail = TextEditingController(
    text: widget.workOrder.requesterEmail ?? '',
  );
  late final _entryNotes = TextEditingController(
    text: widget.workOrder.entryNotes ?? '',
  );
  late final _petWarnings = TextEditingController(
    text: widget.workOrder.petWarnings ?? '',
  );
  late final _accessWarnings = TextEditingController(
    text: widget.workOrder.accessWarnings ?? '',
  );
  late bool _residentMustBePresent =
      widget.workOrder.residentMustBePresent ?? false;
  late bool _callBeforeEntry = widget.workOrder.callBeforeEntry ?? false;
  late bool _callIfNotHome = widget.workOrder.callIfNotHome ?? false;
  late bool _permissionToEnter = widget.workOrder.permissionToEnter ?? false;

  @override
  void dispose() {
    _title.dispose();
    _description.dispose();
    _requesterName.dispose();
    _requesterPhone.dispose();
    _requesterEmail.dispose();
    _entryNotes.dispose();
    _petWarnings.dispose();
    _accessWarnings.dispose();
    super.dispose();
  }

  String? _optional(TextEditingController controller) {
    final text = controller.text.trim();
    return text.isEmpty ? null : text;
  }

  @override
  Widget build(BuildContext context) {
    final bottom = MediaQuery.viewInsetsOf(context).bottom;
    return SafeArea(
      child: Padding(
        padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottom),
        child: ListView(
          shrinkWrap: true,
          children: [
            Text(
              'Update request',
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: 16),
            TextField(
              controller: _title,
              decoration: const InputDecoration(labelText: 'Title'),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _description,
              minLines: 3,
              maxLines: 5,
              decoration: const InputDecoration(labelText: 'Description'),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _requesterName,
              decoration: const InputDecoration(labelText: 'Requester name'),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _requesterPhone,
              keyboardType: TextInputType.phone,
              decoration: const InputDecoration(labelText: 'Phone'),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _requesterEmail,
              keyboardType: TextInputType.emailAddress,
              decoration: const InputDecoration(labelText: 'Email'),
            ),
            const SizedBox(height: 12),
            SwitchListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Resident must be present'),
              value: _residentMustBePresent,
              onChanged: (value) =>
                  setState(() => _residentMustBePresent = value),
            ),
            SwitchListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Call before entry'),
              value: _callBeforeEntry,
              onChanged: (value) => setState(() => _callBeforeEntry = value),
            ),
            SwitchListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Call if not home'),
              value: _callIfNotHome,
              onChanged: (value) => setState(() => _callIfNotHome = value),
            ),
            SwitchListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Permission to enter'),
              value: _permissionToEnter,
              onChanged: (value) => setState(() => _permissionToEnter = value),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _entryNotes,
              minLines: 2,
              maxLines: 4,
              decoration: const InputDecoration(labelText: 'Entry notes'),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _petWarnings,
              minLines: 2,
              maxLines: 4,
              decoration: const InputDecoration(labelText: 'Pets'),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _accessWarnings,
              minLines: 2,
              maxLines: 4,
              decoration: const InputDecoration(labelText: 'Access warnings'),
            ),
            const SizedBox(height: 20),
            FilledButton(
              onPressed: () => Navigator.of(context).pop({
                'title': _title.text.trim(),
                'description': _description.text.trim(),
                'requesterName': _optional(_requesterName),
                'requesterPhone': _optional(_requesterPhone),
                'requesterEmail': _optional(_requesterEmail),
                'residentMustBePresent': _residentMustBePresent,
                'callBeforeEntry': _callBeforeEntry,
                'callIfNotHome': _callIfNotHome,
                'permissionToEnter': _permissionToEnter,
                'entryNotes': _optional(_entryNotes),
                'petWarnings': _optional(_petWarnings),
                'accessWarnings': _optional(_accessWarnings),
              }),
              child: const Text('Save changes'),
            ),
          ],
        ),
      ),
    );
  }
}

class _TenantCancelSheet extends StatefulWidget {
  const _TenantCancelSheet();

  @override
  State<_TenantCancelSheet> createState() => _TenantCancelSheetState();
}

class _TenantCancelSheetState extends State<_TenantCancelSheet> {
  final _note = TextEditingController();

  @override
  void dispose() {
    _note.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final bottom = MediaQuery.viewInsetsOf(context).bottom;
    return SafeArea(
      child: Padding(
        padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottom),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              'Cancel request',
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _note,
              minLines: 2,
              maxLines: 4,
              decoration: const InputDecoration(labelText: 'Note'),
            ),
            const SizedBox(height: 16),
            FilledButton(
              onPressed: () => Navigator.of(context).pop(_note.text.trim()),
              child: const Text('Cancel request'),
            ),
            TextButton(
              onPressed: () => Navigator.of(context).pop(),
              child: const Text('Keep request'),
            ),
          ],
        ),
      ),
    );
  }
}
