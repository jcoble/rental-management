import 'dart:typed_data';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:uuid/uuid.dart';

import '../../core/api/api_exception.dart';
import 'receipt_attachment_repository.dart';

Future<bool> showReceiptUploadSheet({
  required BuildContext context,
  required ReceiptEntityType entityType,
  required int entityId,
}) async {
  final uploaded = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) =>
        _ReceiptUploadSheet(entityType: entityType, entityId: entityId),
  );
  return uploaded ?? false;
}

class _ReceiptUploadSheet extends ConsumerStatefulWidget {
  const _ReceiptUploadSheet({required this.entityType, required this.entityId});

  final ReceiptEntityType entityType;
  final int entityId;

  @override
  ConsumerState<_ReceiptUploadSheet> createState() =>
      _ReceiptUploadSheetState();
}

class _ReceiptUploadSheetState extends ConsumerState<_ReceiptUploadSheet> {
  String? _uploadOperationId;
  bool _busy = false;
  String? _error;

  Future<void> _pickImage(ImageSource source) async {
    final picked = await ImagePicker().pickImage(
      source: source,
      imageQuality: 82,
      maxWidth: 1800,
      maxHeight: 1800,
    );
    if (picked == null) return;
    await _upload(
      Uint8List.fromList(await picked.readAsBytes()),
      picked.name,
      picked.mimeType ?? _mimeFromExtension(picked.name),
    );
  }

  Future<void> _pickFile() async {
    final result = await FilePicker.platform.pickFiles(
      type: FileType.custom,
      allowedExtensions: const ['pdf', 'jpg', 'jpeg', 'png', 'webp', 'heic'],
      withData: true,
    );
    final file = result?.files.single;
    if (file == null) return;
    final bytes = file.bytes;
    if (bytes == null) {
      setState(() => _error = "Couldn't read the selected file.");
      return;
    }
    await _upload(bytes, file.name, _mimeFromExtension(file.name));
  }

  Future<void> _upload(
    Uint8List bytes,
    String fileName,
    String contentType,
  ) async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await ref
          .read(receiptAttachmentRepositoryProvider)
          .uploadReceipt(
            entityType: widget.entityType,
            entityId: widget.entityId,
            bytes: bytes,
            fileName: fileName,
            contentType: contentType,
            clientOperationId: _uploadOperationId ??= const Uuid().v4(),
          );
      _uploadOperationId = null;
      if (mounted) Navigator.of(context).pop(true);
    } on ApiException catch (e) {
      if (e.statusCode >= 400 &&
          e.statusCode < 500 &&
          e.statusCode != 408 &&
          e.statusCode != 429) {
        _uploadOperationId = null;
      }
      if (mounted) {
        setState(() {
          _busy = false;
          _error = e.message;
        });
      }
    } catch (_) {
      if (mounted) {
        setState(() {
          _busy = false;
          _error = 'Something went wrong. Please try again.';
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(20, 18, 20, 20),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    'Upload receipt',
                    style: theme.textTheme.titleLarge?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                IconButton(
                  icon: const Icon(Icons.close),
                  onPressed: _busy ? null : () => Navigator.of(context).pop(),
                ),
              ],
            ),
            const SizedBox(height: 8),
            _SourceTile(
              enabled: !_busy,
              icon: Icons.photo_camera_outlined,
              title: 'Take photo',
              onTap: () => _pickImage(ImageSource.camera),
            ),
            _SourceTile(
              enabled: !_busy,
              icon: Icons.photo_library_outlined,
              title: 'Choose photo',
              onTap: () => _pickImage(ImageSource.gallery),
            ),
            _SourceTile(
              enabled: !_busy,
              icon: Icons.attach_file_outlined,
              title: 'Choose file',
              onTap: _pickFile,
            ),
            if (_error != null) ...[
              const SizedBox(height: 12),
              Text(_error!, style: TextStyle(color: cs.error)),
            ],
            if (_busy) ...[
              const SizedBox(height: 16),
              const LinearProgressIndicator(),
            ],
          ],
        ),
      ),
    );
  }
}

class _SourceTile extends StatelessWidget {
  const _SourceTile({
    required this.enabled,
    required this.icon,
    required this.title,
    required this.onTap,
  });

  final bool enabled;
  final IconData icon;
  final String title;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return ListTile(
      enabled: enabled,
      contentPadding: EdgeInsets.zero,
      leading: Icon(icon),
      title: Text(title),
      onTap: enabled ? onTap : null,
    );
  }
}

String _mimeFromExtension(String filename) {
  final lower = filename.toLowerCase();
  if (lower.endsWith('.pdf')) return 'application/pdf';
  if (lower.endsWith('.png')) return 'image/png';
  if (lower.endsWith('.webp')) return 'image/webp';
  if (lower.endsWith('.heic')) return 'image/heic';
  if (lower.endsWith('.heif')) return 'image/heif';
  return 'image/jpeg';
}
