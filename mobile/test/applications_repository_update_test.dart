import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/applications/applications_repository.dart';

void main() {
  test('update sends a PATCH correction for submitted applications', () async {
    final adapter = _RecordingAdapter();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = ApplicationsRepository(dio);

    final app = await repo.update(
      42,
      const UpdateApplicationInput(
        firstName: 'JESSE',
        lastName: 'NATHANIEL COBLE',
        email: 'updated@example.test',
        phone: '555-0101',
        dateOfBirth: '1988-05-04',
        currentAddress: '123 Updated Ave',
        employer: 'Updated Employer',
        monthlyIncome: 6100,
        clearDesiredMoveInDate: true,
        notes: 'Corrected by landlord',
      ),
    );

    expect(adapter.method, 'PATCH');
    expect(adapter.path, '/applications/42');
    expect(adapter.data, isA<Map<String, dynamic>>());
    final body = adapter.data! as Map<String, dynamic>;
    expect(body['firstName'], 'JESSE');
    expect(body['lastName'], 'NATHANIEL COBLE');
    expect(body['email'], 'updated@example.test');
    expect(body['dateOfBirth'], '1988-05-04');
    expect(body['clearDesiredMoveInDate'], isTrue);
    expect(app.firstName, 'JESSE');
    expect(app.lastName, 'NATHANIEL COBLE');
  });

  test('delete sends DELETE for an application', () async {
    final adapter = _RecordingAdapter();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = ApplicationsRepository(dio);

    await repo.delete(42);

    expect(adapter.method, 'DELETE');
    expect(adapter.path, '/applications/42');
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

    if (options.method == 'DELETE') {
      return ResponseBody.fromString('', 204);
    }

    return ResponseBody.fromString(
      jsonEncode({
        'id': 42,
        'propertyId': null,
        'unitId': null,
        'firstName': 'JESSE',
        'lastName': 'NATHANIEL COBLE',
        'email': 'updated@example.test',
        'phone': '555-0101',
        'dateOfBirth': '1988-05-04T00:00:00.000Z',
        'currentAddress': '123 Updated Ave',
        'employer': 'Updated Employer',
        'monthlyIncome': 6100,
        'desiredMoveInDate': null,
        'notes': 'Corrected by landlord',
        'consentGiven': true,
        'consentAtUtc': null,
        'status': 'Submitted',
        'decisionReason': null,
        'submittedAtUtc': '2026-06-28T00:00:00.000Z',
        'reviewedAtUtc': null,
        'approvedTenantId': null,
        'testId': 'application-42',
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
