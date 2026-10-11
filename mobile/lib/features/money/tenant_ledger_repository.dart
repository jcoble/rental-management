import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';
import '../accounting/accounting_book_models.dart';
import 'tenant_ledger_models.dart';

String _dateOnlyQuery(DateTime value) =>
    value.toIso8601String().split('T').first;

const _tenantLedgerSortAllowlist = <String>{
  'effectiveOn',
  '-effectiveOn',
  'postedAtUtc',
  '-postedAtUtc',
};

class TenantLedgerQuery {
  const TenantLedgerQuery({
    this.skip = 0,
    this.take = 50,
    this.search,
    this.sort = '-effectiveOn',
    this.entryType,
    this.effectiveFrom,
    this.effectiveTo,
    this.openOnly,
    this.settledOnly,
  });

  final int skip;
  final int take;
  final String? search;
  final String sort;
  final TenantLedgerEntryType? entryType;
  final DateTime? effectiveFrom;
  final DateTime? effectiveTo;
  final bool? openOnly;
  final bool? settledOnly;

  String get validatedSort =>
      _tenantLedgerSortAllowlist.contains(sort) ? sort : '-effectiveOn';

  Map<String, dynamic> get queryParameters => {
    'skip': skip,
    'take': take,
    'sort': validatedSort,
    if (search != null && search!.trim().isNotEmpty) 'search': search,
    if (entryType?.wire != null) 'entryType': entryType!.wire,
    if (effectiveFrom != null) 'effectiveFrom': _dateOnlyQuery(effectiveFrom!),
    if (effectiveTo != null) 'effectiveTo': _dateOnlyQuery(effectiveTo!),
    if (openOnly != null) 'openOnly': openOnly,
    if (settledOnly != null) 'settledOnly': settledOnly,
  };
}

class TenantMonthSummaryQuery {
  const TenantMonthSummaryQuery({this.from, this.to});

  final DateTime? from;
  final DateTime? to;

  Map<String, dynamic> get queryParameters => {
    if (from != null) 'from': _dateOnlyQuery(from!),
    if (to != null) 'to': _dateOnlyQuery(to!),
  };
}

class TenantLedgerPeriodSummaryQuery {
  const TenantLedgerPeriodSummaryQuery({this.months = 12});

  final int months;

  Map<String, dynamic> get queryParameters => {'months': months};
}

class RecurringTenantChargeQuery {
  const RecurringTenantChargeQuery({
    this.skip = 0,
    this.take = 50,
    this.search,
    this.sort,
    this.from,
    this.to,
  });

  final int skip;
  final int take;
  final String? search;
  final String? sort;
  final DateTime? from;
  final DateTime? to;

  Map<String, dynamic> get queryParameters => {
    'skip': skip,
    'take': take,
    if (search != null && search!.trim().isNotEmpty) 'search': search,
    if (sort != null && sort!.trim().isNotEmpty) 'sort': sort,
    if (from != null) 'from': _dateOnlyQuery(from!),
    if (to != null) 'to': _dateOnlyQuery(to!),
  };
}

class TenantLedgerRepository {
  const TenantLedgerRepository(this._dio);

  final Dio _dio;

  Future<AccountingPage<TenantLedgerRow>> ledger(
    int tenantAccountId, {
    TenantLedgerQuery query = const TenantLedgerQuery(),
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/tenant-accounts/$tenantAccountId/ledger',
        queryParameters: query.queryParameters,
      );
      return AccountingPage.fromJson(
        _requireMap(response),
        TenantLedgerRow.fromJson,
      );
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<List<TenantMonthSummary>> monthSummary(
    int tenantAccountId, {
    TenantMonthSummaryQuery query = const TenantMonthSummaryQuery(),
  }) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/tenant-accounts/$tenantAccountId/month-summary',
        queryParameters: query.queryParameters,
      );
      return _requireList(response)
          .whereType<Map<String, dynamic>>()
          .map(TenantMonthSummary.fromJson)
          .toList(growable: false);
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<TenantLedgerPeriodSummary> ledgerSummary(
    int tenantAccountId, {
    TenantLedgerPeriodSummaryQuery query =
        const TenantLedgerPeriodSummaryQuery(),
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/tenant-accounts/$tenantAccountId/ledger-summary',
        queryParameters: query.queryParameters,
      );
      return TenantLedgerPeriodSummary.fromJson(_requireMap(response));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<AccountingPage<RecurringTenantChargeRow>> recurringCharges(
    int tenantAccountId, {
    RecurringTenantChargeQuery query = const RecurringTenantChargeQuery(),
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/tenant-accounts/$tenantAccountId/recurring-charges',
        queryParameters: query.queryParameters,
      );
      return AccountingPage.fromJson(
        _requireMap(response),
        RecurringTenantChargeRow.fromJson,
      );
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<RecordTenantReceiptResult> recordReceipt(
    int tenantAccountId,
    RecordTenantReceiptInput input, {
    String? operationKey,
  }) => _runMutation(
    scope:
        'tenant-account.receipt:$tenantAccountId:${jsonEncode(input.toJson())}',
    operationKey: operationKey,
    path: '/tenant-accounts/$tenantAccountId/receipts',
    data: input.toJson(),
    decode: RecordTenantReceiptResult.fromJson,
  );

  Future<TenantChargeMutationResult> postCharge(
    int tenantAccountId,
    PostTenantChargeInput input, {
    String? operationKey,
  }) => _runMutation(
    scope:
        'tenant-account.charge:$tenantAccountId:${jsonEncode(input.toJson())}',
    operationKey: operationKey,
    path: '/tenant-accounts/$tenantAccountId/charges',
    data: input.toJson(),
    decode: TenantChargeMutationResult.fromJson,
  );

  Future<TenantLedgerMutationResult> postCredit(
    int tenantAccountId,
    PostTenantCreditInput input, {
    String? operationKey,
  }) => _runMutation(
    scope:
        'tenant-account.credit:$tenantAccountId:${jsonEncode(input.toJson())}',
    operationKey: operationKey,
    path: '/tenant-accounts/$tenantAccountId/credits',
    data: input.toJson(),
    decode: TenantLedgerMutationResult.fromJson,
  );

  Future<TenantChargeMutationResult> reverseCharge(
    int tenantAccountId,
    int chargeEntryId,
    ReverseTenantChargeInput input, {
    String? operationKey,
  }) => _runMutation(
    scope:
        'tenant-account.charge-reversal:$tenantAccountId:$chargeEntryId:${jsonEncode(input.toJson())}',
    operationKey: operationKey,
    path: '/tenant-accounts/$tenantAccountId/charges/$chargeEntryId/reversals',
    data: input.toJson(),
    decode: TenantChargeMutationResult.fromJson,
  );

  Future<TenantLedgerMutationResult> reverseLedgerEntry(
    int tenantAccountId,
    ReverseTenantLedgerEntryInput input, {
    String? operationKey,
  }) => _runMutation(
    scope:
        'tenant-account.ledger-reversal:$tenantAccountId:${jsonEncode(input.toJson())}',
    operationKey: operationKey,
    path: '/tenant-accounts/$tenantAccountId/reversals',
    data: input.toJson(),
    decode: TenantLedgerMutationResult.fromJson,
  );

  Future<RecurringTenantChargeRow> createRecurringCharge(
    int tenantAccountId,
    CreateRecurringTenantChargeInput input, {
    String? operationKey,
  }) => _runMutation(
    scope:
        'tenant-account.recurring-create:$tenantAccountId:${jsonEncode(input.toJson())}',
    operationKey: operationKey,
    path: '/tenant-accounts/$tenantAccountId/recurring-charges',
    data: input.toJson(),
    decode: RecurringTenantChargeRow.fromJson,
  );

  Future<RecurringTenantChargeRow> updateRecurringCharge(
    int tenantAccountId,
    int recurringChargeId,
    UpdateRecurringTenantChargeInput input, {
    String? operationKey,
  }) => _runMutation(
    scope:
        'tenant-account.recurring-update:$tenantAccountId:$recurringChargeId:${jsonEncode(input.toJson())}',
    operationKey: operationKey,
    path:
        '/tenant-accounts/$tenantAccountId/recurring-charges/$recurringChargeId',
    method: 'PATCH',
    data: input.toJson(),
    decode: RecurringTenantChargeRow.fromJson,
  );

  Future<RecurringTenantChargeRow> deactivateRecurringCharge(
    int tenantAccountId,
    int recurringChargeId, {
    String? operationKey,
  }) => _runMutation(
    scope:
        'tenant-account.recurring-deactivate:$tenantAccountId:$recurringChargeId',
    operationKey: operationKey,
    path:
        '/tenant-accounts/$tenantAccountId/recurring-charges/$recurringChargeId/deactivate',
    data: const <String, dynamic>{},
    decode: RecurringTenantChargeRow.fromJson,
  );

  Future<T> _runMutation<T>({
    required String scope,
    required String path,
    String method = 'POST',
    required Map<String, dynamic> data,
    required T Function(Map<String, dynamic>) decode,
    String? operationKey,
  }) async {
    Future<T> send(String key) async {
      try {
        final response = await _dio.request<Map<String, dynamic>>(
          path,
          options: Options(method: method, headers: {'Idempotency-Key': key}),
          data: data,
        );
        return decode(_requireMap(response));
      } on DioException catch (error) {
        throw ApiException.fromDioException(error);
      }
    }

    if (operationKey != null) return send(operationKey);
    return IdempotentMutation.run(scope, send);
  }
}

Map<String, dynamic> _requireMap(Response<Map<String, dynamic>> response) {
  final data = response.data;
  if (data == null) {
    throw const ApiException(
      statusCode: 0,
      message: 'Empty response from server.',
    );
  }
  return data;
}

List<dynamic> _requireList(Response<List<dynamic>> response) {
  final data = response.data;
  if (data == null) {
    throw const ApiException(
      statusCode: 0,
      message: 'Empty response from server.',
    );
  }
  return data;
}

final tenantLedgerRepositoryProvider = Provider<TenantLedgerRepository>(
  (ref) => TenantLedgerRepository(ref.watch(dioProvider)),
);
