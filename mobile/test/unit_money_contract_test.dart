import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/scan/scan_repository.dart';
import 'package:rental_command/features/units/units_repository.dart';

void main() {
  test('UnitPaymentSummary requires canonical account and relationship ids', () {
    final receipt = UnitPaymentSummary.fromJson({
      'id': 901,
      'tenantAccountId': 41,
      'leaseManagementId': 23,
      'leaseAgreementId': 77,
      'type': 'Rent',
      'status': 'Paid',
      'amount': 1200,
      'dueDate': '2026-07-01T00:00:00Z',
    });

    expect(receipt.tenantAccountId, 41);
    expect(receipt.leaseManagementId, 23);
    expect(receipt.leaseAgreementId, 77);
    expect(
      () => UnitPaymentSummary.fromJson({
        'id': 902,
        'leaseId': 23,
        'type': 'Rent',
        'status': 'Paid',
        'amount': 1200,
        'dueDate': '2026-07-01T00:00:00Z',
      }),
      throwsA(isA<TypeError>()),
      reason: 'legacy leaseId must not be accepted as an identity fallback',
    );
  });

  test('Unit scan upload sends account and relationship context', () async {
    final adapter = _RecordingAdapter();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repository = ScanRepository(dio: dio);

    await repository.uploadImage(
      Uint8List.fromList([1, 2, 3]),
      'rent-check.jpg',
      'image/jpeg',
      targetEntityType: 'Payment',
      propertyId: 7,
      unitId: 12,
      leaseManagementId: 23,
      tenantAccountId: 41,
    );

    final form = adapter.data! as FormData;
    expect(_field(form, 'propertyId'), '7');
    expect(_field(form, 'unitId'), '12');
    expect(_field(form, 'leaseManagementId'), '23');
    expect(_field(form, 'tenantAccountId'), '41');
    expect(_field(form, 'leaseId'), isNull);
  });
}

String? _field(FormData data, String key) {
  for (final entry in data.fields) {
    if (entry.key == key) return entry.value;
  }
  return null;
}

class _RecordingAdapter implements HttpClientAdapter {
  Object? data;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    data = options.data;
    return ResponseBody.fromString(
      jsonEncode({
        'draftId': 99,
        'status': 'Processing',
        'fileUrl': '/api/v1/scans/99/file',
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
