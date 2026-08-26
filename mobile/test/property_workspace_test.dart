import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/properties/property_workspace_sections.dart';

void main() {
  group('SingleRental workspace', () {
    test(
      'opens property details while preserving the server-selected canonical Unit',
      () {
        final entry = resolvePropertyWorkspaceEntry(
          propertyId: 41,
          rentalStructure: 'SingleRental',
          serverEntry: const PropertyWorkspaceEntry(
            destination: PropertyWorkspaceDestination.unit,
            propertyId: 41,
            unitId: 89,
            areas: [],
          ),
        );

        expect(entry.destination, PropertyWorkspaceDestination.property);
        expect(entry.unitId, 89);
        expect(entry.areas, isEmpty);
      },
    );

    test('does not invent a Unit id when setup is incomplete', () {
      final entry = resolvePropertyWorkspaceEntry(
        propertyId: 42,
        rentalStructure: 'SingleRental',
        serverEntry: null,
      );

      expect(entry.destination, PropertyWorkspaceDestination.property);
      expect(entry.unitId, isNull);
    });
  });

  group('MultiRental workspace', () {
    test('exposes the approved six top-level areas', () {
      expect(propertyWorkspaceSections.map((section) => section.label), [
        'Summary',
        'Rentals',
        'Ownership & management',
        'Property work',
        'Property finances',
        'Documents & history',
      ]);
    });

    test('persisted structure wins even while only one Unit exists', () {
      const serverEntry = PropertyWorkspaceEntry(
        destination: PropertyWorkspaceDestination.property,
        propertyId: 43,
        areas: [
          'Summary',
          'Rentals',
          'OwnershipManagement',
          'PropertyWork',
          'PropertyFinances',
          'DocumentsHistory',
        ],
      );
      final entry = resolvePropertyWorkspaceEntry(
        propertyId: 43,
        rentalStructure: 'MultiRental',
        serverEntry: serverEntry,
      );

      expect(entry.destination, PropertyWorkspaceDestination.property);
      expect(entry.unitId, isNull);
      expect(entry.areas, hasLength(6));
    });

    test('stacks property documents and history without nested tabs', () {
      final detail = File(
        'lib/features/properties/property_detail_screen.dart',
      ).readAsStringSync();
      final list = File(
        'lib/features/properties/properties_list_screen.dart',
      ).readAsStringSync();
      final documents = File(
        'lib/features/properties/property_documents_section.dart',
      ).readAsStringSync();
      final repository = File(
        'lib/features/properties/properties_repository.dart',
      ).readAsStringSync();

      expect(detail, contains('PropertyDocumentsSection'));
      expect(detail, contains("entityType: 'Property'"));
      expect(
        detail,
        contains("ValueKey('property-documents-history-surface')"),
      );
      expect(detail, isNot(contains('property-documents-history-tabs')));
      expect(detail, contains("tooltip: 'Edit property'"));
      expect(
        detail,
        isNot(contains('UnitCommandCenterLoaderScreen(unitId: unitId)')),
      );
      expect(list, contains('PropertyDetailScreen(property: property)'));
      expect(list, contains('tooltip: "Open today\'s summary"'));
      expect(documents, contains('uploadPropertyDocument'));
      expect(documents, contains('downloadPropertyDocument'));
      expect(repository, contains("'entityType': 'Property'"));
      expect(repository, contains("'/documents/\$documentId/file'"));
    });
  });
}
