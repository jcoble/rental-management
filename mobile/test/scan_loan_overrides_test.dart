import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/properties/properties_repository.dart';
import 'package:rental_command/features/properties/property_loans_repository.dart';
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

  test('loan match confirm carries existing loan and payment ids', () {
    final overrides = buildOverridesMap(
      editedFields: {
        'lender': 'First Federal',
        'statement_principal_amount': '825.25',
        'statement_interest_amount': '653.00',
        'statement_escrow_amount': '420.00',
        'opening_principal_balance': '196500.00',
        'statementEffectiveDate': '2026-07-01',
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
      existingLoanId: 42,
      existingLoanPaymentId: 501,
      includeLoanStatementPaymentOverrides: true,
    );

    expect(overrides['propertyId'], 7);
    expect(overrides['existingLoanId'], 42);
    expect(overrides['existingLoanPaymentId'], 501);
    expect(overrides['statementPrincipalAmount'], '825.25');
    expect(overrides['statementInterestAmount'], '653.00');
    expect(overrides['statementEscrowAmount'], '420.00');
    expect(overrides['current_balance'], '196500.00');
    expect(overrides['currentBalance'], '196500.00');
    expect(overrides['statementTotalAmount'], '1898.25');
    expect(overrides['statementEffectiveDate'], '2026-07-01');
    expect(overrides.containsKey('is_paid'), isFalse);
  });

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

  testWidgets('loan review defaults to matching an existing loan payment', (
    tester,
  ) async {
    final scanRepo = _FakeScanRepository();
    final loansRepo = _FakePropertyLoansRepository();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          scanRepositoryProvider.overrideWithValue(scanRepo),
          propertiesRepositoryProvider.overrideWithValue(
            _FakePropertiesRepository(),
          ),
          propertyLoansRepositoryProvider.overrideWithValue(loansRepo),
        ],
        child: const MaterialApp(home: ScanReviewScreen(draftId: 99)),
      ),
    );
    await tester.pumpAndSettle();

    await tester.scrollUntilVisible(
      find.text('Which property does this loan belong to?'),
      300,
    );
    await tester.pumpAndSettle();

    expect(
      find.text('Which property does this loan belong to?'),
      findsOneWidget,
    );
    expect(find.text('Select a property to find its loans.'), findsOneWidget);
    expect(find.text('Match existing payment'), findsOneWidget);
    expect(find.text('Add new loan'), findsOneWidget);

    await tester.ensureVisible(find.byType(DropdownButtonFormField<int>));
    await tester.pumpAndSettle();
    await tester.tap(find.byType(DropdownButtonFormField<int>));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Lake House').last);
    await tester.pumpAndSettle();

    await tester.scrollUntilVisible(find.text('Existing loan'), 200);
    await tester.pumpAndSettle();
    await tester.tap(find.byType(DropdownButtonFormField<int>).last);
    await tester.pumpAndSettle();
    await tester.tap(find.textContaining('First Federal').last);
    await tester.pumpAndSettle();

    await tester.drag(find.byType(ListView), const Offset(0, -300));
    await tester.pumpAndSettle();
    await tester.tap(find.byType(DropdownButtonFormField<int>).last);
    await tester.pumpAndSettle();
    await tester.tap(find.textContaining('Paid').last);
    await tester.pumpAndSettle();

    await tester.drag(find.byType(ListView), const Offset(0, -300));
    await tester.pumpAndSettle();
    expect(find.text('P&I total'), findsOneWidget);
    expect(find.text(r'$1,478.25'), findsWidgets);
    expect(find.text('Full cash total'), findsOneWidget);
    expect(find.text(r'$1,898.25'), findsWidgets);
    expect(find.text('Derived balance after'), findsOneWidget);
    expect(find.text(r'$195,674.75'), findsWidgets);

    expect(loansRepo.lastListedPropertyId, 7);
    expect(loansRepo.lastLoanRequest, {
      'propertyId': 7,
      'skip': 0,
      'take': 50,
      'sort': '-createdAt',
    });
    expect(loansRepo.lastPaymentLoanId, 42);
    expect(loansRepo.lastPaymentRequest, {
      'loanId': 42,
      'status': null,
      'sort': 'dueDate',
      'skip': 0,
      'take': 50,
    });

    await tester.tap(find.text('Match & record payment'));
    await tester.pumpAndSettle();

    expect(scanRepo.lastConfirmId, 99);
    expect(scanRepo.lastOverrides?['propertyId'], 7);
    expect(scanRepo.lastOverrides?['existingLoanId'], 42);
    expect(scanRepo.lastOverrides?['existingLoanPaymentId'], 501);
    expect(scanRepo.lastOverrides?['statementPrincipalAmount'], '825.25');
    expect(scanRepo.lastOverrides?['statementInterestAmount'], '653.00');
    expect(scanRepo.lastOverrides?['statementEscrowAmount'], '420.00');
    expect(scanRepo.lastOverrides?['current_balance'], '196500.00');
    expect(scanRepo.lastOverrides?['currentBalance'], '196500.00');
    expect(scanRepo.lastOverrides?['statementTotalAmount'], '1898.25');
    expect(scanRepo.lastOverrides?['statementEffectiveDate'], '2026-07-01');

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
        ScanField(
          name: 'statement_principal_amount',
          value: '825.25',
          confidence: 0.92,
        ),
        ScanField(
          name: 'statement_interest_amount',
          value: '653.00',
          confidence: 0.93,
        ),
        ScanField(
          name: 'statement_escrow_amount',
          value: '420.00',
          confidence: 0.9,
        ),
        ScanField(
          name: 'opening_principal_balance',
          value: '196500.00',
          confidence: 0.89,
        ),
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

class _FakePropertyLoansRepository extends PropertyLoansRepository {
  _FakePropertyLoansRepository() : super(Dio());

  int? lastListedPropertyId;
  Map<String, Object?>? lastLoanRequest;
  int? lastPaymentLoanId;
  Map<String, Object?>? lastPaymentRequest;

  @override
  Future<List<PropertyLoan>> listLoans({
    required int propertyId,
    int skip = 0,
    int? take,
    String sort = '-createdAt',
    String? search,
  }) async {
    lastListedPropertyId = propertyId;
    lastLoanRequest = {
      'propertyId': propertyId,
      'skip': skip,
      'take': take,
      'sort': sort,
    };
    return [
      PropertyLoan.fromJson({
        'id': 42,
        'portfolioId': 1,
        'propertyId': propertyId,
        'propertyName': 'Lake House',
        'lender': 'First Federal',
        'originalAmount': 250000,
        'currentBalance': 196500,
        'annualInterestRatePct': 5.875,
        'termMonths': 360,
        'startDate': '2024-01-15T00:00:00.000Z',
        'dayOfMonthDue': 1,
        'monthlyPrincipalInterest': 1478.25,
        'monthlyEscrow': 420,
        'escrowCoversTaxes': true,
        'escrowCoversInsurance': false,
        'status': 'Active',
        'notes': 'Imported statement verified.',
        'createdAt': '2026-07-01T00:00:00.000Z',
        'updatedAt': '2026-07-01T00:00:00.000Z',
        'testId': 'loan-42',
      }),
    ];
  }

  @override
  Future<List<LoanPayment>> getLoanPayments(
    int loanId, {
    String? status,
    String? sort,
    int? skip,
    int? take,
  }) async {
    lastPaymentLoanId = loanId;
    lastPaymentRequest = {
      'loanId': loanId,
      'status': status,
      'sort': sort,
      'skip': skip,
      'take': take,
    };
    return [
      LoanPayment.fromJson({
        'id': 501,
        'loanId': loanId,
        'periodKey': '2026-07',
        'dueDate': '2026-07-01T00:00:00.000Z',
        'paidDate': null,
        'interestAmount': 653,
        'principalAmount': 825.25,
        'escrowAmount': 420,
        'totalAmount': 1898.25,
        'balanceAfter': 195674.75,
        'status': 'Paid',
        'paymentDoesNotCoverInterest': false,
      }),
    ];
  }
}
