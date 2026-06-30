import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/ai/ai_models.dart';

void main() {
  group('assistant action command detection', () {
    test('detects expense write commands', () {
      expect(
        looksLikeAssistantActionCommand(
          'Log a \$200 plumbing expense for Eastland',
        ),
        isTrue,
      );
      expect(
        looksLikeAssistantActionCommand(
          'record paid receipt for the water heater',
        ),
        isTrue,
      );
    });

    test('leaves read-only questions as Q&A', () {
      expect(looksLikeAssistantActionCommand("Who's late on rent?"), isFalse);
      expect(
        looksLikeAssistantActionCommand('How much did I collect?'),
        isFalse,
      );
    });
  });

  test('parses and serializes an expense action draft', () {
    final response = AssistantActionDraftResponse.fromJson({
      'status': 'DraftReady',
      'message': 'Review this expense before creating it.',
      'requiresWriteMode': true,
      'canExecute': true,
      'missingFields': ['amount'],
      'draft': {
        'kind': 'CreateExpense',
        'risk': 'Medium',
        'summary': 'Create a \$200 Repairs expense.',
        'expense': {
          'propertyId': 12,
          'propertyName': 'Eastland 8-Plex',
          'category': 'Repairs',
          'status': 'Paid',
          'description': 'Plumbing expense',
          'amount': 200,
          'incurredAt': '2026-06-30T00:00:00Z',
        },
      },
    });

    expect(response.status, 'DraftReady');
    expect(response.missingFields, ['amount']);
    expect(response.draft?.expense?.propertyName, 'Eastland 8-Plex');
    expect(response.draft?.expense?.amount, 200);
    expect(response.draft?.toJson()['kind'], 'CreateExpense');
    expect(
      (response.draft?.toJson()['expense']
          as Map<String, dynamic>)['propertyId'],
      12,
    );
  });
}
