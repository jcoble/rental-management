import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'expense_models.dart';
import 'transaction_models.dart';

/// Repository for the Money tab: the unified accounting ledger plus full
/// expense CRUD and receipt streaming.
///
/// Endpoints:
///   GET    /accounting/transactions   — unified paged ledger (Payments+Expenses)
///   GET    /expenses                  — expense list
///   GET    /expenses/{id}             — single expense (with line items)
///   POST   /expenses                  — create
///   PATCH  /expenses/{id}             — update (partial)
///   DELETE /expenses/{id}             — delete
///   GET    /expenses/{id}/receipt     — original scanned receipt bytes
class MoneyRepository {
  const MoneyRepository(this._dio);

  final Dio _dio;

  /// Fetches one page of the unified ledger. Server-side aggregation +
  /// pagination — never load-then-loop client-side.
  Future<AccountingTransactionsPage> transactions({
    required int skip,
    required int take,
    TransactionsFilter filter = const TransactionsFilter(),
  }) async {
    try {
      final params = <String, dynamic>{
        'skip': skip,
        'take': take,
        // Newest-entered first, matching the web ledger default.
        'sort': '-createdAt',
      };
      if (filter.kind != null) params['kind'] = filter.kind;
      if (filter.from != null) {
        params['from'] = filter.from!.toIso8601String();
      }
      if (filter.to != null) params['to'] = filter.to!.toIso8601String();

      final response = await _dio.get<Map<String, dynamic>>(
        '/accounting/transactions',
        queryParameters: params,
      );
      return AccountingTransactionsPage.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<Expense>> listExpenses({int? propertyId, int take = 100}) async {
    try {
      final params = <String, dynamic>{'take': take, 'sort': '-incurredAt'};
      if (propertyId != null) params['propertyId'] = propertyId;
      final response = await _dio.get<List<dynamic>>(
        '/expenses',
        queryParameters: params,
      );
      final data = response.data ?? const [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(Expense.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<Expense> getExpense(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/expenses/$id');
      return Expense.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// PATCH /expenses/{id} — partial update. Pass enum wire names for
  /// category/status. Omit a key to leave it unchanged.
  Future<Expense> updateExpense(int id, Map<String, dynamic> data) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/expenses/$id',
        data: data,
      );
      return Expense.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> deleteExpense(int id) async {
    try {
      await _dio.delete<void>('/expenses/$id');
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Streams the original scanned receipt bytes. The Dio auth interceptor
  /// attaches the Bearer token (a plain Image.network cannot send headers).
  Future<Uint8List> receiptBytes(int id) async {
    try {
      final response = await _dio.get<List<int>>(
        '/expenses/$id/receipt',
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final moneyRepositoryProvider = Provider<MoneyRepository>((ref) {
  return MoneyRepository(ref.watch(dioProvider));
});

// ── Expenses list ────────────────────────────────────────────────────────────

/// The portfolio's expenses, newest-incurred first. autoDispose so the list
/// refreshes on each visit.
final expensesListProvider = FutureProvider.autoDispose<List<Expense>>((ref) {
  return ref.watch(moneyRepositoryProvider).listExpenses();
});

/// A single expense (with line items), keyed by id.
final expenseDetailProvider =
    FutureProvider.autoDispose.family<Expense, int>((ref, id) {
  return ref.watch(moneyRepositoryProvider).getExpense(id);
});

/// Receipt bytes for an expense, keyed by id.
final expenseReceiptProvider =
    FutureProvider.autoDispose.family<Uint8List, int>((ref, id) {
  return ref.watch(moneyRepositoryProvider).receiptBytes(id);
});
