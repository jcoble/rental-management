import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/home/mobile_quick_action_fab.dart';

void main() {
  test('new conversation sheet owns a quick-action visibility hider', () {
    final source = _messagesListSource().readAsStringSync();

    expect(
      source,
      matches(
        RegExp(
          r'builder:\s*\(_\)\s*=>\s*const MobileQuickActionHider'
          r'\(\s*child:\s*_ComposeConversationSheet\(\),?\s*\)',
        ),
      ),
    );
  });

  testWidgets(
    'new conversation hider hides quick actions and restores them on dismiss',
    (tester) async {
      final controller = MobileQuickActionController();
      var showConversationSheet = true;
      late StateSetter setHostState;
      addTearDown(controller.dispose);

      await tester.pumpWidget(
        MaterialApp(
          home: MobileQuickActionScope(
            controller: controller,
            child: StatefulBuilder(
              builder: (context, setState) {
                setHostState = setState;
                return showConversationSheet
                    ? const MobileQuickActionHider(
                        child: Text('New Conversation'),
                      )
                    : const Text('Messages');
              },
            ),
          ),
        ),
      );
      await tester.pump();

      expect(find.text('New Conversation'), findsOneWidget);
      expect(controller.hidden, isTrue);

      setHostState(() => showConversationSheet = false);
      await tester.pump();

      expect(find.text('Messages'), findsOneWidget);
      expect(controller.hidden, isFalse);
    },
  );
}

File _messagesListSource() {
  final candidates = [
    File('lib/features/messages/messages_list_screen.dart'),
    File('mobile/lib/features/messages/messages_list_screen.dart'),
  ];
  return candidates.firstWhere((file) => file.existsSync());
}
