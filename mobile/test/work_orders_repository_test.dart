import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/maintenance/work_orders_repository.dart';

void main() {
  test(
    'listWorkOrders uses the paged endpoint with server-side open filter',
    () async {
      final adapter = _RecordingAdapter();
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = WorkOrdersRepository(dio);

      final orders = await repo.listWorkOrders(
        propertyId: 7,
        openOnly: true,
        take: 25,
        sort: '-updatedAt',
      );

      expect(adapter.path, '/work-orders/page');
      expect(adapter.queryParameters, containsPair('propertyId', 7));
      expect(adapter.queryParameters, containsPair('openOnly', true));
      expect(adapter.queryParameters, containsPair('take', 25));
      expect(adapter.queryParameters, containsPair('sort', '-updatedAt'));
      expect(orders, hasLength(1));
      expect(orders.single.id, 17);
      expect(orders.single.title, 'Kitchen sink leak');
    },
  );

  test('listWorkOrdersPage preserves server-side page metadata', () async {
    final adapter = _RecordingAdapter();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = WorkOrdersRepository(dio);

    final page = await repo.listWorkOrdersPage(
      const WorkOrderListQuery(
        skip: 25,
        take: 25,
        search: 'sink',
        openOnly: true,
        sort: '-requestedAt',
        requestedFrom: '2026-07-01',
        requestedTo: '2026-07-31',
      ),
    );

    expect(adapter.path, '/work-orders/page');
    expect(adapter.queryParameters, containsPair('skip', 25));
    expect(adapter.queryParameters, containsPair('take', 25));
    expect(adapter.queryParameters, containsPair('search', 'sink'));
    expect(adapter.queryParameters, containsPair('openOnly', true));
    expect(adapter.queryParameters, containsPair('sort', '-requestedAt'));
    expect(
      adapter.queryParameters,
      containsPair('requestedFrom', '2026-07-01'),
    );
    expect(adapter.queryParameters, containsPair('requestedTo', '2026-07-31'));
    expect(page.totalCount, 1);
    expect(page.skip, 25);
    expect(page.take, 25);
    expect(page.items.single.id, 17);
  });

  test('listWorkOrdersPage sends field queue sort to the server', () async {
    final adapter = _RecordingAdapter();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = WorkOrdersRepository(dio);

    await repo.listWorkOrdersPage(
      const WorkOrderListQuery(openOnly: true, take: 5, sort: 'fieldQueue'),
    );

    expect(adapter.path, '/work-orders/page');
    expect(adapter.queryParameters, containsPair('openOnly', true));
    expect(adapter.queryParameters, containsPair('take', 5));
    expect(adapter.queryParameters, containsPair('sort', 'fieldQueue'));
  });

  test('getWorkOrderDetail parses server detail capabilities', () async {
    final adapter = _RecordingAdapter(
      detailResponse: {
        'id': 17,
        'portfolioId': 1,
        'propertyId': 7,
        'title': 'Kitchen sink leak',
        'description': 'Water under the cabinet',
        'category': 'Plumbing',
        'priority': 'High',
        'status': 'New',
        'requestedAt': '2026-06-01T00:00:00.000Z',
        'updatedAt': '2026-06-02T00:00:00.000Z',
        'capabilities': {
          'allowedStatusTransitions': ['Scheduled', 'Cancelled'],
          'canDispatchVendor': false,
          'canViewCosts': false,
        },
        'timeline': [],
      },
    );
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = WorkOrdersRepository(dio);

    final detail = await repo.getWorkOrderDetail(17);
    final roleAware = detail as RoleAwareWorkOrderDetail;

    expect(adapter.path, '/work-orders/17');
    expect(roleAware.capabilities.canUpdateStatus, isTrue);
    expect(roleAware.capabilities.allowedStatusTransitions, [
      'Scheduled',
      'Cancelled',
    ]);
    expect(roleAware.capabilities.canDispatchVendor, isFalse);
    expect(roleAware.capabilities.canViewCosts, isFalse);
    expect(roleAware.capabilities.canEdit, isFalse);
  });

  test('getWorkOrderDetail defaults missing capabilities closed', () async {
    final adapter = _RecordingAdapter(
      detailResponse: {
        'id': 17,
        'portfolioId': 1,
        'propertyId': 7,
        'title': 'Kitchen sink leak',
        'description': 'Water under the cabinet',
        'category': 'Plumbing',
        'priority': 'High',
        'status': 'New',
        'requestedAt': '2026-06-01T00:00:00.000Z',
        'updatedAt': '2026-06-02T00:00:00.000Z',
        'timeline': [],
      },
    );
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = WorkOrdersRepository(dio);

    final detail = await repo.getWorkOrderDetail(17);
    final roleAware = detail as RoleAwareWorkOrderDetail;

    expect(roleAware.capabilities.canEdit, isFalse);
    expect(roleAware.capabilities.canAssignTechnician, isFalse);
    expect(roleAware.capabilities.canUpdateStatus, isFalse);
    expect(roleAware.capabilities.canViewCosts, isFalse);
  });
}

class _RecordingAdapter implements HttpClientAdapter {
  _RecordingAdapter({this.detailResponse});

  final Map<String, dynamic>? detailResponse;
  String? path;
  Map<String, dynamic>? queryParameters;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    path = options.path;
    queryParameters = Map<String, dynamic>.from(options.queryParameters);

    final body = detailResponse != null && options.path == '/work-orders/17'
        ? detailResponse
        : {
            'items': [
              {
                'id': 17,
                'portfolioId': 1,
                'propertyId': 7,
                'title': 'Kitchen sink leak',
                'description': 'Water under the cabinet',
                'category': 'Plumbing',
                'priority': 'High',
                'status': 'New',
                'requestedAt': '2026-06-01T00:00:00.000Z',
                'updatedAt': '2026-06-02T00:00:00.000Z',
              },
            ],
            'totalCount': 1,
            'skip': options.queryParameters['skip'] ?? 0,
            'take': options.queryParameters['take'] ?? 25,
          };

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
