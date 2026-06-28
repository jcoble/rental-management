import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/money/money_repository.dart';

void main() {
  test(
    'createExpense posts to the expenses endpoint and parses the response',
    () async {
      final adapter = _RecordingAdapter();
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = MoneyRepository(dio);

      final expense = await repo.createExpense({
        'description': 'Hardware supplies',
        'amount': 42.5,
        'category': 'Repairs',
        'status': 'Paid',
        'incurredAt': '2026-06-28T00:00:00.000Z',
        'paidAt': '2026-06-28T00:00:00.000Z',
        'billableToOwner': false,
      });

      expect(adapter.method, 'POST');
      expect(adapter.path, '/expenses');
      expect(adapter.data, containsPair('description', 'Hardware supplies'));
      expect(expense.id, 42);
      expect(expense.description, 'Hardware supplies');
    },
  );
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
        'id': 42,
        'portfolioId': 7,
        'description': 'Hardware supplies',
        'category': 'Repairs',
        'status': 'Paid',
        'amount': 42.5,
        'incurredAt': '2026-06-28T00:00:00.000Z',
        'paidAt': '2026-06-28T00:00:00.000Z',
        'billableToOwner': false,
        'hasReceipt': false,
        'receiptIsImage': false,
        'lineItems': [],
        'createdAt': '2026-06-28T00:00:00.000Z',
        'updatedAt': '2026-06-28T00:00:00.000Z',
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
