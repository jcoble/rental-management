import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'message_models.dart';

/// Repository for tenant/portal messages.
///
/// Endpoints:
///   GET   /messages?status={Open|InProgress|Resolved|Closed} — list
///   GET   /messages/{id}                                      — single
///   POST  /messages/{id}/reply  { reply, status? }            — send reply
///   PATCH /messages/{id}/status { status }                    — status update
class MessagesRepository {
  MessagesRepository(this._dio);

  final Dio _dio;

  Future<List<Message>> listMessages({String? status}) async {
    try {
      final params = <String, dynamic>{};
      if (status != null) params['status'] = status;
      final response = await _dio.get<List<dynamic>>(
        '/messages',
        queryParameters: params.isEmpty ? null : params,
      );
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(Message.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<Message> getMessage(int id) async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/messages/$id');
      return Message.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// POST /messages/{id}/reply  body: { reply, status? }
  Future<Message> reply(int id, {required String replyText, String? status}) async {
    try {
      final body = <String, dynamic>{'reply': replyText};
      if (status != null) body['status'] = status;
      final response = await _dio.post<Map<String, dynamic>>(
        '/messages/$id/reply',
        data: body,
      );
      return Message.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// PATCH /messages/{id}/status  body: { status }
  Future<Message> setStatus(int id, String status) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/messages/$id/status',
        data: {'status': status},
      );
      return Message.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final messagesRepositoryProvider = Provider<MessagesRepository>((ref) {
  return MessagesRepository(ref.watch(dioProvider));
});

// ── Messages list ─────────────────────────────────────────────────────────────

/// Filter mode for [MessagesNotifier].
enum MessageFilter { open, all }

class MessagesNotifier extends Notifier<AsyncValue<List<Message>>> {
  MessageFilter _filter = MessageFilter.open;

  MessageFilter get filter => _filter;

  @override
  AsyncValue<List<Message>> build() => const AsyncValue.loading();

  MessagesRepository get _repo => ref.read(messagesRepositoryProvider);

  static const _closedStatuses = {'Resolved', 'Closed'};

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final all = await _repo.listMessages();
      final filtered = _filter == MessageFilter.open
          ? all.where((m) => !_closedStatuses.contains(m.status)).toList()
          : all;
      state = AsyncValue.data(filtered);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();

  void setFilter(MessageFilter f) {
    if (_filter == f) return;
    _filter = f;
    load();
  }

  /// Updates a single message's state in-memory after a successful mutation.
  void _updateInList(Message updated) {
    state.whenData((list) {
      final newList = [
        for (final m in list)
          if (m.id == updated.id) updated else m,
      ];
      final filtered = _filter == MessageFilter.open
          ? newList
              .where((m) => !_closedStatuses.contains(m.status))
              .toList()
          : newList;
      state = AsyncValue.data(filtered);
    });
  }

  Future<void> setStatus(int id, String status) async {
    try {
      final updated = await _repo.setStatus(id, status);
      _updateInList(updated);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }
}

final messagesProvider =
    NotifierProvider<MessagesNotifier, AsyncValue<List<Message>>>(
  MessagesNotifier.new,
);

// ── Single message detail ─────────────────────────────────────────────────────

class MessageDetailNotifier extends Notifier<AsyncValue<Message>> {
  MessageDetailNotifier(this._id);

  final int _id;

  @override
  AsyncValue<Message> build() {
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  MessagesRepository get _repo => ref.read(messagesRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final msg = await _repo.getMessage(_id);
      state = AsyncValue.data(msg);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();

  Future<void> reply(String replyText) async {
    try {
      final updated = await _repo.reply(
        _id,
        replyText: replyText,
        status: 'InProgress',
      );
      state = AsyncValue.data(updated);
      ref.read(messagesProvider.notifier).refresh();
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> setStatus(String status) async {
    try {
      final updated = await _repo.setStatus(_id, status);
      state = AsyncValue.data(updated);
      ref.read(messagesProvider.notifier).refresh();
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }
}

final messageDetailProvider = NotifierProvider.family<MessageDetailNotifier,
    AsyncValue<Message>, int>(
  MessageDetailNotifier.new,
);
