import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';
import 'notification_models.dart';

/// Notification inbox + unread-count API calls.
///
/// Endpoints (portfolio + user scoped, camelCase JSON):
///   GET  /notifications?unreadOnly=&skip=&take=  → List&lt;NotificationResponse&gt;
///   GET  /notifications/unread-count             → { count }
///   POST /notifications/{id}/read                → 204
///   POST /notifications/read-all                 → 204
class NotificationsRepository {
  NotificationsRepository(this._dio);

  final Dio _dio;

  Future<List<AppNotification>> list({
    bool unreadOnly = false,
    int skip = 0,
    int take = 20,
  }) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/notifications',
        queryParameters: {'unreadOnly': unreadOnly, 'skip': skip, 'take': take},
      );
      final data = response.data ?? const [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(AppNotification.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<int> unreadCount() async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/notifications/unread-count',
      );
      return (response.data?['count'] as num?)?.toInt() ?? 0;
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> markRead(int id) async {
    try {
      await IdempotentMutation.run(
        'notifications:read:$id',
        (operationKey) => _dio.post<void>(
          '/notifications/$id/read',
          options: Options(headers: {'Idempotency-Key': operationKey}),
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> markAllRead() async {
    try {
      await IdempotentMutation.run(
        'notifications:read-all',
        (operationKey) => _dio.post<void>(
          '/notifications/read-all',
          options: Options(headers: {'Idempotency-Key': operationKey}),
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ────────────────────────────────────────────────────────────────

final notificationsRepositoryProvider = Provider<NotificationsRepository>((
  ref,
) {
  return NotificationsRepository(ref.watch(dioProvider));
});

/// Unread badge count. Refreshed on app resume, SignalR push, and inbox close.
class UnreadCountNotifier extends Notifier<AsyncValue<int>> {
  @override
  AsyncValue<int> build() {
    // Kick off the first load lazily on creation.
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  NotificationsRepository get _repo =>
      ref.read(notificationsRepositoryProvider);

  Future<void> load() async {
    try {
      final count = await _repo.unreadCount();
      if (!ref.mounted) return;
      state = AsyncValue.data(count);
    } on ApiException catch (e) {
      if (!ref.mounted) return;
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final unreadCountProvider =
    NotifierProvider<UnreadCountNotifier, AsyncValue<int>>(
      UnreadCountNotifier.new,
    );

/// Paginated inbox list. Loads the first page on creation; [loadMore] appends.
class InboxNotifier extends Notifier<AsyncValue<List<AppNotification>>> {
  static const _pageSize = 20;

  bool _hasMore = true;
  bool _loadingMore = false;

  bool get hasMore => _hasMore;

  @override
  AsyncValue<List<AppNotification>> build() {
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  NotificationsRepository get _repo =>
      ref.read(notificationsRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    _hasMore = true;
    try {
      final page = await _repo.list(skip: 0, take: _pageSize);
      _hasMore = page.length == _pageSize;
      state = AsyncValue.data(page);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();

  Future<void> loadMore() async {
    if (_loadingMore || !_hasMore) return;
    final current = state.value;
    if (current == null) return;
    _loadingMore = true;
    try {
      final page = await _repo.list(skip: current.length, take: _pageSize);
      _hasMore = page.length == _pageSize;
      state = AsyncValue.data([...current, ...page]);
    } on ApiException {
      // Keep the existing page on a load-more failure; the user can retry.
    } finally {
      _loadingMore = false;
    }
  }

  /// Marks one notification read locally + on the server, then refreshes the
  /// unread badge. On a server failure the optimistic flip is rolled back so the
  /// list and the badge can't disagree (the badge refetch below would otherwise
  /// re-show the true unread count while the list still read all-read).
  Future<void> markRead(int id) async {
    final current = state.value;
    if (current != null) {
      state = AsyncValue.data([
        for (final n in current)
          if (n.id == id) n.copyWith(isRead: true) else n,
      ]);
    }
    try {
      await _repo.markRead(id);
    } on ApiException {
      // Roll back the optimistic flip so list and badge stay consistent.
      if (current != null) state = AsyncValue.data(current);
    }
    await ref.read(unreadCountProvider.notifier).refresh();
  }

  Future<void> markAllRead() async {
    final current = state.value;
    if (current != null) {
      state = AsyncValue.data([
        for (final n in current) n.copyWith(isRead: true),
      ]);
    }
    try {
      await _repo.markAllRead();
    } on ApiException {
      // Roll back the optimistic flip so the list doesn't show all-read while
      // the badge refetch below restores the true (non-zero) unread count.
      if (current != null) state = AsyncValue.data(current);
    }
    await ref.read(unreadCountProvider.notifier).refresh();
  }
}

final inboxProvider =
    NotifierProvider<InboxNotifier, AsyncValue<List<AppNotification>>>(
      InboxNotifier.new,
    );
