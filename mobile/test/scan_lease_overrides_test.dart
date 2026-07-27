import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/scan/guided_rental_flow.dart';
import 'package:rental_command/features/scan/scan_models.dart';
import 'package:rental_command/features/scan/scan_review_screen.dart';

/// Guards the C3 lease-import wire contract: the create-new-property path must
/// drive the server's empty-portfolio bootstrap (no real propertyId/unitId; send
/// propertyId:0 + the extracted/edited address), while the link path must send
/// the chosen ids. This is the invariant that makes "the computer creates the
/// property for you" actually reachable, so it's worth pinning.
void main() {
  test('guided signed import exposes and sends reviewed possession date', () {
    final source = File(
      'lib/features/scan/guided_rental_flow.dart',
    ).readAsStringSync();

    expect(source, contains("o['possessionGivenAtUtc']"));
    expect(source, contains("Text('Possession given')"));
    expect(source, contains("ValueKey('guided-rental-possession-given-date')"));
  });

  test('lease review exposes every tenant and unit confirmation override', () {
    final source = File(
      'lib/features/scan/scan_review_screen.dart',
    ).readAsStringSync();

    for (final field in const [
      'tenant_name',
      'tenant_email',
      'tenant_phone',
      'tenant_emergency_contact',
      'unit_number',
      'unit_bedrooms',
      'unit_bathrooms',
      'unit_square_feet',
      'possession_given_at',
    ]) {
      expect(
        RegExp("'$field'").allMatches(source).length,
        greaterThanOrEqualTo(2),
        reason:
            '$field must be both recognized and rendered in the lease review',
      );
    }

    expect(source, contains("'unit_square_feet',"));
    expect(source, contains("'tenant_email': 'Tenant email'"));
    expect(source, contains("'possession_given_at': 'Possession date'"));
  });

  test('payment confirmation sends tenantAccountId and no legacy leaseId', () {
    final overrides = buildOverridesMap(
      editedFields: const {'total': '1200'},
      isPayment: true,
      isWorkOrder: false,
      isLease: false,
      isApplication: false,
      isLoan: false,
      isPaid: true,
      selectedTenantAccountId: 77,
      applicationPropertyId: null,
      applicationUnitId: null,
      createNewProperty: false,
      selectedPropertyId: null,
      selectedUnitId: null,
      selectedTenantId: null,
      loanPropertyId: null,
    );

    expect(overrides['tenantAccountId'], 77);
    expect(overrides.containsKey('leaseId'), isFalse);
  });

  group('buildOverridesMap — lease create-new-property (C3)', () {
    test('signed import sends explicit disposition without a template', () {
      final overrides = buildOverridesMap(
        editedFields: const {'lease_number': 'LEASE-100'},
        isPayment: false,
        isWorkOrder: false,
        isLease: true,
        isApplication: false,
        isLoan: false,
        applicationPropertyId: null,
        applicationUnitId: null,
        isPaid: false,
        selectedTenantAccountId: null,
        createNewProperty: false,
        selectedPropertyId: 7,
        selectedUnitId: 3,
        selectedTenantId: 5,
        loanPropertyId: null,
        leaseReviewDisposition: LeaseScanReviewDisposition.alreadyFullySigned,
        // A stale selection must not leak into an executed-artifact import.
        documentTemplateId: 91,
      );

      expect(overrides['reviewDisposition'], 'AlreadyFullySigned');
      expect(overrides.containsKey('documentTemplateId'), isFalse);
    });

    test(
      'unsigned import defaults to supplied lease without a template id',
      () {
        final overrides = buildOverridesMap(
          editedFields: const {'lease_number': 'LEASE-101'},
          isPayment: false,
          isWorkOrder: false,
          isLease: true,
          isApplication: false,
          isLoan: false,
          applicationPropertyId: null,
          applicationUnitId: null,
          isPaid: false,
          selectedTenantAccountId: null,
          createNewProperty: false,
          selectedPropertyId: 7,
          selectedUnitId: 3,
          selectedTenantId: 5,
          loanPropertyId: null,
          leaseReviewDisposition: LeaseScanReviewDisposition.needsSignatures,
        );

        expect(overrides['reviewDisposition'], 'NeedsSignatures');
        expect(overrides.containsKey('documentTemplateId'), isFalse);
      },
    );

    test('unsigned import can opt into an active custom template', () {
      final overrides = buildOverridesMap(
        editedFields: const {'lease_number': 'LEASE-102'},
        isPayment: false,
        isWorkOrder: false,
        isLease: true,
        isApplication: false,
        isLoan: false,
        applicationPropertyId: null,
        applicationUnitId: null,
        isPaid: false,
        selectedTenantAccountId: null,
        createNewProperty: false,
        selectedPropertyId: 7,
        selectedUnitId: 3,
        selectedTenantId: 5,
        loanPropertyId: null,
        leaseReviewDisposition: LeaseScanReviewDisposition.needsSignatures,
        documentTemplateId: 91,
      );

      expect(overrides['reviewDisposition'], 'NeedsSignatures');
      expect(overrides['documentTemplateId'], 91);
    });

    test('create mode clears the captured rental chain', () {
      final overrides = buildOverridesMap(
        editedFields: {
          'monthly_rent': '1500',
          // A Unit-command-center scan carries these extracted relationship
          // ids. The explicit create target must win over all of them.
          'tenant_id': '51',
          'lease_management_id': '52',
          'tenant_account_id': '53',
          'lease_agreement_id': '54',
        },
        isPayment: false,
        isWorkOrder: false,
        isLease: true,
        isApplication: false,
        isLoan: false,
        applicationPropertyId: null,
        applicationUnitId: null,
        isPaid: false,
        selectedTenantAccountId: null,
        createNewProperty: true,
        // Even if a stale id lingers in state, create mode must ignore it.
        selectedPropertyId: 99,
        selectedUnitId: 42,
        selectedTenantId: null,
        loanPropertyId: null,
        newPropertyRentalStructure: RentalStructure.singleRental,
        newPropertyName: 'Maple Court',
        newPropertyAddress: '123 Maple St',
        newPropertyCity: 'Austin',
      );

      expect(overrides['propertyId'], 0);
      expect(overrides['unitId'], 0);
      expect(overrides['tenantId'], 0);
      expect(overrides['leaseManagementId'], 0);
      expect(overrides['tenantAccountId'], 0);
      expect(overrides['leaseAgreementId'], 0);
      expect(overrides['rentalStructure'], 'SingleRental');
      expect(overrides['propertyName'], 'Maple Court');
      expect(overrides['propertyAddress'], '123 Maple St');
      expect(overrides['propertyCity'], 'Austin');
      // Lease terms still flow through.
      expect(overrides['monthly_rent'], '1500');
    });

    test('create mode rejects a missing rental structure', () {
      expect(
        () => buildOverridesMap(
          editedFields: const {},
          isPayment: false,
          isWorkOrder: false,
          isLease: true,
          isApplication: false,
          isLoan: false,
          applicationPropertyId: null,
          applicationUnitId: null,
          isPaid: false,
          selectedTenantAccountId: null,
          createNewProperty: true,
          selectedPropertyId: null,
          selectedUnitId: null,
          selectedTenantId: null,
          loanPropertyId: null,
          newPropertyName: 'Maple Court',
        ),
        throwsArgumentError,
      );
    });

    test('link mode sends the chosen property + unit ids', () {
      final overrides = buildOverridesMap(
        editedFields: const {
          'rentalStructure': 'SingleRental',
          'rental_structure': 'MultiRental',
        },
        isPayment: false,
        isWorkOrder: false,
        isLease: true,
        isApplication: false,
        isLoan: false,
        applicationPropertyId: null,
        applicationUnitId: null,
        isPaid: false,
        selectedTenantAccountId: null,
        createNewProperty: false,
        selectedPropertyId: 7,
        selectedUnitId: 3,
        selectedTenantId: 5,
        loanPropertyId: null,
        // A stale create-mode choice must never override persisted structure.
        newPropertyRentalStructure: RentalStructure.multiRental,
      );

      expect(overrides['propertyId'], 7);
      expect(overrides['unitId'], 3);
      expect(overrides['tenantId'], 5);
      // Link mode must not send create-property fields.
      expect(overrides.containsKey('propertyName'), isFalse);
      expect(overrides.containsKey('propertyAddress'), isFalse);
      expect(overrides.containsKey('rentalStructure'), isFalse);
      expect(overrides.containsKey('rental_structure'), isFalse);
    });

    test('link mode can create a new unit under an existing property', () {
      final overrides = buildOverridesMap(
        editedFields: const {'unit_number': 'B', 'monthly_rent': '1900'},
        isPayment: false,
        isWorkOrder: false,
        isLease: true,
        isApplication: false,
        isLoan: false,
        applicationPropertyId: null,
        applicationUnitId: null,
        isPaid: false,
        selectedTenantAccountId: null,
        createNewProperty: false,
        selectedPropertyId: 21,
        selectedUnitId: null,
        selectedTenantId: null,
        loanPropertyId: null,
      );

      expect(overrides['propertyId'], 21);
      expect(overrides['unitId'], 0);
      expect(overrides['unit_number'], 'B');
      expect(overrides['monthly_rent'], '1900');
    });

    test('existing-property picker searches as the user types', () {
      final source = File(
        'lib/features/scan/scan_review_screen.dart',
      ).readAsStringSync();
      final pickerStart = source.indexOf('class _LinkPropertyFieldsState');
      final pickerEnd = source.indexOf(
        'class _CreatePropertyFields',
        pickerStart,
      );
      final pickerSource = source.substring(pickerStart, pickerEnd);

      expect(pickerSource, contains('onChanged: _searchChanged'));
      expect(pickerSource, contains("search: _search"));
      expect(pickerSource, contains('Timer(const Duration(milliseconds: 300)'));
      expect(pickerSource, contains('Create the Unit from this lease scan'));
    });
  });

  group('guided rental bootstrap target', () {
    test('sends the explicit one-rental selection', () {
      expect(buildGuidedRentalTargetOverrides(RentalStructure.singleRental), {
        'propertyId': 0,
        'rentalStructure': 'SingleRental',
      });
    });

    test('sends the explicit multiple-rentals selection', () {
      expect(buildGuidedRentalTargetOverrides(RentalStructure.multiRental), {
        'propertyId': 0,
        'rentalStructure': 'MultiRental',
      });
    });
  });

  group('LeaseImportProposal.fromJson', () {
    test('parses link/create actions and existing id', () {
      final proposal = LeaseImportProposal.fromJson({
        'property': {
          'action': 'link',
          'existingId': 12,
          'label': '123 Maple St',
          'detail': 'Austin',
        },
        'unit': {'action': 'create', 'label': 'Unit 1'},
      });

      expect(proposal.property.isLink, isTrue);
      expect(proposal.property.existingId, 12);
      expect(proposal.unit.isCreate, isTrue);
      expect(proposal.unit.label, 'Unit 1');
    });

    test('defaults an unknown/missing action to select', () {
      final proposal = LeaseImportProposal.fromJson({
        'property': {'label': 'x'},
        'unit': <String, dynamic>{},
      });

      expect(proposal.property.isSelect, isTrue);
      expect(proposal.unit.isSelect, isTrue);
    });
  });

  group('expenseScanReadinessMessage', () {
    test('lists required expense facts without optional accounting fields', () {
      final message = expenseScanReadinessMessage(
        draft: _expenseDraft(
          fields: const [
            ScanField(name: 'tax', value: '0', confidence: 1),
            ScanField(name: 'tip', value: '0', confidence: 1),
            ScanField(name: 'discount', value: '0', confidence: 1),
            ScanField(name: 'shipping', value: '0', confidence: 1),
          ],
        ),
      );

      expect(message, contains('vendor'));
      expect(message, contains('transaction date'));
      expect(message, contains('positive total or subtotal'));
      expect(message, contains('category'));
      expect(message, contains('property, unit, or work order'));
      expect(message, isNot(contains('payment method')));
      expect(message, isNot(contains('card last 4')));
      expect(message, isNot(contains('due date')));
      expect(message, isNot(contains('line items')));
      expect(message, isNot(contains('allocations')));
    });

    test('accepts reviewed minimum with zero optional amount components', () {
      final message = expenseScanReadinessMessage(
        draft: _expenseDraft(
          captureContext: const ScanCaptureContext(propertyId: 44),
          fields: const [
            ScanField(name: 'vendor_name', value: 'Supply Shop', confidence: 1),
            ScanField(
              name: 'transaction_date',
              value: '2026-07-12',
              confidence: 1,
            ),
            ScanField(name: 'total', value: '0', confidence: 1),
            ScanField(name: 'subtotal', value: '157.00', confidence: 1),
            ScanField(name: 'tax', value: '0', confidence: 1),
            ScanField(name: 'tip', value: '0', confidence: 1),
            ScanField(name: 'discount', value: '0', confidence: 1),
            ScanField(name: 'shipping', value: '0', confidence: 1),
            ScanField(name: 'category', value: 'Other', confidence: 1),
            ScanField(name: 'payment_method', value: 'ACH', confidence: 1),
          ],
        ),
      );

      expect(message, isNull);
    });

    test('uses edited fields to clear extraction gaps', () {
      final message = expenseScanReadinessMessage(
        draft: _expenseDraft(
          fields: const [
            ScanField(name: 'vendor_name', value: '', confidence: 0.2),
            ScanField(name: 'total', value: '0', confidence: 0.2),
          ],
        ),
        editedFields: const {
          'vendor_name': 'Hardware House',
          'transaction_date': '2026-07-12',
          'total': r'$1,230.45',
          'category': 'Repairs',
          'property_id': '8',
        },
      );

      expect(message, isNull);
    });
  });
}

ScanDraft _expenseDraft({
  List<ScanField> fields = const [],
  ScanCaptureContext? captureContext,
}) {
  return ScanDraft(
    id: 1,
    portfolioId: 1,
    targetEntityType: 'Expense',
    status: 'Reviewing',
    fileUrl: '',
    fields: fields,
    createdAt: DateTime(2026, 7, 12),
    captureContext: captureContext,
  );
}
