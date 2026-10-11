import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:uuid/uuid.dart';

import '../../core/api/api_exception.dart';
import 'tenant_portal_repository.dart';

enum CreateTenantWorkOrderResult { submitted, submittedPhotoFailed }

class TenantWorkOrderPhotoAttachment {
  const TenantWorkOrderPhotoAttachment({
    required this.bytes,
    required this.fileName,
    required this.contentType,
    required this.clientOperationId,
  });

  final Uint8List bytes;
  final String fileName;
  final String contentType;
  final String clientOperationId;
}

Future<CreateTenantWorkOrderResult?> showCreateTenantWorkOrderSheet({
  required BuildContext context,
  required WidgetRef ref,
}) {
  return showModalBottomSheet<CreateTenantWorkOrderResult>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    useSafeArea: true,
    builder: (_) => UncontrolledProviderScope(
      container: ProviderScope.containerOf(context, listen: false),
      child: const CreateTenantWorkOrderSheet(),
    ),
  );
}

class CreateTenantWorkOrderSheet extends ConsumerStatefulWidget {
  const CreateTenantWorkOrderSheet({
    super.key,
    this.initialPhoto,
    this.imagePicker,
  });

  final TenantWorkOrderPhotoAttachment? initialPhoto;
  final ImagePicker? imagePicker;

  @override
  ConsumerState<CreateTenantWorkOrderSheet> createState() =>
      _CreateTenantWorkOrderSheetState();
}

class _CreateTenantWorkOrderSheetState
    extends ConsumerState<CreateTenantWorkOrderSheet> {
  final _formKey = GlobalKey<FormState>();
  final _title = TextEditingController();
  final _description = TextEditingController();
  late final ImagePicker _imagePicker = widget.imagePicker ?? ImagePicker();
  String _priority = 'Normal';
  TenantWorkOrderPhotoAttachment? _photo;
  String? _error;
  bool _submitting = false;

  @override
  void initState() {
    super.initState();
    _photo = widget.initialPhoto;
  }

  @override
  void dispose() {
    _title.dispose();
    _description.dispose();
    super.dispose();
  }

  Future<void> _pickPhoto(ImageSource source) async {
    final picked = await _imagePicker.pickImage(
      source: source,
      imageQuality: 80,
      maxWidth: 1600,
      maxHeight: 1600,
    );
    if (picked == null) return;
    final bytes = Uint8List.fromList(await picked.readAsBytes());
    if (!mounted) return;
    setState(() {
      _photo = TenantWorkOrderPhotoAttachment(
        bytes: bytes,
        fileName: picked.name,
        contentType: _mimeFromExtension(picked.name),
        clientOperationId: const Uuid().v4(),
      );
    });
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    setState(() {
      _submitting = true;
      _error = null;
    });

    try {
      final repo = ref.read(tenantPortalRepositoryProvider);
      final created = await repo.createWorkOrder(
        title: _title.text.trim(),
        description: _description.text.trim(),
        priority: _priority,
      );

      final photo = _photo;
      if (photo != null) {
        try {
          await repo.uploadWorkOrderPhoto(
            workOrderId: created.id,
            bytes: photo.bytes,
            fileName: photo.fileName,
            contentType: photo.contentType,
            clientOperationId: photo.clientOperationId,
          );
        } on ApiException {
          _finish(CreateTenantWorkOrderResult.submittedPhotoFailed);
          return;
        }
      }

      _finish(CreateTenantWorkOrderResult.submitted);
    } on ApiException catch (error) {
      if (!mounted) return;
      setState(() {
        _error = error.message;
        _submitting = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _error = "We couldn't submit that repair. Please try again.";
        _submitting = false;
      });
    }
  }

  void _finish(CreateTenantWorkOrderResult result) {
    ref.invalidate(tenantPortalSnapshotProvider);
    ref.invalidate(tenantPortalWorkOrdersPageProvider);
    if (mounted) Navigator.of(context).pop(result);
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final viewInsets = MediaQuery.viewInsetsOf(context);

    return Padding(
      padding: EdgeInsets.only(bottom: viewInsets.bottom),
      child: SafeArea(
        top: false,
        child: Form(
          key: _formKey,
          child: ListView(
            shrinkWrap: true,
            padding: const EdgeInsets.fromLTRB(20, 0, 20, 20),
            children: [
              Text(
                'Add repair',
                style: theme.textTheme.titleLarge?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
              ),
              const SizedBox(height: 16),
              TextFormField(
                key: const Key('tenant-repair-title-field'),
                controller: _title,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(labelText: 'Title'),
                validator: (value) =>
                    value == null || value.trim().isEmpty ? 'Required' : null,
              ),
              const SizedBox(height: 12),
              TextFormField(
                key: const Key('tenant-repair-description-field'),
                controller: _description,
                minLines: 3,
                maxLines: 5,
                decoration: const InputDecoration(labelText: 'Description'),
                validator: (value) =>
                    value == null || value.trim().isEmpty ? 'Required' : null,
              ),
              const SizedBox(height: 12),
              DropdownButtonFormField<String>(
                key: const Key('tenant-repair-priority-field'),
                initialValue: _priority,
                decoration: const InputDecoration(labelText: 'Priority'),
                items: const ['Low', 'Normal', 'High', 'Emergency']
                    .map(
                      (priority) => DropdownMenuItem<String>(
                        value: priority,
                        child: Text(priority),
                      ),
                    )
                    .toList(growable: false),
                onChanged: _submitting
                    ? null
                    : (value) => setState(() => _priority = value ?? 'Normal'),
              ),
              const SizedBox(height: 12),
              if (_photo != null) ...[
                ClipRRect(
                  borderRadius: BorderRadius.circular(8),
                  child: Image.memory(
                    _photo!.bytes,
                    height: 140,
                    width: double.infinity,
                    fit: BoxFit.cover,
                  ),
                ),
                const SizedBox(height: 8),
              ],
              Row(
                children: [
                  Expanded(
                    child: OutlinedButton.icon(
                      onPressed: _submitting
                          ? null
                          : () => _pickPhoto(ImageSource.camera),
                      icon: const Icon(Icons.camera_alt_outlined),
                      label: Text(_photo == null ? 'Camera' : 'Retake'),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: OutlinedButton.icon(
                      onPressed: _submitting
                          ? null
                          : () => _pickPhoto(ImageSource.gallery),
                      icon: const Icon(Icons.photo_library_outlined),
                      label: const Text('Gallery'),
                    ),
                  ),
                ],
              ),
              if (_error != null) ...[
                const SizedBox(height: 12),
                Text(
                  _error!,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: colorScheme.error,
                  ),
                ),
              ],
              const SizedBox(height: 18),
              FilledButton(
                key: const Key('tenant-repair-submit-button'),
                onPressed: _submitting ? null : _submit,
                child: Text(_submitting ? 'Submitting...' : 'Submit repair'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

String _mimeFromExtension(String filename) {
  final lower = filename.toLowerCase();
  if (lower.endsWith('.png')) return 'image/png';
  if (lower.endsWith('.webp')) return 'image/webp';
  if (lower.endsWith('.heic')) return 'image/heic';
  return 'image/jpeg';
}
