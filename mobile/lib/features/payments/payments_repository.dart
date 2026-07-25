import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';

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

class TenantLedgerEntryListQuery {
  const TenantLedgerEntryListQuery({
    this.skip = 0,
    this.take = 20,
    this.tenantAccountId,
    this.search,
    this.sort = '-postedAtUtc',
    this.from,
    this.to,
  });

  final int skip;
  final int take;
  final int? tenantAccountId;
  final String? search;
  final String sort;
  final String? from;
  final String? to;

  @override
  bool operator ==(Object other) {
    return other is TenantLedgerEntryListQuery &&
        other.skip == skip &&
        other.take == take &&
        other.tenantAccountId == tenantAccountId &&
        other.search == search &&
        other.sort == sort &&
        other.from == from &&
        other.to == to;
  }

  @override
  int get hashCode =>
      Object.hash(skip, take, tenantAccountId, search, sort, from, to);
}

class StaffTenantLedgerEntry {
  const StaffTenantLedgerEntry({
    required this.tenantAccountId,
    required this.leaseManagementId,
    required this.tenantLedgerEntryId,
    required this.propertyId,
    required this.propertyName,
    required this.unitId,
    required this.unitNumber,
    required this.accountNumber,
    required this.relationshipNumber,
    required this.entryType,
    required this.direction,
    required this.amount,
    required this.currency,
    required this.effectiveOn,
    required this.postedAtUtc,
    required this.description,
    this.primaryTenantName,
    this.providerPaymentAttemptId,
    this.sourceStoredFileId,
  });

  final int tenantAccountId;
  final int leaseManagementId;
  final int tenantLedgerEntryId;
  final int propertyId;
  final String propertyName;
  final int unitId;
  final String unitNumber;
  final String accountNumber;
  final String relationshipNumber;
  final String? primaryTenantName;
  final String entryType;
  final String direction;
  final double amount;
  final String currency;
  final DateTime effectiveOn;
  final DateTime postedAtUtc;
  final String description;
  final int? providerPaymentAttemptId;
  final int? sourceStoredFileId;

  factory StaffTenantLedgerEntry.fromJson(Map<String, dynamic> json) {
    return StaffTenantLedgerEntry(
      tenantAccountId: (json['tenantAccountId'] as num).toInt(),
      leaseManagementId: (json['leaseManagementId'] as num).toInt(),
      tenantLedgerEntryId: (json['tenantLedgerEntryId'] as num).toInt(),
      propertyId: (json['propertyId'] as num).toInt(),
      propertyName: json['propertyName'] as String,
      unitId: (json['unitId'] as num).toInt(),
      unitNumber: json['unitNumber'] as String,
      accountNumber: json['accountNumber'] as String,
      relationshipNumber: json['relationshipNumber'] as String,
      primaryTenantName: json['primaryTenantName'] as String?,
      entryType: json['entryType'] as String,
      direction: json['direction'] as String,
      amount: (json['amount'] as num).toDouble(),
      currency: json['currency'] as String,
      effectiveOn: DateTime.parse(json['effectiveOn'] as String),
      postedAtUtc: DateTime.parse(json['postedAtUtc'] as String),
      description: json['description'] as String,
      providerPaymentAttemptId: (json['providerPaymentAttemptId'] as num?)
          ?.toInt(),
      sourceStoredFileId: (json['sourceStoredFileId'] as num?)?.toInt(),
    );
  }
}

class StaffTenantLedgerEntryDetail extends StaffTenantLedgerEntry {
  const StaffTenantLedgerEntryDetail({
    required super.tenantAccountId,
    required super.leaseManagementId,
    required super.tenantLedgerEntryId,
    required super.propertyId,
    required super.propertyName,
    required super.unitId,
    required super.unitNumber,
    required super.accountNumber,
    required super.relationshipNumber,
    required super.entryType,
    required super.direction,
    required super.amount,
    required super.currency,
    required super.effectiveOn,
    required super.postedAtUtc,
    required super.description,
    super.primaryTenantName,
    super.providerPaymentAttemptId,
    super.sourceStoredFileId,
    this.paymentMethodSummary,
    this.providerReference,
    this.payerName,
    this.checkNumber,
    this.bankName,
  });

  final String? paymentMethodSummary;
  final String? providerReference;
  final String? payerName;
  final String? checkNumber;
  final String? bankName;

  factory StaffTenantLedgerEntryDetail.fromJson(Map<String, dynamic> json) {
    final attempt = json['providerAttempt'] as Map<String, dynamic>?;
    return StaffTenantLedgerEntryDetail(
      tenantAccountId: (json['tenantAccountId'] as num).toInt(),
      leaseManagementId: (json['leaseManagementId'] as num).toInt(),
      tenantLedgerEntryId: (json['tenantLedgerEntryId'] as num).toInt(),
      propertyId: (json['propertyId'] as num).toInt(),
      propertyName: json['propertyName'] as String,
      unitId: (json['unitId'] as num).toInt(),
      unitNumber: json['unitNumber'] as String,
      accountNumber: json['accountNumber'] as String,
      relationshipNumber: json['relationshipNumber'] as String,
      primaryTenantName: json['tenantName'] as String?,
      entryType: json['entryType'] as String,
      direction: json['direction'] as String,
      amount: (json['amount'] as num).toDouble(),
      currency: json['currency'] as String,
      effectiveOn: DateTime.parse(json['effectiveOn'] as String),
      postedAtUtc: DateTime.parse(json['postedAtUtc'] as String),
      description: json['description'] as String,
      providerPaymentAttemptId: (json['providerPaymentAttemptId'] as num?)
          ?.toInt(),
      sourceStoredFileId: (json['sourceStoredFileId'] as num?)?.toInt(),
      paymentMethodSummary: attempt?['paymentMethodSummary'] as String?,
      providerReference: attempt?['providerReference'] as String?,
      payerName: attempt?['payerName'] as String?,
      checkNumber: attempt?['checkNumber'] as String?,
      bankName: attempt?['bankName'] as String?,
    );
  }
}

class TenantLedgerEntryListPage {
  const TenantLedgerEntryListPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<StaffTenantLedgerEntry> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory TenantLedgerEntryListPage.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final items = rawItems is List
        ? rawItems
              .whereType<Map<String, dynamic>>()
              .map(StaffTenantLedgerEntry.fromJson)
              .toList()
        : <StaffTenantLedgerEntry>[];

    return TenantLedgerEntryListPage(
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

class CorrectTenantPaymentInput {
  const CorrectTenantPaymentInput({
    required this.paymentEntryId,
    required this.effectiveOn,
    required this.reason,
    required this.paymentMethodSummary,
    required this.externalReference,
    this.sourceStoredFileId,
  });

  final int paymentEntryId;
  final DateTime effectiveOn;
  final String reason;
  final String paymentMethodSummary;
  final String externalReference;
  final int? sourceStoredFileId;
}

class CorrectTenantPaymentResult {
  const CorrectTenantPaymentResult({
    required this.applied,
    required this.outcome,
    required this.paymentEntryId,
    required this.refundEntryId,
    required this.compensatedAllocationAmount,
    required this.compensatedAllocationCount,
    required this.replayed,
  });

  final bool applied;
  final String outcome;
  final int paymentEntryId;
  final int? refundEntryId;
  final double compensatedAllocationAmount;
  final int compensatedAllocationCount;
  final bool replayed;

  factory CorrectTenantPaymentResult.fromJson(Map<String, dynamic> json) {
    final value = json['value'] as Map<String, dynamic>? ?? const {};
    return CorrectTenantPaymentResult(
      applied: value['applied'] as bool? ?? false,
      outcome: value['outcome'] as String? ?? '',
      paymentEntryId: (value['paymentEntryId'] as num).toInt(),
      refundEntryId: (value['refundEntryId'] as num?)?.toInt(),
      compensatedAllocationAmount:
          (value['compensatedAllocationAmount'] as num?)?.toDouble() ?? 0,
      compensatedAllocationCount:
          (value['compensatedAllocationCount'] as num?)?.toInt() ?? 0,
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

/// Handles canonical tenant-account reads plus payment/accounting commands.
///
/// Endpoints:
///   GET    /tenant-accounts/entries/page — immutable cross-account entries
///   GET    /tenant-accounts/{accountId}/entries/{entryId} — one entry
///   POST   /tenant-accounts/{id}/receipts — record a receipt
///   GET    /accounting/summary          — AccountingSummary rollup
class PaymentsRepository {
  PaymentsRepository(this._dio);

  final Dio _dio;

  Future<TenantLedgerEntryListPage> listTenantLedgerEntriesPage([
    TenantLedgerEntryListQuery query = const TenantLedgerEntryListQuery(),
  ]) async {
    final params = <String, dynamic>{
      'skip': query.skip,
      'take': query.take,
      'tenantAccountId': query.tenantAccountId,
      'entryType': 'PaymentReceipt',
      'search': query.search,
      'sort': query.sort,
      'from': query.from,
      'to': query.to,
    }..removeWhere((_, value) => value == null || value == '');

    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/tenant-accounts/entries/page',
        queryParameters: params,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return TenantLedgerEntryListPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<StaffTenantLedgerEntryDetail> getTenantLedgerEntry(
    int tenantAccountId,
    int tenantLedgerEntryId,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/tenant-accounts/$tenantAccountId/entries/$tenantLedgerEntryId',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return StaffTenantLedgerEntryDetail.fromJson(data);
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

  Future<CorrectTenantPaymentResult> correctPayment(
    int tenantAccountId,
    CorrectTenantPaymentInput input,
  ) async {
    final scope =
        'tenant-payment-refund:$tenantAccountId:${input.paymentEntryId}:'
        '${input.effectiveOn.toIso8601String().split('T').first}:'
        '${input.reason.trim()}:${input.externalReference.trim()}';
    try {
      final response = await IdempotentMutation.run(
        scope,
        (operationKey) => _dio.post<Map<String, dynamic>>(
          '/tenant-accounts/$tenantAccountId/refunds',
          data: <String, dynamic>{
            'paymentEntryId': input.paymentEntryId,
            'effectiveOn': _dateOnly(input.effectiveOn),
            'reason': input.reason.trim(),
            'paymentMethodSummary': input.paymentMethodSummary.trim(),
            'externalReference': input.externalReference.trim(),
            if (input.sourceStoredFileId != null)
              'sourceStoredFileId': input.sourceStoredFileId,
          },
          options: Options(headers: {'Idempotency-Key': operationKey}),
        ),
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return CorrectTenantPaymentResult.fromJson(data);
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

// ── Canonical tenant-ledger receipt page ──────────────────────────────────────

final tenantLedgerEntriesPageProvider = FutureProvider.autoDispose
    .family<TenantLedgerEntryListPage, TenantLedgerEntryListQuery>((
      ref,
      query,
    ) {
      return ref
          .watch(paymentsRepositoryProvider)
          .listTenantLedgerEntriesPage(query);
    });
