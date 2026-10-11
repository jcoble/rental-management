import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/api/dio_client.dart';
import 'package:rental_command/core/time/app_clock.dart';
import 'package:rental_command/features/owner_reports/owner_reports_repository.dart';
import 'package:rental_command/features/owner_reports/owner_reports_screen.dart';

void main() {
  test('owner distributions parse the bare-array response', () async {
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = _BareDistributionAdapter();

    final distributions = await OwnerReportsRepository(
      dio,
    ).listDistributions(ownerEntityId: 4, year: 2026);

    expect(distributions, hasLength(1));
    expect(distributions.single.id, 12);
    expect(distributions.single.ownerName, 'Northstar LLC');
  });

  test(
    'monthly reports run required catalog endpoints with monthly params',
    () async {
      final adapter = _RecordingReportsAdapter();
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final container = ProviderContainer(
        overrides: [dioProvider.overrideWithValue(dio)],
      );
      addTearDown(container.dispose);

      await container
          .read(monthlyReportsProvider.notifier)
          .load(month: DateTime.utc(2027, 2));

      expect(adapter.requests.first.path, '/reports/catalog');
      expect(adapter.requests.map((request) => request.path).skip(1), [
        '/reports/cash-flow',
        '/reports/property-pnl',
        '/reports/cash-flow',
        '/reports/general-ledger',
        '/reports/rent-roll',
        '/reports/rent-ledger',
        '/reports/delinquency',
        '/reports/owner-distributions',
        '/reports/security-deposits',
      ]);
      expect(
        adapter.requestFor('/reports/cash-flow').queryParameters,
        containsPair('from', '2027-02-01'),
      );
      expect(
        adapter.requestFor('/reports/cash-flow').queryParameters,
        containsPair('to', '2027-02-28'),
      );
      expect(
        adapter.requestFor('/reports/property-pnl').queryParameters,
        containsPair('from', '2027-02-01'),
      );
      expect(
        adapter.requestFor('/reports/general-ledger').queryParameters,
        containsPair('to', '2027-02-28'),
      );
      expect(
        adapter.requestFor('/reports/rent-ledger').queryParameters,
        containsPair('from', '2027-02-01'),
      );
      expect(
        adapter.requestFor('/reports/security-deposits').queryParameters,
        containsPair('skip', 0),
      );
      expect(
        adapter.requestFor('/reports/security-deposits').queryParameters,
        containsPair('take', 20),
      );
      expect(
        adapter.requestFor('/reports/security-deposits').queryParameters,
        containsPair('sort', 'property'),
      );
      expect(
        adapter.requestFor('/reports/owner-distributions').queryParameters,
        containsPair('year', 2027),
      );

      await container
          .read(monthlyReportsProvider.notifier)
          .pageReport('security-deposit-register', 1);

      expect(
        adapter.lastRequestFor('/reports/security-deposits').queryParameters,
        containsPair('skip', 20),
      );
      expect(
        adapter.lastRequestFor('/reports/security-deposits').queryParameters,
        containsPair('take', 20),
      );
      expect(
        adapter.lastRequestFor('/reports/security-deposits').queryParameters,
        containsPair('sort', 'property'),
      );
    },
  );

  test('monthly report values render counts and years without currency', () {
    expect(ownerReportDisplayValue(2027, key: 'year'), '2027');
    expect(ownerReportDisplayValue(12, key: 'leaseCount'), '12');
    expect(ownerReportDisplayValue(20, key: 'take'), '20');
    expect(ownerReportDisplayValue(1234, key: 'totalIncome'), r'$1,234.00');
    expect(ownerReportDisplayValue(45.5, key: 'totalNet'), r'$45.50');
  });

  testWidgets('app clock failures remain visible and retryable', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          appNowProvider.overrideWith(
            (ref) async => throw StateError('simulation clock failed'),
          ),
        ],
        child: const MaterialApp(home: OwnerReportsScreen()),
      ),
    );

    await tester.pump();
    await tester.pump();

    expect(find.textContaining('simulation clock failed'), findsOneWidget);
    expect(find.text('Retry'), findsOneWidget);
  });

  test('monthly report renderers display representative DTO row values', () {
    expect(
      _renderedReportLines('income-expense-statement', {
        'months': [
          {'label': 'Feb 2027', 'income': 1200, 'expense': 300, 'net': 900},
        ],
      }),
      containsAll(['Feb 2027', r'Income $1,200.00', r'Net $900.00']),
    );
    expect(
      _renderedReportLines('cash-flow', {
        'months': [
          {'label': 'Feb 2027', 'income': 1200, 'expense': 300, 'net': 900},
        ],
      }),
      containsAll(['Feb 2027', r'Expenses $300.00']),
    );
    expect(
      _renderedReportLines('property-pnl-summary', {
        'rows': [
          {
            'propertyName': 'Maple Ridge',
            'income': 1200,
            'expense': 200,
            'net': 1000,
          },
        ],
      }),
      containsAll(['Maple Ridge', r'Income $1,200.00', r'Net $1,000.00']),
    );
    expect(
      _renderedReportLines('general-ledger', {
        'entries': [
          {
            'date': '2027-02-05',
            'type': 'Expense',
            'description': 'Paint',
            'category': 'Repairs',
            'amount': -80,
            'runningBalance': 1120,
          },
        ],
      }),
      containsAll(['2027-02-05 · Expense · Paint', 'Category Repairs']),
    );
    expect(
      _renderedReportLines('rent-roll', {
        'rows': [
          {
            'propertyName': 'Maple Ridge',
            'unitNumber': '2B',
            'tenantName': 'Verify Tenant',
            'monthlyRent': 1450,
            'securityDeposit': 1450,
            'statusName': 'Active',
          },
        ],
      }),
      containsAll(['Maple Ridge · Unit 2B', r'Rent $1,450.00']),
    );
    expect(
      _renderedReportLines('rent-ledger', {
        'leases': [
          {
            'propertyName': 'Maple Ridge',
            'unitNumber': '2B',
            'tenantName': 'Verify Tenant',
            'totalCharged': 1450,
            'totalCredits': 1200,
            'balance': 250,
            'entries': [
              {
                'date': '2027-02-01',
                'type': 'Charge',
                'description': 'February rent',
                'charge': 1450,
                'credit': 0,
                'balance': 1450,
              },
            ],
          },
        ],
      }),
      containsAll([
        'Maple Ridge · Unit 2B · Verify Tenant',
        '2027-02-01 · Charge · February rent',
        r'Balance $1,450.00',
      ]),
    );
    expect(
      _renderedReportLines('delinquency', {
        'rows': [
          {
            'propertyName': 'Maple Ridge',
            'unitNumber': '2B',
            'tenantName': 'Verify Tenant',
            'total': 250,
            'oldestOverdueDays': 18,
            'buckets': {
              'current': 0,
              'days31To60': 250,
              'days61To90': 0,
              'over90': 0,
            },
          },
        ],
      }),
      containsAll(['Oldest days 18', r'31-60 $250.00']),
    );
    expect(
      _renderedReportLines('security-deposit-register', {
        'rows': [
          {
            'propertyName': 'Maple Ridge',
            'unitNumber': '2B',
            'tenantName': 'Verify Tenant',
            'held': 1450,
            'deductions': 75,
            'returned': 0,
            'currentBalance': 1375,
          },
        ],
      }),
      containsAll([r'Held $1,450.00', r'Current balance $1,375.00']),
    );
    expect(
      _renderedReportLines('owner-distributions', {
        'rows': [
          {
            'ownerName': 'Owner LLC',
            'netToOwner': 1000,
            'totalDistributed': 600,
            'undistributed': 400,
          },
        ],
      }),
      containsAll(['Owner LLC', r'Undistributed $400.00']),
    );
  });
}

class _RecordingReportsAdapter implements HttpClientAdapter {
  final requests = <_RecordedRequest>[];

  _RecordedRequest requestFor(String path) =>
      requests.firstWhere((request) => request.path == path);

  _RecordedRequest lastRequestFor(String path) =>
      requests.lastWhere((request) => request.path == path);

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(
      _RecordedRequest(
        options.path,
        Map<String, dynamic>.from(options.queryParameters),
      ),
    );

    final body = options.path == '/reports/catalog'
        ? _catalogJson()
        : <String, dynamic>{'generatedAt': '2027-02-28T00:00:00Z'};
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

class _BareDistributionAdapter implements HttpClientAdapter {
  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    return ResponseBody.fromString(
      jsonEncode([
        {
          'id': 12,
          'ownerEntityId': 4,
          'ownerName': 'Northstar LLC',
          'date': '2026-08-20',
          'amount': 725,
          'method': 'Ach',
        },
      ]),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

class _RecordedRequest {
  const _RecordedRequest(this.path, this.queryParameters);

  final String path;
  final Map<String, dynamic> queryParameters;
}

Map<String, dynamic> _catalogJson() => {
  'categories': [
    {
      'title': 'Accounting',
      'reports': [
        _report(
          key: 'income-expense-statement',
          endpoint: '/api/v1/reports/cash-flow',
          params: ['from', 'to', 'propertyIds'],
        ),
        _report(
          key: 'property-pnl-summary',
          endpoint: '/api/v1/reports/property-pnl',
          params: ['from', 'to', 'propertyIds'],
        ),
        _report(
          key: 'cash-flow',
          endpoint: '/api/v1/reports/cash-flow',
          params: ['from', 'to', 'propertyIds'],
        ),
        _report(
          key: 'general-ledger',
          endpoint: '/api/v1/reports/general-ledger',
          params: ['from', 'to', 'propertyId'],
        ),
        _report(
          key: 'schedule-e',
          endpoint: '/api/v1/accounting/schedule-e',
          params: ['year'],
          external: true,
        ),
      ],
    },
    {
      'title': 'Rent & Payments',
      'reports': [
        _report(
          key: 'rent-roll',
          endpoint: '/api/v1/reports/rent-roll',
          params: ['propertyIds'],
        ),
        _report(
          key: 'rent-ledger',
          endpoint: '/api/v1/reports/rent-ledger',
          params: ['from', 'to', 'propertyIds'],
        ),
        _report(
          key: 'delinquency',
          endpoint: '/api/v1/reports/delinquency',
          params: ['propertyIds'],
        ),
      ],
    },
    {
      'title': 'Owners',
      'reports': [
        _report(
          key: 'owner-distributions',
          endpoint: '/api/v1/reports/owner-distributions',
          params: ['year'],
        ),
      ],
    },
    {
      'title': 'Operations',
      'reports': [
        _report(
          key: 'security-deposit-register',
          endpoint: '/api/v1/reports/security-deposits',
          params: ['propertyIds', 'skip', 'take', 'sort'],
        ),
      ],
    },
  ],
};

Map<String, dynamic> _report({
  required String key,
  required String endpoint,
  required List<String> params,
  bool external = false,
}) => {
  'key': key,
  'title': key,
  'description': key,
  'endpoint': endpoint,
  'params': params,
  'external': external,
};

List<String> _renderedReportLines(String key, Map<String, dynamic> data) {
  final sections = ownerReportDisplaySections(
    ReportRunResult(
      entry: ReportCatalogEntry(
        key: key,
        title: key,
        description: key,
        endpoint: '/api/v1/reports/$key',
        params: const [],
        external: false,
        categoryTitle: 'Test',
      ),
      data: data,
    ),
  );
  return [
    for (final section in sections) ...[
      section.title,
      for (final row in section.rows) ...[row.title, ...row.details],
    ],
  ];
}
