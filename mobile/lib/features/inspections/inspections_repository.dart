import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:uuid/uuid.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';
import '../../core/models/models.dart';
import 'inspections_models.dart';

class InspectionListQuery {
  const InspectionListQuery({
    this.skip = 0,
    this.take = 20,
    this.propertyId,
    this.search,
    this.sort = '-scheduledFor',
  });

  final int skip;
  final int take;
  final int? propertyId;
  final String? search;
  final String sort;

  @override
  bool operator ==(Object other) {
    return other is InspectionListQuery &&
        other.skip == skip &&
        other.take == take &&
        other.propertyId == propertyId &&
        other.search == search &&
        other.sort == sort;
  }

  @override
  int get hashCode => Object.hash(skip, take, propertyId, search, sort);
}

class InspectionListPage {
  const InspectionListPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<Inspection> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory InspectionListPage.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final items = rawItems is List
        ? rawItems
              .whereType<Map<String, dynamic>>()
              .map(Inspection.fromJson)
              .toList()
        : <Inspection>[];

    return InspectionListPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

/// Repository for the inspections smart-checklist workflow.
///
/// Endpoints (all JWT portfolio-scoped):
///   GET    /inspections/page                     — paged list
///   GET    /inspections/templates                — available checklist templates
///   GET    /inspections/{id}                     — detail (with items)
///   POST   /inspections                          — create (optional templateId)
///   PATCH  /inspections/{id}/items/{itemId}      — set item result/note
///   POST   /documents (entityType=Inspection)    — upload a photo, returns its id
///   POST   /inspections/{id}/items/{itemId}/photo — attach uploaded photo
///   POST   /inspections/{id}/complete            — complete + report + work orders
///   GET    /inspections/{id}/report              — PDF bytes
class InspectionsRepository {
  InspectionsRepository(this._dio);

  final Dio _dio;

  Future<List<Inspection>> list({int? propertyId}) async {
    final page = await listPage(InspectionListQuery(propertyId: propertyId));
    return page.items;
  }

  Future<InspectionListPage> listPage([
    InspectionListQuery query = const InspectionListQuery(),
  ]) async {
    final parameters = <String, dynamic>{
      'propertyId': query.propertyId,
      'skip': query.skip,
      'take': query.take,
      'search': query.search,
      'sort': query.sort,
    }..removeWhere((_, value) => value == null || value == '');

    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/inspections/page',
        queryParameters: parameters,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return InspectionListPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<InspectionTemplate>> listTemplates() async {
    try {
      final response = await _dio.get<List<dynamic>>('/inspections/templates');
      return (response.data ?? [])
          .whereType<Map<String, dynamic>>()
          .map(InspectionTemplate.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<InspectionDetail> get(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/inspections/$id');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return InspectionDetail.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Create body: { propertyId, unitId?, type, scheduledFor, templateId?,
  /// inspector? }. With a templateId the response items are Pending.
  Future<InspectionDetail> create(Map<String, dynamic> data) async {
    try {
      final response = await IdempotentMutation.run(
        'inspections:create:$data',
        (key) => _dio.post<Map<String, dynamic>>(
          '/inspections',
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
      return InspectionDetail.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// PATCH a single item's result and/or note.
  Future<InspectionItem> patchItem(
    int inspectionId,
    int itemId, {
    String? result,
    String? note,
  }) async {
    try {
      final payload = {'result': ?result, 'note': ?note};
      final response = await IdempotentMutation.run(
        'inspections:$inspectionId:items:$itemId:update:$payload',
        (key) => _dio.patch<Map<String, dynamic>>(
          '/inspections/$inspectionId/items/$itemId',
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
      return InspectionItem.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Uploads a photo to the Documents hub (entityType=Inspection) and returns
  /// the new StoredFile id to attach to an item.
  Future<int> uploadInspectionPhoto({
    required int inspectionId,
    required Uint8List bytes,
    required String fileName,
    required String contentType,
    String? clientOperationId,
  }) async {
    try {
      final formData = FormData.fromMap({
        'file': MultipartFile.fromBytes(
          bytes,
          filename: fileName,
          contentType: DioMediaType.parse(contentType),
        ),
        'entityType': 'Inspection',
        'entityId': inspectionId,
        'category': 'Inspection photo',
        'clientOperationId': clientOperationId ?? const Uuid().v4(),
      });
      final response = await _dio.post<Map<String, dynamic>>(
        '/documents',
        data: formData,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return (data['id'] as num).toInt();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Links an already-uploaded photo (StoredFile id) to a checklist item.
  Future<InspectionItem> attachPhoto(
    int inspectionId,
    int itemId,
    int storedFileId,
  ) async {
    try {
      final response = await IdempotentMutation.run(
        'inspections:$inspectionId:items:$itemId:photo:$storedFileId',
        (key) => _dio.post<Map<String, dynamic>>(
          '/inspections/$inspectionId/items/$itemId/photo',
          data: {'storedFileId': storedFileId},
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
      return InspectionItem.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Completes the inspection (409 if already done).
  Future<CompleteInspectionResult> complete(int inspectionId) async {
    try {
      final response = await IdempotentMutation.run(
        'inspections:$inspectionId:complete',
        (key) => _dio.post<Map<String, dynamic>>(
          '/inspections/$inspectionId/complete',
          data: {},
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
      return CompleteInspectionResult.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Fetches the generated PDF report bytes (authed via the shared interceptor).
  Future<Uint8List> reportBytes(int inspectionId) async {
    try {
      final response = await _dio.get<List<int>>(
        '/inspections/$inspectionId/report',
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Raw bytes for a stored file (used to render authed photo thumbnails).
  Future<Uint8List> documentBytes(int storedFileId) async {
    try {
      final response = await _dio.get<List<int>>(
        '/documents/$storedFileId/file',
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<Property>> listProperties() async {
    try {
      final response = await _dio.get<List<dynamic>>('/properties');
      return (response.data ?? [])
          .whereType<Map<String, dynamic>>()
          .map(Property.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<Unit>> listUnits(int propertyId) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/units',
        queryParameters: {'propertyId': propertyId},
      );
      return (response.data ?? [])
          .whereType<Map<String, dynamic>>()
          .map(Unit.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final inspectionsRepositoryProvider = Provider<InspectionsRepository>((ref) {
  return InspectionsRepository(ref.watch(dioProvider));
});

/// The inspections list (auto-disposes so it re-fetches on screen open).
final inspectionsProvider = FutureProvider.autoDispose<List<Inspection>>((ref) {
  return ref.watch(inspectionsRepositoryProvider).list();
});

final inspectionsPageProvider = FutureProvider.autoDispose
    .family<InspectionListPage, InspectionListQuery>((ref, query) {
      return ref.watch(inspectionsRepositoryProvider).listPage(query);
    });

/// Available checklist templates for the New-inspection flow.
final inspectionTemplatesProvider =
    FutureProvider.autoDispose<List<InspectionTemplate>>((ref) {
      return ref.watch(inspectionsRepositoryProvider).listTemplates();
    });

/// Properties for the New-inspection picker.
final inspectionPropertiesProvider = FutureProvider.autoDispose<List<Property>>(
  (ref) {
    return ref.watch(inspectionsRepositoryProvider).listProperties();
  },
);

/// Units for a property in the New-inspection picker.
final inspectionUnitsProvider = FutureProvider.autoDispose
    .family<List<Unit>, int>((ref, propertyId) {
      return ref.watch(inspectionsRepositoryProvider).listUnits(propertyId);
    });

/// Raw bytes for an inspection-item photo, by StoredFile id.
final inspectionPhotoBytesProvider = FutureProvider.autoDispose
    .family<Uint8List, int>((ref, storedFileId) {
      return ref
          .watch(inspectionsRepositoryProvider)
          .documentBytes(storedFileId);
    });

/// Inspection detail (with items) as a notifier so the run screen can patch
/// items optimistically and refresh after photos / completion.
class InspectionDetailNotifier extends Notifier<AsyncValue<InspectionDetail>> {
  InspectionDetailNotifier(this._id);

  final int _id;

  @override
  AsyncValue<InspectionDetail> build() {
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  InspectionsRepository get _repo => ref.read(inspectionsRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final detail = await _repo.get(_id);
      state = AsyncValue.data(detail);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();

  /// Replaces a single item in-place after a successful PATCH/photo attach.
  void replaceItem(InspectionItem updated) {
    state.whenData((detail) {
      final items = [
        for (final i in detail.items)
          if (i.id == updated.id) updated else i,
      ];
      state = AsyncValue.data(detail.copyWith(items: items));
    });
  }
}

final inspectionDetailProvider =
    NotifierProvider.family<
      InspectionDetailNotifier,
      AsyncValue<InspectionDetail>,
      int
    >(InspectionDetailNotifier.new);
