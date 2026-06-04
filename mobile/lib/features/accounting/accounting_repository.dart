import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'accounting_models.dart';

/// Repository for the accounting endpoints.
///
/// Endpoints:
///   GET /api/v1/accounting/snapshot
///   GET /api/v1/accounting/year-end-packet?year={year}  — PDF bytes
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

  /// Fetches the year-end accountant packet PDF bytes (Schedule E, P&L, cash
  /// flow, rent roll) for [year], authed via the shared Dio interceptor.
  Future<Uint8List> yearEndPacketBytes(int year) async {
    try {
      final response = await _dio.get<List<int>>(
        '/accounting/year-end-packet',
        queryParameters: {'year': year},
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
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
