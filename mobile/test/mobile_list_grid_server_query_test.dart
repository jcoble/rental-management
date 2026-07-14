import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/applications/applications_repository.dart';
import 'package:rental_command/features/inspections/inspections_repository.dart';
import 'package:rental_command/features/properties/properties_repository.dart';
import 'package:rental_command/features/tenants/tenants_repository.dart';
import 'package:rental_command/features/units/units_repository.dart';

void main() {
  test('tenants page sends search, sort, and paging to the server', () async {
    final adapter = _RecordingAdapter(_tenantPageJson());
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = TenantsRepository(dio);

    final page = await repo.listPage(
      const TenantListQuery(
        skip: 20,
        take: 20,
        search: 'alex',
        sort: '-updatedAt',
      ),
    );

    expect(adapter.path, '/tenants/page');
    expect(adapter.queryParameters, containsPair('skip', 20));
    expect(adapter.queryParameters, containsPair('take', 20));
    expect(adapter.queryParameters, containsPair('search', 'alex'));
    expect(adapter.queryParameters, containsPair('sort', '-updatedAt'));
    expect(page.totalCount, 1);
    expect(page.items.single.firstName, 'Alex');
  });

  test(
    'tenants page sends lease availability and include-lease filters',
    () async {
      final adapter = _RecordingAdapter(_tenantPageJson());
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = TenantsRepository(dio);

      await repo.listPage(
        const TenantListQuery(
          take: 200,
          sort: 'name',
          availableForLease: true,
          includeLeaseManagementId: 12,
        ),
      );

      expect(adapter.path, '/tenants/page');
      expect(adapter.queryParameters, containsPair('availableForLease', true));
      expect(
        adapter.queryParameters,
        containsPair('includeLeaseManagementId', 12),
      );
    },
  );

  test('properties repository sends available-for-lease filters', () async {
    final adapter = _RecordingAdapter(_propertyListJson());
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = PropertiesRepository(dio);

    await repo.listProperties(availableForLease: true);

    expect(adapter.path, '/properties');
    expect(adapter.queryParameters, containsPair('availableForLease', true));
  });

  test(
    'units repository sends property and lease availability filters',
    () async {
      final adapter = _RecordingAdapter(_unitListJson());
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = PropertiesRepository(dio);

      await repo.listUnits(7, availableForLease: true);

      expect(adapter.path, '/units');
      expect(adapter.queryParameters, containsPair('propertyId', 7));
      expect(adapter.queryParameters, containsPair('availableForLease', true));
    },
  );

  test(
    'applications page sends status, search, sort, and paging to the server',
    () async {
      final adapter = _RecordingAdapter(_applicationPageJson());
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = ApplicationsRepository(dio);

      final page = await repo.listPage(
        const ApplicationListQuery(
          skip: 40,
          take: 20,
          status: 'Submitted',
          unitId: 7,
          search: 'morgan',
          sort: 'name',
        ),
      );

      expect(adapter.path, '/applications/page');
      expect(adapter.queryParameters, containsPair('skip', 40));
      expect(adapter.queryParameters, containsPair('take', 20));
      expect(adapter.queryParameters, containsPair('status', 'Submitted'));
      expect(adapter.queryParameters, containsPair('unitId', 7));
      expect(adapter.queryParameters, containsPair('search', 'morgan'));
      expect(adapter.queryParameters, containsPair('sort', 'name'));
      expect(page.totalCount, 1);
      expect(page.items.single.fullName, 'Morgan Lee');
    },
  );

  test(
    'inspections list sends search, sort, and paging to the server',
    () async {
      final adapter = _RecordingAdapter(_inspectionListJson());
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = InspectionsRepository(dio);

      final page = await repo.listPage(
        const InspectionListQuery(
          skip: 20,
          take: 20,
          search: 'kitchen',
          sort: 'status',
        ),
      );

      expect(adapter.path, '/inspections/page');
      expect(adapter.queryParameters, containsPair('skip', 20));
      expect(adapter.queryParameters, containsPair('take', 20));
      expect(adapter.queryParameters, containsPair('search', 'kitchen'));
      expect(adapter.queryParameters, containsPair('sort', 'status'));
      expect(page.items, hasLength(20));
      expect(page.items.first.id, 33);
      expect(page.totalCount, 40);
      expect(page.hasNext, isFalse);
    },
  );

  test(
    'units health page sends search, sort, filters, and paging to the server',
    () async {
      final adapter = _RecordingAdapter(_unitHealthPageJson());
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = UnitsRepository(dio);

      final page = await repo.listWithHealthPage(
        skip: 20,
        take: 20,
        search: 'cedar',
        sort: '-openWorkOrderCount',
        status: 'Occupied',
        stage: 'Renewal',
      );

      expect(adapter.path, '/units/list-with-health/page');
      expect(adapter.queryParameters, containsPair('skip', 20));
      expect(adapter.queryParameters, containsPair('take', 20));
      expect(adapter.queryParameters, containsPair('search', 'cedar'));
      expect(
        adapter.queryParameters,
        containsPair('sort', '-openWorkOrderCount'),
      );
      expect(adapter.queryParameters, containsPair('status', 'Occupied'));
      expect(adapter.queryParameters, containsPair('stage', 'Renewal'));
      expect(page.totalCount, 1);
      expect(page.items.single.unitNumber, '2A');
    },
  );
}

class _RecordingAdapter implements HttpClientAdapter {
  _RecordingAdapter(this.body);

  final Object body;
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

Map<String, dynamic> _tenantPageJson() => {
  'items': [
    {
      'id': 11,
      'portfolioId': 1,
      'firstName': 'Alex',
      'lastName': 'Rivera',
      'email': 'alex@example.test',
      'activeLeaseCount': 1,
      'createdAt': '2026-06-01T00:00:00.000Z',
      'updatedAt': '2026-06-02T00:00:00.000Z',
    },
  ],
  'totalCount': 1,
  'skip': 20,
  'take': 20,
};

List<Map<String, dynamic>> _propertyListJson() => [
  {
    'id': 7,
    'portfolioId': 1,
    'name': 'Maple Ridge',
    'type': 'SingleFamily',
    'status': 'Active',
    'addressLine1': '100 Main St',
    'city': 'Columbus',
    'state': 'OH',
    'postalCode': '43215',
    'createdAt': '2026-06-01T00:00:00.000Z',
    'updatedAt': '2026-06-02T00:00:00.000Z',
  },
];

List<Map<String, dynamic>> _unitListJson() => [
  {
    'id': 42,
    'propertyId': 7,
    'unitNumber': '4B',
    'bedrooms': 2,
    'bathrooms': 1,
    'marketRent': 1400,
    'status': 'Vacant',
    'createdAt': '2026-06-01T00:00:00.000Z',
    'updatedAt': '2026-06-02T00:00:00.000Z',
  },
];

Map<String, dynamic> _applicationPageJson() => {
  'items': [
    {
      'id': 22,
      'propertyId': 3,
      'unitId': 7,
      'firstName': 'Morgan',
      'lastName': 'Lee',
      'consentGiven': true,
      'status': 'Submitted',
      'submittedAtUtc': '2026-06-03T00:00:00.000Z',
    },
  ],
  'totalCount': 1,
  'skip': 40,
  'take': 20,
};

Map<String, dynamic> _inspectionListJson() => {
  'items': List.generate(
    20,
    (i) => {
      'id': 33 + i,
      'portfolioId': 1,
      'propertyId': 5,
      'type': 'Routine',
      'status': 'Scheduled',
      'scheduledFor': '2026-06-04T00:00:00.000Z',
      'createdAt': '2026-06-01T00:00:00.000Z',
      'updatedAt': '2026-06-02T00:00:00.000Z',
    },
  ),
  'totalCount': 40,
  'skip': 20,
  'take': 20,
};

Map<String, dynamic> _unitHealthPageJson() => {
  'items': [
    {
      'id': 44,
      'propertyId': 5,
      'propertyName': 'Cedar Point Flats',
      'unitNumber': '2A',
      'status': 'Occupied',
      'marketRent': 1450,
      'openWorkOrderCount': 3,
      'docsNeedingReviewCount': 1,
      'simpleStage': 'Renewal',
      'leaseEndsInDays': 45,
    },
  ],
  'totalCount': 1,
  'skip': 20,
  'take': 20,
};
