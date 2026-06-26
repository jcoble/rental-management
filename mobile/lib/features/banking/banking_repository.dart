import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
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

  Future<List<BankTransaction>> transactions({String? status}) async {
    try {
      final query = <String, dynamic>{
        'skip': 0,
        'take': 50,
      };
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
    final suggestion = transaction.suggestedMatch;
    if (suggestion == null) return;
    try {
      await _dio.post<Map<String, dynamic>>(
        '/banking/transactions/${transaction.id}/match',
        data: {
          'entityType': suggestion.entityType,
          'entityId': suggestion.entityId,
        },
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> clearMatch(BankTransaction transaction) async {
    try {
      await _dio.post<Map<String, dynamic>>(
        '/banking/transactions/${transaction.id}/clear-match',
        data: {},
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Bank lines that may already be on record as a payment or expense.
  Future<BankReviewQueue> reviewQueue() async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/banking/review-queue');
      return BankReviewQueue.fromJson(response.data ?? const {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Confirm the duplicate. Omitting [paymentId]/[expenseId] accepts the
  /// backend's suggestion (the common one-tap case).
  Future<void> confirmMatch(
    int transactionId, {
    int? paymentId,
    int? expenseId,
  }) async {
    try {
      final body = <String, dynamic>{};
      if (paymentId != null) body['paymentId'] = paymentId;
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
  Future<void> dismissMatch(int transactionId) async {
    try {
      await _dio.post<Map<String, dynamic>>(
        '/banking/transactions/$transactionId/dismiss-match',
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final bankingRepositoryProvider = Provider<BankingRepository>((ref) {
  return BankingRepository(ref.watch(dioProvider));
});

final bankingSummaryProvider = FutureProvider.autoDispose<BankingSummary>((ref) {
  return ref.watch(bankingRepositoryProvider).summary();
});

final bankingTransactionsProvider =
    FutureProvider.autoDispose<List<BankTransaction>>((ref) {
  return ref.watch(bankingRepositoryProvider).transactions();
});

final bankingReviewQueueProvider =
    FutureProvider.autoDispose<BankReviewQueue>((ref) {
  return ref.watch(bankingRepositoryProvider).reviewQueue();
});
