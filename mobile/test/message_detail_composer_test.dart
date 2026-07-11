import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/api/api_exception.dart';
import 'package:rental_command/features/messages/message_detail_screen.dart';
import 'package:rental_command/features/messages/message_models.dart';
import 'package:rental_command/features/messages/messages_repository.dart';

class _FakeMessagesRepository extends MessagesRepository {
  _FakeMessagesRepository() : super(Dio(), tenantMode: false);

  final operationKeys = <String?>[];
  bool failNextSend = false;

  @override
  Future<Conversation> getConversation(int id) async => _conversation;

  @override
  Future<List<Conversation>> listConversations() async => [_conversation];

  @override
  Future<Conversation> sendMessage(
    int id, {
    required String body,
    required List<String> channels,
    String? operationKey,
  }) async {
    operationKeys.add(operationKey);
    if (failNextSend) {
      failNextSend = false;
      throw const ApiException(statusCode: 503, message: 'Retry send.');
    }
    return _conversation;
  }

  static final _conversation = Conversation(
    id: 42,
    tenantId: 7,
    tenantName: 'Avery Tenant',
    subject: 'Lease question',
    lastMessageAt: DateTime(2026, 7, 8, 17, 42),
    unreadCount: 0,
    messageCount: 1,
    messages: [
      ConversationMessage(
        id: 1,
        senderRole: 'Tenant',
        body: 'Can I pay tomorrow?',
        createdAt: DateTime(2026, 7, 8, 17, 42),
      ),
    ],
  );
}

void main() {
  testWidgets('message composer keeps the text box full width', (tester) async {
    await tester.binding.setSurfaceSize(const Size(390, 760));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          messagesRepositoryProvider.overrideWithValue(
            _FakeMessagesRepository(),
          ),
        ],
        child: const MaterialApp(home: MessageDetailScreen(conversationId: 42)),
      ),
    );
    await tester.pumpAndSettle();

    final fieldRect = tester.getRect(find.byType(TextField));
    final screenWidth = tester.getSize(find.byType(Scaffold)).width;

    expect(fieldRect.left, 8);
    expect(screenWidth - fieldRect.right, 8);
    expect(fieldRect.width, screenWidth - 16);
  });

  testWidgets('message retry reuses one non-null operation key', (tester) async {
    await tester.binding.setSurfaceSize(const Size(390, 760));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final repository = _FakeMessagesRepository()..failNextSend = true;

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          messagesRepositoryProvider.overrideWithValue(repository),
        ],
        child: const MaterialApp(home: MessageDetailScreen(conversationId: 42)),
      ),
    );
    await tester.pumpAndSettle();

    await tester.enterText(find.byType(TextField), 'Please retry this message.');
    final sendButton = find.byKey(const Key('message-send-button'));
    expect(sendButton, findsOneWidget);
    await tester.tap(sendButton);
    await tester.pumpAndSettle();

    expect(repository.operationKeys, hasLength(1));
    final firstOperationKey = repository.operationKeys.single;
    expect(firstOperationKey, isNotNull);
    expect(firstOperationKey, isNotEmpty);

    ScaffoldMessenger.of(
      tester.element(find.byType(Scaffold)),
    ).hideCurrentSnackBar();
    await tester.pumpAndSettle();
    await tester.tap(sendButton);
    await tester.pumpAndSettle();

    expect(repository.operationKeys, [firstOperationKey, firstOperationKey]);
  });
}
