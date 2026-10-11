import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'accounting_book_models.dart';

String _dateOnlyQuery(DateTime value) =>
    value.toIso8601String().split('T').first;

Map<String, dynamic> _pageQuery({
  required int skip,
  required int take,
  String? search,
  String? sort,
  DateTime? from,
  DateTime? to,
}) => {
  'skip': skip,
  'take': take,
  if (search != null && search.trim().isNotEmpty) 'search': search,
  if (sort != null && sort.trim().isNotEmpty) 'sort': sort,
  if (from != null) 'from': _dateOnlyQuery(from),
  if (to != null) 'to': _dateOnlyQuery(to),
};

Map<String, dynamic> _statementQuery(StatementQuery query) => {
  if (query.from != null) 'from': _dateOnlyQuery(query.from!),
  if (query.to != null) 'to': _dateOnlyQuery(query.to!),
  if (query.currency != null && query.currency!.trim().isNotEmpty)
    'currency': query.currency,
  if (query.propertyId != null) 'propertyId': query.propertyId,
  if (query.unitId != null) 'unitId': query.unitId,
};

const _generalLedgerSortAllowlist = <String>{
  'effectiveOn',
  '-effectiveOn',
  'postedAtUtc',
  '-postedAtUtc',
  'accountCode',
  '-accountCode',
};

class ChartOfAccountsQuery {
  const ChartOfAccountsQuery({
    this.skip = 0,
    this.take = 50,
    this.search,
    this.sort,
    this.from,
    this.to,
    this.activeOnly,
  });

  final int skip;
  final int take;
  final String? search;
  final String? sort;
  final DateTime? from;
  final DateTime? to;
  final bool? activeOnly;

  Map<String, dynamic> get queryParameters => {
    ..._pageQuery(
      skip: skip,
      take: take,
      search: search,
      sort: sort,
      from: from,
      to: to,
    ),
    if (activeOnly != null) 'activeOnly': activeOnly,
  };
}

class GeneralLedgerQuery {
  const GeneralLedgerQuery({
    this.skip = 0,
    this.take = 50,
    this.search,
    this.sort = '-effectiveOn',
    this.accountId,
    this.propertyId,
    this.unitId,
    this.sourceType,
    this.effectiveFrom,
    this.effectiveTo,
  });

  final int skip;
  final int take;
  final String? search;
  final String sort;
  final int? accountId;
  final int? propertyId;
  final int? unitId;
  final JournalSourceType? sourceType;
  final DateTime? effectiveFrom;
  final DateTime? effectiveTo;

  String get validatedSort =>
      _generalLedgerSortAllowlist.contains(sort) ? sort : '-effectiveOn';

  Map<String, dynamic> get queryParameters => {
    ..._pageQuery(skip: skip, take: take, search: search, sort: validatedSort),
    if (accountId != null) 'accountId': accountId,
    if (propertyId != null) 'propertyId': propertyId,
    if (unitId != null) 'unitId': unitId,
    if (sourceType?.wire != null) 'sourceType': sourceType!.wire,
    if (effectiveFrom != null) 'effectiveFrom': _dateOnlyQuery(effectiveFrom!),
    if (effectiveTo != null) 'effectiveTo': _dateOnlyQuery(effectiveTo!),
  };
}

class StatementQuery {
  const StatementQuery({
    this.from,
    this.to,
    this.currency,
    this.propertyId,
    this.unitId,
  });

  final DateTime? from;
  final DateTime? to;
  final String? currency;
  final int? propertyId;
  final int? unitId;

  Map<String, dynamic> get queryParameters => _statementQuery(this);
}

class CashFlowQuery {
  const CashFlowQuery({
    this.from,
    this.to,
    this.propertyId,
    this.propertyIds,
    this.skip = 0,
    this.take = 20,
    this.sort,
  });

  final DateTime? from;
  final DateTime? to;
  final int? propertyId;
  final List<int>? propertyIds;
  final int skip;
  final int take;
  final String? sort;

  Map<String, dynamic> get queryParameters => {
    ..._pageQuery(skip: skip, take: take, sort: sort, from: from, to: to),
    if (propertyId != null) 'propertyId': propertyId,
    if (propertyIds != null && propertyIds!.isNotEmpty)
      'propertyIds': propertyIds,
  };
}

class AccountingBooksRepository {
  const AccountingBooksRepository(this._dio);

  final Dio _dio;

  Future<AccountingPage<ChartOfAccountsRow>> chartOfAccounts({
    ChartOfAccountsQuery query = const ChartOfAccountsQuery(),
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/accounting/chart-of-accounts',
        queryParameters: query.queryParameters,
      );
      return AccountingPage.fromJson(
        _requireMap(response),
        ChartOfAccountsRow.fromJson,
      );
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<AccountingPage<GeneralLedgerRow>> generalLedger({
    GeneralLedgerQuery query = const GeneralLedgerQuery(),
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/accounting/general-ledger',
        queryParameters: query.queryParameters,
      );
      return AccountingPage.fromJson(
        _requireMap(response),
        GeneralLedgerRow.fromJson,
      );
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<JournalDetail> journalDetail(String publicId) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/accounting/journal-entries/$publicId',
      );
      return JournalDetail.fromJson(_requireMap(response));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<List<SourceJournalSummary>> sourceJournals({
    required JournalSourceType sourceType,
    required int sourceId,
  }) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/accounting/source-journals',
        queryParameters: {'sourceType': sourceType.wire, 'sourceId': sourceId},
      );
      return _requireList(response)
          .whereType<Map<String, dynamic>>()
          .map(SourceJournalSummary.fromJson)
          .toList(growable: false);
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<MoneyPositionResponse> moneyPosition({
    DateTime? from,
    DateTime? to,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/accounting/money-position',
        queryParameters: {
          if (from != null) 'from': _dateOnlyQuery(from),
          if (to != null) 'to': _dateOnlyQuery(to),
        },
      );
      return MoneyPositionResponse.fromJson(_requireMap(response));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<CashFlowSummaryResponse> cashFlow({
    CashFlowQuery query = const CashFlowQuery(),
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/accounting/cash-flow',
        queryParameters: query.queryParameters,
      );
      return CashFlowSummaryResponse.fromJson(_requireMap(response));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<TrialBalanceResponse> trialBalance({
    StatementQuery query = const StatementQuery(),
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/accounting/trial-balance',
        queryParameters: query.queryParameters,
      );
      return TrialBalanceResponse.fromJson(_requireMap(response));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<FinancialStatementResponse> balanceSheet({
    StatementQuery query = const StatementQuery(),
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/accounting/balance-sheet',
        queryParameters: query.queryParameters,
      );
      return FinancialStatementResponse.fromJson(_requireMap(response));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<FinancialStatementResponse> incomeStatement({
    StatementQuery query = const StatementQuery(),
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/accounting/income-statement',
        queryParameters: query.queryParameters,
      );
      return FinancialStatementResponse.fromJson(_requireMap(response));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
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

final accountingBooksRepositoryProvider = Provider<AccountingBooksRepository>(
  (ref) => AccountingBooksRepository(ref.watch(dioProvider)),
);
