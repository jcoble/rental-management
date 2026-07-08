import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/models/models.dart';

class TenantListQuery {
  const TenantListQuery({
    this.skip = 0,
    this.take = 20,
    this.search,
    this.sort = 'name',
    this.availableForLease,
    this.propertyId,
    this.unitId,
  });

  final int skip;
  final int take;
  final String? search;
  final String sort;
  final bool? availableForLease;
  final int? propertyId;
  final int? unitId;

  @override
  bool operator ==(Object other) {
    return other is TenantListQuery &&
        other.skip == skip &&
        other.take == take &&
        other.search == search &&
        other.sort == sort &&
        other.availableForLease == availableForLease &&
        other.propertyId == propertyId &&
        other.unitId == unitId;
  }

  @override
  int get hashCode => Object.hash(
    skip,
    take,
    search,
    sort,
    availableForLease,
    propertyId,
    unitId,
  );
}

class TenantPage {
  const TenantPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<Tenant> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory TenantPage.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final items = rawItems is List
        ? rawItems
              .whereType<Map<String, dynamic>>()
              .map(Tenant.fromJson)
              .toList()
        : <Tenant>[];

    return TenantPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

/// Result of `POST /tenants/{id}/portal-invite`.
class PortalInviteResult {
  const PortalInviteResult({this.email, required this.alreadyExisted});

  /// The email the invite was sent to (the tenant's sign-in email).
  final String? email;

  /// True when the tenant already had a portal login (this was a resend).
  final bool alreadyExisted;
}

/// Repository for tenants.
///
/// Endpoints used:
///   GET    /tenants                    — list all tenants (JWT-scoped, portfolio from claim)
///   GET    /tenants/page               — paged/filterable tenant list
///   GET    /tenants/{id}               — single tenant
///   POST   /tenants                    — create tenant
///   PATCH  /tenants/{id}               — update tenant (partial)
///   DELETE /tenants/{id}               — delete tenant
///   POST   /tenants/{id}/portal-access — turn the resident-portal login on/off
///   POST   /tenants/{id}/portal-invite — email the tenant their portal invite
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

  Future<TenantPage> listPage([
    TenantListQuery query = const TenantListQuery(),
  ]) async {
    final parameters = <String, dynamic>{
      'skip': query.skip,
      'take': query.take,
      'search': query.search,
      'sort': query.sort,
      'availableForLease': query.availableForLease,
      'propertyId': query.propertyId,
      'unitId': query.unitId,
    }..removeWhere((_, value) => value == null || value == '');

    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/tenants/page',
        queryParameters: parameters,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return TenantPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<Tenant>> listAvailableForLeaseTenants({int take = 200}) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/tenants/page',
        queryParameters: {
          'take': take,
          'sort': 'name',
          'availableForLease': true,
        },
      );
      final items = response.data?['items'];
      final data = items is List ? items : const [];
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
      final response = await _dio.get<Map<String, dynamic>>('/tenants/$id');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Tenant.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Create body: { firstName, lastName, email?, phone?, emergencyContact? }
  Future<Tenant> createTenant(Map<String, dynamic> data) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/tenants',
        data: data,
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Tenant.fromJson(responseData);
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
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Tenant.fromJson(responseData);
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

  /// Turns the tenant's resident-portal login on or off (the staff toggle).
  /// Enabling provisions a login if needed and clears any lock; disabling locks
  /// sign-in. Returns the resulting state: 'none' | 'active' | 'disabled'.
  Future<String> setPortalAccess(int id, {required bool enabled}) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/tenants/$id/portal-access',
        data: {'enabled': enabled},
      );
      return response.data?['portalAccess'] as String? ?? 'none';
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Emails the tenant their resident-portal sign-in details, provisioning the
  /// login first if needed. This is the only place an invite email goes out
  /// (tenant creation provisions silently). Requires the tenant to have an email
  /// (the API 400s otherwise).
  Future<PortalInviteResult> sendPortalInvite(int id) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/tenants/$id/portal-invite',
      );
      final data = response.data ?? const <String, dynamic>{};
      return PortalInviteResult(
        email: data['email'] as String?,
        alreadyExisted: data['alreadyExisted'] as bool? ?? false,
      );
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

final availableForLeaseTenantsProvider =
    FutureProvider.autoDispose<List<Tenant>>((ref) {
      return ref
          .watch(tenantsRepositoryProvider)
          .listAvailableForLeaseTenants();
    });

final tenantsPageProvider = FutureProvider.autoDispose
    .family<TenantPage, TenantListQuery>((ref, query) {
      return ref.watch(tenantsRepositoryProvider).listPage(query);
    });

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
