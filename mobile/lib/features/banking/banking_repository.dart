import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';
import 'banking_models.dart';

class BankingRepository {
  BankingRepository(this._dio);

  final Dio _dio;

  Future<BankingSummary> summary() async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/banking/summary');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return BankingSummary.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<({List<BankTransaction> items, int totalCount})> transactions({
    String? status,
  }) async {
    try {
      final query = <String, dynamic>{'skip': 0, 'take': 50};
      if (status != null) query['status'] = status;
      final response = await _dio.get<Map<String, dynamic>>(
        '/banking/transactions',
        queryParameters: query,
      );
      final items = (response.data?['items'] as List? ?? [])
          .whereType<Map<String, dynamic>>()
          .map(BankTransaction.fromJson)
          .toList();
      return (
        items: items,
        totalCount:
            (response.data?['totalCount'] as num?)?.toInt() ?? items.length,
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> match(BankTransaction transaction) async {
    if (transaction.suggestedMatch == null) return;
    try {
      await IdempotentMutation.run(
        'banking:match:${transaction.id}',
        (key) => _dio.post<Map<String, dynamic>>(
          '/banking/transactions/${transaction.id}/confirm-match',
          data: {
            'operationKey': key,
            'expectedUpdatedAtUtc': transaction.updatedAt
                .toUtc()
                .toIso8601String(),
          },
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> clearMatch(BankTransaction transaction) async {
    try {
      await IdempotentMutation.run(
        'banking:clear-match:${transaction.id}',
        (key) => _dio.post<Map<String, dynamic>>(
          '/banking/transactions/${transaction.id}/clear-match',
          data: {
            'operationKey': key,
            'expectedUpdatedAtUtc': transaction.updatedAt
                .toUtc()
                .toIso8601String(),
          },
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Bank lines that may already be on record as a payment or expense.
  Future<BankReviewQueue> reviewQueue() async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/banking/review-queue',
      );
      return BankReviewQueue.fromJson(response.data ?? const {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Confirm the duplicate. Omitting the receipt identity/[expenseId] accepts the
  /// backend's suggestion (the common one-tap case).
  Future<void> confirmMatch(
    int transactionId, {
    required DateTime expectedUpdatedAt,
    int? tenantAccountId,
    int? tenantLedgerEntryId,
    int? expenseId,
  }) async {
    try {
      await IdempotentMutation.run(
        'banking:confirm-match:$transactionId',
        (key) => _dio.post<Map<String, dynamic>>(
          '/banking/transactions/$transactionId/confirm-match',
          data: {
            'operationKey': key,
            'expectedUpdatedAtUtc': expectedUpdatedAt.toUtc().toIso8601String(),
            'tenantAccountId': ?tenantAccountId,
            'tenantLedgerEntryId': ?tenantLedgerEntryId,
            'expenseId': ?expenseId,
          },
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Not a duplicate — keep the bank line on its own.
  Future<void> dismissMatch(
    int transactionId,
    DateTime expectedUpdatedAt,
  ) async {
    try {
      await IdempotentMutation.run(
        'banking:dismiss-match:$transactionId',
        (key) => _dio.post<Map<String, dynamic>>(
          '/banking/transactions/$transactionId/dismiss-match',
          data: {
            'operationKey': key,
            'expectedUpdatedAtUtc': expectedUpdatedAt.toUtc().toIso8601String(),
          },
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<BankRoutingProperty>> routingProperties() async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/properties',
        queryParameters: const {'take': 200},
      );
      return (response.data ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(BankRoutingProperty.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> routeTransaction(
    BankTransaction transaction,
    int? propertyId,
  ) async {
    try {
      await IdempotentMutation.run(
        'banking:route:${transaction.id}',
        (key) => _dio.put<Map<String, dynamic>>(
          '/banking/transactions/${transaction.id}/route',
          data: {
            'operationKey': key,
            'expectedUpdatedAtUtc': transaction.updatedAt
                .toUtc()
                .toIso8601String(),
            'propertyId': propertyId,
          },
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final bankingRepositoryProvider = Provider<BankingRepository>((ref) {
  return BankingRepository(ref.watch(dioProvider));
});

final bankingSummaryProvider = FutureProvider.autoDispose<BankingSummary>((
  ref,
) {
  return ref.watch(bankingRepositoryProvider).summary();
});

final bankingTransactionsProvider =
    FutureProvider.autoDispose<({List<BankTransaction> items, int totalCount})>(
      (ref) {
        return ref.watch(bankingRepositoryProvider).transactions();
      },
    );

final bankingReviewQueueProvider = FutureProvider.autoDispose<BankReviewQueue>((
  ref,
) {
  return ref.watch(bankingRepositoryProvider).reviewQueue();
});

final bankingRoutingPropertiesProvider =
    FutureProvider.autoDispose<List<BankRoutingProperty>>((ref) {
      return ref.watch(bankingRepositoryProvider).routingProperties();
    });
