/// Typed view of a lease draft's extracted fields used to PRE-FILL the four guided
/// create-step sheets (Property -> Unit -> Tenant -> Lease). Names mirror LeaseExtractionSchema.cs.
class LeasePrefill {
  LeasePrefill({
    this.propertyId = '',
    this.propertyName = '',
    this.propertyAddress = '',
    this.propertyCity = '',
    this.propertyState = '',
    this.propertyPostalCode = '',
    this.unitId = '',
    this.unitNumber = '',
    this.unitBedrooms = '',
    this.unitBathrooms = '',
    this.unitSquareFeet = '',
    this.tenantName = '',
    this.leaseNumber = '',
    this.startDate = '',
    this.endDate = '',
    this.monthlyRent = '',
    this.securityDeposit = '',
    this.lateFee = '',
    this.rentDueDay = '',
  });

  final String propertyId;
  final String propertyName;
  final String propertyAddress;
  final String propertyCity;
  final String propertyState;
  final String propertyPostalCode;
  final String unitId;
  final String unitNumber;
  final String unitBedrooms;
  final String unitBathrooms;
  final String unitSquareFeet;
  final String tenantName;
  final String leaseNumber;
  final String startDate;
  final String endDate;
  final String monthlyRent;
  final String securityDeposit;
  final String lateFee;
  final String rentDueDay;

  /// Build from the draft's extracted fields list ({name,value,confidence} maps) and
  /// the set of field-names that were auto-filled (non-blank), for the "from your lease" badge.
  static ({LeasePrefill values, Set<String> filled}) fromFields(
      List<Map<String, dynamic>> fields) {
    final byName = <String, String>{};
    final filled = <String>{};
    for (final f in fields) {
      final name = (f['name'] ?? '').toString();
      final value = (f['value'] ?? '').toString().trim();
      byName[name] = value;
      if (value.isNotEmpty) filled.add(name);
    }
    String g(String k) => byName[k] ?? '';
    return (
      values: LeasePrefill(
        propertyId: g('property_id'),
        propertyName: g('property_name'),
        propertyAddress: g('property_address'),
        propertyCity: g('property_city'),
        propertyState: g('property_state'),
        propertyPostalCode: g('property_postal_code'),
        unitId: g('unit_id'),
        unitNumber: g('unit_number'),
        unitBedrooms: g('unit_bedrooms'),
        unitBathrooms: g('unit_bathrooms'),
        unitSquareFeet: g('unit_square_feet'),
        tenantName: g('tenant_name'),
        leaseNumber: g('lease_number'),
        startDate: g('start_date'),
        endDate: g('end_date'),
        monthlyRent: g('monthly_rent'),
        securityDeposit: g('security_deposit'),
        lateFee: g('late_fee'),
        rentDueDay: g('rent_due_day'),
      ),
      filled: filled,
    );
  }
}
