import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:uuid/uuid.dart';

import '../../core/api/api_exception.dart';
import '../../core/files/document_opener.dart';
import '../../core/models/document.dart';
import 'properties_repository.dart';

class PropertyDocumentsSection extends ConsumerStatefulWidget {
  const PropertyDocumentsSection({
    super.key,
    required this.propertyId,
    required this.canUpload,
  });

  final int propertyId;
  final bool canUpload;

  @override
  ConsumerState<PropertyDocumentsSection> createState() =>
      _PropertyDocumentsSectionState();
}

class _PropertyDocumentsSectionState
    extends ConsumerState<PropertyDocumentsSection> {
  bool _uploading = false;
  int? _downloadingId;
  String? _pendingUploadFingerprint;
  String? _pendingUploadOperationId;

  Future<void> _upload() async {
    final selection = await FilePicker.platform.pickFiles(
      type: FileType.custom,
      allowedExtensions: const [
        'pdf',
        'jpg',
        'jpeg',
        'png',
        'heic',
        'txt',
        'csv',
        'doc',
        'docx',
        'xls',
        'xlsx',
        'ppt',
        'pptx',
      ],
      withData: true,
    );
    final file = selection?.files.single;
    if (file == null) return;
    final bytes = file.bytes;
    if (bytes == null) {
      _showMessage("Couldn't read the selected file.");
      return;
    }
    final fingerprint = '${widget.propertyId}:${file.name}:${file.size}';
    final operationId = _pendingUploadFingerprint == fingerprint
        ? _pendingUploadOperationId!
        : const Uuid().v4();
    _pendingUploadFingerprint = fingerprint;
    _pendingUploadOperationId = operationId;

    setState(() => _uploading = true);
    try {
      await ref
          .read(propertiesRepositoryProvider)
          .uploadPropertyDocument(
            propertyId: widget.propertyId,
            bytes: bytes,
            fileName: file.name,
            contentType: _contentTypeFor(file.name),
            clientOperationId: operationId,
          );
      _pendingUploadFingerprint = null;
      _pendingUploadOperationId = null;
      ref.invalidate(propertyDocumentsProvider(widget.propertyId));
      _showMessage('Property document uploaded.');
    } on ApiException catch (error) {
      if (error.statusCode >= 400 &&
          error.statusCode < 500 &&
          error.statusCode != 408 &&
          error.statusCode != 429) {
        _pendingUploadFingerprint = null;
        _pendingUploadOperationId = null;
      }
      _showMessage(error.message);
    } catch (_) {
      _showMessage('Could not upload the document.');
    } finally {
      if (mounted) setState(() => _uploading = false);
    }
  }

  Future<void> _download(Document document) async {
    setState(() => _downloadingId = document.id);
    try {
      final bytes = await ref
          .read(propertiesRepositoryProvider)
          .downloadPropertyDocument(document.id);
      await DocumentOpener.openBytes(
        bytes: bytes,
        fileName: document.fileName,
        mimeType: document.contentType,
      );
    } on ApiException catch (error) {
      _showMessage(error.message);
    } catch (_) {
      _showMessage('Could not open the document.');
    } finally {
      if (mounted) setState(() => _downloadingId = null);
    }
  }

  void _showMessage(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(message)));
  }

  @override
  Widget build(BuildContext context) {
    final documents = ref.watch(propertyDocumentsProvider(widget.propertyId));
    final colors = Theme.of(context).colorScheme;

    return Card(
      key: const ValueKey('property-documents-section'),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    'Property documents',
                    style: Theme.of(context).textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                if (widget.canUpload)
                  OutlinedButton.icon(
                    onPressed: _uploading ? null : _upload,
                    icon: _uploading
                        ? const SizedBox(
                            width: 16,
                            height: 16,
                            child: CircularProgressIndicator(strokeWidth: 2),
                          )
                        : const Icon(Icons.upload_file_outlined),
                    label: Text(_uploading ? 'Uploading' : 'Upload'),
                  ),
              ],
            ),
            const SizedBox(height: 12),
            documents.when(
              loading: () => const Center(child: CircularProgressIndicator()),
              error: (error, _) => Column(
                children: [
                  Text(
                    error is ApiException
                        ? error.message
                        : 'Could not load property documents.',
                    style: TextStyle(color: colors.error),
                  ),
                  TextButton(
                    onPressed: () => ref.invalidate(
                      propertyDocumentsProvider(widget.propertyId),
                    ),
                    child: const Text('Retry'),
                  ),
                ],
              ),
              data: (items) {
                if (items.isEmpty) {
                  return Text(
                    'No property documents yet.',
                    style: TextStyle(color: colors.onSurfaceVariant),
                  );
                }
                return Column(
                  children: [
                    for (final document in items)
                      ListTile(
                        key: ValueKey('property-document-${document.id}'),
                        contentPadding: EdgeInsets.zero,
                        leading: Icon(
                          document.isImage
                              ? Icons.image_outlined
                              : Icons.description_outlined,
                        ),
                        title: Text(
                          document.fileName,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                        ),
                        subtitle: Text(
                          '${_formatSize(document.sizeBytes)} · ${_formatDate(document.uploadedAt)}',
                        ),
                        trailing: IconButton(
                          tooltip: 'Download ${document.fileName}',
                          onPressed: _downloadingId == null
                              ? () => _download(document)
                              : null,
                          icon: _downloadingId == document.id
                              ? const SizedBox(
                                  width: 18,
                                  height: 18,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 2,
                                  ),
                                )
                              : const Icon(Icons.download_outlined),
                        ),
                        onTap: _downloadingId == null
                            ? () => _download(document)
                            : null,
                      ),
                  ],
                );
              },
            ),
          ],
        ),
      ),
    );
  }
}

String _formatSize(int bytes) {
  if (bytes < 1024) return '$bytes B';
  if (bytes < 1024 * 1024) {
    return '${(bytes / 1024).toStringAsFixed(1)} KB';
  }
  return '${(bytes / (1024 * 1024)).toStringAsFixed(1)} MB';
}

String _formatDate(DateTime value) =>
    '${value.month}/${value.day}/${value.year}';

String _contentTypeFor(String fileName) {
  final lower = fileName.toLowerCase();
  if (lower.endsWith('.pdf')) return 'application/pdf';
  if (lower.endsWith('.png')) return 'image/png';
  if (lower.endsWith('.jpg') || lower.endsWith('.jpeg')) return 'image/jpeg';
  if (lower.endsWith('.heic')) return 'image/heic';
  if (lower.endsWith('.txt')) return 'text/plain';
  if (lower.endsWith('.csv')) return 'text/csv';
  if (lower.endsWith('.doc')) return 'application/msword';
  if (lower.endsWith('.docx')) {
    return 'application/vnd.openxmlformats-officedocument.wordprocessingml.document';
  }
  if (lower.endsWith('.xls')) return 'application/vnd.ms-excel';
  if (lower.endsWith('.xlsx')) {
    return 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
  }
  if (lower.endsWith('.ppt')) return 'application/vnd.ms-powerpoint';
  if (lower.endsWith('.pptx')) {
    return 'application/vnd.openxmlformats-officedocument.presentationml.presentation';
  }
  return 'application/octet-stream';
}
