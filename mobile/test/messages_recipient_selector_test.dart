import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/tenant.dart';
import 'package:rental_command/features/messages/message_models.dart';
import 'package:rental_command/features/messages/messages_list_screen.dart';

void main() {
  test(
    'new conversation recipient selector reaches beyond the legacy first page',
    () {
      final source = _messagesListSource().readAsStringSync();

      expect(
        source,
        contains('tenantsPageProvider(messageRecipientTenantQuery)'),
      );
      expect(messageRecipientTenantQuery.take, 200);
      expect(messageRecipientTenantQuery.sort, 'name');
      expect(source, isNot(contains('ref.watch(tenantsProvider)')));
      expect(source, isNot(contains('tenantsProvider.notifier).load()')));
    },
  );

  test('recipient labels disambiguate duplicate tenant names by location', () {
    final tenant6 = _tenant(
      id: 6,
      firstName: 'Sage',
      lastName: 'King',
      propertyName: 'Cedar Row',
      unitNumber: '1A',
    );
    final tenant24 = _tenant(
      id: 24,
      firstName: 'Sage',
      lastName: 'King',
      propertyName: 'Union Duplex',
      unitNumber: 'B',
    );

    expect(messageRecipientLabel(tenant6), 'Sage King - Cedar Row / 1A');
    expect(messageRecipientLabel(tenant24), 'Sage King - Union Duplex / B');
  });

  test(
    'conversation display title and avatar use viewer-aware counterparty',
    () {
      final tenantView = _conversation(
        tenantName: 'Yara Brooks',
        counterpartyName: 'Jordan QA Admin',
      );
      final staffView = _conversation(
        tenantName: 'Yara Brooks',
        counterpartyName: 'Yara Brooks',
      );

      expect(tenantView.displayName, 'Jordan QA Admin');
      expect(conversationDisplayTitle(tenantView), 'Jordan QA Admin');
      expect(conversationAvatarInitials(tenantView), 'JA');
      expect(conversationDisplayTitle(staffView), 'Yara Brooks');
      expect(conversationAvatarInitials(staffView), 'YB');
    },
  );

  test('conversation model keeps old tenant-name fallback', () {
    final conversation = Conversation.fromJson({
      'id': 7,
      'tenantId': 9,
      'tenantName': 'Yara Brooks',
      'subject': 'Keys',
      'lastMessageAt': '2027-01-29T15:00:00Z',
      'unreadCount': 1,
      'messageCount': 2,
    });

    expect(conversation.counterpartyName, 'Yara Brooks');
    expect(conversation.displayName, 'Yara Brooks');
  });
}

File _messagesListSource() {
  final candidates = [
    File('lib/features/messages/messages_list_screen.dart'),
    File('mobile/lib/features/messages/messages_list_screen.dart'),
  ];
  return candidates.firstWhere((file) => file.existsSync());
}

Tenant _tenant({
  required int id,
  required String firstName,
  required String lastName,
  String? propertyName,
  String? unitNumber,
}) {
  return Tenant(
    id: id,
    portfolioId: 1,
    firstName: firstName,
    lastName: lastName,
    currentPropertyName: propertyName,
    currentUnitNumber: unitNumber,
    createdAt: DateTime(2026, 7),
    updatedAt: DateTime(2026, 7),
  );
}

Conversation _conversation({
  required String tenantName,
  required String counterpartyName,
}) {
  return Conversation(
    id: 17,
    tenantId: 8,
    tenantName: tenantName,
    counterpartyName: counterpartyName,
    subject: 'Maintenance',
    lastMessageAt: DateTime(2027, 1, 29, 15),
    unreadCount: 0,
    messageCount: 1,
  );
}
