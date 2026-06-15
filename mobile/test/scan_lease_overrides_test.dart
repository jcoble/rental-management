import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/scan/scan_models.dart';
import 'package:rental_command/features/scan/scan_review_screen.dart';

/// Guards the C3 lease-import wire contract: the create-new-property path must
/// drive the server's empty-portfolio bootstrap (no real propertyId/unitId; send
/// propertyId:0 + the extracted/edited address), while the link path must send
/// the chosen ids. This is the invariant that makes "the computer creates the
/// property for you" actually reachable, so it's worth pinning.
void main() {
  group('buildOverridesMap — lease create-new-property (C3)', () {
    test('create mode sends propertyId:0 + address, never a real id', () {
      final overrides = buildOverridesMap(
        editedFields: {'monthly_rent': '1500'},
        isPayment: false,
        isWorkOrder: false,
        isLease: true,
        isPaid: false,
        selectedLeaseId: null,
        createNewProperty: true,
        // Even if a stale id lingers in state, create mode must ignore it.
        selectedPropertyId: 99,
        selectedUnitId: 42,
        selectedTenantId: null,
        newPropertyName: 'Maple Court',
        newPropertyAddress: '123 Maple St',
        newPropertyCity: 'Austin',
      );

      expect(overrides['propertyId'], 0);
      expect(overrides.containsKey('unitId'), isFalse);
      expect(overrides['propertyName'], 'Maple Court');
      expect(overrides['propertyAddress'], '123 Maple St');
      expect(overrides['propertyCity'], 'Austin');
      // Lease terms still flow through.
      expect(overrides['monthly_rent'], '1500');
    });

    test('link mode sends the chosen property + unit ids', () {
      final overrides = buildOverridesMap(
        editedFields: const {},
        isPayment: false,
        isWorkOrder: false,
        isLease: true,
        isPaid: false,
        selectedLeaseId: null,
        createNewProperty: false,
        selectedPropertyId: 7,
        selectedUnitId: 3,
        selectedTenantId: 5,
      );

      expect(overrides['propertyId'], 7);
      expect(overrides['unitId'], 3);
      expect(overrides['tenantId'], 5);
      // Link mode must not send create-property fields.
      expect(overrides.containsKey('propertyName'), isFalse);
      expect(overrides.containsKey('propertyAddress'), isFalse);
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
