import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/owners/owners_list_screen.dart';
import 'package:rental_command/features/owners/owners_models.dart';
import 'package:rental_command/features/owners/owners_repository.dart';

void main() {
  testWidgets('owners screen exposes native add, edit, and delete actions', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          ownersRepositoryProvider.overrideWithValue(_FakeOwnersRepository()),
        ],
        child: const MaterialApp(home: OwnersListScreen()),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.byTooltip('Open quick actions'));
    await tester.pumpAndSettle();

    expect(find.text('Add owner'), findsOneWidget);
    expect(find.byTooltip('Edit owner'), findsOneWidget);
    expect(find.byTooltip('Delete owner'), findsOneWidget);
    expect(find.textContaining('Add owners on the web'), findsNothing);
  });

  testWidgets('assigned owner delete clears assignments after confirmation', (
    tester,
  ) async {
    final repository = _FakeOwnersRepository();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [ownersRepositoryProvider.overrideWithValue(repository)],
        child: const MaterialApp(home: OwnersListScreen()),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.byTooltip('Delete owner'));
    await tester.pumpAndSettle();

    expect(repository.deletedOwnerId, isNull);
    expect(find.textContaining('is assigned to 2 properties'), findsOneWidget);

    await tester.tap(find.text('Clear and delete'));
    await tester.pumpAndSettle();

    expect(repository.deletedOwnerId, 12);
    expect(repository.clearPropertyAssignments, isTrue);
  });

  test(
    'listPage sends server-side page/search/sort params and parses response',
    () async {
      final adapter = _OwnersRecordingAdapter();
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repository = OwnersRepository(dio);

      final page = await repository.listPage(
        const OwnerListQuery(
          skip: 20,
          take: 20,
          search: 'maya',
          sort: '-updatedAt',
        ),
      );

      expect(adapter.requests.single.method, 'GET');
      expect(adapter.requests.single.path, '/owner-entities/page');
      expect(adapter.requests.single.queryParameters, containsPair('skip', 20));
      expect(adapter.requests.single.queryParameters, containsPair('take', 20));
      expect(
        adapter.requests.single.queryParameters,
        containsPair('search', 'maya'),
      );
      expect(
        adapter.requests.single.queryParameters,
        containsPair('sort', '-updatedAt'),
      );
      expect(page.totalCount, 1);
      expect(page.items.single.id, 12);
      expect(page.items.single.assignedPropertyCount, 2);
    },
  );

  test(
    'repository create, update, and delete use owner-entities endpoints',
    () async {
      final adapter = _OwnersRecordingAdapter();
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repository = OwnersRepository(dio);

      await repository.createOwner({'name': 'Maya Chen'});
      await repository.updateOwner(12, {'name': 'Maya Chen LLC'});
      await repository.deleteOwner(12, clearPropertyAssignments: true);

      expect(adapter.requests[0].method, 'POST');
      expect(adapter.requests[0].path, '/owner-entities');
      expect(adapter.requests[0].data, containsPair('name', 'Maya Chen'));

      expect(adapter.requests[1].method, 'PATCH');
      expect(adapter.requests[1].path, '/owner-entities/12');
      expect(adapter.requests[1].data, containsPair('name', 'Maya Chen LLC'));

      expect(adapter.requests[2].method, 'DELETE');
      expect(adapter.requests[2].path, '/owner-entities/12');
      expect(
        adapter.requests[2].queryParameters,
        containsPair('clearPropertyAssignments', true),
      );
    },
  );
}

class _FakeOwnersRepository extends OwnersRepository {
  _FakeOwnersRepository() : super(Dio());

  int? deletedOwnerId;
  bool? clearPropertyAssignments;

  @override
  Future<OwnerEntityPage> listPage([
    OwnerListQuery query = const OwnerListQuery(),
  ]) async {
    return const OwnerEntityPage(
      items: [
        OwnerEntity(
          id: 12,
          portfolioId: 4,
          ownerEntityType: OwnerEntityType.person,
          name: 'Maya Chen',
          email: 'maya@example.com',
          phone: '330-555-0122',
          assignedPropertyCount: 2,
        ),
      ],
      totalCount: 1,
      skip: 0,
      take: 50,
    );
  }

  @override
  Future<void> deleteOwner(
    int id, {
    bool clearPropertyAssignments = false,
  }) async {
    deletedOwnerId = id;
    this.clearPropertyAssignments = clearPropertyAssignments;
  }
}

class _RecordedRequest {
  const _RecordedRequest({
    required this.method,
    required this.path,
    required this.queryParameters,
    required this.data,
  });

  final String method;
  final String path;
  final Map<String, dynamic> queryParameters;
  final Object? data;
}

class _OwnersRecordingAdapter implements HttpClientAdapter {
  final requests = <_RecordedRequest>[];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(
      _RecordedRequest(
        method: options.method,
        path: options.path,
        queryParameters: Map<String, dynamic>.from(options.queryParameters),
        data: options.data,
      ),
    );

    if (options.method == 'DELETE') {
      return ResponseBody.fromString('', 204);
    }

    final body = options.path.endsWith('/page')
        ? {
            'items': [_ownerResponseJson()],
            'totalCount': 1,
            'skip': options.queryParameters['skip'] ?? 0,
            'take': options.queryParameters['take'] ?? 20,
          }
        : _ownerResponseJson();

    return ResponseBody.fromString(
      jsonEncode(body),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

Map<String, dynamic> _ownerResponseJson() {
  return {
    'id': 12,
    'portfolioId': 4,
    'ownerEntityType': 'Person',
    'name': 'Maya Chen',
    'email': 'maya@example.com',
    'phone': '330-555-0122',
    'assignedPropertyCount': 2,
    'isPrimary': false,
    'createdAt': '2026-07-01T00:00:00.000Z',
    'updatedAt': '2026-07-01T00:00:00.000Z',
  };
}
