import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/messages/message_detail_screen.dart';
import 'package:rental_command/features/messages/message_models.dart';
import 'package:rental_command/features/messages/messages_list_screen.dart';
import 'package:rental_command/features/messages/messages_repository.dart';
import 'package:rental_command/features/tenants/tenants_repository.dart';

class _FakeMessagesRepository extends MessagesRepository {
  _FakeMessagesRepository() : super(Dio(), tenantMode: false);

  final sentChannels = <List<String>>[];
  final startedSubjects = <String>[];
  final startedBodies = <String>[];
  final startedChannels = <List<String>>[];
  final startedTenantIds = <int>[];

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
    sentChannels.add(List.of(channels));
    return _conversation;
  }

  @override
  Future<Conversation> startConversation({
    required int tenantId,
    required String subject,
    required String body,
    required List<String> channels,
    String? operationKey,
  }) async {
    startedTenantIds.add(tenantId);
    startedSubjects.add(subject);
    startedBodies.add(body);
    startedChannels.add(List.of(channels));
    return _conversation;
  }

  static final _conversation = Conversation(
    id: 42,
    tenantId: 7,
    tenantName: 'Avery Tenant',
    subject: 'Lease question',
    lastMessageAt: DateTime(2026, 7, 8, 17, 42),
    unreadCount: 0,
    messageCount: 2,
    messages: [
      ConversationMessage(
        id: 1,
        senderRole: 'Tenant',
        body: 'Can I pay tomorrow?',
        createdAt: DateTime(2026, 7, 8, 17, 40),
      ),
      ConversationMessage(
        id: 2,
        senderRole: 'Landlord',
        body: 'Yes, that works.',
        channels: ['Portal', 'Sms'],
        createdAt: DateTime(2026, 7, 8, 17, 42),
      ),
    ],
  );
}

Tenant _tenant() => Tenant(
  id: 7,
  portfolioId: 1,
  firstName: 'Avery',
  lastName: 'Tenant',
  createdAt: DateTime(2026, 7),
  updatedAt: DateTime(2026, 7),
);

Future<void> _pumpDetail(
  WidgetTester tester,
  _FakeMessagesRepository repository,
) async {
  await tester.binding.setSurfaceSize(const Size(420, 900));
  addTearDown(() => tester.binding.setSurfaceSize(null));

  await tester.pumpWidget(
    ProviderScope(
      overrides: [messagesRepositoryProvider.overrideWithValue(repository)],
      child: const MaterialApp(home: MessageDetailScreen(conversationId: 42)),
    ),
  );
  await tester.pumpAndSettle();
}

Future<void> _pumpNewConversationSheet(
  WidgetTester tester,
  _FakeMessagesRepository repository,
) async {
  await tester.binding.setSurfaceSize(const Size(420, 1000));
  addTearDown(() => tester.binding.setSurfaceSize(null));

  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        messagesRepositoryProvider.overrideWithValue(repository),
        tenantsPageProvider(messageRecipientTenantQuery).overrideWith(
          (ref) async =>
              TenantPage(items: [_tenant()], totalCount: 1, skip: 0, take: 200),
        ),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: Builder(
            builder: (context) => FilledButton(
              onPressed: () => showModalBottomSheet<Conversation>(
                context: context,
                isScrollControlled: true,
                builder: (_) => newConversationSheetForTest(),
              ),
              child: const Text('Open compose'),
            ),
          ),
        ),
      ),
    ),
  );

  await tester.tap(find.text('Open compose'));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('reply composer hides chips and shows sending line with change', (
    tester,
  ) async {
    await _pumpDetail(tester, _FakeMessagesRepository());

    expect(find.byType(FilterChip), findsNothing);
    expect(find.text('Sending in the tenant app and by text'), findsOneWidget);
    expect(find.text('Change'), findsOneWidget);
  });

  testWidgets('change reveals chips and send uses the chosen channels', (
    tester,
  ) async {
    final repository = _FakeMessagesRepository();
    await _pumpDetail(tester, repository);

    await tester.tap(find.text('Change'));
    await tester.pumpAndSettle();

    expect(find.byType(FilterChip), findsNWidgets(3));

    await tester.tap(find.widgetWithText(FilterChip, 'Email'));
    await tester.pump();

    await tester.enterText(find.byType(TextField), 'Sending on three lanes.');
    final sendButton = find.byKey(const Key('message-send-button'));
    await tester.ensureVisible(sendButton);
    await tester.pump();
    await tester.tap(sendButton);
    await tester.pumpAndSettle();

    expect(repository.sentChannels, [
      ['Portal', 'Email', 'Sms'],
    ]);
  });

  testWidgets('new conversation is one sheet with derived subject', (
    tester,
  ) async {
    final repository = _FakeMessagesRepository();
    await _pumpNewConversationSheet(tester, repository);

    expect(find.text('Subject'), findsNothing);
    expect(find.text('Channels'), findsNothing);
    expect(find.byType(FilterChip), findsNothing);
    expect(find.text('To (tenant)'), findsOneWidget);
    expect(find.text('Message'), findsOneWidget);
    expect(find.text('Sending in the tenant app'), findsOneWidget);

    await tester.tap(find.byType(DropdownButtonFormField<int>));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Avery Tenant').last);
    await tester.pumpAndSettle();

    const longMessage =
        'The water heater in the basement is leaking again and the floor is wet.';
    await tester.enterText(find.byType(TextFormField).last, longMessage);
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(FilledButton, 'Send'));
    await tester.pumpAndSettle();

    expect(repository.startedTenantIds, [7]);
    expect(repository.startedBodies, [longMessage]);
    expect(repository.startedSubjects, [longMessage.substring(0, 60)]);
    expect(repository.startedChannels, [
      ['Portal'],
    ]);
  });
}
