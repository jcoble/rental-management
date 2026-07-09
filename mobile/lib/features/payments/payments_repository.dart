import 'dart:typed_data';

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
    this.leaseId,
    this.applicationId,
    this.search,
    this.sort = '-createdAt',
    this.dueFrom,
    this.dueTo,
    this.paidFrom,
    this.paidTo,
  });

  final int skip;
  final int take;
  final int? leaseId;
  final int? applicationId;
  final String? search;
  final String sort;
  final String? dueFrom;
  final String? dueTo;
  final String? paidFrom;
  final String? paidTo;

  @override
  bool operator ==(Object other) {
    return other is PaymentListQuery &&
        other.skip == skip &&
        other.take == take &&
        other.leaseId == leaseId &&
        other.applicationId == applicationId &&
        other.search == search &&
        other.sort == sort &&
        other.dueFrom == dueFrom &&
        other.dueTo == dueTo &&
        other.paidFrom == paidFrom &&
        other.paidTo == paidTo;
  }

  @override
  int get hashCode => Object.hash(
    skip,
    take,
    leaseId,
    applicationId,
    search,
    sort,
    dueFrom,
    dueTo,
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

  final List<Payment> items;
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
              .map(Payment.fromJson)
              .toList()
        : <Payment>[];

    return PaymentListPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

class MarkLeasePastDuePaidResult {
  const MarkLeasePastDuePaidResult({
    required this.leaseId,
    required this.markedPaidCount,
    required this.paymentIds,
  });

  final int leaseId;
  final int markedPaidCount;
  final List<int> paymentIds;

  factory MarkLeasePastDuePaidResult.fromJson(Map<String, dynamic> json) {
    return MarkLeasePastDuePaidResult(
      leaseId: (json['leaseId'] as num?)?.toInt() ?? 0,
      markedPaidCount: (json['markedPaidCount'] as num?)?.toInt() ?? 0,
      paymentIds: (json['paymentIds'] as List<dynamic>? ?? const [])
          .whereType<num>()
          .map((id) => id.toInt())
          .toList(),
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
///   GET    /payments                    — list (JWT-scoped)
///   GET    /payments/{id}               — single
///   POST   /payments                    — create { leaseId, amount, dueDate, type, status }
///   PATCH  /payments/{id}               — update (same fields, partial)
///   POST   /payments/{id}/mark-paid     — mark as paid { paidDate?, method? }
///   GET    /accounting/summary          — AccountingSummary rollup
///   GET    /leases                      — full lease list for the dropdown
class PaymentsRepository {
  PaymentsRepository(this._dio);

  final Dio _dio;

  /// Lists payments, newest first by default.
  ///
  /// The API defaults to ascending `CreatedAt` when no `sort` is supplied
  /// (`PaymentService.ListAsync`), which surfaces the oldest payments on top.
  /// We pass `-createdAt` so the Money ledger and the standalone Payments
  /// screen both show the most recent activity first.
  Future<List<Payment>> listPayments({
    int? leaseId,
    int? applicationId,
    String sort = '-createdAt',
    String? dueFrom,
    String? dueTo,
    String? paidFrom,
    String? paidTo,
  }) async {
    try {
      final params = <String, dynamic>{};
      if (leaseId != null) params['leaseId'] = leaseId;
      if (applicationId != null) params['applicationId'] = applicationId;
      if (sort.isNotEmpty) params['sort'] = sort;
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
        '/payments',
        queryParameters: params.isEmpty ? null : params,
      );
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(Payment.fromJson)
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
      'leaseId': query.leaseId,
      'applicationId': query.applicationId,
      'search': query.search,
      'sort': query.sort,
      'dueFrom': query.dueFrom,
      'dueTo': query.dueTo,
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

  Future<Payment> getPayment(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/payments/$id');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Payment.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Streams the latest uploaded payment scan/receipt bytes. The shared Dio
  /// auth interceptor attaches the Bearer token.
  Future<Uint8List> scanBytes(int id) async {
    try {
      final response = await _dio.get<List<int>>(
        '/payments/$id/scan',
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Create body: { leaseId, amount, dueDate (yyyy-MM-dd), type, status }
  Future<Payment> createPayment(Map<String, dynamic> data) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/payments',
        data: data,
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Payment.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Update body: same optional fields as create (partial PATCH)
  Future<Payment> updatePayment(int id, Map<String, dynamic> data) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/payments/$id',
        data: data,
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Payment.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> deletePayment(int id) async {
    try {
      await _dio.delete<void>('/payments/$id');
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// POST /payments/{id}/mark-paid
  /// body: { paidDate?, method?, externalReference?, notes? }
  Future<Payment> markPaid(
    int id, {
    String? paidDate,
    String? method,
    String? externalReference,
    String? notes,
  }) async {
    try {
      final body = <String, dynamic>{};
      if (paidDate != null) body['paidDate'] = paidDate;
      if (method != null) body['method'] = method;
      if (externalReference != null) {
        body['externalReference'] = externalReference;
      }
      if (notes != null) body['notes'] = notes;
      final response = await _dio.post<Map<String, dynamic>>(
        '/payments/$id/mark-paid',
        data: body,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Payment.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// POST /payments/leases/{leaseId}/past-due/mark-paid
  /// body: { paidDate?, method?, externalReference?, notes? }
  ///
  /// The server owns the past-due predicate and updates the matching rows. The
  /// mobile client must not fetch a lease payment list and filter it locally.
  Future<MarkLeasePastDuePaidResult> markLeasePastDuePaid(
    int leaseId, {
    String? paidDate,
    String? method,
    String? externalReference,
    String? notes,
  }) async {
    try {
      final body = <String, dynamic>{};
      if (paidDate != null) body['paidDate'] = paidDate;
      if (method != null) body['method'] = method;
      if (externalReference != null) {
        body['externalReference'] = externalReference;
      }
      if (notes != null) body['notes'] = notes;

      final response = await _dio.post<Map<String, dynamic>>(
        '/payments/leases/$leaseId/past-due/mark-paid',
        data: body,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return MarkLeasePastDuePaidResult.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// GET /accounting/summary — JWT-scoped, no portfolioId param needed.
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

  /// GET /leases — for lease dropdown in the create form.
  Future<List<Lease>> listLeases() async {
    try {
      final response = await _dio.get<List<dynamic>>('/leases');
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(Lease.fromJson)
          .toList();
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

class PaymentsNotifier extends Notifier<AsyncValue<List<Payment>>> {
  @override
  AsyncValue<List<Payment>> build() => const AsyncValue.loading();

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

  /// Marks a payment as paid and updates the in-memory list.
  Future<void> markPaid(int id) async {
    try {
      final today = DateTime.now().toIso8601String().split('T').first;
      final updated = await _repo.markPaid(id, paidDate: today);
      state.whenData((list) {
        state = AsyncValue.data([
          for (final p in list)
            if (p.id == id) updated else p,
        ]);
      });
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }
}

final paymentsProvider =
    NotifierProvider<PaymentsNotifier, AsyncValue<List<Payment>>>(
      PaymentsNotifier.new,
    );

final paymentsPageProvider = FutureProvider.autoDispose
    .family<PaymentListPage, PaymentListQuery>((ref, query) {
      return ref.watch(paymentsRepositoryProvider).listPaymentsPage(query);
    });

// ── Payments for a specific lease ─────────────────────────────────────────────

/// Payments belonging to one lease, newest first. Backs the Payments section on
/// the lease detail screen (`GET /payments?leaseId=…`). autoDispose so it
/// refetches whenever the lease screen is reopened, and invalidate-able after an
/// inline mark-paid.
final leasePaymentsProvider = FutureProvider.autoDispose
    .family<List<Payment>, int>((ref, leaseId) {
      return ref
          .watch(paymentsRepositoryProvider)
          .listPayments(leaseId: leaseId);
    });

// ── Payments for a specific rental application ───────────────────────────────

/// Lease-less application/screening fees for one application. The filter is
/// server-side (`GET /payments?applicationId=…`) so detail screens never load
/// the whole ledger and filter locally.
final applicationPaymentsProvider = FutureProvider.autoDispose
    .family<List<Payment>, int>((ref, applicationId) {
      return ref
          .watch(paymentsRepositoryProvider)
          .listPayments(applicationId: applicationId);
    });

/// Receipt/scan bytes for a payment, keyed by id.
final paymentReceiptProvider = FutureProvider.autoDispose
    .family<Uint8List, int>((ref, id) {
      return ref.watch(paymentsRepositoryProvider).scanBytes(id);
    });

// ── Leases (for the create-payment dropdown) ──────────────────────────────────

class LeasesNotifier extends Notifier<AsyncValue<List<Lease>>> {
  @override
  AsyncValue<List<Lease>> build() => const AsyncValue.loading();

  PaymentsRepository get _repo => ref.read(paymentsRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final list = await _repo.listLeases();
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }
}

final leasesForPaymentProvider =
    NotifierProvider<LeasesNotifier, AsyncValue<List<Lease>>>(
      LeasesNotifier.new,
    );
