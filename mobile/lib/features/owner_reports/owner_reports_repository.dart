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
        totalDistributed:
            (json['totalDistributed'] as num?)?.toDouble() ?? 0,
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
      totalManagementFee:
          (json['totalManagementFee'] as num?)?.toDouble() ?? 0,
      totalNetToOwner:
          (json['totalNetToOwner'] as num?)?.toDouble() ?? 0,
      totalDistributed:
          (json['totalDistributed'] as num?)?.toDouble() ?? 0,
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
      date: DateTime.tryParse(json['date'] as String? ?? '') ??
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
      final response = await _dio.post<Map<String, dynamic>>(
        '/owner-distributions',
        data: input.toJson(),
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
      await _dio.delete<void>('/owner-distributions/$id');
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
  int? _ownerId;
  int? _year;

  @override
  AsyncValue<OwnerStatement?> build() => const AsyncValue.data(null);

  OwnerReportsRepository get _repo =>
      ref.read(ownerReportsRepositoryProvider);

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

final ownerStatementProvider = NotifierProvider<OwnerStatementNotifier,
    AsyncValue<OwnerStatement?>>(
  OwnerStatementNotifier.new,
);

final ownerDistributionsProvider = FutureProvider.autoDispose
    .family<List<OwnerDistribution>, OwnerDistributionQuery>((ref, query) {
  return ref.watch(ownerReportsRepositoryProvider).listDistributions(
        ownerEntityId: query.ownerEntityId,
        year: query.year,
      );
});
