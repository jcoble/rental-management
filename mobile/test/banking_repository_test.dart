import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/banking/banking_models.dart';
import 'package:rental_command/features/banking/banking_repository.dart';
import 'package:rental_command/features/banking/banking_screen.dart';

void main() {
  test('failed banking mutation retry reuses its operation key', () async {
    final adapter = _BankingAdapter(failFirstMutation: true);
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repository = BankingRepository(dio);
    final transaction = _transaction();

    await expectLater(repository.match(transaction), throwsA(anything));
    await repository.match(transaction);

    expect(adapter.operationKeys, hasLength(2));
    expect(adapter.operationKeys[1], adapter.operationKeys[0]);
  });

  test('transactions retain the server total count', () async {
    final adapter = _BankingAdapter(totalCount: 73);
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;

    final page = await BankingRepository(dio).transactions();

    expect(page.items, hasLength(1));
    expect(page.totalCount, 73);
  });

  testWidgets('review card ignores a second tap while save is in flight', (
    tester,
  ) async {
    final repository = _PendingBankingRepository();
    final item = BankReviewItem(
      transaction: BankReviewTransaction(
        id: 42,
        postedAt: DateTime.utc(2026, 8, 25),
        amount: 25,
        description: 'Rent',
        matchStatus: 'Suggested',
        updatedAt: DateTime.utc(2026, 8, 25),
      ),
      suggestion: const BankReviewSuggestion(
        confidence: .9,
        label: 'Payment',
        reason: 'Same amount',
      ),
    );

    await tester.pumpWidget(
      ProviderScope(
        overrides: [bankingRepositoryProvider.overrideWithValue(repository)],
        child: MaterialApp(
          home: Scaffold(body: BankReviewCard(item: item, canDismiss: true)),
        ),
      ),
    );

    await tester.tap(find.text('Confirm'));
    await tester.pump();
    await tester.tap(find.text('Confirm'), warnIfMissed: false);

    expect(repository.confirmCalls, 1);
    repository.pending.complete();
    await tester.pump();
  });
}

BankTransaction _transaction() => BankTransaction(
  id: 42,
  postedAt: DateTime.utc(2026, 8, 25),
  description: 'Rent',
  amount: 25,
  matchStatus: 'Suggested',
  suggestedMatch: const BankMatchSuggestion(
    entityType: 'Payment',
    entityId: 7,
    confidence: .9,
    label: 'Payment',
    reason: 'Same amount',
  ),
  institutionName: 'Bank',
  accountName: 'Checking',
  updatedAt: DateTime.utc(2026, 8, 25),
);

class _PendingBankingRepository extends BankingRepository {
  _PendingBankingRepository() : super(Dio());

  final pending = Completer<void>();
  int confirmCalls = 0;

  @override
  Future<void> confirmMatch(
    int transactionId, {
    required DateTime expectedUpdatedAt,
    int? tenantAccountId,
    int? tenantLedgerEntryId,
    int? expenseId,
  }) {
    confirmCalls++;
    return pending.future;
  }
}

class _BankingAdapter implements HttpClientAdapter {
  _BankingAdapter({this.failFirstMutation = false, this.totalCount = 1});

  final bool failFirstMutation;
  final int totalCount;
  final operationKeys = <String>[];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    if (options.method == 'POST') {
      operationKeys.add(
        (options.data as Map<String, dynamic>)['operationKey'] as String,
      );
      if (failFirstMutation && operationKeys.length == 1) {
        return ResponseBody.fromString(
          '{}',
          500,
          headers: {
            Headers.contentTypeHeader: [Headers.jsonContentType],
          },
        );
      }
      return ResponseBody.fromString(
        '{}',
        200,
        headers: {
          Headers.contentTypeHeader: [Headers.jsonContentType],
        },
      );
    }
    return ResponseBody.fromString(
      jsonEncode({
        'items': [_transactionJson()],
        'totalCount': totalCount,
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

Map<String, dynamic> _transactionJson() => {
  'id': 42,
  'postedAt': '2026-08-25T00:00:00Z',
  'description': 'Rent',
  'amount': 25,
  'matchStatus': 'Suggested',
  'institutionName': 'Bank',
  'accountName': 'Checking',
  'updatedAt': '2026-08-25T00:00:00Z',
};
