import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/api/api_exception.dart';
import 'package:rental_command/features/scan/scan_models.dart';
import 'package:rental_command/features/scan/scan_repository.dart';
import 'package:rental_command/features/scan/scan_review_screen.dart';

void main() {
  testWidgets('failed scan can be retried or rejected but not confirmed', (
    tester,
  ) async {
    final scanRepo = _FakeFailedScanRepository(status: 'Failed');

    await tester.pumpWidget(
      ProviderScope(
        overrides: [scanRepositoryProvider.overrideWithValue(scanRepo)],
        child: const MaterialApp(home: ScanReviewScreen(draftId: 107)),
      ),
    );
    await tester.pumpAndSettle();

    expect(
      find.text(
        'Extraction failed before review fields could be recovered. Retry extraction or reject this scan.',
      ),
      findsOneWidget,
    );
    expect(
      find.text(
        'Extraction failed. You can still enter the values below and confirm.',
      ),
      findsNothing,
    );

    final confirmButton = tester.widget<FilledButton>(
      find.widgetWithText(FilledButton, 'Confirm & Create Expense'),
    );
    expect(confirmButton.onPressed, isNull);

    final retryButtonFinder = find.widgetWithText(
      OutlinedButton,
      'Retry extraction',
    );
    expect(retryButtonFinder, findsOneWidget);
    expect(
      tester.widget<OutlinedButton>(retryButtonFinder).onPressed,
      isNotNull,
    );

    final rejectButtonFinder = find.widgetWithText(OutlinedButton, 'Reject');
    expect(rejectButtonFinder, findsOneWidget);
    expect(
      tester.widget<OutlinedButton>(rejectButtonFinder).onPressed,
      isNotNull,
    );

    await tester.tap(retryButtonFinder);
    await tester.pump();
    await tester.pump();

    expect(scanRepo.retryCount, 1);
    expect(scanRepo.lastRetryId, 107);

    await tester.pumpWidget(const SizedBox.shrink());
    await tester.pump(const Duration(seconds: 11));
  });

  testWidgets('confirmed and rejected scans keep actions locked', (
    tester,
  ) async {
    for (final status in ['Confirmed', 'Rejected']) {
      final scanRepo = _FakeFailedScanRepository(status: status);

      await tester.pumpWidget(
        ProviderScope(
          overrides: [scanRepositoryProvider.overrideWithValue(scanRepo)],
          child: const MaterialApp(home: ScanReviewScreen(draftId: 108)),
        ),
      );
      await tester.pumpAndSettle();

      expect(
        find.widgetWithText(FilledButton, 'Confirm & Create Expense'),
        findsNothing,
        reason: status,
      );
      expect(
        find.widgetWithText(OutlinedButton, 'Reject'),
        findsNothing,
        reason: status,
      );
      expect(
        find.widgetWithText(OutlinedButton, 'Retry extraction'),
        findsNothing,
      );
      expect(find.text('Result'), findsOneWidget, reason: status);
      expect(find.text(status), findsWidgets, reason: status);
      expect(find.text('Save command'), findsNothing, reason: status);
      expect(
        find.text('This result is final and read-only.'),
        findsOneWidget,
        reason: status,
      );
      expect(
        find.textContaining('Nothing is created until'),
        findsNothing,
        reason: status,
      );

      await tester.pumpWidget(const SizedBox.shrink());
      await tester.pump(const Duration(seconds: 11));
    }
  });

  testWidgets('confirmed loan scan is read-only and exposes no loan actions', (
    tester,
  ) async {
    final scanRepo = _FakeFailedScanRepository(
      status: 'Confirmed',
      targetEntityType: 'Loan',
    );

    await tester.pumpWidget(
      ProviderScope(
        overrides: [scanRepositoryProvider.overrideWithValue(scanRepo)],
        child: const MaterialApp(home: ScanReviewScreen(draftId: 109)),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('This scan has already been confirmed.'), findsOneWidget);
    expect(find.text('Create Loan'), findsNothing);
    expect(find.text('Match & record payment'), findsNothing);
    expect(find.text('Which property does this loan belong to?'), findsNothing);
    expect(find.text('Add new loan'), findsNothing);
    expect(find.text('Match existing payment'), findsNothing);
    expect(find.byType(DropdownButtonFormField<int>), findsNothing);
    expect(find.byType(TextFormField), findsNothing);

    await tester.pumpWidget(const SizedBox.shrink());
    await tester.pump(const Duration(seconds: 11));
  });

  test('repository retry posts to the retry endpoint', () async {
    final adapter = _RetryAdapter();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repository = ScanRepository(dio: dio);

    final draft = await repository.retry(107);

    expect(adapter.requests, hasLength(1));
    expect(adapter.requests.single.method, 'POST');
    expect(adapter.requests.single.path, '/scans/107/retry');
    expect(adapter.requests.single.headers['Idempotency-Key'], isNotEmpty);
    expect(draft.id, 107);
    expect(draft.status, 'Pending');
  });

  test(
    'repository retry reuses idempotency key after ambiguous failure',
    () async {
      final adapter = _RetryAdapter(failFirst: true);
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repository = ScanRepository(dio: dio);

      await expectLater(repository.retry(108), throwsA(isA<ApiException>()));
      final firstKey = adapter.requests.single.headers['Idempotency-Key'];

      final draft = await repository.retry(108);

      expect(adapter.requests, hasLength(2));
      expect(firstKey, isNotEmpty);
      expect(adapter.requests.last.headers['Idempotency-Key'], firstKey);
      expect(draft.status, 'Pending');
    },
  );
}

class _FakeFailedScanRepository extends ScanRepository {
  _FakeFailedScanRepository({
    required this.status,
    this.targetEntityType = 'Expense',
  }) : super(dio: Dio());

  final String status;
  final String targetEntityType;
  int retryCount = 0;
  int? lastRetryId;

  @override
  Future<ScanDraft> getDraft(int id) async => _draft(
    id,
    status,
    targetEntityType: targetEntityType,
  );

  @override
  Future<Uint8List> downloadFile(int id) async {
    return base64Decode(
      'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=',
    );
  }

  @override
  Future<ScanDraft> retry(int id) async {
    retryCount += 1;
    lastRetryId = id;
    return _draft(id, 'Pending', targetEntityType: targetEntityType);
  }
}

class _RetryAdapter implements HttpClientAdapter {
  _RetryAdapter({this.failFirst = false});

  final bool failFirst;
  final requests = <RequestOptions>[];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(options);

    if (failFirst && requests.length == 1) {
      throw DioException(
        requestOptions: options,
        type: DioExceptionType.receiveTimeout,
        message: 'simulated ambiguous retry failure',
      );
    }

    return ResponseBody.fromString(
      jsonEncode({
        'id': int.parse(options.path.split('/')[2]),
        'portfolioId': 1,
        'targetEntityType': 'Expense',
        'status': 'Pending',
        'fileUrl': '/api/v1/scans/107/file',
        'fields': [],
        'createdAt': '2027-01-18T00:00:00Z',
      }),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

ScanDraft _draft(
  int id,
  String status, {
  String targetEntityType = 'Expense',
}) {
  return ScanDraft(
    id: id,
    portfolioId: 1,
    targetEntityType: targetEntityType,
    status: status,
    fileUrl: '/api/v1/scans/$id/file',
    fields: const [],
    failureReason: status == 'Failed' ? 'OCR timed out.' : null,
    createdAt: DateTime(2027, 1, 18),
  );
}
