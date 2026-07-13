import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/leases/leases_repository.dart';

void main() {
  test('successor agreement draft posts a fresh idempotency key', () async {
    final adapter = _RecordingAdapter();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = LeaseManagementsRepository(dio);

    final draft = await repo.createSuccessorDraft(
      leaseManagementId: 44,
      sourceAgreementId: 81,
      operation: 'renew',
      termStartOn: DateTime(2027, 1, 1),
      termEndOn: DateTime(2027, 12, 31),
      governingFromOn: DateTime(2027, 1, 1),
    );

    expect(adapter.method, 'POST');
    expect(adapter.path, '/lease-managements/44/agreements/81/renew');
    expect(adapter.data, isA<Map<String, dynamic>>());
    final body = adapter.data! as Map<String, dynamic>;
    expect(body['termStartOn'], '2027-01-01');
    expect(body['termEndOn'], '2027-12-31');
    expect(body['governingFromOn'], '2027-01-01');
    expect(
      adapter.headers?['Idempotency-Key'],
      matches(RegExp(r'^[0-9a-f]{32}$')),
    );
    expect(draft.leaseManagementId, 44);
    expect(draft.leaseAgreementId, 82);
  });
}

class _RecordingAdapter implements HttpClientAdapter {
  String? method;
  String? path;
  Object? data;
  Map<String, dynamic>? headers;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    method = options.method;
    path = options.path;
    data = options.data;
    headers = Map<String, dynamic>.from(options.headers);

    return ResponseBody.fromString(
      jsonEncode({
        'leaseManagementId': 44,
        'leaseAgreementId': 82,
        'versionNumber': 2,
        'draftRevision': 1,
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
