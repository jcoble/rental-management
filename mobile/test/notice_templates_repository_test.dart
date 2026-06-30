import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/notices/notice_templates_repository.dart';

void main() {
  test('list parses templates with available fields', () async {
    final adapter = _Adapter(jsonEncode([
      {
        'noticeType': 'LateRentNotice',
        'subject': 'S',
        'body': 'B',
        'hasTemplate': true,
        'availableFields': ['tenant_name', 'overdue_amount'],
      },
    ]));
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = NoticeTemplatesRepository(dio);

    final list = await repo.list();

    expect(adapter.path, '/notices/templates');
    expect(list.single.noticeType, 'LateRentNotice');
    expect(list.single.availableFields, contains('overdue_amount'));
  });

  test('upsert PUTs subject and body to the typed endpoint', () async {
    final adapter = _Adapter(jsonEncode({
      'noticeType': 'RentReminder',
      'subject': 'New subj',
      'body': 'New body',
      'hasTemplate': true,
      'availableFields': ['tenant_name'],
    }));
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = NoticeTemplatesRepository(dio);

    final result = await repo.upsert(
      'RentReminder',
      subject: 'New subj',
      body: 'New body',
    );

    expect(adapter.method, 'PUT');
    expect(adapter.path, '/notices/templates/RentReminder');
    expect(adapter.data, containsPair('subject', 'New subj'));
    expect(adapter.data, containsPair('body', 'New body'));
    expect(result.hasTemplate, isTrue);
    expect(result.subject, 'New subj');
  });
}

class _Adapter implements HttpClientAdapter {
  _Adapter(this.body);
  final String body;
  String? method;
  String? path;
  Map<String, dynamic>? data;

  @override
  Future<ResponseBody> fetch(
    RequestOptions o,
    Stream<Uint8List>? s,
    Future<void>? c,
  ) async {
    method = o.method;
    path = o.path;
    data = o.data as Map<String, dynamic>?;
    return ResponseBody.fromString(
      body,
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}
