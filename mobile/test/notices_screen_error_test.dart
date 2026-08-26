import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/api/api_exception.dart';
import 'package:rental_command/features/notices/notices_models.dart';
import 'package:rental_command/features/notices/notices_repository.dart';
import 'package:rental_command/features/notices/notices_screen.dart';

void main() {
  testWidgets('approve failure shows the API message', (tester) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          noticesRepositoryProvider.overrideWithValue(
            _FailingNoticesRepository(),
          ),
        ],
        child: const MaterialApp(home: NoticesScreen()),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(FilledButton, 'Approve'));
    await tester.pump();

    expect(find.text('Approval failed'), findsOneWidget);
  });
}

class _FailingNoticesRepository extends NoticesRepository {
  _FailingNoticesRepository() : super(Dio());

  @override
  Future<List<NoticeDraft>> list({String status = 'Draft'}) async => [
    NoticeDraft(
      id: 1,
      leaseManagementId: 2,
      tenantAccountId: 3,
      recipientTenantId: 4,
      tenantName: 'Jordan Lee',
      noticeType: 'rent-reminder',
      status: 'Draft',
      subject: 'Rent reminder',
      body: 'Rent is due.',
      reason: 'Upcoming due date',
      triggerDate: DateTime.utc(2026, 8, 25),
    ),
  ];

  @override
  Future<void> approve(int id, List<String> channels) async {
    throw const ApiException(statusCode: 400, message: 'Approval failed');
  }
}
