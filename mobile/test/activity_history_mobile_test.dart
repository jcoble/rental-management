import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/activity/activity_history_screen.dart';
import 'package:rental_command/features/activity/activity_repository.dart';
import 'package:rental_command/features/home/mobile_destination.dart';
import 'package:rental_command/features/home/mobile_domain_navigation.dart';

void main() {
  test(
    'activity repository sends server-side audit filters and paging',
    () async {
      final adapter = _RecordingAdapter(
        responseBody: jsonEncode([_auditJson()]),
      );
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = ActivityRepository(dio);

      final entries = await repo.list(
        skip: 20,
        take: 20,
        search: 'rent',
        operation: 'Updated',
        entityType: 'Payment',
        entityId: 42,
        sort: '-timestamp',
      );

      expect(adapter.method, 'GET');
      expect(adapter.path, '/audit');
      expect(adapter.query, containsPair('skip', 20));
      expect(adapter.query, containsPair('take', 20));
      expect(adapter.query, containsPair('search', 'rent'));
      expect(adapter.query, containsPair('operation', 'Updated'));
      expect(adapter.query, containsPair('entityType', 'Payment'));
      expect(adapter.query, containsPair('entityId', 42));
      expect(adapter.query, containsPair('sort', '-timestamp'));
      expect(entries.single.description, 'Rent payment updated');
      expect(entries.single.changes.single.field, 'Amount');
    },
  );

  test(
    'activity history notifier loads the next page with skip/take',
    () async {
      final repo = _FakeActivityRepository(
        pages: [
          [
            for (var i = 1; i <= 20; i++)
              ActivityEntry.fromJson(_auditJson(id: i)),
          ],
          [ActivityEntry.fromJson(_auditJson(id: 21))],
        ],
      );
      final container = ProviderContainer(
        overrides: [activityRepositoryProvider.overrideWithValue(repo)],
      );
      addTearDown(container.dispose);

      final notifier = container.read(activityHistoryProvider.notifier);
      await notifier.refresh();

      expect(container.read(activityHistoryProvider).items, hasLength(20));
      expect(container.read(activityHistoryProvider).hasMore, isTrue);
      expect(repo.requests.single, {
        'skip': 0,
        'take': 20,
        'sort': '-timestamp',
        'search': null,
        'operation': null,
        'entityType': null,
        'entityId': null,
      });

      await notifier.loadMore();

      expect(container.read(activityHistoryProvider).items, hasLength(21));
      expect(container.read(activityHistoryProvider).hasMore, isFalse);
      expect(repo.requests.last, {
        'skip': 20,
        'take': 20,
        'sort': '-timestamp',
        'search': null,
        'operation': null,
        'entityType': null,
        'entityId': null,
      });
    },
  );

  test(
    'activity history notifier discards stale load-more responses after filter changes',
    () async {
      final staleLoadMore = Completer<List<ActivityEntry>>();
      final filteredEntry = ActivityEntry.fromJson(
        _auditJson(id: 100, description: 'Filtered rent update'),
      );
      final repo = _ControlledActivityRepository((request) {
        if (request.skip == 0 && request.search == null) {
          return Future.value([
            for (var i = 1; i <= 20; i++)
              ActivityEntry.fromJson(_auditJson(id: i)),
          ]);
        }
        if (request.skip == 20 && request.search == null) {
          return staleLoadMore.future;
        }
        if (request.skip == 0 && request.search == 'rent') {
          return Future.value([filteredEntry]);
        }
        fail('Unexpected request: $request');
      });
      final container = ProviderContainer(
        overrides: [activityRepositoryProvider.overrideWithValue(repo)],
      );
      addTearDown(container.dispose);

      final notifier = container.read(activityHistoryProvider.notifier);
      await notifier.refresh();
      final loadMore = notifier.loadMore();
      await Future<void>.delayed(Duration.zero);

      await notifier.setSearch('rent');
      expect(container.read(activityHistoryProvider).items, [filteredEntry]);

      staleLoadMore.complete([ActivityEntry.fromJson(_auditJson(id: 21))]);
      await loadMore;

      final state = container.read(activityHistoryProvider);
      expect(state.items, [filteredEntry]);
      expect(state.loadingMore, isFalse);
    },
  );

  testWidgets('activity history screen renders global audit entries', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          activityRepositoryProvider.overrideWithValue(
            _FakeActivityRepository(
              pages: [
                [ActivityEntry.fromJson(_auditJson())],
              ],
            ),
          ),
        ],
        child: const MaterialApp(home: ActivityHistoryScreen()),
      ),
    );
    await tester.pump();
    await tester.pump();

    expect(find.text('Activity history'), findsOneWidget);
    expect(find.text('Rent payment updated'), findsOneWidget);
    expect(find.textContaining('Alex Manager'), findsOneWidget);
    expect(find.textContaining('Updated'), findsOneWidget);
    expect(find.textContaining('Payment #42'), findsOneWidget);
  });

  testWidgets('activity history screen shows retained search filter', (
    tester,
  ) async {
    final repo = _FakeActivityRepository(
      pages: [
        [ActivityEntry.fromJson(_auditJson())],
        [ActivityEntry.fromJson(_auditJson())],
      ],
    );
    final container = ProviderContainer(
      overrides: [activityRepositoryProvider.overrideWithValue(repo)],
    );
    addTearDown(container.dispose);

    await container.read(activityHistoryProvider.notifier).setSearch('rent');

    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: ActivityHistoryScreen()),
      ),
    );
    await tester.pump();

    final field = tester.widget<TextField>(find.byType(TextField));
    expect(field.controller?.text, 'rent');
    expect(find.byTooltip('Clear search'), findsOneWidget);
  });

  test('activity history is an Inbox destination', () {
    final destination = inboxHubDestinations.singleWhere(
      (d) => d.id == MobileDestinationId.activityHistory,
    );

    expect(destination.label, 'Activity history');
    expect(destination.subtitle, contains('audit'));
  });
}

Map<String, dynamic> _auditJson({
  int id = 42,
  String description = 'Rent payment updated',
}) => {
  'id': id,
  'portfolioId': 1,
  'operation': 'Updated',
  'operationName': 'Updated',
  'entityType': 'Payment',
  'entityId': 42,
  'actor': 'Alex Manager',
  'description': description,
  'detailHref': '/accounting/payments/42',
  'timestamp': '2026-07-01T12:30:00.000Z',
  'testId': 'audit-$id',
  'changes': [
    {'field': 'Amount', 'oldValue': r'$1,200', 'newValue': r'$1,250'},
  ],
};

class _ActivityRequest {
  const _ActivityRequest({
    required this.skip,
    required this.take,
    required this.sort,
    required this.search,
    required this.operation,
    required this.entityType,
    required this.entityId,
  });

  final int skip;
  final int take;
  final String sort;
  final String? search;
  final String? operation;
  final String? entityType;
  final int? entityId;

  @override
  String toString() {
    return 'ActivityRequest(skip: $skip, take: $take, sort: $sort, '
        'search: $search, operation: $operation, entityType: $entityType, '
        'entityId: $entityId)';
  }
}

class _RecordingAdapter implements HttpClientAdapter {
  _RecordingAdapter({required this.responseBody});

  final String responseBody;
  String? method;
  String? path;
  Map<String, dynamic>? query;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    method = options.method;
    path = options.path;
    query = Map<String, dynamic>.from(options.queryParameters);

    return ResponseBody.fromString(
      responseBody,
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

class _FakeActivityRepository extends ActivityRepository {
  _FakeActivityRepository({required this.pages}) : super(Dio());

  final List<List<ActivityEntry>> pages;
  final requests = <Map<String, Object?>>[];

  @override
  Future<List<ActivityEntry>> list({
    int skip = 0,
    int take = 20,
    String sort = '-timestamp',
    String? search,
    String? operation,
    String? entityType,
    int? entityId,
  }) async {
    requests.add({
      'skip': skip,
      'take': take,
      'sort': sort,
      'search': search,
      'operation': operation,
      'entityType': entityType,
      'entityId': entityId,
    });
    final pageIndex = requests.length - 1;
    return pageIndex < pages.length ? pages[pageIndex] : const [];
  }
}

class _ControlledActivityRepository extends ActivityRepository {
  _ControlledActivityRepository(this.handler) : super(Dio());

  final Future<List<ActivityEntry>> Function(_ActivityRequest request) handler;
  final requests = <_ActivityRequest>[];

  @override
  Future<List<ActivityEntry>> list({
    int skip = 0,
    int take = 20,
    String sort = '-timestamp',
    String? search,
    String? operation,
    String? entityType,
    int? entityId,
  }) {
    final request = _ActivityRequest(
      skip: skip,
      take: take,
      sort: sort,
      search: search,
      operation: operation,
      entityType: entityType,
      entityId: entityId,
    );
    requests.add(request);
    return handler(request);
  }
}
