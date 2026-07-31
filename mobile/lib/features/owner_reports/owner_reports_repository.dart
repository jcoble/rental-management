import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';

// ── Models ────────────────────────────────────────────────────────────────────

class OwnerSummary {
  const OwnerSummary({
    required this.ownerId,
    required this.ownerName,
    required this.netToOwner,
    required this.totalDistributed,
    required this.undistributed,
  });

  final int ownerId;
  final String ownerName;
  final double netToOwner;
  final double totalDistributed;
  final double undistributed;

  factory OwnerSummary.fromJson(Map<String, dynamic> json) => OwnerSummary(
    ownerId: (json['ownerId'] as num).toInt(),
    ownerName: json['ownerName'] as String? ?? '',
    netToOwner: (json['netToOwner'] as num?)?.toDouble() ?? 0,
    totalDistributed: (json['totalDistributed'] as num?)?.toDouble() ?? 0,
    undistributed: (json['undistributed'] as num?)?.toDouble() ?? 0,
  );
}

class PropertyStatement {
  const PropertyStatement({
    required this.propertyId,
    required this.propertyName,
    required this.rentalIncome,
    required this.expenses,
    required this.managementFee,
    required this.netToOwner,
  });

  final int propertyId;
  final String propertyName;
  final double rentalIncome;
  final double expenses;
  final double managementFee;
  final double netToOwner;

  factory PropertyStatement.fromJson(Map<String, dynamic> json) =>
      PropertyStatement(
        propertyId: (json['propertyId'] as num?)?.toInt() ?? 0,
        propertyName: json['propertyName'] as String? ?? '',
        rentalIncome: (json['rentalIncome'] as num?)?.toDouble() ?? 0,
        expenses: (json['expenses'] as num?)?.toDouble() ?? 0,
        managementFee: (json['managementFee'] as num?)?.toDouble() ?? 0,
        netToOwner: (json['netToOwner'] as num?)?.toDouble() ?? 0,
      );
}

class OwnerStatement {
  const OwnerStatement({
    required this.ownerId,
    required this.ownerName,
    required this.year,
    required this.properties,
    required this.totalIncome,
    required this.totalExpenses,
    required this.totalManagementFee,
    required this.totalNetToOwner,
    required this.totalDistributed,
    required this.undistributed,
  });

  final int ownerId;
  final String ownerName;
  final int year;
  final List<PropertyStatement> properties;
  final double totalIncome;
  final double totalExpenses;
  final double totalManagementFee;
  final double totalNetToOwner;
  final double totalDistributed;
  final double undistributed;

  factory OwnerStatement.fromJson(Map<String, dynamic> json) {
    final props = (json['properties'] as List<dynamic>? ?? [])
        .whereType<Map<String, dynamic>>()
        .map(PropertyStatement.fromJson)
        .toList();
    return OwnerStatement(
      ownerId: (json['ownerId'] as num).toInt(),
      ownerName: json['ownerName'] as String? ?? '',
      year: (json['year'] as num?)?.toInt() ?? 0,
      properties: props,
      totalIncome: (json['totalIncome'] as num?)?.toDouble() ?? 0,
      totalExpenses: (json['totalExpenses'] as num?)?.toDouble() ?? 0,
      totalManagementFee: (json['totalManagementFee'] as num?)?.toDouble() ?? 0,
      totalNetToOwner: (json['totalNetToOwner'] as num?)?.toDouble() ?? 0,
      totalDistributed: (json['totalDistributed'] as num?)?.toDouble() ?? 0,
      undistributed: (json['undistributed'] as num?)?.toDouble() ?? 0,
    );
  }
}

enum DistributionMethod {
  check('Check'),
  ach('ACH'),
  wire('Wire'),
  cash('Cash'),
  other('Other');

  const DistributionMethod(this.label);

  final String label;

  String get apiValue => switch (this) {
    DistributionMethod.check => 'Check',
    DistributionMethod.ach => 'Ach',
    DistributionMethod.wire => 'Wire',
    DistributionMethod.cash => 'Cash',
    DistributionMethod.other => 'Other',
  };

  static DistributionMethod fromJson(Object? value) {
    final raw = value?.toString().toLowerCase();
    return switch (raw) {
      'ach' => DistributionMethod.ach,
      'wire' => DistributionMethod.wire,
      'cash' => DistributionMethod.cash,
      'other' => DistributionMethod.other,
      _ => DistributionMethod.check,
    };
  }
}

class OwnerDistribution {
  const OwnerDistribution({
    required this.id,
    required this.ownerEntityId,
    required this.ownerName,
    required this.date,
    required this.amount,
    required this.method,
    this.propertyId,
    this.propertyName,
    this.memo,
  });

  final int id;
  final int ownerEntityId;
  final String ownerName;
  final int? propertyId;
  final String? propertyName;
  final DateTime date;
  final double amount;
  final DistributionMethod method;
  final String? memo;

  factory OwnerDistribution.fromJson(Map<String, dynamic> json) {
    return OwnerDistribution(
      id: (json['id'] as num).toInt(),
      ownerEntityId: (json['ownerEntityId'] as num?)?.toInt() ?? 0,
      ownerName: json['ownerName'] as String? ?? '',
      propertyId: (json['propertyId'] as num?)?.toInt(),
      propertyName: json['propertyName'] as String?,
      date:
          DateTime.tryParse(json['date'] as String? ?? '') ??
          DateTime.fromMillisecondsSinceEpoch(0),
      amount: (json['amount'] as num?)?.toDouble() ?? 0,
      method: DistributionMethod.fromJson(json['method']),
      memo: json['memo'] as String?,
    );
  }
}

class OwnerDistributionQuery {
  const OwnerDistributionQuery({
    required this.ownerEntityId,
    required this.year,
  });

  final int ownerEntityId;
  final int year;

  @override
  bool operator ==(Object other) {
    return other is OwnerDistributionQuery &&
        other.ownerEntityId == ownerEntityId &&
        other.year == year;
  }

  @override
  int get hashCode => Object.hash(ownerEntityId, year);
}

class CreateOwnerDistributionInput {
  const CreateOwnerDistributionInput({
    required this.ownerEntityId,
    required this.date,
    required this.amount,
    required this.method,
    this.propertyId,
    this.memo,
  });

  final int ownerEntityId;
  final int? propertyId;
  final DateTime date;
  final double amount;
  final DistributionMethod method;
  final String? memo;

  Map<String, dynamic> toJson() => {
    'ownerEntityId': ownerEntityId,
    if (propertyId != null) 'propertyId': propertyId,
    'date': DateTime.utc(date.year, date.month, date.day).toIso8601String(),
    'amount': amount,
    'method': method.apiValue,
    if (memo != null && memo!.isNotEmpty) 'memo': memo,
  };
}

class ReportCatalogEntry {
  const ReportCatalogEntry({
    required this.key,
    required this.title,
    required this.description,
    required this.endpoint,
    required this.params,
    required this.external,
    required this.categoryTitle,
  });

  final String key;
  final String title;
  final String description;
  final String endpoint;
  final List<String> params;
  final bool external;
  final String categoryTitle;

  factory ReportCatalogEntry.fromJson(
    Map<String, dynamic> json, {
    required String categoryTitle,
  }) {
    final rawParams = json['params'];
    return ReportCatalogEntry(
      key: json['key'] as String? ?? '',
      title: json['title'] as String? ?? '',
      description: json['description'] as String? ?? '',
      endpoint: json['endpoint'] as String? ?? '',
      params: rawParams is List
          ? rawParams.map((value) => value.toString()).toList()
          : const [],
      external: json['external'] as bool? ?? false,
      categoryTitle: categoryTitle,
    );
  }
}

class ReportRunResult {
  const ReportRunResult({required this.entry, required this.data});

  final ReportCatalogEntry entry;
  final Map<String, dynamic> data;
}

class ReportPaging {
  const ReportPaging({this.skip = 0, this.take = 20, this.sort = 'property'});

  final int skip;
  final int take;
  final String sort;

  ReportPaging copyWith({int? skip, int? take, String? sort}) => ReportPaging(
    skip: skip ?? this.skip,
    take: take ?? this.take,
    sort: sort ?? this.sort,
  );
}

class MonthlyReportsState {
  const MonthlyReportsState({
    required this.month,
    this.results = const [],
    this.pagingByReportKey = const {},
    this.loading = false,
    this.error,
  });

  final DateTime month;
  final List<ReportRunResult> results;
  final Map<String, ReportPaging> pagingByReportKey;
  final bool loading;
  final String? error;

  ReportPaging pagingFor(String reportKey) =>
      pagingByReportKey[reportKey] ?? const ReportPaging();

  MonthlyReportsState copyWith({
    DateTime? month,
    List<ReportRunResult>? results,
    Map<String, ReportPaging>? pagingByReportKey,
    bool? loading,
    String? error,
    bool clearError = false,
  }) => MonthlyReportsState(
    month: month ?? this.month,
    results: results ?? this.results,
    pagingByReportKey: pagingByReportKey ?? this.pagingByReportKey,
    loading: loading ?? this.loading,
    error: clearError ? null : error ?? this.error,
  );
}

// ── Repository ────────────────────────────────────────────────────────────────

/// Owner report API calls.
///
/// Endpoints:
///   GET /accounting/owner-statements?year=     — [OwnerSummary] list
///   GET /accounting/owner-statement?ownerId=&year= — OwnerStatement detail
///   GET/POST/DELETE /owner-distributions       — recorded owner payouts
class OwnerReportsRepository {
  OwnerReportsRepository(this._dio);

  final Dio _dio;

  Future<List<OwnerSummary>> listOwnerSummaries(int year) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/accounting/owner-statements',
        queryParameters: {'year': year},
      );
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(OwnerSummary.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<OwnerStatement> getOwnerStatement(int ownerId, int year) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/accounting/owner-statement',
        queryParameters: {'ownerId': ownerId, 'year': year},
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return OwnerStatement.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<OwnerDistribution>> listDistributions({
    required int ownerEntityId,
    required int year,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/owner-distributions',
        queryParameters: {
          'ownerEntityId': ownerEntityId,
          'year': year,
          'take': 200,
          'sort': '-date',
        },
      );
      final rawItems = response.data?['items'];
      final items = rawItems is List ? rawItems : const [];
      return items
          .whereType<Map<String, dynamic>>()
          .map(OwnerDistribution.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<OwnerDistribution> createDistribution(
    CreateOwnerDistributionInput input,
  ) async {
    try {
      final payload = input.toJson();
      final response = await IdempotentMutation.run(
        'owner-distributions:create:${jsonEncode(payload)}',
        (key) => _dio.post<Map<String, dynamic>>(
          '/owner-distributions',
          data: payload,
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return OwnerDistribution.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> deleteDistribution(int id) async {
    try {
      await IdempotentMutation.run(
        'owner-distributions:delete:$id',
        (key) => _dio.delete<void>(
          '/owner-distributions/$id',
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<ReportCatalogEntry>> listReportsCatalog() async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/reports/catalog');
      final categories = response.data?['categories'];
      if (categories is! List) return const [];
      return categories
          .whereType<Map<String, dynamic>>()
          .expand((category) {
            final title = category['title'] as String? ?? '';
            final reports = category['reports'];
            if (reports is! List) return const <ReportCatalogEntry>[];
            return reports.whereType<Map<String, dynamic>>().map(
              (report) =>
                  ReportCatalogEntry.fromJson(report, categoryTitle: title),
            );
          })
          .where((entry) => entry.key.isNotEmpty && entry.endpoint.isNotEmpty)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ReportRunResult> runReport(
    ReportCatalogEntry entry, {
    required DateTime month,
    ReportPaging paging = const ReportPaging(),
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        _relativeReportEndpoint(entry.endpoint),
        queryParameters: _queryFor(entry, month, paging),
      );
      return ReportRunResult(entry: entry, data: response.data ?? const {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  static String _relativeReportEndpoint(String endpoint) =>
      endpoint.replaceFirst(RegExp(r'^/api/v1'), '');

  static Map<String, dynamic> _queryFor(
    ReportCatalogEntry entry,
    DateTime month,
    ReportPaging paging,
  ) {
    final params = entry.params.toSet();
    final from = DateTime.utc(month.year, month.month, 1);
    final to = DateTime.utc(month.year, month.month + 1, 0);
    return {
      if (params.contains('from')) 'from': _dateOnly(from),
      if (params.contains('to')) 'to': _dateOnly(to),
      if (params.contains('year')) 'year': month.year,
      if (params.contains('skip')) 'skip': paging.skip,
      if (params.contains('take')) 'take': paging.take,
      if (params.contains('sort')) 'sort': paging.sort,
    };
  }

  static String _dateOnly(DateTime value) =>
      '${value.year.toString().padLeft(4, '0')}-'
      '${value.month.toString().padLeft(2, '0')}-'
      '${value.day.toString().padLeft(2, '0')}';
}

// ── Providers ─────────────────────────────────────────────────────────────────

final ownerReportsRepositoryProvider = Provider<OwnerReportsRepository>((ref) {
  return OwnerReportsRepository(ref.watch(dioProvider));
});

class OwnerSummariesNotifier extends Notifier<AsyncValue<List<OwnerSummary>>> {
  int _year = DateTime.now().year;

  int get year => _year;

  @override
  AsyncValue<List<OwnerSummary>> build() => const AsyncValue.loading();

  OwnerReportsRepository get _repo => ref.read(ownerReportsRepositoryProvider);

  Future<void> load({int? year}) async {
    if (year != null) _year = year;
    state = const AsyncValue.loading();
    try {
      final list = await _repo.listOwnerSummaries(_year);
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final ownerSummariesProvider =
    NotifierProvider<OwnerSummariesNotifier, AsyncValue<List<OwnerSummary>>>(
      OwnerSummariesNotifier.new,
    );

class OwnerStatementNotifier extends Notifier<AsyncValue<OwnerStatement?>> {
  int? _ownerId;
  int? _year;

  @override
  AsyncValue<OwnerStatement?> build() => const AsyncValue.data(null);

  OwnerReportsRepository get _repo => ref.read(ownerReportsRepositoryProvider);

  Future<void> load(int ownerId, int year) async {
    _ownerId = ownerId;
    _year = year;
    state = const AsyncValue.loading();
    try {
      final stmt = await _repo.getOwnerStatement(ownerId, year);
      state = AsyncValue.data(stmt);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() async {
    final ownerId = _ownerId;
    final year = _year;
    if (ownerId == null || year == null) return;
    await load(ownerId, year);
  }

  void clear() => state = const AsyncValue.data(null);
}

final ownerStatementProvider =
    NotifierProvider<OwnerStatementNotifier, AsyncValue<OwnerStatement?>>(
      OwnerStatementNotifier.new,
    );

final ownerDistributionsProvider = FutureProvider.autoDispose
    .family<List<OwnerDistribution>, OwnerDistributionQuery>((ref, query) {
      return ref
          .watch(ownerReportsRepositoryProvider)
          .listDistributions(
            ownerEntityId: query.ownerEntityId,
            year: query.year,
          );
    });

class MonthlyReportsNotifier extends Notifier<MonthlyReportsState> {
  static const _requiredCloseReportKeys = <String>{
    'income-expense-statement',
    'property-pnl-summary',
    'cash-flow',
    'general-ledger',
    'rent-roll',
    'rent-ledger',
    'security-deposit-register',
    'delinquency',
    'owner-distributions',
  };

  @override
  MonthlyReportsState build() {
    final now = DateTime.now();
    return MonthlyReportsState(month: DateTime.utc(now.year, now.month));
  }

  OwnerReportsRepository get _repo => ref.read(ownerReportsRepositoryProvider);

  Future<void> load({required DateTime month}) async {
    final normalizedMonth = DateTime.utc(month.year, month.month);
    final pagingByReportKey = state.pagingByReportKey;
    state = MonthlyReportsState(
      month: normalizedMonth,
      pagingByReportKey: pagingByReportKey,
      loading: true,
    );
    try {
      final catalog = await _repo.listReportsCatalog();
      final entries = catalog
          .where(
            (entry) =>
                !entry.external && _requiredCloseReportKeys.contains(entry.key),
          )
          .toList();
      final results = <ReportRunResult>[];
      for (final entry in entries) {
        results.add(
          await _repo.runReport(
            entry,
            month: normalizedMonth,
            paging: state.pagingFor(entry.key),
          ),
        );
      }
      state = MonthlyReportsState(
        month: normalizedMonth,
        results: results,
        pagingByReportKey: pagingByReportKey,
      );
    } on ApiException catch (error) {
      state = state.copyWith(loading: false, error: error.message);
    } catch (_) {
      state = state.copyWith(
        loading: false,
        error: "Couldn't load monthly reports.",
      );
    }
  }

  Future<void> refresh() => load(month: state.month);

  Future<void> pageReport(String reportKey, int pageDelta) {
    final current = state.pagingFor(reportKey);
    final nextSkip = current.skip + (pageDelta * current.take);
    final updatedPaging = current.copyWith(skip: nextSkip < 0 ? 0 : nextSkip);
    state = state.copyWith(
      pagingByReportKey: {
        ...state.pagingByReportKey,
        reportKey: updatedPaging,
      },
    );
    return load(month: state.month);
  }
}

final monthlyReportsProvider =
    NotifierProvider<MonthlyReportsNotifier, MonthlyReportsState>(
      MonthlyReportsNotifier.new,
    );
