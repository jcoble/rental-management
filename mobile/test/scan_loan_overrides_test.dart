import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/properties/properties_repository.dart';
import 'package:rental_command/features/scan/scan_models.dart';
import 'package:rental_command/features/scan/scan_repository.dart';
import 'package:rental_command/features/scan/scan_review_screen.dart';

void main() {
  test(
    'loan confirm carries the property context and no expense paid toggle',
    () {
      final overrides = buildOverridesMap(
        editedFields: {
          'lender': 'First Federal',
          'original_amount': '250000',
          'annual_interest_rate_pct': '6.25',
          'notes': 'Statement ending June 2026',
        },
        isPayment: false,
        isWorkOrder: false,
        isLease: false,
        isApplication: false,
        isLoan: true,
        isPaid: true,
        selectedTenantAccountId: null,
        applicationPropertyId: null,
        applicationUnitId: null,
        createNewProperty: false,
        selectedPropertyId: null,
        selectedUnitId: null,
        selectedTenantId: null,
        loanPropertyId: 7,
      );

      expect(overrides['propertyId'], 7);
      expect(overrides['lender'], 'First Federal');
      expect(overrides['original_amount'], '250000');
      expect(overrides.containsKey('is_paid'), isFalse);
    },
  );

  test('ScanDraft identifies loan target type', () {
    final draft = ScanDraft(
      id: 1,
      portfolioId: 1,
      targetEntityType: 'Loan',
      status: 'Reviewing',
      fileUrl: '/api/v1/scans/1/file',
      fields: const [],
      createdAt: DateTime(2026),
    );

    expect(draft.isLoan, isTrue);
    expect(draft.isPayment, isFalse);
  });

  testWidgets('reopened loan review can select a property before confirming', (
    tester,
  ) async {
    final scanRepo = _FakeScanRepository();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          scanRepositoryProvider.overrideWithValue(scanRepo),
          propertiesRepositoryProvider.overrideWithValue(
            _FakePropertiesRepository(),
          ),
        ],
        child: const MaterialApp(home: ScanReviewScreen(draftId: 99)),
      ),
    );
    await tester.pumpAndSettle();

    expect(
      find.text('Which property does this loan belong to?'),
      findsOneWidget,
    );
    expect(
      find.text('Select a property to enable loan creation.'),
      findsOneWidget,
    );

    await tester.ensureVisible(find.byType(DropdownButtonFormField<int>));
    await tester.pumpAndSettle();
    await tester.tap(find.byType(DropdownButtonFormField<int>));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Lake House').last);
    await tester.pumpAndSettle();

    await tester.tap(find.text('Create Loan'));
    await tester.pumpAndSettle();

    expect(scanRepo.lastConfirmId, 99);
    expect(scanRepo.lastOverrides?['propertyId'], 7);
    expect(scanRepo.lastOverrides?['lender'], 'First Federal');

    await tester.pumpWidget(const SizedBox.shrink());
    await tester.pump(const Duration(seconds: 11));
  });
}

class _FakeScanRepository extends ScanRepository {
  _FakeScanRepository() : super(dio: Dio());

  int? lastConfirmId;
  Map<String, dynamic>? lastOverrides;

  @override
  Future<ScanDraft> getDraft(int id) async {
    return ScanDraft(
      id: id,
      portfolioId: 1,
      targetEntityType: 'Loan',
      status: 'Reviewing',
      fileUrl: '/api/v1/scans/$id/file',
      fields: const [
        ScanField(name: 'lender', value: 'First Federal', confidence: 0.94),
        ScanField(name: 'original_amount', value: '250000', confidence: 0.91),
      ],
      createdAt: DateTime(2026),
    );
  }

  @override
  Future<Uint8List> downloadFile(int id) async {
    return base64Decode(
      'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=',
    );
  }

  @override
  Future<Map<String, dynamic>?> confirm(
    int id,
    Map<String, dynamic> overrides,
  ) async {
    lastConfirmId = id;
    lastOverrides = Map<String, dynamic>.from(overrides);
    return {'id': id};
  }
}

class _FakePropertiesRepository extends PropertiesRepository {
  _FakePropertiesRepository() : super(Dio());

  @override
  Future<List<Property>> listProperties({
    bool availableForLease = false,
  }) async => [
    Property(
      id: 7,
      portfolioId: 1,
      name: 'Lake House',
      type: 'SingleFamily',
      rentalStructure: RentalStructure.singleRental,
      status: 'Active',
      addressLine1: '12 Lake Dr',
      city: 'Akron',
      state: 'OH',
      postalCode: '44308',
      unitCount: 1,
      occupiedUnits: 1,
      createdAt: DateTime(2026),
      updatedAt: DateTime(2026),
    ),
  ];
}
