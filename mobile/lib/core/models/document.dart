/// A stored document/attachment returned by the Documents hub.
///
/// Wire shape (camelCase):
///   { id, fileName, contentType, sizeBytes, entityType, entityId, isImage,
///     uploadedAt, category? }
class Document {
  final int id;
  final String fileName;
  final String contentType;
  final int sizeBytes;
  final String entityType;
  final int? entityId;
  final bool isImage;
  final DateTime uploadedAt;
  final String? category;

  const Document({
    required this.id,
    required this.fileName,
    required this.contentType,
    required this.sizeBytes,
    required this.entityType,
    this.entityId,
    required this.isImage,
    required this.uploadedAt,
    this.category,
  });

  factory Document.fromJson(Map<String, dynamic> json) {
    return Document(
      id: (json['id'] as num).toInt(),
      fileName: json['fileName'] as String? ?? '',
      contentType: json['contentType'] as String? ?? '',
      sizeBytes: (json['sizeBytes'] as num?)?.toInt() ?? 0,
      entityType: json['entityType'] as String? ?? '',
      entityId: (json['entityId'] as num?)?.toInt(),
      isImage: json['isImage'] as bool? ?? false,
      uploadedAt:
          DateTime.tryParse(json['uploadedAt'] as String? ?? '')?.toLocal() ??
          DateTime(0),
      category: json['category'] as String?,
    );
  }
}
