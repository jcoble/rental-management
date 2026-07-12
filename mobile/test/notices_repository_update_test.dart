import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/notices/notices_repository.dart';

void main() {
  test('update PATCHes the notice and parses the response', () async {
    final adapter = _Adapter();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = NoticesRepository(dio);

    final draft = await repo.update(7, subject: 'New subj', body: 'New body');

    expect(adapter.method, 'PATCH');
    expect(adapter.path, '/notices/7');
    expect(adapter.data, containsPair('subject', 'New subj'));
    expect(adapter.data, containsPair('body', 'New body'));
    expect(draft.id, 7);
    expect(draft.subject, 'New subj');
  });

  test(
    'generate sends canonical recipient, relationship, account, and ledger scope',
    () async {
      final adapter = _Adapter();
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = NoticesRepository(dio);

      final drafts = await repo.generateScoped(
        3,
        leaseManagementId: 10,
        tenantAccountId: 12,
        tenantLedgerEntryId: 8,
        noticeType: 'RentReminder',
      );

      expect(adapter.method, 'POST');
      expect(adapter.path, '/notices/generate');
      expect(adapter.data, containsPair('recipientTenantId', 3));
      expect(adapter.data, containsPair('leaseManagementId', 10));
      expect(adapter.data, containsPair('tenantAccountId', 12));
      expect(adapter.data, containsPair('tenantLedgerEntryId', 8));
      expect(adapter.data, containsPair('noticeType', 'RentReminder'));
      expect(drafts.single.tenantLedgerEntryId, 8);
    },
  );
}

class _Adapter implements HttpClientAdapter {
  String? method;
  String? path;
  Map<String, dynamic>? data;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    method = options.method;
    path = options.path;
    data = options.data as Map<String, dynamic>?;
    if (options.path == '/notices/generate') {
      return ResponseBody.fromString(
        jsonEncode({
          'createdCount': 1,
          'drafts': [
            {
              'id': 9,
              'leaseManagementId': 10,
              'tenantAccountId': 12,
              'tenantLedgerEntryId': 8,
              'recipientTenantId': 3,
              'tenantName': 'Jordan Lee',
              'noticeType': 'RentReminder',
              'status': 'Draft',
              'subject': 'Upcoming rent',
              'body': 'Rent is due soon.',
              'reason': '',
              'triggerDate': '2026-06-29T00:00:00.000Z',
            },
          ],
        }),
        200,
        headers: {
          Headers.contentTypeHeader: [Headers.jsonContentType],
        },
      );
    }
    return ResponseBody.fromString(
      jsonEncode({
        'id': 7,
        'leaseManagementId': 10,
        'tenantAccountId': 12,
        'recipientTenantId': 3,
        'tenantName': 'Jordan Lee',
        'noticeType': 'RentReminder',
        'status': 'Draft',
        'subject': 'New subj',
        'body': 'New body',
        'reason': '',
        'triggerDate': '2026-06-29T00:00:00.000Z',
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
