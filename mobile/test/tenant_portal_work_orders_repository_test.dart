import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/maintenance/work_orders_repository.dart';
import 'package:rental_command/features/portal/tenant_portal_repository.dart';

void main() {
  test(
    'workOrdersPage sends explicit tenant repairs query parameters',
    () async {
      final adapter = _RecordingAdapter(
        response: {'items': [], 'totalCount': 0, 'skip': 40, 'take': 20},
      );
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = TenantPortalRepository(dio);

      final page = await repo.workOrdersPage(
        skip: 40,
        take: 20,
        openOnly: false,
        search: ' leak ',
        status: 'InProgress',
        sort: 'status',
        requestedFrom: '2026-07-01',
        requestedTo: '2026-07-31',
      );

      expect(page.skip, 40);
      expect(adapter.method, 'GET');
      expect(adapter.path, '/portal/work-orders');
      expect(adapter.queryParameters, {
        'skip': 40,
        'take': 20,
        'sort': 'status',
        'openOnly': false,
        'search': 'leak',
        'status': 'InProgress',
        'from': '2026-07-01',
        'to': '2026-07-31',
      });
    },
  );

  test(
    'getWorkOrderDetail parses tenant-safe role-aware capabilities',
    () async {
      final adapter = _RecordingAdapter(
        response: {
          'id': 31,
          'portfolioId': 1,
          'propertyId': 9,
          'title': 'Heat is out',
          'description': 'No heat upstairs.',
          'category': 'HVAC',
          'priority': 'Emergency',
          'status': 'Scheduled',
          'requestedAt': '2026-07-01T00:00:00.000Z',
          'updatedAt': '2026-07-01T01:00:00.000Z',
          'capabilities': {
            'canUploadPhoto': true,
            'canViewCosts': false,
            'canDispatchVendor': false,
          },
          'timeline': [],
        },
      );
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = TenantPortalRepository(dio);

      final detail = await repo.getWorkOrderDetail(31);
      final roleAware = detail as RoleAwareWorkOrderDetail;

      expect(adapter.method, 'GET');
      expect(adapter.path, '/portal/work-orders/31');
      expect(roleAware.capabilities.canUploadPhoto, isTrue);
      expect(roleAware.capabilities.canViewCosts, isFalse);
      expect(roleAware.capabilities.canDispatchVendor, isFalse);
    },
  );
}

class _RecordingAdapter implements HttpClientAdapter {
  _RecordingAdapter({required this.response});

  final Map<String, dynamic> response;
  String? method;
  String? path;
  Map<String, dynamic>? queryParameters;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    method = options.method;
    path = options.path;
    queryParameters = Map<String, dynamic>.from(options.queryParameters);

    return ResponseBody.fromString(
      jsonEncode(response),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}
