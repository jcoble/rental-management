import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/leases/leases_repository.dart';

void main() {
  test('sendForSignature posts a fresh idempotency key', () async {
    final adapter = _RecordingAdapter();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = LeasesRepository(dio);

    final status = await repo.sendForSignature(44);

    expect(adapter.method, 'POST');
    expect(adapter.path, '/leases/44/send-for-signature');
    expect(adapter.data, isA<Map<String, dynamic>>());
    final body = adapter.data! as Map<String, dynamic>;
    expect(body.keys, ['idempotencyKey']);
    expect(body['idempotencyKey'], matches(RegExp(r'^[0-9a-f]{32}$')));
    expect(status.leaseId, 44);
    expect(status.esignStatus, 'Sent');
  });
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
        'leaseId': 44,
        'esignStatus': 'Sent',
        'leaseStatus': 'PendingSignature',
        'hasSignedDocument': false,
      }),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}
