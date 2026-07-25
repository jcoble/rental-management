import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:uuid/uuid.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'banking_models.dart';

class BankingRepository {
  BankingRepository(this._dio);

  final Dio _dio;
  static const _uuid = Uuid();

  Map<String, dynamic> _operation(DateTime expectedUpdatedAt) => {
    'operationKey': _uuid.v4(),
    'expectedUpdatedAtUtc': expectedUpdatedAt.toUtc().toIso8601String(),
  };

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

  Future<List<BankTransaction>> transactions({String? status}) async {
    try {
      final query = <String, dynamic>{'skip': 0, 'take': 50};
      if (status != null) query['status'] = status;
      final response = await _dio.get<Map<String, dynamic>>(
        '/banking/transactions',
        queryParameters: query,
      );
      return (response.data?['items'] as List? ?? [])
          .whereType<Map<String, dynamic>>()
          .map(BankTransaction.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> match(BankTransaction transaction) async {
    if (transaction.suggestedMatch == null) return;
    try {
      await _dio.post<Map<String, dynamic>>(
        '/banking/transactions/${transaction.id}/confirm-match',
        data: _operation(transaction.updatedAt),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> clearMatch(BankTransaction transaction) async {
    try {
      await _dio.post<Map<String, dynamic>>(
        '/banking/transactions/${transaction.id}/clear-match',
        data: _operation(transaction.updatedAt),
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
      final body = _operation(expectedUpdatedAt);
      if (tenantAccountId != null) body['tenantAccountId'] = tenantAccountId;
      if (tenantLedgerEntryId != null) {
        body['tenantLedgerEntryId'] = tenantLedgerEntryId;
      }
      if (expenseId != null) body['expenseId'] = expenseId;
      await _dio.post<Map<String, dynamic>>(
        '/banking/transactions/$transactionId/confirm-match',
        data: body,
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
      await _dio.post<Map<String, dynamic>>(
        '/banking/transactions/$transactionId/dismiss-match',
        data: _operation(expectedUpdatedAt),
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
      await _dio.put<Map<String, dynamic>>(
        '/banking/transactions/${transaction.id}/route',
        data: {..._operation(transaction.updatedAt), 'propertyId': propertyId},
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
    FutureProvider.autoDispose<List<BankTransaction>>((ref) {
      return ref.watch(bankingRepositoryProvider).transactions();
    });

final bankingReviewQueueProvider = FutureProvider.autoDispose<BankReviewQueue>((
  ref,
) {
  return ref.watch(bankingRepositoryProvider).reviewQueue();
});

final bankingRoutingPropertiesProvider =
    FutureProvider.autoDispose<List<BankRoutingProperty>>((ref) {
      return ref.watch(bankingRepositoryProvider).routingProperties();
    });
