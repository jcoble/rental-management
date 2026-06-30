import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/voice/voice_intake_models.dart';

void main() {
  group('VoiceTurn.fromJson', () {
    test('parses fields, missingRequired, nextPrompt, complete', () {
      final turn = VoiceTurn.fromJson({
        'id': 7,
        'targetEntityType': 'Expense',
        'fields': [
          {'name': 'category', 'value': 'plumbing', 'confidence': 0.8},
          {'name': 'property_id', 'value': '5', 'confidence': 0.8},
          {
            'name': 'transcript',
            'value': 'log a plumbing expense for 123 Main',
            'confidence': 1.0,
          },
        ],
        'missingRequired': ['amount'],
        'nextPrompt': 'How much was it?',
        'complete': false,
        'ambiguous': false,
      });

      expect(turn.draftId, 7);
      expect(turn.recordType, 'Expense');
      expect(turn.fields['category'], 'plumbing');
      expect(turn.missingRequired, ['amount']);
      expect(turn.nextPrompt, 'How much was it?');
      expect(turn.complete, isFalse);
      expect(turn.ambiguous, isFalse);
      expect(turn.transcript, contains('123 Main'));
    });

    test('defaults complete=true / empty missing / not ambiguous when those keys are absent', () {
      final turn = VoiceTurn.fromJson({
        'id': 1,
        'targetEntityType': 'Expense',
        'fields': <dynamic>[],
      });

      expect(turn.complete, isTrue);
      expect(turn.ambiguous, isFalse);
      expect(turn.missingRequired, isEmpty);
      expect(turn.nextPrompt, isNull);
    });

    test('parses ambiguous voice turns', () {
      final turn = VoiceTurn.fromJson({
        'id': 2,
        'targetEntityType': 'Expense',
        'fields': [
          {'name': 'voice_intent', 'value': 'WorkOrder'},
          {'name': 'voice_ambiguous', 'value': 'true'},
        ],
        'nextPrompt': 'I can save expenses by voice right now.',
        'complete': false,
        'ambiguous': true,
      });

      expect(turn.ambiguous, isTrue);
      expect(turn.complete, isFalse);
      expect(phaseForTurn(turn), VoicePhase.asking);
    });
  });

  group('displayFields & summary', () {
    test('shows friendly fields, prefixes amount with \$, hides internals', () {
      final turn = VoiceTurn.fromJson({
        'id': 1,
        'targetEntityType': 'Expense',
        'fields': [
          {'name': 'amount', 'value': '40'},
          {'name': 'category', 'value': 'plumbing'},
          {'name': 'property_id', 'value': '5'},
          {'name': 'target_entity_type', 'value': 'Expense'},
        ],
        'complete': true,
      });

      final labels = turn.displayFields.map((f) => f.label).toList();
      expect(labels, contains('Amount'));
      expect(labels, contains('Category'));
      expect(labels, isNot(contains('property_id')));

      final amount = turn.displayFields.firstWhere((f) => f.label == 'Amount');
      expect(amount.value, r'$40');
      expect(turn.summary, contains(r'Amount: $40'));
    });
  });

  group('phaseForTurn', () {
    test('complete turn → review', () {
      final turn = VoiceTurn.fromJson({
        'id': 1,
        'targetEntityType': 'Expense',
        'fields': <dynamic>[],
        'complete': true,
      });
      expect(phaseForTurn(turn), VoicePhase.review);
    });

    test('incomplete turn → asking', () {
      final turn = VoiceTurn.fromJson({
        'id': 1,
        'targetEntityType': 'Expense',
        'fields': <dynamic>[],
        'missingRequired': ['amount'],
        'nextPrompt': 'How much was it?',
        'complete': false,
      });
      expect(phaseForTurn(turn), VoicePhase.asking);
    });
  });
}
