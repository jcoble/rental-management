import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'activity_models.dart';

export 'activity_models.dart';

class ActivityRepository {
  ActivityRepository(this._dio);

  final Dio _dio;

  Future<List<ActivityEntry>> list({
    int skip = 0,
    int take = 20,
    String sort = '-timestamp',
    String? search,
    String? operation,
    String? entityType,
    int? entityId,
  }) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/audit',
        queryParameters: {
          'skip': skip,
          'take': take,
          if (sort.isNotEmpty) 'sort': sort,
          if (search != null && search.trim().isNotEmpty)
            'search': search.trim(),
          if (operation != null && operation.isNotEmpty) 'operation': operation,
          if (entityType != null && entityType.isNotEmpty)
            'entityType': entityType,
          'entityId': ?entityId,
        },
      );
      final data = response.data ?? const [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(ActivityEntry.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final activityRepositoryProvider = Provider<ActivityRepository>((ref) {
  return ActivityRepository(ref.watch(dioProvider));
});

class ActivityHistoryNotifier extends Notifier<ActivityHistoryState> {
  static const _pageSize = 20;
  int _requestGeneration = 0;

  @override
  ActivityHistoryState build() => const ActivityHistoryState();

  ActivityRepository get _repo => ref.read(activityRepositoryProvider);

  Future<void> refresh() => _loadFirstPage();

  Future<void> _loadFirstPage() async {
    final generation = ++_requestGeneration;
    final filter = state.filter;
    state = state.copyWith(
      loading: true,
      loadingMore: false,
      clearError: true,
      hasMore: true,
    );
    try {
      final page = await _repo.list(
        skip: 0,
        take: _pageSize,
        sort: '-timestamp',
        search: filter.search,
        operation: filter.operation,
        entityType: filter.entityType,
        entityId: filter.entityId,
      );
      if (generation != _requestGeneration) return;
      state = state.copyWith(
        items: page,
        loading: false,
        loadingMore: false,
        hasMore: page.length == _pageSize,
      );
    } on ApiException catch (e) {
      if (generation != _requestGeneration) return;
      state = state.copyWith(loading: false, error: e.message);
    }
  }

  Future<void> loadMore() async {
    if (state.loading || state.loadingMore || !state.hasMore) return;
    final generation = _requestGeneration;
    final filter = state.filter;
    final skip = state.items.length;
    state = state.copyWith(loadingMore: true, clearError: true);
    try {
      final page = await _repo.list(
        skip: skip,
        take: _pageSize,
        sort: '-timestamp',
        search: filter.search,
        operation: filter.operation,
        entityType: filter.entityType,
        entityId: filter.entityId,
      );
      if (generation != _requestGeneration) return;
      state = state.copyWith(
        items: [...state.items, ...page],
        loadingMore: false,
        hasMore: page.length == _pageSize,
      );
    } on ApiException catch (e) {
      if (generation != _requestGeneration) return;
      state = state.copyWith(loadingMore: false, error: e.message);
    }
  }

  Future<void> setSearch(String value) async {
    final trimmed = value.trim();
    state = state.copyWith(
      filter: state.filter.copyWith(
        search: trimmed.isEmpty ? null : trimmed,
        clearSearch: trimmed.isEmpty,
      ),
    );
    await refresh();
  }

  Future<void> setOperation(String? operation) async {
    state = state.copyWith(
      filter: state.filter.copyWith(
        operation: operation,
        clearOperation: operation == null,
      ),
    );
    await refresh();
  }
}

final activityHistoryProvider =
    NotifierProvider<ActivityHistoryNotifier, ActivityHistoryState>(
      ActivityHistoryNotifier.new,
    );
