import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';
import '../../core/models/models.dart' show Property, Unit;
import '../properties/properties_repository.dart';
import '../vendors/vendors_models.dart';
import '../vendors/vendors_repository.dart';
import 'recurring_maintenance_models.dart';

class RecurringMaintenanceListQuery {
  const RecurringMaintenanceListQuery({
    required this.unitId,
    this.skip = 0,
    this.take = 20,
    this.search,
    this.sort = 'nextDueDate',
    this.activeOnly,
  });

  final int unitId;
  final int skip;
  final int take;
  final String? search;
  final String sort;
  final bool? activeOnly;

  @override
  bool operator ==(Object other) =>
      other is RecurringMaintenanceListQuery &&
      other.unitId == unitId &&
      other.skip == skip &&
      other.take == take &&
      other.search == search &&
      other.sort == sort &&
      other.activeOnly == activeOnly;

  @override
  int get hashCode => Object.hash(unitId, skip, take, search, sort, activeOnly);
}

class RecurringMaintenanceListPage {
  const RecurringMaintenanceListPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<RecurringMaintenanceTask> items;
  final int totalCount;
  final int skip;
  final int take;

  factory RecurringMaintenanceListPage.fromJson(Map<String, dynamic> json) {
    final items = (json['items'] as List<dynamic>? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(RecurringMaintenanceTask.fromJson)
        .toList();
    return RecurringMaintenanceListPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

/// Repository for recurring maintenance tasks.
///
/// Endpoints (all JWT-scoped, portfolio from claim):
///   GET    /recurring-maintenance?propertyId&activeOnly  — list
///   GET    /recurring-maintenance/{id}                    — single
///   POST   /recurring-maintenance                         — create
///   PATCH  /recurring-maintenance/{id}                    — update (partial)
///   PATCH  /recurring-maintenance/{id}/active { isActive }— toggle active
///   DELETE /recurring-maintenance/{id}                    — soft delete
class RecurringMaintenanceRepository {
  RecurringMaintenanceRepository(this._dio);

  final Dio _dio;

  Future<RecurringMaintenanceListPage> listUnitPage(
    RecurringMaintenanceListQuery query,
  ) async {
    final params = <String, dynamic>{
      'unitId': query.unitId,
      'skip': query.skip,
      'take': query.take,
      'search': query.search,
      'sort': query.sort,
      'activeOnly': query.activeOnly,
    }..removeWhere((_, value) => value == null || value == '');
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/recurring-maintenance/page',
        queryParameters: params,
      );
      return RecurringMaintenanceListPage.fromJson(
        response.data ?? const <String, dynamic>{},
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<RecurringMaintenanceTask>> list({
    int? propertyId,
    bool? activeOnly,
  }) async {
    try {
      final params = <String, dynamic>{};
      if (propertyId != null) params['propertyId'] = propertyId;
      if (activeOnly != null) params['activeOnly'] = activeOnly;
      final response = await _dio.get<List<dynamic>>(
        '/recurring-maintenance',
        queryParameters: params.isEmpty ? null : params,
      );
      return (response.data ?? [])
          .whereType<Map<String, dynamic>>()
          .map(RecurringMaintenanceTask.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<RecurringMaintenanceTask> get(int id) async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/recurring-maintenance/$id');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return RecurringMaintenanceTask.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Create body: { propertyId, unitId?, vendorId?, title, description?,
  /// category?, recurrenceInterval, nextDueDate, isActive, priority }
  Future<RecurringMaintenanceTask> create(Map<String, dynamic> data) async {
    try {
      final response = await IdempotentMutation.run(
        'recurring-maintenance:create:${jsonEncode(data)}',
        (key) => _dio.post<Map<String, dynamic>>(
          '/recurring-maintenance',
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
      return RecurringMaintenanceTask.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Update body: any subset of the task fields (partial PATCH).
  Future<RecurringMaintenanceTask> update(
    int id,
    Map<String, dynamic> data,
  ) async {
    try {
      final response = await IdempotentMutation.run(
        'recurring-maintenance:update:$id:${jsonEncode(data)}',
        (key) => _dio.patch<Map<String, dynamic>>(
          '/recurring-maintenance/$id',
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
      return RecurringMaintenanceTask.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// PATCH /recurring-maintenance/{id}/active  body: { isActive }
  Future<RecurringMaintenanceTask> setActive(int id, bool isActive) async {
    try {
      final response = await IdempotentMutation.run(
        'recurring-maintenance:active:$id:$isActive',
        (key) => _dio.patch<Map<String, dynamic>>(
          '/recurring-maintenance/$id/active',
          data: {'isActive': isActive},
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
      return RecurringMaintenanceTask.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Soft delete.
  Future<void> delete(int id) async {
    try {
      await IdempotentMutation.run(
        'recurring-maintenance:delete:$id',
        (key) => _dio.delete<dynamic>(
          '/recurring-maintenance/$id',
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final recurringMaintenanceRepositoryProvider =
    Provider<RecurringMaintenanceRepository>((ref) {
  return RecurringMaintenanceRepository(ref.watch(dioProvider));
});

final unitRecurringMaintenancePageProvider = FutureProvider.autoDispose
    .family<RecurringMaintenanceListPage, RecurringMaintenanceListQuery>(
      (ref, query) => ref
          .watch(recurringMaintenanceRepositoryProvider)
          .listUnitPage(query),
    );

// ── Task list ─────────────────────────────────────────────────────────────────

/// Loads recurring maintenance tasks and supports an active/all filter plus
/// optimistic inline toggling and deletion.
class RecurringMaintenanceNotifier
    extends Notifier<AsyncValue<List<RecurringMaintenanceTask>>> {
  bool _activeOnly = false;

  bool get activeOnly => _activeOnly;

  @override
  AsyncValue<List<RecurringMaintenanceTask>> build() =>
      const AsyncValue.loading();

  RecurringMaintenanceRepository get _repo =>
      ref.read(recurringMaintenanceRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final list = await _repo.list(activeOnly: _activeOnly ? true : null);
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();

  void setActiveOnly(bool value) {
    if (_activeOnly == value) return;
    _activeOnly = value;
    load();
  }

  /// Toggles the active flag for a task, refreshing the list afterward so the
  /// active-only filter stays consistent.
  Future<void> toggleActive(int id, bool isActive) async {
    await _repo.setActive(id, isActive);
    await load();
  }

  /// Soft-deletes a task, then reloads.
  Future<void> delete(int id) async {
    await _repo.delete(id);
    await load();
  }
}

final recurringMaintenanceProvider = NotifierProvider<
    RecurringMaintenanceNotifier,
    AsyncValue<List<RecurringMaintenanceTask>>>(
  RecurringMaintenanceNotifier.new,
);

// ── Pickers (reuse existing repositories) ─────────────────────────────────────

/// All properties for the property picker.
final recurringPropertiesProvider =
    FutureProvider.autoDispose<List<Property>>((ref) {
  return ref.watch(propertiesRepositoryProvider).listProperties();
});

/// Units scoped to a property, for the (optional) unit picker.
final recurringUnitsProvider =
    FutureProvider.autoDispose.family<List<Unit>, int>((ref, propertyId) {
  return ref.watch(propertiesRepositoryProvider).listUnits(propertyId);
});

/// All vendors for the (optional) vendor picker.
final recurringVendorsProvider =
    FutureProvider.autoDispose<List<Vendor>>((ref) {
  return ref.watch(vendorsRepositoryProvider).list();
});
