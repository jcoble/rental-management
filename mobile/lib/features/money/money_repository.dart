import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';
import '../properties/capital_assets_repository.dart';
import '../properties/property_loans_repository.dart';
import '../payments/payments_repository.dart';
import 'expense_models.dart';
import 'transaction_models.dart';

class ExpenseListQuery {
  const ExpenseListQuery({
    this.skip = 0,
    this.take = 20,
    this.operationalScope,
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
  final String? operationalScope;
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
        other.operationalScope == operationalScope &&
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
    operationalScope,
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

class TenantChargePosition {
  const TenantChargePosition({
    required this.tenantLedgerEntryId,
    required this.description,
    required this.currency,
    required this.originalAmount,
    required this.netAllocations,
    required this.openAmount,
    required this.isPastDue,
  });

  final int tenantLedgerEntryId;
  final String description;
  final String currency;
  final double originalAmount;
  final double netAllocations;
  final double openAmount;
  final bool isPastDue;

  factory TenantChargePosition.fromJson(Map<String, dynamic> json) =>
      TenantChargePosition(
        tenantLedgerEntryId: (json['tenantLedgerEntryId'] as num).toInt(),
        description: json['description'] as String? ?? '',
        currency: json['currency'] as String? ?? 'USD',
        originalAmount: (json['originalAmount'] as num?)?.toDouble() ?? 0,
        netAllocations: (json['netAllocations'] as num?)?.toDouble() ?? 0,
        openAmount: (json['openAmount'] as num?)?.toDouble() ?? 0,
        isPastDue: json['isPastDue'] as bool? ?? false,
      );
}

class TenantDepositPosition {
  const TenantDepositPosition({
    required this.securityDepositAccountId,
    required this.accountNumber,
    required this.currency,
    required this.heldBalance,
    required this.status,
  });

  final int securityDepositAccountId;
  final String accountNumber;
  final String currency;
  final double heldBalance;
  final String status;

  factory TenantDepositPosition.fromJson(Map<String, dynamic> json) =>
      TenantDepositPosition(
        securityDepositAccountId: (json['securityDepositAccountId'] as num)
            .toInt(),
        accountNumber: json['accountNumber'] as String? ?? '',
        currency: json['currency'] as String? ?? 'USD',
        heldBalance: (json['heldBalance'] as num?)?.toDouble() ?? 0,
        status: json['status'] as String? ?? '',
      );
}

class TenantLedgerAllocationRef {
  const TenantLedgerAllocationRef({
    required this.targetDescription,
    required this.amount,
    required this.effectiveOn,
  });
  final String targetDescription;
  final double amount;
  final DateTime effectiveOn;
  factory TenantLedgerAllocationRef.fromJson(Map<String, dynamic> json) =>
      TenantLedgerAllocationRef(
        targetDescription: json['targetDescription'] as String? ?? '',
        amount: (json['amount'] as num?)?.toDouble() ?? 0,
        effectiveOn: DateTime.parse(json['effectiveOn'] as String),
      );
}

class TenantLedgerRow {
  const TenantLedgerRow({
    required this.tenantLedgerEntryId,
    required this.effectiveOn,
    required this.postedAtUtc,
    required this.type,
    required this.description,
    required this.chargeAmount,
    required this.paymentAmount,
    required this.creditAmount,
    required this.runningAmountOwed,
    required this.openAmount,
    required this.currency,
    required this.allocations,
    this.paymentMethod,
    this.reference,
    this.accountLabel,
    this.recurringScheduleContext,
    this.sourceDocumentContext,
    this.journalEntryPublicId,
  });
  final int tenantLedgerEntryId;
  final DateTime effectiveOn, postedAtUtc;
  final String type, description, currency;
  final double chargeAmount,
      paymentAmount,
      creditAmount,
      runningAmountOwed,
      openAmount;
  final String? paymentMethod,
      reference,
      accountLabel,
      recurringScheduleContext,
      sourceDocumentContext,
      journalEntryPublicId;
  final List<TenantLedgerAllocationRef> allocations;
  factory TenantLedgerRow.fromJson(Map<String, dynamic> json) =>
      TenantLedgerRow(
        tenantLedgerEntryId: (json['tenantLedgerEntryId'] as num).toInt(),
        effectiveOn: DateTime.parse(json['effectiveOn'] as String),
        postedAtUtc: DateTime.parse(json['postedAtUtc'] as String),
        type: json['type'] as String? ?? '',
        description: json['description'] as String? ?? '',
        currency: json['currency'] as String? ?? 'USD',
        chargeAmount: (json['chargeAmount'] as num?)?.toDouble() ?? 0,
        paymentAmount: (json['paymentAmount'] as num?)?.toDouble() ?? 0,
        creditAmount: (json['creditAmount'] as num?)?.toDouble() ?? 0,
        runningAmountOwed: (json['runningAmountOwed'] as num?)?.toDouble() ?? 0,
        openAmount: (json['openAmount'] as num?)?.toDouble() ?? 0,
        paymentMethod: json['paymentMethod'] as String?,
        reference: json['reference'] as String?,
        accountLabel: json['accountLabel'] as String?,
        recurringScheduleContext: json['recurringScheduleContext'] as String?,
        sourceDocumentContext: json['sourceDocumentContext'] as String?,
        journalEntryPublicId: json['journalEntryPublicId'] as String?,
        allocations: ((json['allocations'] as List?) ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(TenantLedgerAllocationRef.fromJson)
            .toList(growable: false),
      );
}

class TenantMonthSummary {
  const TenantMonthSummary({
    required this.year,
    required this.month,
    required this.currency,
    required this.openingBalance,
    required this.chargeAmount,
    required this.paymentAmount,
    required this.creditAmount,
    required this.closingBalance,
  });
  final int year, month;
  final String currency;
  final double openingBalance,
      chargeAmount,
      paymentAmount,
      creditAmount,
      closingBalance;
  factory TenantMonthSummary.fromJson(Map<String, dynamic> json) =>
      TenantMonthSummary(
        year: (json['year'] as num).toInt(),
        month: (json['month'] as num).toInt(),
        currency: json['currency'] as String? ?? 'USD',
        openingBalance: (json['openingBalance'] as num?)?.toDouble() ?? 0,
        chargeAmount: (json['chargeAmount'] as num?)?.toDouble() ?? 0,
        paymentAmount: (json['paymentAmount'] as num?)?.toDouble() ?? 0,
        creditAmount: (json['creditAmount'] as num?)?.toDouble() ?? 0,
        closingBalance: (json['closingBalance'] as num?)?.toDouble() ?? 0,
      );
}

class RecurringTenantCharge {
  const RecurringTenantCharge({
    required this.id,
    required this.displayName,
    required this.amount,
    required this.currency,
    required this.ledgerAccountId,
    required this.effectiveStartOn,
    required this.effectiveEndOn,
    required this.monthlyDueDay,
    required this.nextRunDate,
    required this.isActive,
  });

  final int id;
  final String displayName, currency;
  final double amount;
  final int ledgerAccountId, monthlyDueDay;
  final DateTime effectiveStartOn, nextRunDate;
  final DateTime? effectiveEndOn;
  final bool isActive;

  factory RecurringTenantCharge.fromJson(Map<String, dynamic> json) =>
      RecurringTenantCharge(
        id: (json['id'] as num).toInt(),
        displayName: json['displayName'] as String? ?? '',
        amount: (json['amount'] as num?)?.toDouble() ?? 0,
        currency: json['currency'] as String? ?? 'USD',
        ledgerAccountId: (json['ledgerAccountId'] as num).toInt(),
        effectiveStartOn: DateTime.parse(json['effectiveStartOn'] as String),
        effectiveEndOn: json['effectiveEndOn'] == null
            ? null
            : DateTime.parse(json['effectiveEndOn'] as String),
        monthlyDueDay: (json['monthlyDueDay'] as num).toInt(),
        nextRunDate: DateTime.parse(json['nextRunDate'] as String),
        isActive: json['isActive'] as bool? ?? false,
      );
}

typedef TenantLedgerRequest = ({int tenantAccountId, String from, String to});

class UnitMoneyPage<T> {
  const UnitMoneyPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<T> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;
}

typedef UnitMoneyPageKey = ({
  int tenantAccountId,
  int propertyId,
  int unitId,
  int skip,
  int take,
});

typedef PropertyMoneyPageKey = ({int propertyId, int skip, int take});

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

  Future<List<TenantLedgerRow>> tenantLedger(
    TenantLedgerRequest request,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/tenant-accounts/${request.tenantAccountId}/ledger',
        queryParameters: {
          'skip': 0,
          'take': 200,
          'effectiveFrom': request.from,
          'effectiveTo': request.to,
          'sort': 'effectiveOn,postedAtUtc,tenantLedgerEntryId',
        },
      );
      return ((response.data?['items'] as List?) ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(TenantLedgerRow.fromJson)
          .toList(growable: false);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<TenantMonthSummary>> tenantMonthSummaries(
    TenantLedgerRequest request,
  ) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/tenant-accounts/${request.tenantAccountId}/month-summary',
        queryParameters: {'from': request.from, 'to': request.to},
      );
      return (response.data ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(TenantMonthSummary.fromJson)
          .toList(growable: false);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> postTenantCharge({
    required int tenantAccountId,
    required double amount,
    required DateTime effectiveOn,
    required DateTime dueOn,
    required String description,
  }) async {
    final data = {
      'amount': amount,
      'effectiveOn': _wireDate(effectiveOn),
      'dueOn': _wireDate(dueOn),
      'description': description.trim(),
    };
    try {
      await IdempotentMutation.run(
        'tenant-charge:$tenantAccountId:${jsonEncode(data)}',
        (key) => _dio.post<void>(
          '/tenant-accounts/$tenantAccountId/charges',
          data: data,
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<RecurringTenantCharge>> recurringCharges(
    int tenantAccountId,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/tenant-accounts/$tenantAccountId/recurring-charges',
        queryParameters: {'skip': 0, 'take': 100, 'sort': 'displayName'},
      );
      return ((response.data?['items'] as List?) ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(RecurringTenantCharge.fromJson)
          .toList(growable: false);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<RecurringTenantCharge> createRecurringCharge({
    required int tenantAccountId,
    required int leaseAgreementId,
    required int propertyId,
    required int unitId,
    required String displayName,
    required double amount,
    required int ledgerAccountId,
    required int monthlyDueDay,
    required DateTime effectiveStartOn,
    DateTime? effectiveEndOn,
  }) async {
    final data = {
      'displayName': displayName.trim(),
      'amount': amount,
      'ledgerAccountId': ledgerAccountId,
      'leaseAgreementId': leaseAgreementId,
      'effectiveStartOn': _wireDate(effectiveStartOn),
      'effectiveEndOn': effectiveEndOn == null
          ? null
          : _wireDate(effectiveEndOn),
      'monthlyDueDay': monthlyDueDay,
      'propertyId': propertyId,
      'unitId': unitId,
    };
    try {
      final response = await IdempotentMutation.run(
        'recurring-charge:create:$tenantAccountId:${jsonEncode(data)}',
        (key) => _dio.post<Map<String, dynamic>>(
          '/tenant-accounts/$tenantAccountId/recurring-charges',
          data: data,
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
      return RecurringTenantCharge.fromJson(response.data ?? const {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<RecurringTenantCharge> setRecurringChargeActive({
    required int tenantAccountId,
    required int id,
    required bool isActive,
  }) async {
    try {
      final response = await IdempotentMutation.run(
        'recurring-charge:active:$tenantAccountId:$id:$isActive',
        (key) => isActive
            ? _dio.patch<Map<String, dynamic>>(
                '/tenant-accounts/$tenantAccountId/recurring-charges/$id',
                data: const {'isActive': true},
                options: Options(headers: {'Idempotency-Key': key}),
              )
            : _dio.post<Map<String, dynamic>>(
                '/tenant-accounts/$tenantAccountId/recurring-charges/$id/deactivate',
                data: const <String, dynamic>{},
                options: Options(headers: {'Idempotency-Key': key}),
              ),
      );
      return RecurringTenantCharge.fromJson(response.data ?? const {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  static String _wireDate(DateTime value) =>
      '${value.year.toString().padLeft(4, '0')}-'
      '${value.month.toString().padLeft(2, '0')}-'
      '${value.day.toString().padLeft(2, '0')}';

  Future<UnitMoneyPage<StaffTenantLedgerEntry>> accountActivityPage(
    UnitMoneyPageKey key,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/tenant-accounts/entries/page',
        queryParameters: {
          'tenantAccountId': key.tenantAccountId,
          'skip': key.skip,
          'take': key.take,
          'sort': '-postedAtUtc',
        },
      );
      return _page(response.data, StaffTenantLedgerEntry.fromJson);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<UnitMoneyPage<TenantChargePosition>> chargePositionsPage(
    UnitMoneyPageKey key,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/tenant-accounts/${key.tenantAccountId}/charges/page',
        queryParameters: {
          'skip': key.skip,
          'take': key.take,
          'sort': '-effectiveOn',
        },
      );
      return _page(response.data, TenantChargePosition.fromJson);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<UnitMoneyPage<TenantDepositPosition>> depositsPage(
    UnitMoneyPageKey key,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/tenant-accounts/deposits/page',
        queryParameters: {
          'tenantAccountId': key.tenantAccountId,
          'skip': key.skip,
          'take': key.take,
          'sort': '-createdAtUtc',
        },
      );
      return _page(response.data, TenantDepositPosition.fromJson);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<UnitMoneyPage<PropertyLoan>> financingPage(
    PropertyMoneyPageKey key,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/loans/page',
        queryParameters: {
          'propertyId': key.propertyId,
          'skip': key.skip,
          'take': key.take,
          'sort': '-startDate',
        },
      );
      return _page(response.data, PropertyLoan.fromJson);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  UnitMoneyPage<T> _page<T>(
    Map<String, dynamic>? json,
    T Function(Map<String, dynamic>) decode,
  ) {
    if (json == null) {
      throw const ApiException(
        statusCode: 0,
        message: 'Empty response from server.',
      );
    }
    final items = (json['items'] as List<dynamic>? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(decode)
        .toList(growable: false);
    return UnitMoneyPage<T>(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? 0,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }

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
      if (filter.displayType != null) {
        params['displayType'] = filter.displayType;
      }
      if (filter.accountId != null) params['accountId'] = filter.accountId;
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
      'operationalScope': query.operationalScope,
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
      final response = await IdempotentMutation.run(
        'expenses:create:${jsonEncode(data)}',
        (key) => _dio.post<Map<String, dynamic>>(
          '/expenses',
          data: data,
          options: Options(headers: {'Idempotency-Key': key}),
        ),
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
      final response = await IdempotentMutation.run(
        'expenses:update:$id:${jsonEncode(data)}',
        (key) => _dio.patch<Map<String, dynamic>>(
          '/expenses/$id',
          data: data,
          options: Options(headers: {'Idempotency-Key': key}),
        ),
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
      final response = await IdempotentMutation.run(
        'expenses:capitalize:$id:${jsonEncode(data)}',
        (key) => _dio.post<Map<String, dynamic>>(
          '/expenses/$id/capitalize',
          data: data,
          options: Options(headers: {'Idempotency-Key': key}),
        ),
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
      await IdempotentMutation.run(
        'expenses:delete:$id',
        (key) => _dio.delete<void>(
          '/expenses/$id',
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
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

final unitMoneyActivityPageProvider = FutureProvider.autoDispose
    .family<UnitMoneyPage<StaffTenantLedgerEntry>, UnitMoneyPageKey>((
      ref,
      key,
    ) {
      return ref.watch(moneyRepositoryProvider).accountActivityPage(key);
    });

final tenantLedgerProvider = FutureProvider.autoDispose
    .family<List<TenantLedgerRow>, TenantLedgerRequest>(
      (ref, request) =>
          ref.watch(moneyRepositoryProvider).tenantLedger(request),
    );
final tenantMonthSummariesProvider = FutureProvider.autoDispose
    .family<List<TenantMonthSummary>, TenantLedgerRequest>(
      (ref, request) =>
          ref.watch(moneyRepositoryProvider).tenantMonthSummaries(request),
    );
final recurringTenantChargesProvider = FutureProvider.autoDispose
    .family<List<RecurringTenantCharge>, int>(
      (ref, tenantAccountId) =>
          ref.watch(moneyRepositoryProvider).recurringCharges(tenantAccountId),
    );

final unitMoneyChargesPageProvider = FutureProvider.autoDispose
    .family<UnitMoneyPage<TenantChargePosition>, UnitMoneyPageKey>((ref, key) {
      return ref.watch(moneyRepositoryProvider).chargePositionsPage(key);
    });

final unitMoneyDepositsPageProvider = FutureProvider.autoDispose
    .family<UnitMoneyPage<TenantDepositPosition>, UnitMoneyPageKey>((ref, key) {
      return ref.watch(moneyRepositoryProvider).depositsPage(key);
    });

final unitMoneyFinancingPageProvider = FutureProvider.autoDispose
    .family<UnitMoneyPage<PropertyLoan>, PropertyMoneyPageKey>((ref, key) {
      return ref.watch(moneyRepositoryProvider).financingPage(key);
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
