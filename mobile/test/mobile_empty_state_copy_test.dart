import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/messages/messages_list_screen.dart';
import 'package:rental_command/features/recurring_maintenance/recurring_maintenance_list_screen.dart';

void main() {
  test('recurring maintenance names the action that actually exists', () {
    expect(
      recurringMaintenanceEmptyInstruction(false),
      'Open the action button and choose New recurring task.',
    );
    expect(
      recurringMaintenanceEmptyInstruction(false),
      isNot(contains('Tap +')),
    );
    expect(
      recurringMaintenanceEmptyInstruction(true),
      'Nothing scheduled right now.',
    );
  });

  test('messages names New conversation only when creation is available', () {
    expect(
      messagesEmptyInstruction(
        hasCriteria: false,
        canStartConversation: true,
        tenantMode: false,
      ),
      'Open the action button and choose New conversation.',
    );

    final denied = messagesEmptyInstruction(
      hasCriteria: false,
      canStartConversation: false,
      tenantMode: false,
    );
    expect(denied, contains('workspace admin'));
    expect(denied, isNot(contains('pencil')));
    expect(denied, isNot(contains('New conversation')));
  });

  test('tenant and filtered empty states keep context-specific guidance', () {
    expect(
      messagesEmptyInstruction(
        hasCriteria: false,
        canStartConversation: false,
        tenantMode: true,
      ),
      'Messages from your rental team will appear here.',
    );
    expect(
      messagesEmptyInstruction(
        hasCriteria: true,
        canStartConversation: false,
        tenantMode: true,
      ),
      'Try changing your search or filters.',
    );
    expect(
      messagesEmptyInstruction(
        hasCriteria: true,
        canStartConversation: true,
        tenantMode: false,
      ),
      'Try changing your search or filters.',
    );
  });
}
