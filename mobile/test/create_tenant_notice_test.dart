import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/api/api_exception.dart';
import 'package:rental_command/features/notices/create_tenant_notice.dart';
import 'package:rental_command/features/notices/notices_models.dart';
import 'package:rental_command/features/notices/notices_repository.dart';

void main() {
  testWidgets(
    'retry after partial notice failure sends only the unsent draft',
    (tester) async {
      final repository = _PartiallyFailingNoticesRepository();

      await tester.pumpWidget(
        ProviderScope(
          overrides: [noticesRepositoryProvider.overrideWithValue(repository)],
          child: MaterialApp(
            home: Scaffold(
              body: ReviewNoticeSheet(drafts: [_draft(1), _draft(2)]),
            ),
          ),
        ),
      );

      await tester.ensureVisible(find.text('Send notice'));
      await tester.tap(find.text('Send notice'));
      await tester.pump();

      expect(repository.approvedIds, [1, 2]);
      expect(find.textContaining('1 of 2 sent.'), findsOneWidget);

      await tester.ensureVisible(find.text('Send notice'));
      await tester.tap(find.text('Send notice'));
      await tester.pumpAndSettle();

      expect(repository.approvedIds, [1, 2, 2]);
    },
  );
}

NoticeDraft _draft(int id) => NoticeDraft(
  id: id,
  leaseManagementId: 10,
  tenantAccountId: 20,
  recipientTenantId: 30,
  tenantName: 'Tenant $id',
  noticeType: 'rent-reminder',
  status: 'Draft',
  subject: 'Rent reminder $id',
  body: 'Rent is due soon for draft $id.',
  reason: '',
  triggerDate: DateTime.utc(2026, 8, 25),
);

class _PartiallyFailingNoticesRepository extends NoticesRepository {
  _PartiallyFailingNoticesRepository() : super(Dio());

  final approvedIds = <int>[];
  bool failedSecond = false;

  @override
  Future<void> approve(int id, List<String> channels) async {
    approvedIds.add(id);
    if (id == 2 && !failedSecond) {
      failedSecond = true;
      throw const ApiException(statusCode: 500, message: 'Send failed.');
    }
  }
}
