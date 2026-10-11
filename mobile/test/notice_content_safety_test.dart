import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/notices/notice_content_safety.dart';

void main() {
  test('rejects reserved merge residue and internal instructions', () {
    for (final unsafe in [
      '{{tenant_name}}',
      '{Sofia Rodriguez}',
      '{Sofia\nRodriguez}',
      'Workspace administrator: review this first.',
      'WORKSPACE ADMINISTRATOR: review this first.',
    ]) {
      expect(
        noticeContentSafetyIssue(subject: 'Notice', body: unsafe),
        noticeContentCorrectionMessage,
      );
    }
  });

  test('allows safe copy, ordinary punctuation, and unbalanced braces', () {
    for (final safe in [
      'Hello Sofia, your rent is due Friday.',
      'Use commas, periods, dashes - and parentheses (safely).',
      'Opening brace {',
      'Closing brace } without a balanced fragment.',
      'Empty braces {}',
    ]) {
      expect(noticeContentSafetyIssue(subject: 'Notice?', body: safe), isNull);
    }
  });

  test('returns one guidance message without echoing notice content', () {
    final issue = noticeContentSafetyIssue(
      subject: '{Private Tenant Name}',
      body: 'Workspace administrator: private body',
    );

    expect(issue, noticeContentCorrectionMessage);
    expect(issue, isNot(contains('Private Tenant Name')));
    expect(issue, isNot(contains('private body')));
  });
}
