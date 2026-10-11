import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/lease.dart';
import 'package:rental_command/features/leases/leases_repository.dart';
import 'package:rental_command/features/leases/move_in_sheet.dart';
import 'package:rental_command/features/leases/return_possession_sheet.dart';

void main() {
  testWidgets(
    'move in sheet posts confirm move in with deposit fields and idempotency',
    (tester) async {
      final adapter = _RecordingAdapter(
        status: 200,
        body: {
          'leaseManagementId': 44,
          'unitId': 12,
          'possessionGivenAtUtc': '2026-07-16T12:00:00Z',
          'replayed': false,
        },
      );

      await _pumpSheetHost(
        tester,
        adapter,
        open: (context) => showMoveInSheet(context, summary: _summary()),
      );
      await tester.tap(find.text('Open'));
      await tester.pumpAndSettle();

      expect(find.text('Tenant has moved in'), findsOneWidget);
      expect(find.text('Deposit received?'), findsOneWidget);
      expect(find.text('Do a move-in inspection'), findsOneWidget);

      await tester.tap(find.byKey(const Key('move-in-deposit-received')));
      await tester.pumpAndSettle();
      await tester.enterText(
        find.byKey(const Key('move-in-deposit-method')),
        'Check',
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Record move-in'));
      await tester.pumpAndSettle();

      expect(adapter.path, '/lease-managements/44/confirm-move-in');
      expect(adapter.data['unitId'], 12);
      expect(adapter.data['depositPaymentMethodSummary'], 'Check');
      expect(adapter.data['depositEffectiveOn'], '2026-07-16');
      expect(
        (adapter.headers['Idempotency-Key'] ?? '').toString().isNotEmpty,
        isTrue,
      );
    },
  );

  testWidgets('move in sheet maps prerequisite failures to plain sentences', (
    tester,
  ) async {
    final adapter = _RecordingAdapter(
      status: 422,
      body: {
        'error':
            'An executed governing agreement is required before move-in can '
            'be confirmed.',
      },
    );

    await _pumpSheetHost(
      tester,
      adapter,
      open: (context) => showMoveInSheet(context, summary: _summary()),
    );
    await tester.tap(find.text('Open'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Record move-in'));
    await tester.pumpAndSettle();

    expect(find.text("The lease isn't signed yet."), findsOneWidget);
    expect(find.text('Record move-in'), findsOneWidget);
  });

  testWidgets(
    'move out sheet defaults every member and access and sends default reason',
    (tester) async {
      final adapter = _RecordingAdapter(
        status: 200,
        body: {
          'leaseManagementId': 44,
          'unitId': 12,
          'turnoverPeriodId': 5,
          'possessionReturnedAtUtc': '2026-07-16T12:00:00Z',
          'replayed': false,
        },
      );

      await _pumpSheetHost(
        tester,
        adapter,
        open: (context) => showReturnPossessionSheet(
          context,
          summary: _summary(),
          returnContext: _returnContext(),
        ),
      );
      await tester.tap(find.text('Open'));
      await tester.pumpAndSettle();

      expect(find.text('Move-out'), findsOneWidget);
      expect(
        find.text(
          'Everyone on this lease moves out and their app access ends.',
        ),
        findsOneWidget,
      );

      await tester.tap(find.text('Record move-out'));
      await tester.pumpAndSettle();

      expect(adapter.path, '/lease-managements/44/return-possession');
      expect(adapter.data['parties'], [
        {'leaseManagementPartyId': 61, 'disposition': 'EndMembership'},
      ]);
      expect(adapter.data['accesses'], [
        {'tenantUserAccessId': 91, 'disposition': 'RevokeNow'},
      ]);
      expect(adapter.data['turnoverReason'], 'Tenant moved out');
    },
  );

  testWidgets('move out disclosure reveals per person controls', (
    tester,
  ) async {
    final adapter = _RecordingAdapter(status: 200, body: const {});

    await _pumpSheetHost(
      tester,
      adapter,
      open: (context) => showReturnPossessionSheet(
        context,
        summary: _summary(),
        returnContext: _returnContext(),
      ),
    );
    await tester.tap(find.text('Open'));
    await tester.pumpAndSettle();

    expect(find.byKey(const ValueKey('return-party-61')), findsNothing);
    expect(find.byKey(const ValueKey('return-access-91')), findsNothing);

    await tester.tap(find.text('Change what happens to their access'));
    await tester.pumpAndSettle();

    expect(find.byKey(const ValueKey('return-party-61')), findsOneWidget);
    expect(find.byKey(const ValueKey('return-access-91')), findsOneWidget);
    expect(find.text('Ada Tenant · Primary tenant'), findsOneWidget);
  });
}

Future<void> _pumpSheetHost(
  WidgetTester tester,
  _RecordingAdapter adapter, {
  required Future<void> Function(BuildContext context) open,
}) async {
  tester.view.physicalSize = const Size(430, 2400);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  final repository = LeaseManagementsRepository(
    Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter,
  );

  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        leaseManagementsRepositoryProvider.overrideWithValue(repository),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: Builder(
            builder: (context) => Center(
              child: ElevatedButton(
                onPressed: () => open(context),
                child: const Text('Open'),
              ),
            ),
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

LeaseManagementSummary _summary() => LeaseManagementSummary(
  id: 44,
  publicId: 'DEMO-LM-044',
  relationshipNumber: 'REL-44',
  propertyId: 8,
  propertyName: 'Elm Haven',
  unitId: 12,
  unitNumber: 'Main',
  lifecycle: 'Active',
  businessDate: DateTime(2026, 7, 16),
  currentPartyCount: 1,
  currentResidentCount: 1,
  hasReconciliationException: false,
  updatedAt: DateTime(2026, 7, 16),
);

ReturnPossessionContext _returnContext() => ReturnPossessionContext(
  parties: [
    LeaseManagementParty(
      id: 61,
      tenantId: 71,
      tenantName: 'Ada Tenant',
      role: 'PrimaryTenant',
      effectiveFrom: DateTime(2026, 1, 1),
      isCurrent: true,
      canGrantTenantPortalAccess: true,
      guarantorLegalNoticeEligible: false,
    ),
  ],
  activeTenantUserAccesses: [
    ActiveTenantUserAccess(
      id: 91,
      publicId: 'access-91',
      leaseManagementPartyId: 61,
      accessContextId: 5,
      applicationUserId: 15,
      tenantName: 'Ada Tenant',
      userDisplayName: 'Ada Tenant',
      userEmail: 'ada@example.test',
      grantedAt: DateTime(2026, 1, 1),
      reason: 'Portal',
    ),
  ],
);

class _RecordingAdapter implements HttpClientAdapter {
  _RecordingAdapter({required this.status, required this.body});

  final int status;
  final Map<String, dynamic> body;

  String path = '';
  Map<String, dynamic> data = <String, dynamic>{};
  Map<String, dynamic> headers = <String, dynamic>{};

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    path = options.path;
    data = (options.data as Map).cast<String, dynamic>();
    headers = options.headers;
    return ResponseBody.fromString(
      jsonEncode(body),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}
