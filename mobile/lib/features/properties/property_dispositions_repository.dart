import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';

class PropertyDisposition {
  const PropertyDisposition({
    required this.id,
    required this.portfolioId,
    required this.propertyId,
    this.propertyName,
    required this.closedOnDate,
    required this.salePrice,
    required this.sellingCosts,
    required this.netSaleProceeds,
    required this.purchasePrice,
    required this.landValue,
    required this.buildingBasis,
    required this.accumulatedDepreciationBeforeSale,
    required this.saleYearDepreciation,
    required this.totalDepreciation,
    required this.adjustedBasis,
    required this.gainLoss,
    required this.unrecapturedSection1250Gain,
    this.buyerName,
    this.memo,
    required this.createdAt,
    required this.updatedAt,
    required this.testId,
  });

  final int id;
  final int portfolioId;
  final int propertyId;
  final String? propertyName;
  final DateTime closedOnDate;
  final double salePrice;
  final double sellingCosts;
  final double netSaleProceeds;
  final double purchasePrice;
  final double landValue;
  final double buildingBasis;
  final double accumulatedDepreciationBeforeSale;
  final double saleYearDepreciation;
  final double totalDepreciation;
  final double adjustedBasis;
  final double gainLoss;
  final double unrecapturedSection1250Gain;
  final String? buyerName;
  final String? memo;
  final DateTime createdAt;
  final DateTime updatedAt;
  final String testId;

  factory PropertyDisposition.fromJson(Map<String, dynamic> json) {
    return PropertyDisposition(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num?)?.toInt() ?? 0,
      propertyId: (json['propertyId'] as num?)?.toInt() ?? 0,
      propertyName: json['propertyName'] as String?,
      closedOnDate:
          DateTime.tryParse(json['closedOnDate'] as String? ?? '') ??
          DateTime(0),
      salePrice: (json['salePrice'] as num?)?.toDouble() ?? 0,
      sellingCosts: (json['sellingCosts'] as num?)?.toDouble() ?? 0,
      netSaleProceeds: (json['netSaleProceeds'] as num?)?.toDouble() ?? 0,
      purchasePrice: (json['purchasePrice'] as num?)?.toDouble() ?? 0,
      landValue: (json['landValue'] as num?)?.toDouble() ?? 0,
      buildingBasis: (json['buildingBasis'] as num?)?.toDouble() ?? 0,
      accumulatedDepreciationBeforeSale:
          (json['accumulatedDepreciationBeforeSale'] as num?)?.toDouble() ?? 0,
      saleYearDepreciation:
          (json['saleYearDepreciation'] as num?)?.toDouble() ?? 0,
      totalDepreciation: (json['totalDepreciation'] as num?)?.toDouble() ?? 0,
      adjustedBasis: (json['adjustedBasis'] as num?)?.toDouble() ?? 0,
      gainLoss: (json['gainLoss'] as num?)?.toDouble() ?? 0,
      unrecapturedSection1250Gain:
          (json['unrecapturedSection1250Gain'] as num?)?.toDouble() ?? 0,
      buyerName: json['buyerName'] as String?,
      memo: json['memo'] as String?,
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt:
          DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
      testId: json['testId'] as String? ?? 'property-disposition-${json['id']}',
    );
  }
}

class PropertyDispositionListPage {
  const PropertyDispositionListPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<PropertyDisposition> items;
  final int totalCount;
  final int skip;
  final int take;

  factory PropertyDispositionListPage.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final items = rawItems is List
        ? rawItems
              .whereType<Map<String, dynamic>>()
              .map(PropertyDisposition.fromJson)
              .toList()
        : <PropertyDisposition>[];

    return PropertyDispositionListPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

class PropertyDispositionsRepository {
  PropertyDispositionsRepository(this._dio);

  final Dio _dio;

  Future<PropertyDispositionListPage> listDispositionsPage({
    required int propertyId,
    int skip = 0,
    int take = _propertyDispositionsPageSize,
    String sort = '-closedOnDate',
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/property-dispositions/page',
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
      return PropertyDispositionListPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<PropertyDisposition> createDisposition({
    required int propertyId,
    required Map<String, dynamic> data,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/property-dispositions',
        data: {...data, 'propertyId': propertyId},
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return PropertyDisposition.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<PropertyDisposition> updateDisposition(
    int id,
    Map<String, dynamic> data,
  ) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/property-dispositions/$id',
        data: data,
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return PropertyDisposition.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> deleteDisposition(int id) async {
    try {
      await _dio.delete<dynamic>('/property-dispositions/$id');
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

const _propertyDispositionsPageSize = 20;

class PropertyDispositionsPage {
  const PropertyDispositionsPage({
    required this.dispositions,
    required this.hasMore,
    this.isLoadingMore = false,
    this.loadMoreError,
  });

  final List<PropertyDisposition> dispositions;
  final bool hasMore;
  final bool isLoadingMore;
  final Object? loadMoreError;
}

final propertyDispositionsRepositoryProvider =
    Provider<PropertyDispositionsRepository>((ref) {
      return PropertyDispositionsRepository(ref.watch(dioProvider));
    });

class PropertyDispositionsNotifier
    extends Notifier<AsyncValue<PropertyDispositionsPage>> {
  PropertyDispositionsNotifier(this._propertyId);

  final int _propertyId;
  Future<void>? _initialLoad;

  PropertyDispositionsRepository get _repo =>
      ref.read(propertyDispositionsRepositoryProvider);

  @override
  AsyncValue<PropertyDispositionsPage> build() {
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
      final page = await _repo.listDispositionsPage(
        propertyId: _propertyId,
        skip: 0,
        take: _propertyDispositionsPageSize,
        sort: '-closedOnDate',
      );
      state = AsyncValue.data(
        PropertyDispositionsPage(
          dispositions: page.items,
          hasMore: page.items.length == _propertyDispositionsPageSize,
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
      PropertyDispositionsPage(
        dispositions: current.dispositions,
        hasMore: current.hasMore,
        isLoadingMore: true,
      ),
    );

    try {
      final next = await _repo.listDispositionsPage(
        propertyId: _propertyId,
        skip: current.dispositions.length,
        take: _propertyDispositionsPageSize,
        sort: '-closedOnDate',
      );
      state = AsyncValue.data(
        PropertyDispositionsPage(
          dispositions: [...current.dispositions, ...next.items],
          hasMore: next.items.length == _propertyDispositionsPageSize,
        ),
      );
    } catch (e) {
      state = AsyncValue.data(
        PropertyDispositionsPage(
          dispositions: current.dispositions,
          hasMore: current.hasMore,
          loadMoreError: e,
        ),
      );
    }
  }
}

final propertyDispositionsProvider =
    NotifierProvider.family<
      PropertyDispositionsNotifier,
      AsyncValue<PropertyDispositionsPage>,
      int
    >(PropertyDispositionsNotifier.new);
