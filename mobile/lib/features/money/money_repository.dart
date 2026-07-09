import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../properties/capital_assets_repository.dart';
import 'expense_models.dart';
import 'transaction_models.dart';

class ExpenseListQuery {
  const ExpenseListQuery({
    this.skip = 0,
    this.take = 20,
    this.propertyId,
    this.unitId,
    this.workOrderId,
    this.workOrderLinkedOnly = false,
    this.search,
    this.sort = '-incurredAt',
    this.incurredFrom,
    this.incurredTo,
    this.dueFrom,
    this.dueTo,
    this.paidFrom,
    this.paidTo,
  });

  final int skip;
  final int take;
  final int? propertyId;
  final int? unitId;
  final int? workOrderId;
  final bool workOrderLinkedOnly;
  final String? search;
  final String sort;
  final String? incurredFrom;
  final String? incurredTo;
  final String? dueFrom;
  final String? dueTo;
  final String? paidFrom;
  final String? paidTo;

  @override
  bool operator ==(Object other) {
    return other is ExpenseListQuery &&
        other.skip == skip &&
        other.take == take &&
        other.propertyId == propertyId &&
        other.unitId == unitId &&
        other.workOrderId == workOrderId &&
        other.workOrderLinkedOnly == workOrderLinkedOnly &&
        other.search == search &&
        other.sort == sort &&
        other.incurredFrom == incurredFrom &&
        other.incurredTo == incurredTo &&
        other.dueFrom == dueFrom &&
        other.dueTo == dueTo &&
        other.paidFrom == paidFrom &&
        other.paidTo == paidTo;
  }

  @override
  int get hashCode => Object.hash(
    skip,
    take,
    propertyId,
    unitId,
    workOrderId,
    workOrderLinkedOnly,
    search,
    sort,
    incurredFrom,
    incurredTo,
    dueFrom,
    dueTo,
    paidFrom,
    paidTo,
  );
}

class ExpenseListPage {
  const ExpenseListPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<Expense> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory ExpenseListPage.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final items = rawItems is List
        ? rawItems
              .whereType<Map<String, dynamic>>()
              .map(Expense.fromJson)
              .toList()
        : <Expense>[];

    return ExpenseListPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

/// Repository for the Money tab: the unified accounting ledger plus full
/// expense CRUD and receipt streaming.
///
/// Endpoints:
///   GET    /accounting/transactions   — unified paged ledger (Payments+Expenses)
///   GET    /expenses                  — expense list
///   GET    /expenses/{id}             — single expense (with line items)
///   POST   /expenses                  — create
///   PATCH  /expenses/{id}             — update (partial)
///   POST   /expenses/{id}/capitalize  — convert an expense to a capital asset
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
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return AccountingTransactionsPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<Expense>> listExpenses({
    int? propertyId,
    int? unitId,
    int take = 100,
    String? incurredFrom,
    String? incurredTo,
    String? dueFrom,
    String? dueTo,
    String? paidFrom,
    String? paidTo,
  }) async {
    try {
      final params = <String, dynamic>{'take': take, 'sort': '-incurredAt'};
      if (propertyId != null) params['propertyId'] = propertyId;
      if (unitId != null) params['unitId'] = unitId;
      if (incurredFrom != null && incurredFrom.isNotEmpty) {
        params['incurredFrom'] = incurredFrom;
      }
      if (incurredTo != null && incurredTo.isNotEmpty) {
        params['incurredTo'] = incurredTo;
      }
      if (dueFrom != null && dueFrom.isNotEmpty) {
        params['dueFrom'] = dueFrom;
      }
      if (dueTo != null && dueTo.isNotEmpty) {
        params['dueTo'] = dueTo;
      }
      if (paidFrom != null && paidFrom.isNotEmpty) {
        params['paidFrom'] = paidFrom;
      }
      if (paidTo != null && paidTo.isNotEmpty) {
        params['paidTo'] = paidTo;
      }
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

  Future<ExpenseListPage> listExpensesPage([
    ExpenseListQuery query = const ExpenseListQuery(),
  ]) async {
    final params = <String, dynamic>{
      'skip': query.skip,
      'take': query.take,
      'propertyId': query.propertyId,
      'unitId': query.unitId,
      'workOrderId': query.workOrderId,
      if (query.workOrderLinkedOnly) 'workOrderLinkedOnly': true,
      'search': query.search,
      'sort': query.sort,
      'incurredFrom': query.incurredFrom,
      'incurredTo': query.incurredTo,
      'dueFrom': query.dueFrom,
      'dueTo': query.dueTo,
      'paidFrom': query.paidFrom,
      'paidTo': query.paidTo,
    }..removeWhere((_, value) => value == null || value == '');

    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/expenses/page',
        queryParameters: params,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ExpenseListPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<Expense> getExpense(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/expenses/$id');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Expense.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// POST /expenses — create a manual expense. Pass enum wire names for
  /// category/status.
  Future<Expense> createExpense(Map<String, dynamic> data) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/expenses',
        data: data,
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Expense.fromJson(responseData);
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
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Expense.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<CapitalAsset> capitalizeExpense(
    int id,
    Map<String, dynamic> data,
  ) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/expenses/$id/capitalize',
        data: data,
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return CapitalAsset.fromJson(responseData);
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

final expensesPageProvider = FutureProvider.autoDispose
    .family<ExpenseListPage, ExpenseListQuery>((ref, query) {
      return ref.watch(moneyRepositoryProvider).listExpensesPage(query);
    });

/// Unit-scoped operating costs, filtered and capped by the API.
final unitExpensesProvider = FutureProvider.autoDispose
    .family<List<Expense>, int>((ref, unitId) {
      return ref
          .watch(moneyRepositoryProvider)
          .listExpenses(unitId: unitId, take: 20);
    });

/// A single expense (with line items), keyed by id.
final expenseDetailProvider = FutureProvider.autoDispose.family<Expense, int>((
  ref,
  id,
) {
  return ref.watch(moneyRepositoryProvider).getExpense(id);
});

/// Receipt bytes for an expense, keyed by id.
final expenseReceiptProvider = FutureProvider.autoDispose
    .family<Uint8List, int>((ref, id) {
      return ref.watch(moneyRepositoryProvider).receiptBytes(id);
    });
