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
  final bodies = <String>[];
  final channelSelections = <List<String>>[];
  bool failNextSend = false;
  int getConversationCalls = 0;

  @override
  Future<Conversation> getConversation(int id) async {
    getConversationCalls++;
    return _conversation;
  }

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
    bodies.add(body);
    channelSelections.add(List.of(channels));
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
    counterpartyName: 'North Star Management',
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
  Future<void> dismissSendError(WidgetTester tester) async {
    ScaffoldMessenger.of(
      tester.element(find.byType(Scaffold)),
    ).hideCurrentSnackBar();
    await tester.pumpAndSettle();
  }

  Future<void> tapSend(WidgetTester tester) async {
    final sendButton = find.byKey(const Key('message-send-button'));
    expect(sendButton, findsOneWidget);
    await tester.ensureVisible(sendButton);
    await tester.pump();
    await tester.tap(sendButton);
    await tester.pumpAndSettle();
  }

  testWidgets('opening and invalidating message detail reloads conversation', (
    tester,
  ) async {
    final repository = _FakeMessagesRepository();
    final scope = ProviderContainer(
      overrides: [messagesRepositoryProvider.overrideWithValue(repository)],
    );
    addTearDown(scope.dispose);

    Future<void> openDetail() async {
      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: scope,
          child: const MaterialApp(
            home: MessageDetailScreen(conversationId: 42),
          ),
        ),
      );
      await tester.pumpAndSettle();
    }

    await openDetail();
    expect(repository.getConversationCalls, 2);

    scope.invalidate(conversationProvider(42));
    await tester.pumpAndSettle();

    expect(repository.getConversationCalls, 3);
    expect(find.text('Can I pay tomorrow?'), findsOneWidget);

    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: scope,
        child: const MaterialApp(home: SizedBox()),
      ),
    );
    await openDetail();

    expect(repository.getConversationCalls, 4);
  });

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

  testWidgets('message detail title uses counterparty instead of tenant name', (
    tester,
  ) async {
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

    expect(find.text('North Star Management'), findsOneWidget);
    expect(find.text('Avery Tenant'), findsNothing);
  });

  testWidgets('message retry reuses one non-null operation key', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(390, 760));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final repository = _FakeMessagesRepository()..failNextSend = true;

    await tester.pumpWidget(
      ProviderScope(
        overrides: [messagesRepositoryProvider.overrideWithValue(repository)],
        child: const MaterialApp(home: MessageDetailScreen(conversationId: 42)),
      ),
    );
    await tester.pumpAndSettle();

    await tester.enterText(
      find.byType(TextField),
      'Please retry this message.',
    );
    await tapSend(tester);

    expect(repository.operationKeys, hasLength(1));
    final firstOperationKey = repository.operationKeys.single;
    expect(firstOperationKey, isNotNull);
    expect(firstOperationKey, isNotEmpty);

    await dismissSendError(tester);
    await tapSend(tester);

    expect(repository.operationKeys, [firstOperationKey, firstOperationKey]);
  });

  testWidgets('editing failed message creates a new payload key', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(390, 760));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final repository = _FakeMessagesRepository()..failNextSend = true;

    await tester.pumpWidget(
      ProviderScope(
        overrides: [messagesRepositoryProvider.overrideWithValue(repository)],
        child: const MaterialApp(home: MessageDetailScreen(conversationId: 42)),
      ),
    );
    await tester.pumpAndSettle();

    final field = find.byType(TextField);
    await tester.enterText(field, 'Original payload');
    await tapSend(tester);
    final failedKey = repository.operationKeys.single;

    await dismissSendError(tester);
    await tester.enterText(field, 'Corrected payload');
    await tapSend(tester);

    expect(repository.bodies, ['Original payload', 'Corrected payload']);
    expect(repository.operationKeys, hasLength(2));
    expect(repository.operationKeys.last, isNot(equals(failedKey)));
  });

  testWidgets('changing failed send channels creates a new payload key', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(390, 760));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final repository = _FakeMessagesRepository()..failNextSend = true;

    await tester.pumpWidget(
      ProviderScope(
        overrides: [messagesRepositoryProvider.overrideWithValue(repository)],
        child: const MaterialApp(home: MessageDetailScreen(conversationId: 42)),
      ),
    );
    await tester.pumpAndSettle();

    await tester.enterText(find.byType(TextField), 'Channel payload');
    await tapSend(tester);
    final failedKey = repository.operationKeys.single;

    await dismissSendError(tester);
    await tester.tap(find.widgetWithText(FilterChip, 'Email'));
    await tester.pump();
    await tapSend(tester);

    expect(repository.channelSelections, [
      ['Portal'],
      ['Portal', 'Email'],
    ]);
    expect(repository.operationKeys, hasLength(2));
    expect(repository.operationKeys.last, isNot(equals(failedKey)));
  });
}
