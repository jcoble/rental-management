import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/models/models.dart';

/// Repository for work orders.
///
/// Endpoints:
///   GET    /work-orders                       — list (JWT-scoped, optional ?propertyId=)
///   GET    /work-orders/{id}                  — single
///   POST   /work-orders                       — create { propertyId, title, description, priority, category }
///   PATCH  /work-orders/{id}                  — update (same fields, partial)
///   PATCH  /work-orders/{id}  { status }      — status update (status is a normal field)
///   GET    /properties                        — for the property dropdown in create form
class WorkOrdersRepository {
  WorkOrdersRepository(this._dio);

  final Dio _dio;

  Future<List<WorkOrder>> listWorkOrders({int? propertyId}) async {
    try {
      final params = <String, dynamic>{};
      if (propertyId != null) params['propertyId'] = propertyId;
      final response = await _dio.get<List<dynamic>>(
        '/work-orders',
        queryParameters: params.isEmpty ? null : params,
      );
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(WorkOrder.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<WorkOrder> getWorkOrder(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/work-orders/$id');
      return WorkOrder.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Create body: { propertyId, title, description, priority, category }
  Future<WorkOrder> createWorkOrder(Map<String, dynamic> data) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/work-orders',
        data: data,
      );
      return WorkOrder.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> uploadWorkOrderPhoto({
    required int workOrderId,
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async {
    try {
      final formData = FormData.fromMap({
        'file': MultipartFile.fromBytes(
          bytes,
          filename: fileName,
          contentType: DioMediaType.parse(contentType),
        ),
        'entityType': 'WorkOrder',
        'entityId': workOrderId,
        'category': 'Maintenance photo',
      });
      await _dio.post<Map<String, dynamic>>('/documents', data: formData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Update body: any subset of work order fields (partial PATCH).
  Future<WorkOrder> updateWorkOrder(int id, Map<String, dynamic> data) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/work-orders/$id',
        data: data,
      );
      return WorkOrder.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// PATCH /work-orders/{id}  body: { status: "..." }
  Future<WorkOrder> updateStatus(int id, String status) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/work-orders/$id',
        data: {'status': status},
      );
      return WorkOrder.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// GET /properties — full list for the create-form dropdown.
  Future<List<Property>> listProperties() async {
    try {
      final response = await _dio.get<List<dynamic>>('/properties');
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(Property.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final workOrdersRepositoryProvider = Provider<WorkOrdersRepository>((ref) {
  return WorkOrdersRepository(ref.watch(dioProvider));
});

// ── Work orders list ──────────────────────────────────────────────────────────

/// Filter mode for [WorkOrdersNotifier].
enum WorkOrderFilter { open, all }

class WorkOrdersNotifier extends Notifier<AsyncValue<List<WorkOrder>>> {
  WorkOrderFilter _filter = WorkOrderFilter.open;

  WorkOrderFilter get filter => _filter;

  @override
  AsyncValue<List<WorkOrder>> build() => const AsyncValue.loading();

  WorkOrdersRepository get _repo => ref.read(workOrdersRepositoryProvider);

  static const _closedStatuses = {'Completed', 'Cancelled'};

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final all = await _repo.listWorkOrders();
      final filtered = _filter == WorkOrderFilter.open
          ? all.where((w) => !_closedStatuses.contains(w.status)).toList()
          : all;
      state = AsyncValue.data(filtered);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();

  void setFilter(WorkOrderFilter f) {
    if (_filter == f) return;
    _filter = f;
    load();
  }

  /// Updates a single work order's status in-memory after a successful PATCH.
  Future<void> updateStatus(int id, String status) async {
    try {
      final updated = await _repo.updateStatus(id, status);
      state.whenData((list) {
        final newList = [
          for (final w in list)
            if (w.id == id) updated else w,
        ];
        // Re-apply filter so closed items disappear from the open view.
        final filtered = _filter == WorkOrderFilter.open
            ? newList.where((w) => !_closedStatuses.contains(w.status)).toList()
            : newList;
        state = AsyncValue.data(filtered);
      });
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }
}

final workOrdersProvider =
    NotifierProvider<WorkOrdersNotifier, AsyncValue<List<WorkOrder>>>(
      WorkOrdersNotifier.new,
    );

// ── Single work order ─────────────────────────────────────────────────────────

class WorkOrderDetailNotifier extends Notifier<AsyncValue<WorkOrder>> {
  WorkOrderDetailNotifier(this._id);

  final int _id;

  @override
  AsyncValue<WorkOrder> build() {
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  WorkOrdersRepository get _repo => ref.read(workOrdersRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final wo = await _repo.getWorkOrder(_id);
      state = AsyncValue.data(wo);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();

  Future<void> updateStatus(String status) async {
    try {
      final updated = await _repo.updateStatus(_id, status);
      state = AsyncValue.data(updated);
      // Also refresh the list so the status change propagates there too.
      ref.read(workOrdersProvider.notifier).refresh();
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> update(Map<String, dynamic> data) async {
    try {
      final updated = await _repo.updateWorkOrder(_id, data);
      state = AsyncValue.data(updated);
      ref.read(workOrdersProvider.notifier).refresh();
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }
}

final workOrderDetailProvider =
    NotifierProvider.family<
      WorkOrderDetailNotifier,
      AsyncValue<WorkOrder>,
      int
    >(WorkOrderDetailNotifier.new);

// ── Properties for dropdown ───────────────────────────────────────────────────

class PropertiesForWoNotifier extends Notifier<AsyncValue<List<Property>>> {
  @override
  AsyncValue<List<Property>> build() => const AsyncValue.loading();

  WorkOrdersRepository get _repo => ref.read(workOrdersRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final list = await _repo.listProperties();
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }
}

final propertiesForWoProvider =
    NotifierProvider<PropertiesForWoNotifier, AsyncValue<List<Property>>>(
      PropertiesForWoNotifier.new,
    );
