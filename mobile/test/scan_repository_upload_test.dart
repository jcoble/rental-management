import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/api/api_exception.dart';
import 'package:rental_command/features/scan/scan_repository.dart';

void main() {
  test(
    'upload replay after receive timeout reuses operation id and returns draft',
    () async {
      final adapter = _UploadReplayAdapter(
        firstFailure: DioExceptionType.receiveTimeout,
      );
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repository = ScanRepository(dio: dio);

      final created = await repository.uploadImage(
        Uint8List.fromList([1, 2, 3]),
        'lease.pdf',
        'application/pdf',
        targetEntityType: 'LeaseAgreement',
        clientOperationId: 'scan-op-754',
      );

      expect(created.draftId, 754);
      expect(created.status, 'Pending');
      expect(created.fileUrl, '/api/v1/scans/754/file');
      expect(adapter.requests, hasLength(2));
      expect(_field(adapter.forms[0], 'clientOperationId'), 'scan-op-754');
      expect(_field(adapter.forms[1], 'clientOperationId'), 'scan-op-754');
      expect(_field(adapter.forms[1], 'targetEntityType'), 'LeaseAgreement');
    },
  );

  test('upload connection failure is not replayed as success', () async {
    final adapter = _UploadReplayAdapter(
      firstFailure: DioExceptionType.connectionError,
    );
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repository = ScanRepository(dio: dio);

    await expectLater(
      repository.uploadImage(
        Uint8List.fromList([1, 2, 3]),
        'receipt.jpg',
        'image/jpeg',
        clientOperationId: 'scan-op-network-fail',
      ),
      throwsA(
        isA<ApiException>().having(
          (e) => e.message,
          'message',
          'Unable to connect. Check your network connection.',
        ),
      ),
    );
    expect(adapter.requests, hasLength(1));
  });
}

String? _field(FormData data, String key) {
  for (final entry in data.fields) {
    if (entry.key == key) return entry.value;
  }
  return null;
}

class _UploadReplayAdapter implements HttpClientAdapter {
  _UploadReplayAdapter({required this.firstFailure});

  final DioExceptionType firstFailure;
  final requests = <RequestOptions>[];
  final forms = <FormData>[];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(options);
    forms.add(options.data as FormData);

    if (requests.length == 1) {
      throw DioException(
        requestOptions: options,
        type: firstFailure,
        message: 'simulated upload transport failure',
      );
    }

    return ResponseBody.fromString(
      jsonEncode({
        'draftId': 754,
        'status': 'Pending',
        'fileUrl': '/api/v1/scans/754/file',
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
