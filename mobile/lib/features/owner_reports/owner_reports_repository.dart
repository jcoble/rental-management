import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';

// ── Models ────────────────────────────────────────────────────────────────────

class OwnerSummary {
  const OwnerSummary({
    required this.ownerId,
    required this.ownerName,
    required this.netToOwner,
  });

  final int ownerId;
  final String ownerName;
  final double netToOwner;

  factory OwnerSummary.fromJson(Map<String, dynamic> json) => OwnerSummary(
        ownerId: (json['ownerId'] as num).toInt(),
        ownerName: json['ownerName'] as String? ?? '',
        netToOwner: (json['netToOwner'] as num?)?.toDouble() ?? 0,
      );
}

class PropertyStatement {
  const PropertyStatement({
    required this.propertyName,
    required this.rentalIncome,
    required this.expenses,
    required this.managementFee,
    required this.netToOwner,
  });

  final String propertyName;
  final double rentalIncome;
  final double expenses;
  final double managementFee;
  final double netToOwner;

  factory PropertyStatement.fromJson(Map<String, dynamic> json) =>
      PropertyStatement(
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
  });

  final int ownerId;
  final String ownerName;
  final int year;
  final List<PropertyStatement> properties;
  final double totalIncome;
  final double totalExpenses;
  final double totalManagementFee;
  final double totalNetToOwner;

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
      totalManagementFee:
          (json['totalManagementFee'] as num?)?.toDouble() ?? 0,
      totalNetToOwner:
          (json['totalNetToOwner'] as num?)?.toDouble() ?? 0,
    );
  }
}

// ── Repository ────────────────────────────────────────────────────────────────

/// Owner report API calls.
///
/// Endpoints:
///   GET /accounting/owner-statements?year=     — [OwnerSummary] list
///   GET /accounting/owner-statement?ownerId=&year= — OwnerStatement detail
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
      return OwnerStatement.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final ownerReportsRepositoryProvider =
    Provider<OwnerReportsRepository>((ref) {
  return OwnerReportsRepository(ref.watch(dioProvider));
});

class OwnerSummariesNotifier
    extends Notifier<AsyncValue<List<OwnerSummary>>> {
  int _year = DateTime.now().year;

  int get year => _year;

  @override
  AsyncValue<List<OwnerSummary>> build() => const AsyncValue.loading();

  OwnerReportsRepository get _repo =>
      ref.read(ownerReportsRepositoryProvider);

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

class OwnerStatementNotifier
    extends Notifier<AsyncValue<OwnerStatement?>> {
  @override
  AsyncValue<OwnerStatement?> build() => const AsyncValue.data(null);

  OwnerReportsRepository get _repo =>
      ref.read(ownerReportsRepositoryProvider);

  Future<void> load(int ownerId, int year) async {
    state = const AsyncValue.loading();
    try {
      final stmt = await _repo.getOwnerStatement(ownerId, year);
      state = AsyncValue.data(stmt);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  void clear() => state = const AsyncValue.data(null);
}

final ownerStatementProvider = NotifierProvider<OwnerStatementNotifier,
    AsyncValue<OwnerStatement?>>(
  OwnerStatementNotifier.new,
);
