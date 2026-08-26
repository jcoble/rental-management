import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:rental_command/features/scan/scan_models.dart';
import 'package:rental_command/features/scan/scan_repository.dart';
import 'package:rental_command/features/scan/scan_review_screen.dart';

void main() {
  test(
    'payment scan preserves tenant-account launch context for preselection',
    () {
      final draft = ScanDraft.fromJson({
        'id': 8,
        'portfolioId': 1,
        'targetEntityType': 'Payment',
        'status': 'Reviewing',
        'fileUrl': '/api/v1/scans/8/file',
        'fields': <Map<String, dynamic>>[],
        'createdAt': '2026-07-13T12:00:00Z',
        'captureContext': {'tenantAccountId': 42},
      });

      expect(draft.captureContext?.tenantAccountId, 42);
    },
  );

  test('contextual account lookup uses one exact canonical read', () {
    final repository = File(
      'lib/features/scan/scan_repository.dart',
    ).readAsStringSync();
    final review = File(
      'lib/features/scan/scan_review_screen.dart',
    ).readAsStringSync();

    expect(repository, contains("'/tenant-accounts/\$tenantAccountId'"));
    expect(review, contains('_tenantAccountOptionProvider(exactAccountId)'));
    expect(review, contains('accounts.insert(0, contextualAccount)'));
    expect(review, contains('if (error.statusCode == 404) return null'));
  });

  test('payment context conflict blocks exact contradictory extracted ids', () {
    final draft = ScanDraft(
      id: 67,
      portfolioId: 1,
      targetEntityType: 'Payment',
      status: 'Reviewing',
      fileUrl: '/api/v1/scans/67/file',
      fields: const [
        ScanField(name: 'unit_id', value: '5', confidence: 0.96),
        ScanField(name: 'lease_number', value: 'L005', confidence: 0.94),
        ScanField(name: 'unit_number', value: 'U005', confidence: 0.94),
        ScanField(name: 'payer_name', value: 'Parker Flores', confidence: 0.9),
      ],
      createdAt: DateTime(2027, 1, 5),
      captureContext: const ScanCaptureContext(
        tenantAccountId: 29,
        sourceLabel: 'Nolan · Zenith',
      ),
    );
    const selectedAccount = TenantAccountOption(
      tenantAccountId: 29,
      leaseManagementId: 29,
      propertyId: 12,
      unitId: 4,
      relationshipNumber: 'L029',
      propertyName: 'Zenith',
      unitNumber: 'U004',
      primaryTenantName: 'Nolan',
    );

    expect(
      paymentContextConflictMessage(
        draft: draft,
        selectedTenantAccountId: 29,
        selectedTenantAccount: selectedAccount,
      ),
      contains('different tenant balance'),
    );
    expect(
      paymentContextConflictMessage(
        draft: draft,
        selectedTenantAccountId: 31,
        selectedTenantAccount: const TenantAccountOption(
          tenantAccountId: 31,
          leaseManagementId: 31,
          propertyId: 12,
          unitId: 5,
          relationshipNumber: 'L005',
          propertyName: 'Zenith',
          unitNumber: 'U005',
          primaryTenantName: 'Parker Flores',
        ),
      ),
      isNull,
      reason:
          'Choosing an account whose exact canonical Unit matches the extraction is the conservative correction path.',
    );
  });

  test('payment context conflict uses reviewer-corrected unit ids', () {
    final draft = ScanDraft(
      id: 83,
      portfolioId: 1,
      targetEntityType: 'Payment',
      status: 'Reviewing',
      fileUrl: '/api/v1/scans/83/file',
      fields: const [
        ScanField(name: 'property_id', value: '19', confidence: 0.96),
        ScanField(name: 'unit_id', value: '26', confidence: 0.96),
        ScanField(name: 'payer_name', value: 'Elena Vargas', confidence: 0.9),
      ],
      createdAt: DateTime(2027, 1, 8),
      captureContext: const ScanCaptureContext(
        tenantAccountId: 83,
        sourceLabel: 'Walnut Duplex Unit B · Elena',
      ),
    );
    const selectedAccount = TenantAccountOption(
      tenantAccountId: 83,
      leaseManagementId: 83,
      propertyId: 19,
      unitId: 27,
      relationshipNumber: 'WAL-B',
      propertyName: 'Walnut Duplex',
      unitNumber: 'Unit B',
      primaryTenantName: 'Elena Vargas',
    );

    expect(
      paymentContextConflictMessage(
        draft: draft,
        selectedTenantAccountId: 83,
        selectedTenantAccount: selectedAccount,
      ),
      contains('different tenant balance'),
      reason: 'The original extracted Unit still blocks a genuine mismatch.',
    );
    expect(
      paymentContextConflictMessage(
        draft: draft,
        selectedTenantAccountId: 83,
        selectedTenantAccount: selectedAccount,
        editedFields: const {'unit_id': '27'},
      ),
      isNull,
      reason:
          'The reviewer-corrected Unit is the effective ID validated by the payment gate.',
    );
  });

  test('scan review hides the shell quick action launcher', () {
    final review = File(
      'lib/features/scan/scan_review_screen.dart',
    ).readAsStringSync();

    expect(review, contains('MobileQuickActionHider'));
    expect(review, contains('child: Scaffold('));
    expect(review, contains('paymentConflictMessage == null'));
  });

  testWidgets('reopened reviewing payment draft selects captured account', (
    tester,
  ) async {
    final scanRepo = _FakePaymentScanRepository();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [scanRepositoryProvider.overrideWithValue(scanRepo)],
        child: const MaterialApp(home: ScanReviewScreen(draftId: 63)),
      ),
    );
    await tester.pumpAndSettle();

    expect(
      find.text('Which tenant balance is this payment for?'),
      findsOneWidget,
    );
    expect(find.text('Select a tenant balance'), findsNothing);
    expect(
      find.text('Select a tenant balance above to enable payment creation.'),
      findsNothing,
    );
    expect(find.textContaining('Dana Garcia'), findsOneWidget);

    final createPayment = tester.widget<FilledButton>(
      find.widgetWithText(FilledButton, 'Create Payment'),
    );
    expect(createPayment.onPressed, isNotNull);

    await tester.tap(find.text('Create Payment'));
    await tester.pumpAndSettle();

    expect(scanRepo.lastConfirmId, 63);
    expect(scanRepo.confirmCount, 1);
    expect(scanRepo.exactAccountReads, 1);
    expect(scanRepo.lastOverrides?['tenantAccountId'], 17);
    expect(scanRepo.lastOverrides?['total'], '1250.00');

    await tester.pumpWidget(const SizedBox.shrink());
    await tester.pump(const Duration(seconds: 11));
  });

  testWidgets(
    'root scan route stays mounted and shows confirmed payment result',
    (tester) async {
      final scanRepo = _FakePaymentScanRepository();
      final router = GoRouter(
        initialLocation: '/scan/166',
        routes: [
          GoRoute(
            path: '/scan/:draftId',
            builder: (_, state) => ScanReviewScreen(
              draftId: int.parse(state.pathParameters['draftId']!),
            ),
          ),
        ],
      );
      addTearDown(router.dispose);

      await tester.pumpWidget(
        ProviderScope(
          overrides: [scanRepositoryProvider.overrideWithValue(scanRepo)],
          child: MaterialApp.router(routerConfig: router),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Create Payment'));
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
      expect(router.state.uri.path, '/scan/166');
      expect(
        find.text('This scan has already been confirmed.'),
        findsOneWidget,
      );
      expect(
        find.text('Which tenant balance is this payment for?'),
        findsNothing,
      );
      await tester.scrollUntilVisible(
        find.text('CONFIRMED DETAILS'),
        300,
        scrollable: find.byType(Scrollable).first,
      );
      expect(find.text('CONFIRMED DETAILS'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Create Payment'), findsNothing);

      await tester.pumpWidget(const SizedBox.shrink());
      await tester.pump(const Duration(seconds: 11));
    },
  );

  testWidgets('draft67-shaped payment conflict disables Create Payment', (
    tester,
  ) async {
    final scanRepo = _FakePaymentScanRepository.conflictingDraft67();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [scanRepositoryProvider.overrideWithValue(scanRepo)],
        child: const MaterialApp(home: ScanReviewScreen(draftId: 67)),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.textContaining('different tenant balance'), findsWidgets);
    expect(find.textContaining('Nolan'), findsWidgets);

    final createPayment = tester.widget<FilledButton>(
      find.widgetWithText(FilledButton, 'Create Payment'),
    );
    expect(createPayment.onPressed, isNull);

    expect(scanRepo.confirmCount, 0);

    await tester.pumpWidget(const SizedBox.shrink());
    await tester.pump(const Duration(seconds: 11));
  });

  testWidgets(
    'draft68-shaped matching account metadata enables Create Payment',
    (tester) async {
      final scanRepo = _FakePaymentScanRepository.matchingDraft68();

      await tester.pumpWidget(
        ProviderScope(
          overrides: [scanRepositoryProvider.overrideWithValue(scanRepo)],
          child: const MaterialApp(home: ScanReviewScreen(draftId: 68)),
        ),
      );
      await tester.pumpAndSettle();

    expect(find.textContaining('different tenant balance'), findsNothing);

      final createPayment = tester.widget<FilledButton>(
        find.widgetWithText(FilledButton, 'Create Payment'),
      );
      expect(createPayment.onPressed, isNotNull);
      expect(scanRepo.exactAccountReads, 1);
      expect(scanRepo.confirmCount, 0);

      await tester.tap(find.text('Create Payment'));
      await tester.pumpAndSettle();

      expect(scanRepo.confirmCount, 1);
      expect(scanRepo.lastConfirmId, 68);
      expect(scanRepo.lastOverrides?['tenantAccountId'], 29);

      await tester.pumpWidget(const SizedBox.shrink());
      await tester.pump(const Duration(seconds: 11));
    },
  );
}

class _FakePaymentScanRepository extends ScanRepository {
  _FakePaymentScanRepository()
    : shape = _FakePaymentShape.normal,
      super(dio: Dio());

  _FakePaymentScanRepository.conflictingDraft67()
    : shape = _FakePaymentShape.draft67Conflict,
      super(dio: Dio());

  _FakePaymentScanRepository.matchingDraft68()
    : shape = _FakePaymentShape.draft68Match,
      super(dio: Dio());

  final _FakePaymentShape shape;

  int? lastConfirmId;
  Map<String, dynamic>? lastOverrides;
  int exactAccountReads = 0;
  int confirmCount = 0;
  bool confirmed = false;

  @override
  Future<ScanDraft> getDraft(int id) async {
    if (shape == _FakePaymentShape.draft67Conflict) {
      return ScanDraft.fromJson({
        'id': id,
        'portfolioId': 1,
        'targetEntityType': 'Payment',
        'status': confirmed ? 'Confirmed' : 'Reviewing',
        'fileUrl': '/api/v1/scans/$id/file',
        'fields': [
          {'name': 'total', 'value': '1250.00', 'confidence': 0.98},
          {'name': 'unit_id', 'value': '5', 'confidence': 0.96},
          {'name': 'lease_number', 'value': 'L005', 'confidence': 0.94},
          {'name': 'unit_number', 'value': 'U005', 'confidence': 0.94},
          {'name': 'payer_name', 'value': 'Parker Flores', 'confidence': 0.9},
        ],
        'createdAt': '2027-01-05T12:00:00Z',
        'captureContext': {
          'tenantAccountId': 29,
          'sourceLabel': 'Nolan · Zenith',
        },
      });
    }
    if (shape == _FakePaymentShape.draft68Match) {
      return ScanDraft.fromJson({
        'id': id,
        'portfolioId': 1,
        'targetEntityType': 'Payment',
        'status': 'Reviewing',
        'fileUrl': '/api/v1/scans/$id/file',
        'fields': [
          {'name': 'total', 'value': '1250.00', 'confidence': 0.98},
          {'name': 'property_id', 'value': '26', 'confidence': 0.96},
          {'name': 'unit_id', 'value': '30', 'confidence': 0.96},
        ],
        'createdAt': '2027-01-05T12:00:00Z',
        'captureContext': {
          'tenantAccountId': 29,
          'sourceLabel': 'Nolan · Zenith',
        },
      });
    }

    return ScanDraft.fromJson({
      'id': id,
      'portfolioId': 1,
      'targetEntityType': 'Payment',
      'status': confirmed ? 'Confirmed' : 'Reviewing',
      'fileUrl': '/api/v1/scans/$id/file',
      'fields': [
        {'name': 'total', 'value': '1250.00', 'confidence': 0.98},
        {'name': 'transaction_date', 'value': '2027-01-05', 'confidence': 0.96},
        {'name': 'payment_method', 'value': 'Money order', 'confidence': 0.9},
      ],
      'createdAt': '2027-01-05T12:00:00Z',
      'captureContext': {'tenantAccountId': 17, 'sourceLabel': 'SCN-0096'},
    });
  }

  @override
  Future<Uint8List> downloadFile(int id) async {
    return base64Decode(
      'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=',
    );
  }

  @override
  Future<TenantAccountOptionPage> listTenantAccountOptions({
    String? search,
    bool? closed,
    int skip = 0,
    int take = 25,
  }) async {
    return const TenantAccountOptionPage(
      items: [],
      totalCount: 0,
      skip: 0,
      take: 25,
    );
  }

  @override
  Future<TenantAccountOption> getTenantAccountOption(
    int tenantAccountId,
  ) async {
    exactAccountReads += 1;
    return TenantAccountOption(
      tenantAccountId: tenantAccountId,
      leaseManagementId: switch (shape) {
        _FakePaymentShape.normal => 17,
        _FakePaymentShape.draft67Conflict => 29,
        _FakePaymentShape.draft68Match => 29,
      },
      propertyId: switch (shape) {
        _FakePaymentShape.normal => 7,
        _FakePaymentShape.draft67Conflict => 12,
        _FakePaymentShape.draft68Match => 26,
      },
      unitId: switch (shape) {
        _FakePaymentShape.normal => 17,
        _FakePaymentShape.draft67Conflict => 4,
        _FakePaymentShape.draft68Match => 30,
      },
      relationshipNumber: shape == _FakePaymentShape.normal ? 'L017' : 'L029',
      propertyName: shape == _FakePaymentShape.normal
          ? 'Quarry House'
          : 'Zenith',
      unitNumber: switch (shape) {
        _FakePaymentShape.normal => 'Main',
        _FakePaymentShape.draft67Conflict => 'U004',
        _FakePaymentShape.draft68Match => 'U030',
      },
      primaryTenantName: shape == _FakePaymentShape.normal
          ? 'Dana Garcia'
          : 'Nolan',
    );
  }

  @override
  Future<Map<String, dynamic>?> confirm(
    int id,
    Map<String, dynamic> overrides,
  ) async {
    confirmCount += 1;
    confirmed = true;
    lastConfirmId = id;
    lastOverrides = Map<String, dynamic>.from(overrides);
    return {
      'receiptId': 7001,
      'tenantAccountId': shape == _FakePaymentShape.normal ? 17 : 29,
      'status': 'confirmed',
    };
  }
}

enum _FakePaymentShape { normal, draft67Conflict, draft68Match }
