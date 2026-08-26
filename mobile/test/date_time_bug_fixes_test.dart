import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/api/dio_client.dart';
import 'package:rental_command/core/models/appointment.dart';
import 'package:rental_command/core/models/lease.dart';
import 'package:rental_command/features/accounting/accounting_book_models.dart';
import 'package:rental_command/features/accounting/accounting_books_repository.dart';
import 'package:rental_command/features/accounting/accounting_repository.dart';
import 'package:rental_command/features/appointments/appointments_screen.dart';
import 'package:rental_command/features/leases/successor_agreement_sheet.dart';
import 'package:rental_command/features/money/money_repository.dart';
import 'package:rental_command/features/money/money_screen.dart';
import 'package:rental_command/features/money/tenant_ledger_view.dart';
import 'package:rental_command/features/scan/scan_models.dart';

void main() {
  test('appointment response exposes scheduled instants in local time', () {
    final appointment = Appointment.fromJson(
      _appointmentJson(
        scheduledStart: '2026-08-25T14:00:00Z',
        scheduledEnd: '2026-08-25T15:00:00Z',
      ),
    );

    expect(appointment.scheduledStart, DateTime.utc(2026, 8, 25, 14).toLocal());
    expect(appointment.scheduledEnd, DateTime.utc(2026, 8, 25, 15).toLocal());
  });

  testWidgets('appointment request includes the local UTC offset', (
    tester,
  ) async {
    final adapter = _AppointmentAdapter();
    final start = DateTime(2026, 8, 25, 10);
    final end = DateTime(2026, 8, 25, 11);

    await _pumpAppointmentSheet(tester, adapter, start: start, end: end);
    await _advanceAndSave(tester);

    final body = adapter.savedBody!;
    final offset = start.timeZoneOffset;
    final sign = offset.isNegative ? '-' : '+';
    final hours = offset.abs().inHours.toString().padLeft(2, '0');
    final minutes = (offset.abs().inMinutes % 60).toString().padLeft(2, '0');
    expect(body['scheduledStart'], '2026-08-25T10:00:00$sign$hours:$minutes');
    expect(body['scheduledEnd'], '2026-08-25T11:00:00$sign$hours:$minutes');
  });

  testWidgets('appointment rejects an end time before its start time', (
    tester,
  ) async {
    final adapter = _AppointmentAdapter();
    await _pumpAppointmentSheet(
      tester,
      adapter,
      start: DateTime(2026, 8, 25, 10),
      end: DateTime(2026, 8, 25, 9),
    );

    await tester.tap(find.widgetWithText(FilledButton, 'Next'));
    await tester.pump();

    expect(find.text('End time must be after start time.'), findsOneWidget);
    expect(adapter.savedBody, isNull);
  });

  test('tenant ledger month range uses the UTC business date', () {
    final range = tenantLedgerPeriodRange(DateTime.utc(2026, 9, 1, 1), 3);

    expect(range.from, DateTime(2026, 7, 1));
    expect(range.to, DateTime(2026, 9, 30));
  });

  test('lease renewal starts on local midnight after DST fall-back', () {
    final source = LeaseAgreementHistory.fromJson({
      'leaseAgreementId': 1,
      'versionNumber': 1,
      'agreementNumber': 'L-1',
      'changeType': 'New',
      'termType': 'FixedTerm',
      'termStartOn': '2025-11-02',
      'termEndOn': '2026-11-01',
      'governingFromOn': '2025-11-02',
      'baseRentAmount': 1000,
      'agreementStatus': 'Active',
      'isGoverning': true,
      'hasLiveReissue': false,
    });

    final renewal = initialLeaseSuccessorDates(
      source,
      LeaseSuccessorOperation.renewal,
      businessDate: DateTime(2026, 8, 25),
    );

    expect(renewal.termStart, DateTime(2026, 11, 2));
  });

  testWidgets('money periods use the UTC business calendar', (tester) async {
    final books = _MoneyBooksRepository();
    await _pumpMoneyScreen(tester, books);

    expect(books.lastFrom, DateTime.utc(books.lastTo!.year, books.lastTo!.month));
    expect(books.lastTo!.isUtc, isTrue);
  });

  testWidgets('cancelling custom money period restores prior selection', (
    tester,
  ) async {
    final books = _MoneyBooksRepository();
    await _pumpMoneyScreen(tester, books);

    await tester.drag(find.byType(ListView), const Offset(0, -600));
    await tester.pumpAndSettle();
    await tester.tap(find.text('This month'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Custom').last);
    await tester.pumpAndSettle();
    final picker = find.byType(DateRangePickerDialog);
    expect(picker, findsOneWidget);
    Navigator.of(tester.element(picker)).pop();
    await tester.pumpAndSettle();
    await tester.drag(find.byType(ListView), const Offset(0, -600));
    await tester.pumpAndSettle();

    expect(find.text('This month'), findsOneWidget);
  });

  test('scan response exposes created time in the device timezone', () {
    final draft = ScanDraft.fromJson({
      'id': 1,
      'portfolioId': 1,
      'createdAt': '2026-08-25T23:30:00Z',
    });
    final instant = DateTime.utc(2026, 8, 25, 23, 30);
    final fixedWest = instant.subtract(const Duration(hours: 4));

    expect(draft.createdAt.isUtc, isFalse);
    expect(draft.createdAt, instant.toLocal());
    expect((fixedWest.month, fixedWest.day), (8, 25));
  });
}

Future<void> _pumpMoneyScreen(
  WidgetTester tester,
  _MoneyBooksRepository books,
) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        accountingBooksRepositoryProvider.overrideWithValue(books),
        moneySnapshotProvider.overrideWith(
          (ref) async => throw StateError('legacy snapshot is not used'),
        ),
      ],
      child: const MaterialApp(
        home: MoneyScreen(initialView: MoneyScreenView.overview),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

Future<void> _pumpAppointmentSheet(
  WidgetTester tester,
  _AppointmentAdapter adapter, {
  required DateTime start,
  required DateTime end,
}) async {
  final dio = Dio()..httpClientAdapter = adapter;
  await tester.pumpWidget(
    ProviderScope(
      overrides: [dioProvider.overrideWithValue(dio)],
      child: MaterialApp(
        home: Scaffold(
          body: AppointmentFormSheet(
            existing: Appointment.fromJson(
              _appointmentJson(
                scheduledStart: start.toIso8601String(),
                scheduledEnd: end.toIso8601String(),
              ),
            ),
            onSaved: () {},
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

Future<void> _advanceAndSave(WidgetTester tester) async {
  for (var step = 0; step < 2; step++) {
    await tester.tap(find.widgetWithText(FilledButton, 'Next'));
    await tester.pump(const Duration(milliseconds: 400));
  }
  await tester.tap(find.widgetWithText(FilledButton, 'Save Changes'));
  await tester.pump(const Duration(milliseconds: 400));
}

Map<String, dynamic> _appointmentJson({
  required String scheduledStart,
  required String scheduledEnd,
}) => {
  'id': 7,
  'portfolioId': 1,
  'title': 'Showing',
  'type': 'Showing',
  'status': 'Scheduled',
  'scheduledStart': scheduledStart,
  'scheduledEnd': scheduledEnd,
  'createdAt': '2026-08-01T00:00:00Z',
  'updatedAt': '2026-08-01T00:00:00Z',
};

class _AppointmentAdapter implements HttpClientAdapter {
  Map<String, dynamic>? savedBody;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    Object body;
    if (options.path == '/properties') {
      body = <dynamic>[];
    } else if (options.path == '/tenants/page') {
      body = {'items': <dynamic>[], 'totalCount': 0, 'skip': 0, 'take': 200};
    } else if (options.path == '/appointments/7') {
      savedBody = Map<String, dynamic>.from(options.data as Map);
      body = _appointmentJson(
        scheduledStart: savedBody!['scheduledStart'] as String,
        scheduledEnd: savedBody!['scheduledEnd'] as String,
      );
    } else {
      throw StateError('Unexpected request: ${options.method} ${options.path}');
    }
    return ResponseBody.fromString(
      jsonEncode(body),
      200,
      headers: {
        Headers.contentTypeHeader: ['application/json'],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

class _MoneyBooksRepository extends AccountingBooksRepository {
  _MoneyBooksRepository() : super(Dio());

  DateTime? lastFrom;
  DateTime? lastTo;

  @override
  Future<MoneyPositionResponse> moneyPosition({
    DateTime? from,
    DateTime? to,
  }) async {
    lastFrom = from;
    lastTo = to;
    return MoneyPositionResponse(
      asOfUtc: DateTime.utc(2027, 3, 1, 1),
      fromUtc: from ?? DateTime.utc(2027, 3, 1),
      toUtc: to ?? DateTime.utc(2027, 3, 1, 1),
      totalCashOnHand: 1000,
      tenantDepositsHeld: 100,
      cashAfterTenantDeposits: 900,
      rentStillOwed: 0,
      loanBalance: 0,
      bookEquity: 900,
      cashReceived: 1000,
      cashPaid: 100,
      netCashMovement: 900,
      profitOrLoss: 900,
    );
  }
}
