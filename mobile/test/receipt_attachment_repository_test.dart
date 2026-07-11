import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/money/receipt_attachment_repository.dart';

void main() {
  test(
    'uploadReceipt attaches a payment receipt through the documents API',
    () async {
      final adapter = _RecordingAdapter();
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = ReceiptAttachmentRepository(dio);

      final document = await repo.uploadReceipt(
        entityType: ReceiptEntityType.payment,
        entityId: 17,
        bytes: Uint8List.fromList([1, 2, 3]),
        fileName: 'rent-receipt.jpg',
        contentType: 'image/jpeg',
        clientOperationId: 'receipt-upload-17',
      );

      expect(adapter.method, 'POST');
      expect(adapter.path, '/documents');
      expect(adapter.data, isA<FormData>());
      final formData = adapter.data! as FormData;
      expect(_field(formData, 'entityType'), 'Payment');
      expect(_field(formData, 'entityId'), '17');
      expect(_field(formData, 'category'), 'Receipt');
      expect(_field(formData, 'clientOperationId'), 'receipt-upload-17');
      expect(formData.files.single.key, 'file');
      expect(formData.files.single.value.filename, 'rent-receipt.jpg');
      expect(document.id, 33);
      expect(document.entityType, 'Payment');
      expect(document.entityId, 17);
    },
  );

  test(
    'uploadReceipt attaches an expense receipt through the documents API',
    () async {
      final adapter = _RecordingAdapter();
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = ReceiptAttachmentRepository(dio);

      await repo.uploadReceipt(
        entityType: ReceiptEntityType.expense,
        entityId: 22,
        bytes: Uint8List.fromList([4, 5, 6]),
        fileName: 'supply-receipt.pdf',
        contentType: 'application/pdf',
      );

      final formData = adapter.data! as FormData;
      expect(_field(formData, 'entityType'), 'Expense');
      expect(_field(formData, 'entityId'), '22');
      expect(_field(formData, 'category'), 'Receipt');
      expect(formData.files.single.value.filename, 'supply-receipt.pdf');
    },
  );
}

String? _field(FormData data, String key) {
  for (final entry in data.fields) {
    if (entry.key == key) return entry.value;
  }
  return null;
}

class _RecordingAdapter implements HttpClientAdapter {
  String? method;
  String? path;
  Object? data;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    method = options.method;
    path = options.path;
    data = options.data;

    return ResponseBody.fromString(
      jsonEncode({
        'id': 33,
        'fileName': 'rent-receipt.jpg',
        'contentType': 'image/jpeg',
        'sizeBytes': 3,
        'entityType': 'Payment',
        'entityId': 17,
        'isImage': true,
        'uploadedAt': '2026-06-28T00:00:00.000Z',
        'category': 'Receipt',
      }),
      201,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}
