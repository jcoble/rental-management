import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/voice/voice_command.dart';
import 'package:rental_command/core/voice/voice_command_controller.dart';

/// Tests for the voice/App-Actions deep-link parser — the heart of the bridge
/// between Google Assistant and the app. These assert that the URIs our
/// `shortcuts.xml` capabilities emit map to the right typed command + params.
void main() {
  group('auth email links', () {
    test('recognizes reset-password and verify-email https paths', () {
      expect(
        authEmailLinkPath(
          Uri.parse(
            'https://rentalcommand.net/reset-password?userId=u&token=t',
          ),
        ),
        'reset-password',
      );
      expect(
        authEmailLinkPath(
          Uri.parse(
            'https://rentalcommand.net/verify-email?userId=u&token=t',
          ),
        ),
        'verify-email',
      );
    });

    test('recognizes custom-scheme auth hosts and rejects other links', () {
      expect(
        authEmailLinkPath(Uri.parse('rentalcommand://reset-password?token=t')),
        'reset-password',
      );
      expect(
        authEmailLinkPath(Uri.parse('rentalcommand://verify-email?token=t')),
        'verify-email',
      );
      expect(
        authEmailLinkPath(Uri.parse('https://rentalcommand.net/settings')),
        isNull,
      );
    });
  });

  group('parseVoiceCommand — custom scheme', () {
    test('scan a document', () {
      final cmd = parseVoiceCommand(Uri.parse('rentalcommand://voice/scan'));
      expect(cmd, isNotNull);
      expect(cmd!.action, VoiceAction.scanDocument);
    });

    test('log expense with amount, category and property', () {
      final cmd = parseVoiceCommand(
        Uri.parse(
          'rentalcommand://voice/log-expense?amount=40&category=plumbing&property=123%20Main',
        ),
      );
      expect(cmd, isNotNull);
      expect(cmd!.action, VoiceAction.logExpense);
      expect(cmd.amount, 40.0);
      expect(cmd.category, 'plumbing');
      expect(cmd.property, '123 Main');
    });

    test('log expense tolerates a "\$40.00" / "1,200" style amount', () {
      expect(
        parseVoiceCommand(
          Uri.parse('rentalcommand://voice/log-expense?amount=%2440.00'),
        )!.amount,
        40.0,
      );
      expect(
        parseVoiceCommand(
          Uri.parse('rentalcommand://voice/log-expense?amount=1%2C200'),
        )!.amount,
        1200.0,
      );
    });

    test('vendor is accepted as an alias for category', () {
      final cmd = parseVoiceCommand(
        Uri.parse('rentalcommand://voice/expense?vendor=roofing'),
      );
      expect(cmd!.category, 'roofing');
    });

    test('show overdue rent', () {
      final cmd = parseVoiceCommand(
        Uri.parse('rentalcommand://voice/overdue-rent'),
      );
      expect(cmd!.action, VoiceAction.showOverdueRent);
    });

    test('open work orders for a unit', () {
      final cmd = parseVoiceCommand(
        Uri.parse('rentalcommand://voice/work-orders?unit=4'),
      );
      expect(cmd!.action, VoiceAction.openWorkOrders);
      expect(cmd.unit, '4');
    });
  });

  group('parseVoiceCommand — leniency', () {
    test('action matching is case-insensitive and _/- interchangeable', () {
      expect(
        parseVoiceCommand(Uri.parse('rentalcommand://voice/LOG_EXPENSE'))!.action,
        VoiceAction.logExpense,
      );
    });

    test('natural-language aliases map to the right action', () {
      expect(
        parseVoiceCommand(Uri.parse('rentalcommand://voice/maintenance'))!.action,
        VoiceAction.openWorkOrders,
      );
      expect(
        parseVoiceCommand(Uri.parse('rentalcommand://voice/late-rent'))!.action,
        VoiceAction.showOverdueRent,
      );
    });

    test('https app-link form (path-based) parses identically', () {
      final cmd = parseVoiceCommand(
        Uri.parse(
          'https://rentalcommand.net/voice/log-expense?amount=12',
        ),
      );
      expect(cmd!.action, VoiceAction.logExpense);
      expect(cmd.amount, 12.0);
    });
  });

  group('parseVoiceCommand — rejects non-commands', () {
    test('unknown action returns null', () {
      expect(
        parseVoiceCommand(Uri.parse('rentalcommand://voice/teleport')),
        isNull,
      );
    });

    test('non-voice deep link returns null', () {
      expect(
        parseVoiceCommand(Uri.parse('rentalcommand://settings/profile')),
        isNull,
      );
    });

    test('empty / bare URI returns null', () {
      expect(parseVoiceCommand(Uri.parse('rentalcommand://')), isNull);
    });
  });

  group('understoodSummary', () {
    test('reads back the parsed expense in plain language', () {
      final summary = const VoiceCommand(
        VoiceAction.logExpense,
        amount: 40,
        category: 'plumbing',
        property: '123 Main',
      ).understoodSummary;
      expect(summary, contains('\$40.00'));
      expect(summary, contains('plumbing'));
      expect(summary, contains('123 Main'));
    });
  });
}
