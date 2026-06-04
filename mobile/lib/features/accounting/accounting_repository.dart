import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'accounting_models.dart';

/// Repository for the accounting endpoints.
///
/// Endpoints:
///   GET /api/v1/accounting/snapshot
class AccountingRepository {
  const AccountingRepository(this._dio);

  final Dio _dio;

  /// Fetches the plain-English money snapshot for the caller's portfolio.
  Future<MoneySnapshot> snapshot() async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/accounting/snapshot');
      return MoneySnapshot.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final accountingRepositoryProvider = Provider<AccountingRepository>((ref) {
  return AccountingRepository(ref.watch(dioProvider));
});

/// The landlord home money snapshot. autoDispose so it refreshes on each visit.
final moneySnapshotProvider = FutureProvider.autoDispose<MoneySnapshot>((ref) {
  return ref.watch(accountingRepositoryProvider).snapshot();
});
