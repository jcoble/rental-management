import '../../core/models/unit.dart';

class UnitLeaseFormDefaults {
  const UnitLeaseFormDefaults({
    required this.propertyId,
    required this.unitId,
    required this.propertyLabel,
    required this.unitLabel,
    required this.monthlyRent,
    required this.securityDeposit,
    required this.lateFeeAmount,
    required this.rentDueDay,
    required this.status,
  });

  final int propertyId;
  final int unitId;
  final String propertyLabel;
  final String unitLabel;
  final String monthlyRent;
  final String securityDeposit;
  final String lateFeeAmount;
  final String rentDueDay;
  final String status;
}

UnitLeaseFormDefaults buildUnitLeaseFormDefaults({
  required Unit unit,
  required String propertyName,
}) {
  final propertyLabel = propertyName.trim().isEmpty
      ? 'Property #${unit.propertyId}'
      : propertyName.trim();
  final unitNumber = unit.unitNumber.trim();

  return UnitLeaseFormDefaults(
    propertyId: unit.propertyId,
    unitId: unit.id,
    propertyLabel: propertyLabel,
    unitLabel: unitNumber.isEmpty ? 'Unit #${unit.id}' : 'Unit $unitNumber',
    monthlyRent: unit.marketRent > 0 ? _formatNumber(unit.marketRent) : '',
    securityDeposit: '',
    lateFeeAmount: '75',
    rentDueDay: '1',
    status: 'Active',
  );
}

String _formatNumber(double value) {
  if (value == value.roundToDouble()) return value.toInt().toString();
  return value.toString();
}
