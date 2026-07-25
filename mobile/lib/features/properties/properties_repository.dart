import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';
import '../../core/models/models.dart';
import 'property_workspace_sections.dart';

class PropertyOwnerOption {
  const PropertyOwnerOption({required this.id, required this.name});

  final int id;
  final String name;

  factory PropertyOwnerOption.fromJson(Map<String, dynamic> json) {
    return PropertyOwnerOption(
      id: (json['id'] as num).toInt(),
      name: json['name'] as String? ?? '',
    );
  }
}

class PropertyListQuery {
  const PropertyListQuery({
    this.skip = 0,
    this.take = 20,
    this.search,
    this.sort = 'name',
    this.type,
    this.status,
  });

  final int skip;
  final int take;
  final String? search;
  final String sort;
  final String? type;
  final String? status;

  @override
  bool operator ==(Object other) {
    return other is PropertyListQuery &&
        other.skip == skip &&
        other.take == take &&
        other.search == search &&
        other.sort == sort &&
        other.type == type &&
        other.status == status;
  }

  @override
  int get hashCode => Object.hash(skip, take, search, sort, type, status);
}

class PropertyListPage {
  const PropertyListPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
    required this.workspaceEntries,
  });

  final List<Property> items;
  final int totalCount;
  final int skip;
  final int take;
  final Map<int, PropertyWorkspaceEntry> workspaceEntries;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory PropertyListPage.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final itemMaps = (rawItems is List ? rawItems : const [])
        .whereType<Map<String, dynamic>>()
        .toList(growable: false);
    final items = itemMaps.map(Property.fromJson).toList(growable: false);
    return PropertyListPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? 0,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? 20,
      workspaceEntries: {
        for (final item in itemMaps)
          if (item['workspaceEntry'] case final Map<dynamic, dynamic> entry)
            (item['id'] as num).toInt(): PropertyWorkspaceEntry.fromJson(
              Map<String, dynamic>.from(entry),
            ),
      },
    );
  }
}

class PropertyWorkspaceDetail {
  const PropertyWorkspaceDetail({
    required this.property,
    required this.workspaceEntry,
  });

  final Property property;
  final PropertyWorkspaceEntry workspaceEntry;

  factory PropertyWorkspaceDetail.fromJson(Map<String, dynamic> json) {
    final rawEntry = json['workspaceEntry'];
    return PropertyWorkspaceDetail(
      property: Property.fromJson(json),
      workspaceEntry: rawEntry is Map
          ? PropertyWorkspaceEntry.fromJson(Map<String, dynamic>.from(rawEntry))
          : PropertyWorkspaceEntry(
              destination: PropertyWorkspaceDestination.property,
              propertyId: (json['id'] as num).toInt(),
              areas: const [],
            ),
    );
  }
}

class PropertyWorkspaceUnit {
  const PropertyWorkspaceUnit({
    required this.id,
    required this.unitNumber,
    required this.status,
    required this.marketRent,
    required this.openWorkOrderCount,
  });

  final int id;
  final String unitNumber;
  final String status;
  final double marketRent;
  final int openWorkOrderCount;

  factory PropertyWorkspaceUnit.fromJson(Map<String, dynamic> json) {
    return PropertyWorkspaceUnit(
      id: (json['id'] as num).toInt(),
      unitNumber: json['unitNumber'] as String? ?? '',
      status: json['status'] as String? ?? '',
      marketRent: (json['marketRent'] as num?)?.toDouble() ?? 0,
      openWorkOrderCount: (json['openWorkOrderCount'] as num?)?.toInt() ?? 0,
    );
  }
}

class PropertyWorkspaceUnitPage {
  const PropertyWorkspaceUnitPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<PropertyWorkspaceUnit> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory PropertyWorkspaceUnitPage.fromJson(Map<String, dynamic> json) {
    final items = (json['items'] as List<dynamic>? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(PropertyWorkspaceUnit.fromJson)
        .toList(growable: false);
    return PropertyWorkspaceUnitPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? 20,
    );
  }
}

class PropertyWorkspacePageQuery {
  const PropertyWorkspacePageQuery({
    required this.propertyId,
    this.skip = 0,
    this.take = 20,
  });

  final int propertyId;
  final int skip;
  final int take;

  @override
  bool operator ==(Object other) =>
      other is PropertyWorkspacePageQuery &&
      other.propertyId == propertyId &&
      other.skip == skip &&
      other.take == take;

  @override
  int get hashCode => Object.hash(propertyId, skip, take);
}

class PropertyWorkspaceLeasePage {
  const PropertyWorkspaceLeasePage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<LeaseManagementSummary> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory PropertyWorkspaceLeasePage.fromJson(Map<String, dynamic> json) {
    final items = (json['items'] as List<dynamic>? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(LeaseManagementSummary.fromJson)
        .toList(growable: false);
    return PropertyWorkspaceLeasePage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? 20,
    );
  }
}

class PropertySetupResult {
  const PropertySetupResult({
    required this.property,
    required this.units,
    required this.updated,
  });

  final Property property;
  final List<Unit> units;
  final bool updated;

  factory PropertySetupResult.fromJson(Map<String, dynamic> json) {
    final rawUnits = json['units'];
    return PropertySetupResult(
      property: Property.fromJson(
        Map<String, dynamic>.from(json['property'] as Map),
      ),
      units: (rawUnits is List ? rawUnits : const [])
          .whereType<Map<String, dynamic>>()
          .map(Unit.fromJson)
          .toList(growable: false),
      updated: json['updated'] as bool? ?? false,
    );
  }
}

/// Repository for properties, units, and read-only rental relationships.
///
/// Endpoints used:
///   GET    /properties/page                  — searchable, filtered property page
///   GET    /properties                       — bounded property selector list
///   GET    /properties/{id}                  — single property
///   POST   /properties/setup                 — atomically create property + rentals
///   PATCH  /properties/{id}                  — update property
///   DELETE /properties/{id}                  — delete property
///   GET    /units?propertyId={id}            — list units for a property
///   POST   /units  (body includes propertyId) — add a unit
///   PATCH  /units/{id}                       — update a unit
///   GET    /lease-managements/page           — relationships by property
///   GET    /owner-entities?take=200          — owner selector options
class PropertiesRepository {
  PropertiesRepository(this._dio);

  final Dio _dio;

  // ── Properties ─────────────────────────────────────────────────────────────

  Future<PropertyListPage> listPropertiesPage([
    PropertyListQuery query = const PropertyListQuery(),
  ]) async {
    final parameters = <String, dynamic>{
      'skip': query.skip,
      'take': query.take,
      'search': query.search,
      'sort': query.sort,
      'type': query.type,
      'status': query.status,
    }..removeWhere((_, value) => value == null || value == '');

    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/properties/page',
        queryParameters: parameters,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return PropertyListPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<Property>> listProperties({
    bool availableForLease = false,
  }) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/properties',
        queryParameters: availableForLease ? {'availableForLease': true} : null,
      );
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(Property.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<Property> getProperty(int id) async {
    return (await getPropertyWorkspaceDetail(id)).property;
  }

  Future<PropertyWorkspaceDetail> getPropertyWorkspaceDetail(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/properties/$id');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return PropertyWorkspaceDetail.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<Document>> listPropertyDocuments(int propertyId) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/documents',
        queryParameters: {'entityType': 'Property', 'entityId': propertyId},
      );
      return (response.data ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(Document.fromJson)
          .toList(growable: false);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<Document> uploadPropertyDocument({
    required int propertyId,
    required Uint8List bytes,
    required String fileName,
    required String contentType,
    required String clientOperationId,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/documents',
        data: FormData.fromMap({
          'file': MultipartFile.fromBytes(
            bytes,
            filename: fileName,
            contentType: DioMediaType.parse(contentType),
          ),
          'entityType': 'Property',
          'entityId': propertyId,
          'category': 'Property document',
          'clientOperationId': clientOperationId,
        }),
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Document.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<Uint8List> downloadPropertyDocument(int documentId) async {
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

  Future<PropertyWorkspaceUnitPage> listWorkspaceUnitsPage(
    PropertyWorkspacePageQuery query,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/units/list-with-health/page',
        queryParameters: {
          'propertyId': query.propertyId,
          'skip': query.skip,
          'take': query.take,
          'sort': 'unitNumber',
        },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return PropertyWorkspaceUnitPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<PropertyWorkspaceLeasePage> listWorkspaceLeasesPage(
    PropertyWorkspacePageQuery query,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/page',
        queryParameters: {
          'propertyId': query.propertyId,
          'skip': query.skip,
          'take': query.take,
          'sort': '-updatedAtUtc',
        },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return PropertyWorkspaceLeasePage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Creates the physical property and its initial rental units in one server
  /// transaction. A SingleRental still owns one canonical Unit; the UI simply
  /// presents the pair as one rental.
  Future<PropertySetupResult> setupProperty({
    int? propertyId,
    required Map<String, dynamic> property,
    required List<Map<String, dynamic>> units,
  }) async {
    final payload = <String, dynamic>{
      if (propertyId != null) 'propertyId': propertyId,
      'property': property,
      'units': units,
    };

    try {
      final response = await IdempotentMutation.run(
        'properties:setup:$payload',
        (key) => _dio.post<Map<String, dynamic>>(
          '/properties/setup',
          data: payload,
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
      return PropertySetupResult.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Update body: same optional fields as create (partial PATCH)
  Future<Property> updateProperty(int id, Map<String, dynamic> data) async {
    try {
      final response = await IdempotentMutation.run(
        'properties:update:$id:$data',
        (key) => _dio.patch<Map<String, dynamic>>(
          '/properties/$id',
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
      return Property.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> deleteProperty(int id) async {
    try {
      await IdempotentMutation.run(
        'properties:delete:$id',
        (key) => _dio.delete<dynamic>(
          '/properties/$id',
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<PropertyOwnerOption>> listOwnerOptions() async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/owner-entities',
        queryParameters: {'take': 200},
      );
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(PropertyOwnerOption.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  // ── Units ──────────────────────────────────────────────────────────────────

  Future<List<Unit>> listUnits(
    int propertyId, {
    bool availableForLease = false,
  }) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/units',
        queryParameters: {
          'propertyId': propertyId,
          if (availableForLease) 'availableForLease': true,
        },
      );
      final data = response.data ?? [];
      return data.whereType<Map<String, dynamic>>().map(Unit.fromJson).toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Create body: { unitNumber, bedrooms, bathrooms, marketRent, status? }
  Future<Unit> createUnit(int propertyId, Map<String, dynamic> data) async {
    final payload = {...data, 'propertyId': propertyId};
    return IdempotentMutation.run('units:create:$payload', (
      operationKey,
    ) async {
      try {
        final response = await _dio.post<Map<String, dynamic>>(
          '/units',
          options: Options(headers: {'Idempotency-Key': operationKey}),
          data: payload,
        );
        final responseData = response.data;
        if (responseData == null) {
          throw const ApiException(
            statusCode: 0,
            message: 'Empty response from server.',
          );
        }
        return Unit.fromJson(responseData);
      } on DioException catch (e) {
        throw ApiException.fromDioException(e);
      }
    });
  }

  /// Update body: same optional fields as create (partial PATCH)
  Future<Unit> updateUnit(int id, Map<String, dynamic> data) async {
    return IdempotentMutation.run('units:update:$id:$data', (
      operationKey,
    ) async {
      try {
        final response = await _dio.patch<Map<String, dynamic>>(
          '/units/$id',
          options: Options(headers: {'Idempotency-Key': operationKey}),
          data: data,
        );
        final responseData = response.data;
        if (responseData == null) {
          throw const ApiException(
            statusCode: 0,
            message: 'Empty response from server.',
          );
        }
        return Unit.fromJson(responseData);
      } on DioException catch (e) {
        throw ApiException.fromDioException(e);
      }
    });
  }

  // ── Tenant relationships (read-only) ───────────────────────────────────────

  /// Returns the property's continuous tenant relationships. Filtering and
  /// paging stay on the canonical server query.
  Future<List<LeaseManagementSummary>> listLeaseManagementsForProperty(
    int propertyId,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/page',
        queryParameters: {'propertyId': propertyId, 'take': 200},
      );
      final data = response.data?['items'] as List<dynamic>? ?? const [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(LeaseManagementSummary.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final propertiesRepositoryProvider = Provider<PropertiesRepository>((ref) {
  return PropertiesRepository(ref.watch(dioProvider));
});

// ── Properties list ───────────────────────────────────────────────────────────

class PropertiesNotifier extends Notifier<AsyncValue<List<Property>>> {
  @override
  AsyncValue<List<Property>> build() => const AsyncValue.loading();

  PropertiesRepository get _repo => ref.read(propertiesRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final list = await _repo.listProperties();
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final propertiesProvider =
    NotifierProvider<PropertiesNotifier, AsyncValue<List<Property>>>(
      PropertiesNotifier.new,
    );

final propertiesPageProvider = FutureProvider.autoDispose
    .family<PropertyListPage, PropertyListQuery>((ref, query) {
      return ref.watch(propertiesRepositoryProvider).listPropertiesPage(query);
    });

final propertyWorkspaceUnitsPageProvider = FutureProvider.autoDispose
    .family<PropertyWorkspaceUnitPage, PropertyWorkspacePageQuery>((
      ref,
      query,
    ) {
      return ref
          .watch(propertiesRepositoryProvider)
          .listWorkspaceUnitsPage(query);
    });

final propertyWorkspaceLeasesPageProvider = FutureProvider.autoDispose
    .family<PropertyWorkspaceLeasePage, PropertyWorkspacePageQuery>((
      ref,
      query,
    ) {
      return ref
          .watch(propertiesRepositoryProvider)
          .listWorkspaceLeasesPage(query);
    });

final propertyDocumentsProvider = FutureProvider.autoDispose
    .family<List<Document>, int>((ref, propertyId) {
      return ref
          .watch(propertiesRepositoryProvider)
          .listPropertyDocuments(propertyId);
    });

final availableForLeasePropertiesProvider =
    FutureProvider.autoDispose<List<Property>>((ref) {
      return ref
          .watch(propertiesRepositoryProvider)
          .listProperties(availableForLease: true);
    });

// ── Units for a specific property ─────────────────────────────────────────────

class UnitsNotifier extends Notifier<AsyncValue<List<Unit>>> {
  UnitsNotifier(this._propertyId);

  final int _propertyId;

  @override
  AsyncValue<List<Unit>> build() {
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  PropertiesRepository get _repo => ref.read(propertiesRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final list = await _repo.listUnits(_propertyId);
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final unitsProvider =
    NotifierProvider.family<UnitsNotifier, AsyncValue<List<Unit>>, int>(
      UnitsNotifier.new,
    );

final availableForLeaseUnitsProvider = FutureProvider.autoDispose
    .family<List<Unit>, int>((ref, propertyId) {
      return ref
          .watch(propertiesRepositoryProvider)
          .listUnits(propertyId, availableForLease: true);
    });

// ── Tenant relationships for a specific property ─────────────────────────────

class PropertyLeaseManagementsNotifier
    extends Notifier<AsyncValue<List<LeaseManagementSummary>>> {
  PropertyLeaseManagementsNotifier(this._propertyId);

  final int _propertyId;

  @override
  AsyncValue<List<LeaseManagementSummary>> build() {
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  PropertiesRepository get _repo => ref.read(propertiesRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final list = await _repo.listLeaseManagementsForProperty(_propertyId);
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final propertyLeaseManagementsProvider =
    NotifierProvider.family<
      PropertyLeaseManagementsNotifier,
      AsyncValue<List<LeaseManagementSummary>>,
      int
    >(PropertyLeaseManagementsNotifier.new);

// ── Single property (by id) ───────────────────────────────────────────────────

/// Fetches one property by id. Backs by-id drill-through (e.g. a lease's
/// property link, which only carries `propertyId`). autoDispose so it refetches
/// when reopened.
final propertyDetailProvider = FutureProvider.autoDispose.family<Property, int>(
  (ref, id) {
    return ref.watch(propertiesRepositoryProvider).getProperty(id);
  },
);

final propertyWorkspaceDetailProvider = FutureProvider.autoDispose
    .family<PropertyWorkspaceDetail, int>((ref, id) {
      return ref
          .watch(propertiesRepositoryProvider)
          .getPropertyWorkspaceDetail(id);
    });
