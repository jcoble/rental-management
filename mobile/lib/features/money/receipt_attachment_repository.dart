import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/models/document.dart';

enum ReceiptEntityType {
  payment('Payment'),
  expense('Expense');

  const ReceiptEntityType(this.wireName);

  final String wireName;
}

/// Uploads receipt files directly onto finance entities through the shared
/// documents hub. The backend already authorizes Payment and Expense entity
/// attachments and the existing scan/receipt endpoints display the latest file.
class ReceiptAttachmentRepository {
  const ReceiptAttachmentRepository(this._dio);

  final Dio _dio;

  Future<Document> uploadReceipt({
    required ReceiptEntityType entityType,
    required int entityId,
    required Uint8List bytes,
    required String fileName,
    required String contentType,
    required String clientOperationId,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/documents',
        data: FormData.fromMap({
          'file': MultipartFile.fromBytes(
            bytes,
            filename: fileName,
            contentType: DioMediaType.parse(contentType),
          ),
          'entityType': entityType.wireName,
          'entityId': entityId,
          'category': 'Receipt',
          'clientOperationId': clientOperationId,
        }),
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Document.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final receiptAttachmentRepositoryProvider =
    Provider<ReceiptAttachmentRepository>((ref) {
      return ReceiptAttachmentRepository(ref.watch(dioProvider));
    });
