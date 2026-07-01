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

  Future<List<WorkOrder>> listWorkOrders({
    int? propertyId,
    int? unitId,
    int? vendorId,
    bool openOnly = false,
    int take = 50,
    String sort = '-updatedAt',
    String? requestedFrom,
    String? requestedTo,
    String? scheduledFrom,
    String? scheduledTo,
    String? completedFrom,
    String? completedTo,
  }) async {
    try {
      final params = <String, dynamic>{'take': take, 'sort': sort};
      if (propertyId != null) params['propertyId'] = propertyId;
      if (unitId != null) params['unitId'] = unitId;
      if (vendorId != null) params['vendorId'] = vendorId;
      if (openOnly) params['openOnly'] = true;
      if (requestedFrom != null && requestedFrom.isNotEmpty) {
        params['requestedFrom'] = requestedFrom;
      }
      if (requestedTo != null && requestedTo.isNotEmpty) {
        params['requestedTo'] = requestedTo;
      }
      if (scheduledFrom != null && scheduledFrom.isNotEmpty) {
        params['scheduledFrom'] = scheduledFrom;
      }
      if (scheduledTo != null && scheduledTo.isNotEmpty) {
        params['scheduledTo'] = scheduledTo;
      }
      if (completedFrom != null && completedFrom.isNotEmpty) {
        params['completedFrom'] = completedFrom;
      }
      if (completedTo != null && completedTo.isNotEmpty) {
        params['completedTo'] = completedTo;
      }
      final response = await _dio.get<Map<String, dynamic>>(
        '/work-orders/page',
        queryParameters: params,
      );
      final data = response.data?['items'] as List<dynamic>? ?? [];
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
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return WorkOrder.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// GET /work-orders/{id} — single work order PLUS its status timeline.
  Future<WorkOrderDetail> getWorkOrderDetail(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/work-orders/$id');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return WorkOrderDetail.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// GET /documents?entityType=WorkOrder&entityId={id} — attachments for a WO.
  Future<List<Document>> listWorkOrderDocuments(int workOrderId) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/documents',
        queryParameters: {'entityType': 'WorkOrder', 'entityId': workOrderId},
      );
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(Document.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// GET /documents/{id}/file — raw blob bytes (authenticated via the shared
  /// Dio interceptor, so this works for the photo strip thumbnails).
  Future<Uint8List> downloadDocument(int documentId) async {
    try {
      final response = await _dio.get<List<int>>(
        '/documents/$documentId/file',
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
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
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return WorkOrder.fromJson(responseData);
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
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return WorkOrder.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// PATCH /work-orders/{id}  body: { status, statusNote? }
  ///
  /// [statusNote] is recorded on the status timeline for this transition.
  Future<WorkOrder> updateStatus(int id, String status, {String? note}) async {
    try {
      final trimmed = note?.trim();
      final response = await _dio.patch<Map<String, dynamic>>(
        '/work-orders/$id',
        data: {
          'status': status,
          if (trimmed != null && trimmed.isNotEmpty) 'statusNote': trimmed,
        },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return WorkOrder.fromJson(data);
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

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final workOrders = await _repo.listWorkOrders(
        openOnly: _filter == WorkOrderFilter.open,
      );
      state = AsyncValue.data(workOrders);
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
      if (_filter == WorkOrderFilter.open) {
        await load();
        return;
      }
      state.whenData((list) {
        final newList = [
          for (final w in list)
            if (w.id == id) updated else w,
        ];
        state = AsyncValue.data(newList);
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

// ── Single work order (with status timeline) ──────────────────────────────────

class WorkOrderDetailNotifier extends Notifier<AsyncValue<WorkOrderDetail>> {
  WorkOrderDetailNotifier(this._id);

  final int _id;

  @override
  AsyncValue<WorkOrderDetail> build() {
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  WorkOrdersRepository get _repo => ref.read(workOrdersRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final detail = await _repo.getWorkOrderDetail(_id);
      state = AsyncValue.data(detail);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();

  /// Changes status (optionally with a [note] recorded on the timeline), then
  /// reloads so the new timeline entry is shown.
  Future<void> updateStatus(String status, {String? note}) async {
    try {
      await _repo.updateStatus(_id, status, note: note);
      // Reload to pick up the freshly-appended timeline entry.
      final detail = await _repo.getWorkOrderDetail(_id);
      state = AsyncValue.data(detail);
      // Also refresh the list so the status change propagates there too.
      ref.read(workOrdersProvider.notifier).refresh();
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> update(Map<String, dynamic> data) async {
    try {
      await _repo.updateWorkOrder(_id, data);
      final detail = await _repo.getWorkOrderDetail(_id);
      state = AsyncValue.data(detail);
      ref.read(workOrdersProvider.notifier).refresh();
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }
}

final workOrderDetailProvider =
    NotifierProvider.family<
      WorkOrderDetailNotifier,
      AsyncValue<WorkOrderDetail>,
      int
    >(WorkOrderDetailNotifier.new);

/// Photo/document strip for a work order. Auto-disposes so it re-fetches each
/// time the detail screen is opened; invalidate it after an upload.
final workOrderDocumentsProvider = FutureProvider.autoDispose
    .family<List<Document>, int>((ref, workOrderId) {
      return ref
          .watch(workOrdersRepositoryProvider)
          .listWorkOrderDocuments(workOrderId);
    });

/// Raw bytes for a single document, used to render authenticated thumbnails.
final documentBytesProvider = FutureProvider.autoDispose.family<Uint8List, int>(
  (ref, documentId) {
    return ref.watch(workOrdersRepositoryProvider).downloadDocument(documentId);
  },
);

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
