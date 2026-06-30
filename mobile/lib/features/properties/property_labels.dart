const propertyUnitTypes = {'SingleFamily', 'Condo', 'Townhome'};

bool isPropertyUnitType(String? type) => propertyUnitTypes.contains(type);

String formatPropertyType(String type) {
  switch (type) {
    case 'SingleFamily':
      return 'Single-family';
    case 'MultiFamily':
      return 'Multi-family';
    case 'Condo':
      return 'Condo';
    case 'Townhome':
      return 'Townhome';
    case 'Apartment':
      return 'Apartment';
    case 'Commercial':
      return 'Commercial';
    default:
      return type.isEmpty ? 'Property' : type;
  }
}
