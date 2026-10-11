enum PropertyWorkspaceSection {
  summary('Summary'),
  rentals('Rentals'),
  ownershipManagement('Ownership & management'),
  propertyWork('Property work'),
  propertyFinances('Property finances'),
  documentsHistory('Documents & history');

  const PropertyWorkspaceSection(this.label);

  final String label;
}

const propertyWorkspaceSections = PropertyWorkspaceSection.values;

enum PropertyWorkspaceDestination { property, unit }

class PropertyWorkspaceEntry {
  const PropertyWorkspaceEntry({
    required this.destination,
    required this.propertyId,
    required this.areas,
    this.unitId,
  });

  final PropertyWorkspaceDestination destination;
  final int propertyId;
  final int? unitId;
  final List<String> areas;

  factory PropertyWorkspaceEntry.fromJson(Map<String, dynamic> json) {
    return PropertyWorkspaceEntry(
      destination: json['destination'] == 'Unit'
          ? PropertyWorkspaceDestination.unit
          : PropertyWorkspaceDestination.property,
      propertyId: (json['propertyId'] as num?)?.toInt() ?? 0,
      unitId: (json['unitId'] as num?)?.toInt(),
      areas: (json['areas'] as List<dynamic>? ?? const [])
          .whereType<String>()
          .toList(growable: false),
    );
  }
}

/// Uses persisted RentalStructure plus the server-selected destination. Unit
/// count and Property type are deliberately not accepted as inputs.
PropertyWorkspaceEntry resolvePropertyWorkspaceEntry({
  required int propertyId,
  required String rentalStructure,
  PropertyWorkspaceEntry? serverEntry,
}) {
  if (rentalStructure == 'SingleRental') {
    return PropertyWorkspaceEntry(
      destination: PropertyWorkspaceDestination.property,
      propertyId: propertyId,
      unitId:
          serverEntry?.destination == PropertyWorkspaceDestination.unit &&
              serverEntry?.propertyId == propertyId
          ? serverEntry?.unitId
          : null,
      areas: serverEntry?.areas ?? const [],
    );
  }

  return PropertyWorkspaceEntry(
    destination: PropertyWorkspaceDestination.property,
    propertyId: propertyId,
    areas: serverEntry?.destination == PropertyWorkspaceDestination.property
        ? serverEntry!.areas
        : const [],
  );
}
