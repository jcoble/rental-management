import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/models/models.dart';

/// Repository for leases.
///
/// Endpoints used:
///   GET    /leases                              — list (JWT-scoped; optional tenantId / propertyId query params)
///   GET    /leases/{id}                         — single lease
///   POST   /leases                              — create
///   PATCH  /leases/{id}                         — update (partial)
///   DELETE /leases/{id}                         — delete
///
/// Create / update body shape:
///   {
///     unitId, tenantId, startDate, endDate, monthlyRent, securityDeposit,
///     lateFeeAmount, rentDueDay, status?,
///     (on create: propertyId is inferred server-side from unitId,
///      but we still pass it for completeness)
///   }
class LeasesRepository {
  LeasesRepository(this._dio);

  final Dio _dio;

  Future<List<Lease>> listLeases({int? tenantId, int? propertyId}) async {
    try {
      final params = <String, dynamic>{
        'tenantId': tenantId,
        'propertyId': propertyId,
      }..removeWhere((_, v) => v == null);
      final response = await _dio.get<List<dynamic>>(
        '/leases',
        queryParameters: params.isNotEmpty ? params : null,
      );
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(Lease.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<Lease> getLease(int id) async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/leases/$id');
      return Lease.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Create body:
  /// { unitId, tenantId, startDate (yyyy-MM-dd), endDate (yyyy-MM-dd),
  ///   monthlyRent, securityDeposit, lateFeeAmount, rentDueDay, status? }
  Future<Lease> createLease(Map<String, dynamic> data) async {
    try {
      final response =
          await _dio.post<Map<String, dynamic>>('/leases', data: data);
      return Lease.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Update body: same optional fields as create (partial PATCH)
  Future<Lease> updateLease(int id, Map<String, dynamic> data) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/leases/$id',
        data: data,
      );
      return Lease.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Convenience wrapper: update only the status field.
  Future<Lease> updateStatus(int id, String status) async {
    return updateLease(id, {'status': status});
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final leasesRepositoryProvider = Provider<LeasesRepository>((ref) {
  return LeasesRepository(ref.watch(dioProvider));
});

// ── All leases ────────────────────────────────────────────────────────────────

class LeasesNotifier extends Notifier<AsyncValue<List<Lease>>> {
  @override
  AsyncValue<List<Lease>> build() => const AsyncValue.loading();

  LeasesRepository get _repo => ref.read(leasesRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final list = await _repo.listLeases();
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final leasesProvider =
    NotifierProvider<LeasesNotifier, AsyncValue<List<Lease>>>(
  LeasesNotifier.new,
);

// ── Leases for a specific tenant ──────────────────────────────────────────────

class TenantLeasesNotifier extends Notifier<AsyncValue<List<Lease>>> {
  TenantLeasesNotifier(this._tenantId);

  final int _tenantId;

  @override
  AsyncValue<List<Lease>> build() {
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  LeasesRepository get _repo => ref.read(leasesRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final filtered = await _repo.listLeases(tenantId: _tenantId);
      state = AsyncValue.data(filtered);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final tenantLeasesProvider =
    NotifierProvider.family<TenantLeasesNotifier, AsyncValue<List<Lease>>, int>(
  TenantLeasesNotifier.new,
);

// ── Single lease detail ───────────────────────────────────────────────────────

class LeaseDetailNotifier extends Notifier<AsyncValue<Lease>> {
  LeaseDetailNotifier(this._leaseId);

  final int _leaseId;

  @override
  AsyncValue<Lease> build() {
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  LeasesRepository get _repo => ref.read(leasesRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final lease = await _repo.getLease(_leaseId);
      state = AsyncValue.data(lease);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final leaseDetailProvider =
    NotifierProvider.family<LeaseDetailNotifier, AsyncValue<Lease>, int>(
  LeaseDetailNotifier.new,
);
