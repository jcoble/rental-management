import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';
import '../../core/models/models.dart';

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

/// Repository for properties, units, and read-only rental relationships.
///
/// Endpoints used:
///   GET    /properties                       — list all properties (JWT-scoped)
///   GET    /properties/{id}                  — single property
///   POST   /properties                       — create property
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
    try {
      final response = await _dio.get<Map<String, dynamic>>('/properties/$id');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Property.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Create body: { name, type, addressLine1, city, state, postalCode, ownerId? }
  Future<Property> createProperty(Map<String, dynamic> data) async {
    try {
      final response = await IdempotentMutation.run(
        'properties:create:$data',
        (key) => _dio.post<Map<String, dynamic>>(
          '/properties',
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
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/units',
        data: {...data, 'propertyId': propertyId},
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
  }

  /// Update body: same optional fields as create (partial PATCH)
  Future<Unit> updateUnit(int id, Map<String, dynamic> data) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/units/$id',
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
