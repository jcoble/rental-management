import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';

// ── Models ────────────────────────────────────────────────────────────────────

class TrendPoint {
  const TrendPoint({
    required this.month,
    required this.income,
    required this.expenses,
    required this.net,
  });

  final String month;
  final double income;
  final double expenses;
  final double net;

  factory TrendPoint.fromJson(Map<String, dynamic> json) => TrendPoint(
        month: json['month'] as String? ?? '',
        income: (json['income'] as num?)?.toDouble() ?? 0,
        expenses: (json['expenses'] as num?)?.toDouble() ?? 0,
        net: (json['net'] as num?)?.toDouble() ?? 0,
      );
}

class WorkOrderPriorityStat {
  const WorkOrderPriorityStat({
    required this.priority,
    required this.count,
  });

  final String priority;
  final int count;

  factory WorkOrderPriorityStat.fromJson(Map<String, dynamic> json) =>
      WorkOrderPriorityStat(
        priority: json['priority'] as String? ?? '',
        count: (json['count'] as num?)?.toInt() ?? 0,
      );
}

class AnalyticsOverview {
  const AnalyticsOverview({
    required this.totalUnits,
    required this.occupiedUnits,
    required this.occupancyRate,
    required this.monthRentScheduled,
    required this.monthRentCollected,
    required this.collectionRate,
    required this.overdueCount,
    required this.overdueAmount,
    required this.trend,
    required this.leasesExpiring30,
    required this.leasesExpiring60,
    required this.leasesExpiring90,
    required this.openWorkOrders,
    required this.monthlyRecurringRent,
  });

  final int totalUnits;
  final int occupiedUnits;
  final double occupancyRate;
  final double monthRentScheduled;
  final double monthRentCollected;
  final double collectionRate;
  final int overdueCount;
  final double overdueAmount;
  final List<TrendPoint> trend;
  final int leasesExpiring30;
  final int leasesExpiring60;
  final int leasesExpiring90;
  final List<WorkOrderPriorityStat> openWorkOrders;
  final double monthlyRecurringRent;

  factory AnalyticsOverview.fromJson(Map<String, dynamic> json) {
    final overdue = json['overdue'] as Map<String, dynamic>? ?? {};
    final trend = (json['trend'] as List<dynamic>? ?? [])
        .whereType<Map<String, dynamic>>()
        .map(TrendPoint.fromJson)
        .toList();
    final workOrders = (json['openWorkOrders'] as List<dynamic>? ?? [])
        .whereType<Map<String, dynamic>>()
        .map(WorkOrderPriorityStat.fromJson)
        .toList();
    return AnalyticsOverview(
      totalUnits: (json['totalUnits'] as num?)?.toInt() ?? 0,
      occupiedUnits: (json['occupiedUnits'] as num?)?.toInt() ?? 0,
      occupancyRate: (json['occupancyRate'] as num?)?.toDouble() ?? 0,
      monthRentScheduled:
          (json['monthRentScheduled'] as num?)?.toDouble() ?? 0,
      monthRentCollected:
          (json['monthRentCollected'] as num?)?.toDouble() ?? 0,
      collectionRate: (json['collectionRate'] as num?)?.toDouble() ?? 0,
      overdueCount: (overdue['count'] as num?)?.toInt() ?? 0,
      overdueAmount: (overdue['amount'] as num?)?.toDouble() ?? 0,
      trend: trend,
      leasesExpiring30: (json['leasesExpiring30'] as num?)?.toInt() ?? 0,
      leasesExpiring60: (json['leasesExpiring60'] as num?)?.toInt() ?? 0,
      leasesExpiring90: (json['leasesExpiring90'] as num?)?.toInt() ?? 0,
      openWorkOrders: workOrders,
      monthlyRecurringRent:
          (json['monthlyRecurringRent'] as num?)?.toDouble() ?? 0,
    );
  }
}

// ── Repository ────────────────────────────────────────────────────────────────

/// Fetches portfolio analytics.
///
/// Endpoints:
///   GET /analytics/overview  — AnalyticsOverview (JWT-scoped)
class AnalyticsRepository {
  AnalyticsRepository(this._dio);

  final Dio _dio;

  Future<AnalyticsOverview> getOverview() async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/analytics/overview');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return AnalyticsOverview.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final analyticsRepositoryProvider = Provider<AnalyticsRepository>((ref) {
  return AnalyticsRepository(ref.watch(dioProvider));
});

class AnalyticsOverviewNotifier
    extends Notifier<AsyncValue<AnalyticsOverview>> {
  @override
  AsyncValue<AnalyticsOverview> build() => const AsyncValue.loading();

  AnalyticsRepository get _repo => ref.read(analyticsRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final overview = await _repo.getOverview();
      state = AsyncValue.data(overview);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final analyticsOverviewProvider = NotifierProvider<AnalyticsOverviewNotifier,
    AsyncValue<AnalyticsOverview>>(
  AnalyticsOverviewNotifier.new,
);
