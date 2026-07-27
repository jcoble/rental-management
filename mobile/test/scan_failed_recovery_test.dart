import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
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

      final confirmButton = tester.widget<FilledButton>(
        find.widgetWithText(FilledButton, 'Confirm & Create Expense'),
      );
      expect(confirmButton.onPressed, isNull, reason: status);

      final rejectButton = tester.widget<OutlinedButton>(
        find.widgetWithText(OutlinedButton, 'Reject'),
      );
      expect(rejectButton.onPressed, isNull, reason: status);
      expect(
        find.widgetWithText(OutlinedButton, 'Retry extraction'),
        findsNothing,
      );

      await tester.pumpWidget(const SizedBox.shrink());
      await tester.pump(const Duration(seconds: 11));
    }
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
    expect(draft.id, 107);
    expect(draft.status, 'Pending');
  });
}

class _FakeFailedScanRepository extends ScanRepository {
  _FakeFailedScanRepository({required this.status}) : super(dio: Dio());

  final String status;
  int retryCount = 0;
  int? lastRetryId;

  @override
  Future<ScanDraft> getDraft(int id) async => _draft(id, status);

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
    return _draft(id, 'Pending');
  }
}

class _RetryAdapter implements HttpClientAdapter {
  final requests = <RequestOptions>[];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(options);
    return ResponseBody.fromString(
      jsonEncode({
        'id': 107,
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

ScanDraft _draft(int id, String status) {
  return ScanDraft(
    id: id,
    portfolioId: 1,
    targetEntityType: 'Expense',
    status: status,
    fileUrl: '/api/v1/scans/$id/file',
    fields: const [],
    failureReason: status == 'Failed' ? 'OCR timed out.' : null,
    createdAt: DateTime(2027, 1, 18),
  );
}
