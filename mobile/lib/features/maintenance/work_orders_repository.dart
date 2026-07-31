import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';
import '../../core/auth/auth_controller.dart';
import '../../core/models/models.dart';

class WorkOrderListQuery {
  const WorkOrderListQuery({
    this.skip = 0,
    this.take = 20,
    this.propertyId,
    this.unitId,
    this.vendorId,
    this.openOnly = false,
    this.search,
    this.sort = '-updatedAt',
    this.requestedFrom,
    this.requestedTo,
    this.scheduledFrom,
    this.scheduledTo,
    this.completedFrom,
    this.completedTo,
  });

  final int skip;
  final int take;
  final int? propertyId;
  final int? unitId;
  final int? vendorId;
  final bool openOnly;
  final String? search;
  final String sort;
  final String? requestedFrom;
  final String? requestedTo;
  final String? scheduledFrom;
  final String? scheduledTo;
  final String? completedFrom;
  final String? completedTo;

  @override
  bool operator ==(Object other) {
    return other is WorkOrderListQuery &&
        other.skip == skip &&
        other.take == take &&
        other.propertyId == propertyId &&
        other.unitId == unitId &&
        other.vendorId == vendorId &&
        other.openOnly == openOnly &&
        other.search == search &&
        other.sort == sort &&
        other.requestedFrom == requestedFrom &&
        other.requestedTo == requestedTo &&
        other.scheduledFrom == scheduledFrom &&
        other.scheduledTo == scheduledTo &&
        other.completedFrom == completedFrom &&
        other.completedTo == completedTo;
  }

  @override
  int get hashCode => Object.hash(
    skip,
    take,
    propertyId,
    unitId,
    vendorId,
    openOnly,
    search,
    sort,
    requestedFrom,
    requestedTo,
    scheduledFrom,
    scheduledTo,
    completedFrom,
    completedTo,
  );
}

class WorkOrderListPage {
  const WorkOrderListPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<WorkOrder> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory WorkOrderListPage.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final items = rawItems is List
        ? rawItems
              .whereType<Map<String, dynamic>>()
              .map(WorkOrder.fromJson)
              .toList()
        : <WorkOrder>[];

    return WorkOrderListPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

bool _jsonBool(
  Map<String, dynamic> json,
  String key, {
  bool fallback = false,
}) => json[key] as bool? ?? fallback;

class WorkOrderDetailCapabilities {
  const WorkOrderDetailCapabilities({
    required this.canViewTenantContact,
    required this.canViewResidents,
    required this.canViewAccessInstructions,
    required this.canViewPrivateManagementNotes,
    required this.canViewCosts,
    required this.canCommentPublicly,
    required this.canCommentPrivately,
    required this.canUploadPhoto,
    required this.canDeletePhoto,
    required this.canCancel,
    required this.canEditRequestFields,
    required this.canEditManagementFields,
    required this.canAssignTechnician,
    required this.canDispatchVendor,
    required this.allowedStatusTransitions,
  });

  final bool canViewTenantContact;
  final bool canViewResidents;
  final bool canViewAccessInstructions;
  final bool canViewPrivateManagementNotes;
  final bool canViewCosts;
  final bool canCommentPublicly;
  final bool canCommentPrivately;
  final bool canUploadPhoto;
  final bool canDeletePhoto;
  final bool canCancel;
  final bool canEditRequestFields;
  final bool canEditManagementFields;
  final bool canAssignTechnician;
  final bool canDispatchVendor;
  final List<String> allowedStatusTransitions;

  bool get canEdit => canEditRequestFields || canEditManagementFields;
  bool get canUpdateStatus => allowedStatusTransitions.isNotEmpty;
  bool get canContactVendor => canDispatchVendor;
  bool get canRateVendor => canEditManagementFields;

  static const none = WorkOrderDetailCapabilities(
    canViewTenantContact: false,
    canViewResidents: false,
    canViewAccessInstructions: false,
    canViewPrivateManagementNotes: false,
    canViewCosts: false,
    canCommentPublicly: false,
    canCommentPrivately: false,
    canUploadPhoto: false,
    canDeletePhoto: false,
    canCancel: false,
    canEditRequestFields: false,
    canEditManagementFields: false,
    canAssignTechnician: false,
    canDispatchVendor: false,
    allowedStatusTransitions: [],
  );

  factory WorkOrderDetailCapabilities.fromJson(Map<String, dynamic> json) {
    final raw = json['capabilities'];
    if (raw is! Map<String, dynamic>) return none;
    final source = raw;
    final rawTransitions = source['allowedStatusTransitions'];
    return WorkOrderDetailCapabilities(
      canViewTenantContact: _jsonBool(source, 'canViewTenantContact'),
      canViewResidents: _jsonBool(source, 'canViewResidents'),
      canViewAccessInstructions: _jsonBool(source, 'canViewAccessInstructions'),
      canViewPrivateManagementNotes: _jsonBool(
        source,
        'canViewPrivateManagementNotes',
      ),
      canViewCosts: _jsonBool(source, 'canViewCosts'),
      canCommentPublicly: _jsonBool(source, 'canCommentPublicly'),
      canCommentPrivately: _jsonBool(source, 'canCommentPrivately'),
      canUploadPhoto: _jsonBool(source, 'canUploadPhoto'),
      canDeletePhoto: _jsonBool(source, 'canDeletePhoto'),
      canCancel: _jsonBool(source, 'canCancel'),
      canEditRequestFields: _jsonBool(source, 'canEditRequestFields'),
      canEditManagementFields: _jsonBool(source, 'canEditManagementFields'),
      canAssignTechnician: _jsonBool(source, 'canAssignTechnician'),
      canDispatchVendor: _jsonBool(source, 'canDispatchVendor'),
      allowedStatusTransitions: rawTransitions is List
          ? rawTransitions.map((value) => value.toString()).toList()
          : const [],
    );
  }
}

class WorkOrderActivityItem {
  const WorkOrderActivityItem({
    required this.id,
    required this.kind,
    this.fromStatus,
    this.toStatus,
    this.note,
    this.actorLabel,
    this.visibility,
    required this.createdAtUtc,
  });

  final int id;
  final String kind;
  final String? fromStatus;
  final String? toStatus;
  final String? note;
  final String? actorLabel;
  final String? visibility;
  final DateTime createdAtUtc;

  factory WorkOrderActivityItem.fromJson(Map<String, dynamic> json) {
    return WorkOrderActivityItem(
      id: (json['id'] as num?)?.toInt() ?? 0,
      kind: json['kind'] as String? ?? 'Activity',
      fromStatus: json['fromStatus'] as String?,
      toStatus: json['toStatus'] as String?,
      note: json['note'] as String?,
      actorLabel: json['actorLabel'] as String?,
      visibility: json['visibility'] as String?,
      createdAtUtc:
          DateTime.tryParse(json['createdAtUtc'] as String? ?? '')?.toLocal() ??
          DateTime(0),
    );
  }
}

class RoleAwareWorkOrderDetail extends WorkOrderDetail {
  const RoleAwareWorkOrderDetail({
    required super.workOrder,
    required super.timeline,
    required this.detailRole,
    required this.capabilities,
    required this.residentNames,
    this.privateManagementNotes,
    required this.activity,
    required this.hasActiveDispatch,
    this.activeDispatchId,
    this.activeDispatchVendorId,
    this.activeDispatchVendorName,
  });

  final String detailRole;
  final WorkOrderDetailCapabilities capabilities;
  final List<String> residentNames;
  final String? privateManagementNotes;
  final List<WorkOrderActivityItem> activity;
  final bool hasActiveDispatch;
  final int? activeDispatchId;
  final int? activeDispatchVendorId;
  final String? activeDispatchVendorName;

  factory RoleAwareWorkOrderDetail.fromJson(Map<String, dynamic> json) {
    final base = WorkOrderDetail.fromJson(json);
    final residents = json['residentNames'];
    final activity = json['activity'];
    return RoleAwareWorkOrderDetail(
      workOrder: base.workOrder,
      timeline: base.timeline,
      detailRole: json['detailRole'] as String? ?? '',
      capabilities: WorkOrderDetailCapabilities.fromJson(json),
      residentNames: residents is List
          ? residents.map((value) => value.toString()).toList()
          : const [],
      privateManagementNotes: json['privateManagementNotes'] as String?,
      activity: activity is List
          ? activity
                .whereType<Map<String, dynamic>>()
                .map(WorkOrderActivityItem.fromJson)
                .toList()
          : const [],
      hasActiveDispatch: _jsonBool(json, 'hasActiveDispatch'),
      activeDispatchId: (json['activeDispatchId'] as num?)?.toInt(),
      activeDispatchVendorId: (json['activeDispatchVendorId'] as num?)?.toInt(),
      activeDispatchVendorName: json['activeDispatchVendorName'] as String?,
    );
  }

  factory RoleAwareWorkOrderDetail.fromBase(WorkOrderDetail detail) {
    if (detail is RoleAwareWorkOrderDetail) return detail;
    return RoleAwareWorkOrderDetail(
      workOrder: detail.workOrder,
      timeline: detail.timeline,
      detailRole: '',
      capabilities: WorkOrderDetailCapabilities.none,
      residentNames: const [],
      activity: const [],
      hasActiveDispatch: false,
    );
  }
}

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

  Future<WorkOrderListPage> listWorkOrdersPage([
    WorkOrderListQuery query = const WorkOrderListQuery(),
  ]) async {
    final params = <String, dynamic>{
      'skip': query.skip,
      'take': query.take,
      'sort': query.sort,
      'search': query.search,
      'propertyId': query.propertyId,
      'unitId': query.unitId,
      'vendorId': query.vendorId,
      if (query.openOnly) 'openOnly': true,
      'requestedFrom': query.requestedFrom,
      'requestedTo': query.requestedTo,
      'scheduledFrom': query.scheduledFrom,
      'scheduledTo': query.scheduledTo,
      'completedFrom': query.completedFrom,
      'completedTo': query.completedTo,
    }..removeWhere((_, value) => value == null || value == '');

    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/work-orders/page',
        queryParameters: params,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return WorkOrderListPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<WorkOrder>> listWorkOrders({
    int? propertyId,
    int? unitId,
    int? vendorId,
    bool openOnly = false,
    int skip = 0,
    int take = 50,
    String? search,
    String sort = '-updatedAt',
    String? requestedFrom,
    String? requestedTo,
    String? scheduledFrom,
    String? scheduledTo,
    String? completedFrom,
    String? completedTo,
  }) async {
    final page = await listWorkOrdersPage(
      WorkOrderListQuery(
        propertyId: propertyId,
        unitId: unitId,
        vendorId: vendorId,
        openOnly: openOnly,
        skip: skip,
        take: take,
        search: search,
        sort: sort,
        requestedFrom: requestedFrom,
        requestedTo: requestedTo,
        scheduledFrom: scheduledFrom,
        scheduledTo: scheduledTo,
        completedFrom: completedFrom,
        completedTo: completedTo,
      ),
    );
    return page.items;
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
      return RoleAwareWorkOrderDetail.fromJson(data);
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
      final response = await IdempotentMutation.run(
        'work-order:create:${jsonEncode(data)}',
        (key) => _dio.post<Map<String, dynamic>>(
          '/work-orders',
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
    required String clientOperationId,
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
        'clientOperationId': clientOperationId,
      });
      await _dio.post<Map<String, dynamic>>('/documents', data: formData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Update body: any subset of work order fields (partial PATCH).
  Future<WorkOrder> updateWorkOrder(int id, Map<String, dynamic> data) async {
    try {
      final response = await IdempotentMutation.run(
        'work-order:update:$id:${jsonEncode(data)}',
        (key) => _dio.patch<Map<String, dynamic>>(
          '/work-orders/$id',
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
      return WorkOrder.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> commentWorkOrder(
    int id, {
    required String body,
    bool isPrivate = false,
  }) async {
    final payload = {'body': body, 'isPrivate': isPrivate};
    try {
      await IdempotentMutation.run(
        'work-order:comment:$id:${jsonEncode(payload)}',
        (key) => _dio.post<Map<String, dynamic>>(
          '/work-orders/$id/comments',
          data: payload,
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> updateTechnicianAssignment(
    int id,
    Map<String, dynamic> data,
  ) async {
    try {
      await IdempotentMutation.run(
        'technician:assignment-update:$id:${jsonEncode(data)}',
        (key) => _dio.patch<Map<String, dynamic>>(
          '/technician/assignments/$id',
          data: data,
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<Map<String, dynamic>>> listResponsibilities(
    int workOrderId,
  ) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/work-orders/$workOrderId/responsibilities',
      );
      return (response.data ?? const [])
          .whereType<Map<String, dynamic>>()
          .toList(growable: false);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<Map<String, dynamic>>> listResponsibilityCandidates(
    int workOrderId,
  ) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/work-orders/$workOrderId/responsibilities/candidates',
      );
      return (response.data ?? const [])
          .whereType<Map<String, dynamic>>()
          .toList(growable: false);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> assignResponsibility(
    int workOrderId,
    Map<String, dynamic> data,
  ) async {
    try {
      await IdempotentMutation.run(
        'work-order:responsibility:assign:$workOrderId:${jsonEncode(data)}',
        (key) => _dio.put<Map<String, dynamic>>(
          '/work-orders/$workOrderId/responsibilities/current',
          data: data,
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> closeResponsibility(
    int workOrderId,
    String responsibilityId,
    Map<String, dynamic> data,
  ) async {
    try {
      await IdempotentMutation.run(
        'work-order:responsibility:close:$workOrderId:$responsibilityId:${jsonEncode(data)}',
        (key) => _dio.post<Map<String, dynamic>>(
          '/work-orders/$workOrderId/responsibilities/$responsibilityId/close',
          data: data,
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
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
      final body = {
        'status': status,
        if (trimmed != null && trimmed.isNotEmpty) 'statusNote': trimmed,
      };
      final response = await IdempotentMutation.run(
        'work-order:status:$id:${jsonEncode(body)}',
        (key) => _dio.patch<Map<String, dynamic>>(
          '/work-orders/$id',
          data: body,
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

final workOrdersPageProvider = FutureProvider.autoDispose
    .family<WorkOrderListPage, WorkOrderListQuery>((ref, query) {
      return ref.watch(workOrdersRepositoryProvider).listWorkOrdersPage(query);
    });

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
      final auth = ref.read(authControllerProvider);
      if (auth is AuthStateAuthenticated &&
          auth.capabilities.contains('maintenance.assigned-work.update') &&
          !auth.capabilities.contains('work.manage')) {
        await _repo.updateTechnicianAssignment(_id, {
          'status': status,
          'expectedUpdatedAtUtc': state.value?.workOrder.updatedAt
              .toUtc()
              .toIso8601String(),
          'technicianNote': ?note,
        });
      } else {
        await _repo.updateStatus(_id, status, note: note);
      }
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
