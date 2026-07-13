import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/models/models.dart';

// ── Inline model: accounting summary ─────────────────────────────────────────

class AccountingSummary {
  const AccountingSummary({
    required this.portfolioId,
    required this.collected,
    required this.outstanding,
    required this.overdue,
    required this.overdueCount,
    required this.totalExpenses,
    required this.expensesByCategory,
    required this.snapshot,
  });

  final int portfolioId;
  final double collected;
  final double outstanding;
  final double overdue;
  final int overdueCount;
  final double totalExpenses;
  final List<ExpenseCategoryTotal> expensesByCategory;
  final MoneySnapshot snapshot;

  factory AccountingSummary.fromJson(Map<String, dynamic> json) {
    final payments = json['payments'] as Map<String, dynamic>? ?? {};
    final cats = (json['expensesByCategory'] as List<dynamic>? ?? [])
        .whereType<Map<String, dynamic>>()
        .map(ExpenseCategoryTotal.fromJson)
        .toList();
    return AccountingSummary(
      portfolioId: (json['portfolioId'] as num?)?.toInt() ?? 0,
      collected: (payments['collected'] as num?)?.toDouble() ?? 0,
      outstanding: (payments['outstanding'] as num?)?.toDouble() ?? 0,
      overdue: (payments['overdue'] as num?)?.toDouble() ?? 0,
      overdueCount: (payments['overdueCount'] as num?)?.toInt() ?? 0,
      totalExpenses: (json['totalExpenses'] as num?)?.toDouble() ?? 0,
      expensesByCategory: cats,
      snapshot: MoneySnapshot.fromJson(
        json['snapshot'] as Map<String, dynamic>? ?? const {},
      ),
    );
  }
}

class PaymentListQuery {
  const PaymentListQuery({
    this.skip = 0,
    this.take = 20,
    this.tenantAccountId,
    this.leaseManagementId,
    this.search,
    this.sort = '-postedAtUtc',
    this.paidFrom,
    this.paidTo,
  });

  final int skip;
  final int take;
  final int? tenantAccountId;
  final int? leaseManagementId;
  final String? search;
  final String sort;
  final String? paidFrom;
  final String? paidTo;

  @override
  bool operator ==(Object other) {
    return other is PaymentListQuery &&
        other.skip == skip &&
        other.take == take &&
        other.tenantAccountId == tenantAccountId &&
        other.leaseManagementId == leaseManagementId &&
        other.search == search &&
        other.sort == sort &&
        other.paidFrom == paidFrom &&
        other.paidTo == paidTo;
  }

  @override
  int get hashCode => Object.hash(
    skip,
    take,
    tenantAccountId,
    leaseManagementId,
    search,
    sort,
    paidFrom,
    paidTo,
  );
}

class PaymentListPage {
  const PaymentListPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<PaymentReceipt> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory PaymentListPage.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final items = rawItems is List
        ? rawItems
              .whereType<Map<String, dynamic>>()
              .map(PaymentReceipt.fromJson)
              .toList()
        : <PaymentReceipt>[];

    return PaymentListPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

class RecordTenantReceiptInput {
  const RecordTenantReceiptInput({
    required this.amount,
    required this.effectiveOn,
    required this.description,
    required this.paymentMethodSummary,
    this.externalReference,
    this.payerName,
    this.checkNumber,
    this.bankName,
    this.sourceStoredFileId,
    this.allocateOldestCharges = true,
  });

  final double amount;
  final DateTime effectiveOn;
  final String description;
  final String paymentMethodSummary;
  final String? externalReference;
  final String? payerName;
  final String? checkNumber;
  final String? bankName;
  final int? sourceStoredFileId;
  final bool allocateOldestCharges;
}

class RecordTenantReceiptResult {
  const RecordTenantReceiptResult({
    required this.tenantAccountId,
    required this.ledgerEntryId,
    required this.paymentAttemptId,
    required this.amount,
    required this.allocatedAmount,
    required this.allocationCount,
    required this.replayed,
  });

  final int tenantAccountId;
  final int ledgerEntryId;
  final int paymentAttemptId;
  final double amount;
  final double allocatedAmount;
  final int allocationCount;
  final bool replayed;

  factory RecordTenantReceiptResult.fromJson(Map<String, dynamic> json) {
    final value = json['value'] as Map<String, dynamic>? ?? const {};
    return RecordTenantReceiptResult(
      tenantAccountId: (value['tenantAccountId'] as num).toInt(),
      ledgerEntryId: (value['ledgerEntryId'] as num).toInt(),
      paymentAttemptId: (value['paymentAttemptId'] as num).toInt(),
      amount: (value['amount'] as num).toDouble(),
      allocatedAmount: (value['allocatedAmount'] as num).toDouble(),
      allocationCount: (value['allocationCount'] as num).toInt(),
      replayed: json['replayed'] as bool? ?? false,
    );
  }
}

class MoneySnapshot {
  const MoneySnapshot({
    required this.title,
    required this.summary,
    required this.bullets,
  });

  final String title;
  final String summary;
  final List<String> bullets;

  factory MoneySnapshot.fromJson(Map<String, dynamic> json) {
    return MoneySnapshot(
      title: json['title'] as String? ?? '',
      summary: json['summary'] as String? ?? '',
      bullets: (json['bullets'] as List<dynamic>? ?? [])
          .whereType<String>()
          .toList(),
    );
  }
}

class ExpenseCategoryTotal {
  const ExpenseCategoryTotal({
    required this.category,
    required this.categoryName,
    required this.total,
    required this.count,
  });

  final String category;
  final String categoryName;
  final double total;
  final int count;

  factory ExpenseCategoryTotal.fromJson(Map<String, dynamic> json) {
    return ExpenseCategoryTotal(
      category: json['category'] as String? ?? '',
      categoryName: json['categoryName'] as String? ?? '',
      total: (json['total'] as num?)?.toDouble() ?? 0,
      count: (json['count'] as num?)?.toInt() ?? 0,
    );
  }
}

// ── Repository ────────────────────────────────────────────────────────────────

/// Handles all payment and accounting API calls.
///
/// Endpoints:
///   GET    /payments                    — immutable receipt list
///   GET    /payments/{id}               — one immutable receipt
///   POST   /tenant-accounts/{id}/receipts — record a receipt
///   GET    /accounting/summary          — AccountingSummary rollup
class PaymentsRepository {
  PaymentsRepository(this._dio);

  final Dio _dio;

  Future<List<PaymentReceipt>> listPayments({
    int? tenantAccountId,
    int? leaseManagementId,
    String sort = '-postedAtUtc',
    String? paidFrom,
    String? paidTo,
  }) async {
    try {
      final params = <String, dynamic>{};
      if (tenantAccountId != null) {
        params['tenantAccountId'] = tenantAccountId;
      }
      if (leaseManagementId != null) {
        params['leaseManagementId'] = leaseManagementId;
      }
      if (sort.isNotEmpty) params['sort'] = sort;
      if (paidFrom != null && paidFrom.isNotEmpty) {
        params['paidFrom'] = paidFrom;
      }
      if (paidTo != null && paidTo.isNotEmpty) {
        params['paidTo'] = paidTo;
      }
      final response = await _dio.get<List<dynamic>>(
        '/payments',
        queryParameters: params.isEmpty ? null : params,
      );
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(PaymentReceipt.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<PaymentListPage> listPaymentsPage([
    PaymentListQuery query = const PaymentListQuery(),
  ]) async {
    final params = <String, dynamic>{
      'skip': query.skip,
      'take': query.take,
      'tenantAccountId': query.tenantAccountId,
      'leaseManagementId': query.leaseManagementId,
      'search': query.search,
      'sort': query.sort,
      'paidFrom': query.paidFrom,
      'paidTo': query.paidTo,
    }..removeWhere((_, value) => value == null || value == '');

    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/payments/page',
        queryParameters: params,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return PaymentListPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<PaymentReceipt> getPayment(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/payments/$id');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return PaymentReceipt.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<RecordTenantReceiptResult> recordReceipt(
    int tenantAccountId,
    RecordTenantReceiptInput input, {
    required String operationKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/tenant-accounts/$tenantAccountId/receipts',
        data: <String, dynamic>{
          'amount': input.amount,
          'effectiveOn': _dateOnly(input.effectiveOn),
          'description': input.description.trim(),
          'paymentMethodSummary': input.paymentMethodSummary.trim(),
          if (input.externalReference?.trim().isNotEmpty == true)
            'externalReference': input.externalReference!.trim(),
          if (input.payerName?.trim().isNotEmpty == true)
            'payerName': input.payerName!.trim(),
          if (input.checkNumber?.trim().isNotEmpty == true)
            'checkNumber': input.checkNumber!.trim(),
          if (input.bankName?.trim().isNotEmpty == true)
            'bankName': input.bankName!.trim(),
          if (input.sourceStoredFileId != null)
            'sourceStoredFileId': input.sourceStoredFileId,
          'allocateOldestCharges': input.allocateOldestCharges,
        },
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return RecordTenantReceiptResult.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  String _dateOnly(DateTime value) =>
      '${value.year.toString().padLeft(4, '0')}-'
      '${value.month.toString().padLeft(2, '0')}-'
      '${value.day.toString().padLeft(2, '0')}';

  /// GET /accounting/summary — canonical-access-scoped, no portfolioId param needed.
  Future<AccountingSummary> accountingSummary() async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/accounting/summary',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return AccountingSummary.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final paymentsRepositoryProvider = Provider<PaymentsRepository>((ref) {
  return PaymentsRepository(ref.watch(dioProvider));
});

// ── Accounting Summary ────────────────────────────────────────────────────────

class AccountingSummaryNotifier
    extends Notifier<AsyncValue<AccountingSummary>> {
  @override
  AsyncValue<AccountingSummary> build() => const AsyncValue.loading();

  PaymentsRepository get _repo => ref.read(paymentsRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final summary = await _repo.accountingSummary();
      state = AsyncValue.data(summary);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final accountingSummaryProvider =
    NotifierProvider<AccountingSummaryNotifier, AsyncValue<AccountingSummary>>(
      AccountingSummaryNotifier.new,
    );

// ── Payments list ─────────────────────────────────────────────────────────────

class PaymentsNotifier extends Notifier<AsyncValue<List<PaymentReceipt>>> {
  @override
  AsyncValue<List<PaymentReceipt>> build() => const AsyncValue.loading();

  PaymentsRepository get _repo => ref.read(paymentsRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final list = await _repo.listPayments();
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final paymentsProvider =
    NotifierProvider<PaymentsNotifier, AsyncValue<List<PaymentReceipt>>>(
      PaymentsNotifier.new,
    );

final paymentsPageProvider = FutureProvider.autoDispose
    .family<PaymentListPage, PaymentListQuery>((ref, query) {
      return ref.watch(paymentsRepositoryProvider).listPaymentsPage(query);
    });

/// Receipts belonging to one continuous lease-management relationship.
final leaseManagementReceiptsProvider = FutureProvider.autoDispose
    .family<List<PaymentReceipt>, int>((ref, leaseManagementId) {
      return ref
          .watch(paymentsRepositoryProvider)
          .listPayments(leaseManagementId: leaseManagementId);
    });

final tenantAccountReceiptsProvider = FutureProvider.autoDispose
    .family<List<PaymentReceipt>, int>((ref, tenantAccountId) {
      return ref
          .watch(paymentsRepositoryProvider)
          .listPayments(tenantAccountId: tenantAccountId);
    });
