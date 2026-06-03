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
      return BankingSummary.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<BankTransaction>> transactions({String? status}) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/banking/transactions',
        queryParameters: status == null ? null : {'status': status},
      );
      return (response.data ?? [])
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
