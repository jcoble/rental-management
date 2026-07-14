import 'dart:math';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';
import '../../core/auth/auth_controller.dart';
import 'message_models.dart';

/// Repository for conversation threads (the threaded messenger).
///
/// Endpoints:
///   GET  /conversations               — list summaries (newest activity first)
///   GET  /conversations/{id}          — full thread (marks read for landlord)
///   POST /conversations               — start a new conversation
///                                       body: { tenantId, subject, body, channels }
///   POST /conversations/{id}/messages — reply to a thread
///                                       body: { body, channels }
///
/// Channel strings: 'Portal' | 'Email' | 'Sms'.
class MessagesRepository {
  MessagesRepository(this._dio, {required this.tenantMode});

  final Dio _dio;
  final bool tenantMode;

  static String createOperationKey() {
    final random = Random.secure();
    return List.generate(
      16,
      (_) => random.nextInt(256),
    ).map((value) => value.toRadixString(16).padLeft(2, '0')).join();
  }

  /// GET /conversations → ConversationSummary[] (messages list empty).
  Future<List<Conversation>> listConversations() async {
    try {
      final response = await _dio.get<List<dynamic>>(
        tenantMode ? '/portal/conversations' : '/conversations',
      );
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(Conversation.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// The management dashboard's bounded recent-thread projection. Paging and
  /// ordering are applied by the API/EF query; the client never loads the full
  /// conversation list and truncates it in memory.
  Future<List<Conversation>> listRecentConversations({int take = 5}) async {
    if (tenantMode) return listConversations();
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/conversations/page',
        queryParameters: {'skip': 0, 'take': take, 'sort': '-lastMessageAt'},
      );
      final items = response.data?['items'];
      return (items is List ? items : const [])
          .whereType<Map<String, dynamic>>()
          .map(Conversation.fromJson)
          .toList(growable: false);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// GET /conversations/{id} → full thread with messages (asc).
  Future<Conversation> getConversation(int id) async {
    try {
      final prefix = tenantMode ? '/portal' : '';
      await IdempotentMutation.run(
        '${tenantMode ? 'portal:' : ''}conversations:read:$id',
        (operationKey) => _dio.post<void>(
          '$prefix/conversations/$id/read',
          data: const <String, dynamic>{},
          options: Options(headers: {'Idempotency-Key': operationKey}),
        ),
      );
      final response = await _dio.get<Map<String, dynamic>>(
        '$prefix/conversations/$id',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Conversation.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// POST /conversations  body: { tenantId, subject, body, channels }
  Future<Conversation> startConversation({
    required int tenantId,
    required String subject,
    required String body,
    required List<String> channels,
    String? operationKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        tenantMode ? '/portal/conversations' : '/conversations',
        data: tenantMode
            ? {
                'operationKey': operationKey ?? createOperationKey(),
                'subject': subject,
                'body': body,
              }
            : {
                'operationKey': operationKey ?? createOperationKey(),
                'tenantId': tenantId,
                'subject': subject,
                'body': body,
                'channels': channels,
              },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Conversation.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// POST /conversations/{id}/messages  body: { body, channels }
  Future<Conversation> sendMessage(
    int id, {
    required String body,
    required List<String> channels,
    String? operationKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        tenantMode
            ? '/portal/conversations/$id/messages'
            : '/conversations/$id/messages',
        data: tenantMode
            ? {
                'operationKey': operationKey ?? createOperationKey(),
                'body': body,
              }
            : {
                'operationKey': operationKey ?? createOperationKey(),
                'body': body,
                'channels': channels,
              },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Conversation.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final messagesRepositoryProvider = Provider<MessagesRepository>((ref) {
  final auth = ref.watch(authControllerProvider);
  final tenantMode = auth is AuthStateAuthenticated && auth.isTenantExperience;
  return MessagesRepository(ref.watch(dioProvider), tenantMode: tenantMode);
});

// ── Conversation list ─────────────────────────────────────────────────────────

class ConversationsNotifier extends Notifier<AsyncValue<List<Conversation>>> {
  @override
  AsyncValue<List<Conversation>> build() => const AsyncValue.loading();

  MessagesRepository get _repo => ref.read(messagesRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final list = await _repo.listConversations();
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final conversationsProvider =
    NotifierProvider<ConversationsNotifier, AsyncValue<List<Conversation>>>(
      ConversationsNotifier.new,
    );

// ── Single conversation (thread) ──────────────────────────────────────────────

class ConversationNotifier extends Notifier<AsyncValue<Conversation>> {
  ConversationNotifier(this._id);

  final int _id;

  @override
  AsyncValue<Conversation> build() {
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  MessagesRepository get _repo => ref.read(messagesRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final convo = await _repo.getConversation(_id);
      state = AsyncValue.data(convo);
      // Opening the thread marks it read server-side; refresh the list so the
      // unread badge clears in the inbox.
      ref.read(conversationsProvider.notifier).refresh();
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();

  /// Sends a reply on the given [channels]. The server returns the updated
  /// thread (with the new message appended); the list is refreshed so the
  /// preview/timestamp stay in sync.
  ///
  /// Rethrows [ApiException] so the compose bar can surface the error without
  /// dropping the thread out of its loaded state.
  Future<void> sendMessage(
    String body,
    List<String> channels, {
    String? operationKey,
  }) async {
    final updated = await _repo.sendMessage(
      _id,
      body: body,
      channels: channels,
      operationKey: operationKey,
    );
    state = AsyncValue.data(updated);
    ref.read(conversationsProvider.notifier).refresh();
  }
}

final conversationProvider =
    NotifierProvider.family<
      ConversationNotifier,
      AsyncValue<Conversation>,
      int
    >(ConversationNotifier.new);
