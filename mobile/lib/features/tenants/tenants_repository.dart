import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/models/models.dart';

/// Repository for tenants.
///
/// Endpoints used:
///   GET    /tenants            — list all tenants (JWT-scoped, portfolio from claim)
///   GET    /tenants/{id}       — single tenant
///   POST   /tenants            — create tenant
///   PATCH  /tenants/{id}       — update tenant (partial)
///   DELETE /tenants/{id}       — delete tenant
class TenantsRepository {
  TenantsRepository(this._dio);

  final Dio _dio;

  /// List body: returns Tenant[].
  Future<List<Tenant>> listTenants() async {
    try {
      final response = await _dio.get<List<dynamic>>('/tenants');
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(Tenant.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<Tenant> getTenant(int id) async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/tenants/$id');
      return Tenant.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Create body: { firstName, lastName, email?, phone?, emergencyContact? }
  Future<Tenant> createTenant(Map<String, dynamic> data) async {
    try {
      final response =
          await _dio.post<Map<String, dynamic>>('/tenants', data: data);
      return Tenant.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Update body: same optional fields as create (partial PATCH)
  Future<Tenant> updateTenant(int id, Map<String, dynamic> data) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/tenants/$id',
        data: data,
      );
      return Tenant.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> deleteTenant(int id) async {
    try {
      await _dio.delete<dynamic>('/tenants/$id');
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final tenantsRepositoryProvider = Provider<TenantsRepository>((ref) {
  return TenantsRepository(ref.watch(dioProvider));
});

// ── Tenants list ──────────────────────────────────────────────────────────────

class TenantsNotifier extends Notifier<AsyncValue<List<Tenant>>> {
  @override
  AsyncValue<List<Tenant>> build() => const AsyncValue.loading();

  TenantsRepository get _repo => ref.read(tenantsRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final list = await _repo.listTenants();
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final tenantsProvider =
    NotifierProvider<TenantsNotifier, AsyncValue<List<Tenant>>>(
  TenantsNotifier.new,
);

// ── Single tenant ─────────────────────────────────────────────────────────────

class TenantDetailNotifier extends Notifier<AsyncValue<Tenant>> {
  TenantDetailNotifier(this._tenantId);

  final int _tenantId;

  @override
  AsyncValue<Tenant> build() {
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  TenantsRepository get _repo => ref.read(tenantsRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final tenant = await _repo.getTenant(_tenantId);
      state = AsyncValue.data(tenant);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final tenantDetailProvider =
    NotifierProvider.family<TenantDetailNotifier, AsyncValue<Tenant>, int>(
  TenantDetailNotifier.new,
);
