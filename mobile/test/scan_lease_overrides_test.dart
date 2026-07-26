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

    test('create mode sends propertyId:0 + address, never a real id', () {
      final overrides = buildOverridesMap(
        editedFields: {'monthly_rent': '1500'},
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
      expect(overrides['rentalStructure'], 'SingleRental');
      expect(overrides.containsKey('unitId'), isFalse);
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
}
