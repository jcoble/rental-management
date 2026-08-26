import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/ai/ai_models.dart';
import 'package:rental_command/features/ai/ai_repository.dart';
import 'package:rental_command/features/ai/qa_screen.dart';

class _FakeAiRepository extends AiRepository {
  _FakeAiRepository() : super(Dio());

  final executeResult = Completer<AssistantActionExecuteResponse>();
  final deliveryResult = Completer<AskResponse>();
  var ordinaryAskCount = 0;

  static const draft = AssistantActionDraft(
    kind: 'CreateExpense',
    risk: 'Medium',
    summary: 'Plumbing expense',
    expense: AssistantExpenseDraft(
      category: 'Repairs',
      status: 'Paid',
      description: 'Plumbing',
      amount: 40,
      incurredAt: '2026-08-25',
    ),
  );

  @override
  Future<AssistantActionDraftResponse> draftAction(
    String command,
    bool writeModeEnabled,
  ) async => const AssistantActionDraftResponse(
    status: 'DraftReady',
    message: 'Ready to create.',
    requiresWriteMode: false,
    canExecute: true,
    draft: draft,
  );

  @override
  Future<AssistantActionExecuteResponse> executeAction(
    AssistantActionDraft draft,
    bool writeModeEnabled,
  ) => executeResult.future;

  @override
  Future<AskResponse> ask(
    String question,
    List<QaTurn> history, {
    AskDelivery? delivery,
  }) {
    if (delivery != null) return deliveryResult.future;
    ordinaryAskCount++;
    return Future.value(_answer('Original answer'));
  }

  static AskResponse _answer(
    String answer, {
    List<String> deliveredChannels = const [],
  }) => AskResponse(
    answer: answer,
    toolsUsed: const [],
    llmAvailable: true,
    tokensUsed: 0,
    modelId: 'test',
    deliveredChannels: deliveredChannels,
  );
}

Future<void> _pumpScreen(
  WidgetTester tester,
  _FakeAiRepository repository,
) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [aiRepositoryProvider.overrideWithValue(repository)],
      child: const MaterialApp(home: Scaffold(body: QaScreen())),
    ),
  );
}

Future<void> _submit(WidgetTester tester, String text) async {
  await tester.enterText(find.byType(TextField), text);
  await tester.testTextInput.receiveAction(TextInputAction.send);
  await tester.pump();
}

void main() {
  testWidgets(
    'action execution blocks a newer answer from replacing its turn',
    (tester) async {
      final repository = _FakeAiRepository();
      await _pumpScreen(tester, repository);
      await tester.tap(find.byType(Switch));
      await _submit(tester, 'log a plumbing expense');
      await tester.pumpAndSettle();

      await tester.tap(find.widgetWithText(FilledButton, 'Confirm'));
      await tester.pump();
      expect(tester.widget<TextField>(find.byType(TextField)).enabled, isFalse);

      await _submit(tester, 'What is the balance?');
      expect(repository.ordinaryAskCount, 0);

      repository.executeResult.complete(
        const AssistantActionExecuteResponse(
          status: 'Created',
          message: 'Expense created.',
          kind: 'CreateExpense',
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('Expense created.'), findsOneWidget);
    },
  );

  testWidgets(
    'delivery stays attached to its answer while delivery is active',
    (tester) async {
      final repository = _FakeAiRepository();
      await _pumpScreen(tester, repository);
      await _submit(tester, 'What is the balance?');
      await tester.pumpAndSettle();

      await tester.tap(find.text('Text me this'));
      await tester.pump();
      expect(tester.widget<TextField>(find.byType(TextField)).enabled, isFalse);
      await _submit(tester, 'What is overdue?');
      expect(repository.ordinaryAskCount, 1);

      repository.deliveryResult.complete(
        _FakeAiRepository._answer(
          'Original answer',
          deliveredChannels: const ['Sms'],
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('Texted to you.'), findsOneWidget);
      expect(find.text('What is overdue?'), findsNothing);
    },
  );

  testWidgets('terminal action response removes the dead confirm card', (
    tester,
  ) async {
    final repository = _FakeAiRepository();
    await _pumpScreen(tester, repository);
    await tester.tap(find.byType(Switch));
    await _submit(tester, 'log a plumbing expense');
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(FilledButton, 'Confirm'));
    repository.executeResult.complete(
      const AssistantActionExecuteResponse(
        status: 'InvalidDraft',
        message: 'The expense could not be created.',
        kind: 'CreateExpense',
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('The expense could not be created.'), findsOneWidget);
    expect(find.text('Confirm'), findsNothing);
  });
}
