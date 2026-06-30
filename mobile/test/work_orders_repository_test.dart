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
}

class _RecordingAdapter implements HttpClientAdapter {
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

    return ResponseBody.fromString(
      jsonEncode({
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
        'skip': 0,
        'take': 25,
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
