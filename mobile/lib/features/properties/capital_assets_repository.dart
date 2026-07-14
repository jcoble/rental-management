import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';

enum DepreciationMethod {
  straightLine('StraightLine', 'Straight-line'),
  macrs('Macrs', 'MACRS');

  const DepreciationMethod(this.wire, this.label);

  final String wire;
  final String label;

  static DepreciationMethod fromWire(String? wire) {
    return DepreciationMethod.values.firstWhere(
      (method) => method.wire == wire,
      orElse: () => DepreciationMethod.straightLine,
    );
  }
}

enum DepreciationConvention {
  midMonth('MidMonth', 'Mid-month'),
  halfYear('HalfYear', 'Half-year'),
  midQuarter('MidQuarter', 'Mid-quarter');

  const DepreciationConvention(this.wire, this.label);

  final String wire;
  final String label;

  static DepreciationConvention fromWire(String? wire) {
    return DepreciationConvention.values.firstWhere(
      (convention) => convention.wire == wire,
      orElse: () => DepreciationConvention.midMonth,
    );
  }
}

class CapitalAsset {
  const CapitalAsset({
    required this.id,
    required this.portfolioId,
    required this.propertyId,
    this.propertyName,
    this.unitId,
    this.unitNumber,
    this.sourceExpenseId,
    this.sourceExpenseDescription,
    required this.description,
    required this.costBasis,
    required this.inServiceDate,
    required this.method,
    required this.recoveryYears,
    required this.convention,
    required this.accumulatedDepreciation,
    this.disposedOnDate,
    required this.depreciationYear,
    required this.annualDepreciation,
    required this.isFirstYearEstimate,
    required this.createdAt,
    required this.updatedAt,
    required this.testId,
  });

  final int id;
  final int portfolioId;
  final int propertyId;
  final String? propertyName;
  final int? unitId;
  final String? unitNumber;
  final int? sourceExpenseId;
  final String? sourceExpenseDescription;
  final String description;
  final double costBasis;
  final DateTime inServiceDate;
  final DepreciationMethod method;
  final double recoveryYears;
  final DepreciationConvention convention;
  final double accumulatedDepreciation;
  final DateTime? disposedOnDate;
  final int depreciationYear;
  final double annualDepreciation;
  final bool isFirstYearEstimate;
  final DateTime createdAt;
  final DateTime updatedAt;
  final String testId;

  factory CapitalAsset.fromJson(Map<String, dynamic> json) {
    return CapitalAsset(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num?)?.toInt() ?? 0,
      propertyId: (json['propertyId'] as num?)?.toInt() ?? 0,
      propertyName: json['propertyName'] as String?,
      unitId: (json['unitId'] as num?)?.toInt(),
      unitNumber: json['unitNumber'] as String?,
      sourceExpenseId: (json['sourceExpenseId'] as num?)?.toInt(),
      sourceExpenseDescription: json['sourceExpenseDescription'] as String?,
      description: json['description'] as String? ?? '',
      costBasis: (json['costBasis'] as num?)?.toDouble() ?? 0,
      inServiceDate:
          DateTime.tryParse(json['inServiceDate'] as String? ?? '') ??
          DateTime(0),
      method: DepreciationMethod.fromWire(json['method'] as String?),
      recoveryYears: (json['recoveryYears'] as num?)?.toDouble() ?? 0,
      convention: DepreciationConvention.fromWire(
        json['convention'] as String?,
      ),
      accumulatedDepreciation:
          (json['accumulatedDepreciation'] as num?)?.toDouble() ?? 0,
      disposedOnDate: DateTime.tryParse(
        json['disposedOnDate'] as String? ?? '',
      ),
      depreciationYear: (json['depreciationYear'] as num?)?.toInt() ?? 0,
      annualDepreciation: (json['annualDepreciation'] as num?)?.toDouble() ?? 0,
      isFirstYearEstimate: json['isFirstYearEstimate'] as bool? ?? false,
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt:
          DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
      testId: json['testId'] as String? ?? 'capital-asset-${json['id']}',
    );
  }
}

class CapitalAssetListPage {
  const CapitalAssetListPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<CapitalAsset> items;
  final int totalCount;
  final int skip;
  final int take;

  factory CapitalAssetListPage.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final items = rawItems is List
        ? rawItems
              .whereType<Map<String, dynamic>>()
              .map(CapitalAsset.fromJson)
              .toList()
        : <CapitalAsset>[];

    return CapitalAssetListPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

class CapitalAssetsRepository {
  CapitalAssetsRepository(this._dio);

  final Dio _dio;

  Future<CapitalAssetListPage> listAssetsPage({
    required int propertyId,
    int skip = 0,
    int take = _propertyCapitalAssetsPageSize,
    String sort = '-inServiceDate',
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/capital-assets/page',
        queryParameters: {
          'propertyId': propertyId,
          'skip': skip,
          'take': take,
          if (sort.isNotEmpty) 'sort': sort,
        },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return CapitalAssetListPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<CapitalAsset> createAsset({
    required int propertyId,
    required Map<String, dynamic> data,
  }) async {
    try {
      final payload = {...data, 'propertyId': propertyId};
      final response = await IdempotentMutation.run(
        'capital-assets:create:${jsonEncode(payload)}',
        (key) => _dio.post<Map<String, dynamic>>(
          '/capital-assets',
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
      return CapitalAsset.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<CapitalAsset> updateAsset(int id, Map<String, dynamic> data) async {
    try {
      final response = await IdempotentMutation.run(
        'capital-assets:update:$id:${jsonEncode(data)}',
        (key) => _dio.patch<Map<String, dynamic>>(
          '/capital-assets/$id',
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
      return CapitalAsset.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> deleteAsset(int id) async {
    try {
      await IdempotentMutation.run(
        'capital-assets:delete:$id',
        (key) => _dio.delete<dynamic>(
          '/capital-assets/$id',
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

const _propertyCapitalAssetsPageSize = 20;

class PropertyCapitalAssetsPage {
  const PropertyCapitalAssetsPage({
    required this.assets,
    required this.hasMore,
    this.isLoadingMore = false,
    this.loadMoreError,
  });

  final List<CapitalAsset> assets;
  final bool hasMore;
  final bool isLoadingMore;
  final Object? loadMoreError;
}

final capitalAssetsRepositoryProvider = Provider<CapitalAssetsRepository>((
  ref,
) {
  return CapitalAssetsRepository(ref.watch(dioProvider));
});

class PropertyCapitalAssetsNotifier
    extends Notifier<AsyncValue<PropertyCapitalAssetsPage>> {
  PropertyCapitalAssetsNotifier(this._propertyId);

  final int _propertyId;
  Future<void>? _initialLoad;

  CapitalAssetsRepository get _repo =>
      ref.read(capitalAssetsRepositoryProvider);

  @override
  AsyncValue<PropertyCapitalAssetsPage> build() {
    _initialLoad = Future<void>.microtask(_loadFirstPage);
    return const AsyncValue.loading();
  }

  Future<void> refresh() {
    final pending = _initialLoad;
    if (pending != null && state.isLoading) return pending;
    return _loadFirstPage();
  }

  Future<void> _loadFirstPage() async {
    state = const AsyncValue.loading();
    try {
      final page = await _repo.listAssetsPage(
        propertyId: _propertyId,
        skip: 0,
        take: _propertyCapitalAssetsPageSize,
        sort: '-inServiceDate',
      );
      state = AsyncValue.data(
        PropertyCapitalAssetsPage(
          assets: page.items,
          hasMore: page.items.length == _propertyCapitalAssetsPageSize,
        ),
      );
    } catch (e, st) {
      state = AsyncValue.error(e, st);
    } finally {
      _initialLoad = null;
    }
  }

  Future<void> loadMore() async {
    final current = state.value;
    if (current == null || !current.hasMore || current.isLoadingMore) return;

    state = AsyncValue.data(
      PropertyCapitalAssetsPage(
        assets: current.assets,
        hasMore: current.hasMore,
        isLoadingMore: true,
      ),
    );

    try {
      final next = await _repo.listAssetsPage(
        propertyId: _propertyId,
        skip: current.assets.length,
        take: _propertyCapitalAssetsPageSize,
        sort: '-inServiceDate',
      );
      state = AsyncValue.data(
        PropertyCapitalAssetsPage(
          assets: [...current.assets, ...next.items],
          hasMore: next.items.length == _propertyCapitalAssetsPageSize,
        ),
      );
    } catch (e) {
      state = AsyncValue.data(
        PropertyCapitalAssetsPage(
          assets: current.assets,
          hasMore: current.hasMore,
          loadMoreError: e,
        ),
      );
    }
  }
}

final propertyCapitalAssetsProvider =
    NotifierProvider.family<
      PropertyCapitalAssetsNotifier,
      AsyncValue<PropertyCapitalAssetsPage>,
      int
    >(PropertyCapitalAssetsNotifier.new);
