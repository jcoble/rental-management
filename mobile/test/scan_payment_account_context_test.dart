import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
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

  testWidgets('contextual payment review confirms captured account id', (
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
      find.text('Which rental account is this payment for?'),
      findsOneWidget,
    );
    expect(find.text('Create Payment'), findsOneWidget);

    await tester.tap(find.text('Create Payment'));
    await tester.pumpAndSettle();

    expect(scanRepo.lastConfirmId, 63);
    expect(scanRepo.lastOverrides?['tenantAccountId'], 63);
    expect(scanRepo.lastOverrides?['total'], '1250.00');

    await tester.pumpWidget(const SizedBox.shrink());
    await tester.pump(const Duration(seconds: 11));
  });
}

class _FakePaymentScanRepository extends ScanRepository {
  _FakePaymentScanRepository() : super(dio: Dio());

  int? lastConfirmId;
  Map<String, dynamic>? lastOverrides;

  @override
  Future<ScanDraft> getDraft(int id) async {
    return ScanDraft(
      id: id,
      portfolioId: 1,
      targetEntityType: 'Payment',
      status: 'Reviewing',
      fileUrl: '/api/v1/scans/$id/file',
      fields: const [
        ScanField(name: 'total', value: '1250.00', confidence: 0.98),
        ScanField(
          name: 'transaction_date',
          value: '2027-01-05',
          confidence: 0.96,
        ),
        ScanField(name: 'payment_method', value: 'Check', confidence: 0.9),
      ],
      createdAt: DateTime(2027, 1, 5),
      captureContext: const ScanCaptureContext(tenantAccountId: 63),
    );
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
    return TenantAccountOption(
      tenantAccountId: tenantAccountId,
      leaseManagementId: 31,
      relationshipNumber: 'REL-0063',
      propertyName: 'January Flats',
      unitNumber: '5',
      primaryTenantName: 'Jan Five',
    );
  }

  @override
  Future<Map<String, dynamic>?> confirm(
    int id,
    Map<String, dynamic> overrides,
  ) async {
    lastConfirmId = id;
    lastOverrides = Map<String, dynamic>.from(overrides);
    return {'receiptId': 7001, 'tenantAccountId': 63, 'status': 'confirmed'};
  }
}
