import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/vendors/vendors_list_screen.dart';
import 'package:rental_command/features/vendors/vendors_models.dart';
import 'package:rental_command/features/vendors/vendors_repository.dart';

void main() {
  testWidgets('vendors screen drills into detail for edit and delete actions', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          vendorsRepositoryProvider.overrideWithValue(_FakeVendorsRepository()),
        ],
        child: const MaterialApp(home: VendorsListScreen()),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('vendors-controls-button')), findsOneWidget);
    expect(find.byTooltip('Edit vendor'), findsNothing);
    expect(find.byTooltip('Delete vendor'), findsNothing);
    expect(find.textContaining('Add vendors on the web'), findsNothing);

    await tester.tap(find.byKey(const Key('vendors-controls-button')));
    await tester.pumpAndSettle();

    expect(find.text('Sort and filter'), findsOneWidget);
    expect(find.text('Name A-Z'), findsOneWidget);

    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();

    await tester.tap(find.byTooltip('Open quick actions'));
    await tester.pumpAndSettle();

    expect(find.text('Add vendor'), findsOneWidget);

    await tester.tap(find.byTooltip('Close quick actions'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Akron Plumbing').first);
    await tester.pumpAndSettle();

    expect(find.byTooltip('Edit vendor'), findsOneWidget);
    expect(find.byTooltip('View vendor activity'), findsOneWidget);
    expect(find.byTooltip('Delete vendor'), findsOneWidget);
  });

  test('vendors repository sends server-side sort and page params', () async {
    final adapter = _VendorRecordingAdapter();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repository = VendorsRepository(dio);

    final vendors = await repository.list(sort: '-updatedAt', skip: 20);

    expect(adapter.method, 'GET');
    expect(adapter.path, '/vendors');
    expect(adapter.query, containsPair('skip', 20));
    expect(adapter.query, containsPair('take', 50));
    expect(adapter.query, containsPair('sort', '-updatedAt'));
    expect(vendors.single.name, 'Akron Plumbing');
    expect(vendors.single.website, 'https://akron.example');
  });

  test('vendors repository parses server-side page metadata', () async {
    final adapter = _VendorRecordingAdapter();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repository = VendorsRepository(dio);

    final page = await repository.listPage(
      const VendorListQuery(skip: 20, take: 20, search: 'plumb', sort: 'name'),
    );

    expect(adapter.method, 'GET');
    expect(adapter.path, '/vendors/page');
    expect(adapter.query, containsPair('skip', 20));
    expect(adapter.query, containsPair('take', 20));
    expect(adapter.query, containsPair('search', 'plumb'));
    expect(adapter.query, containsPair('sort', 'name'));
    expect(page.totalCount, 41);
    expect(page.skip, 20);
    expect(page.take, 20);
    expect(page.items.single.name, 'Akron Plumbing');
  });

  test(
    'mobile vendors repository sends atomic create update and delete requests',
    () async {
      final adapter = _VendorRecordingAdapter();
      final repository = VendorsRepository(
        Dio(BaseOptions(baseUrl: 'https://example.test'))
          ..httpClientAdapter = adapter,
      );

      await repository.createVendor({'name': 'Akron Plumbing'});
      expect(adapter.method, 'POST');
      expect(adapter.path, '/vendors');
      expect(adapter.headers?['Idempotency-Key'], isNotEmpty);

      await repository.updateVendor(8, {'name': 'Akron Plumbing LLC'});
      expect(adapter.method, 'PATCH');
      expect(adapter.path, '/vendors/8');
      expect(adapter.headers?['Idempotency-Key'], isNotEmpty);

      await repository.deleteVendor(8);
      expect(adapter.method, 'DELETE');
      expect(adapter.path, '/vendors/8');
      expect(adapter.headers?['Idempotency-Key'], isNotEmpty);
    },
  );

  test('vendors screen does not sort list data client-side', () {
    final source = File(
      'lib/features/vendors/vendors_list_screen.dart',
    ).readAsStringSync();

    expect(source, contains('vendorsPageProvider'));
    expect(source, isNot(contains('.sort(')));
  });

  test('vendor form exposes website and includes it in the payload', () {
    final source = File(
      'lib/features/vendors/vendor_form_sheet.dart',
    ).readAsStringSync();

    expect(source, contains("key: const Key('vendor-website-field')"));
    expect(source, contains("'website': _text(_websiteCtrl)"));
  });
}

class _FakeVendorsRepository extends VendorsRepository {
  _FakeVendorsRepository() : super(Dio());

  @override
  Future<VendorPage> listPage([
    VendorListQuery query = const VendorListQuery(),
  ]) async => const VendorPage(
    items: [
      Vendor(
        id: 8,
        name: 'Akron Plumbing',
        serviceType: 'Plumbing',
        phone: '330-555-0199',
        website: 'https://akron.example',
        jobsCompleted: 3,
      ),
    ],
    totalCount: 1,
    skip: 0,
    take: 20,
  );

  @override
  Future<List<Vendor>> list({
    int skip = 0,
    int take = 50,
    String sort = 'name',
  }) async => const [
    Vendor(
      id: 8,
      name: 'Akron Plumbing',
      serviceType: 'Plumbing',
      phone: '330-555-0199',
      website: 'https://akron.example',
      jobsCompleted: 3,
    ),
  ];

  @override
  Future<VendorScorecard> scorecard(int vendorId) async =>
      const VendorScorecard(
        vendorId: 8,
        name: 'Akron Plumbing',
        averageRating: 4.5,
        ratingCount: 2,
        jobsCompleted: 3,
      );
}

class _VendorRecordingAdapter implements HttpClientAdapter {
  String? method;
  String? path;
  Map<String, dynamic>? query;
  Map<String, dynamic>? headers;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    method = options.method;
    path = options.path;
    query = Map<String, dynamic>.from(options.queryParameters);
    headers = Map<String, dynamic>.from(options.headers);

    final item = {
      'id': 8,
      'name': 'Akron Plumbing',
      'serviceType': 'Plumbing',
      'website': 'https://akron.example',
    };
    final body = options.method == 'DELETE'
        ? <String, dynamic>{}
        : options.method != 'GET'
        ? item
        : options.path.endsWith('/page')
        ? {
            'items': [item],
            'totalCount': 41,
            'skip': options.queryParameters['skip'] ?? 0,
            'take': options.queryParameters['take'] ?? 20,
          }
        : [item];

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
