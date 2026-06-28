import '../../core/models/models.dart';

String _clean(String? value) => value?.trim() ?? '';

String _unitLabel(String? value) {
  final unit = _clean(value);
  if (unit.isEmpty) return '';
  if (unit.toLowerCase().startsWith('unit ')) return unit;
  return 'Unit $unit';
}

String _homeLabel({String? propertyName, String? unitNumber}) {
  final property = _clean(propertyName);
  final unit = _unitLabel(unitNumber);
  if (property.isNotEmpty && unit.isNotEmpty) return '$property · $unit';
  if (property.isNotEmpty) return property;
  if (unit.isNotEmpty) return unit;
  return '';
}

String _leaseFallback({String? tenantName, String? leaseNumber}) {
  final tenant = _clean(tenantName);
  final number = _clean(leaseNumber);
  if (tenant.isNotEmpty) return tenant;
  if (number.isNotEmpty) return 'Lease $number';
  return '';
}

String formatPaymentLeaseDisplay(Payment payment) {
  final home = _homeLabel(
    propertyName: payment.propertyName,
    unitNumber: payment.unitNumber,
  );
  if (home.isNotEmpty) return home;
  return _leaseFallback(
    tenantName: payment.tenantName,
    leaseNumber: payment.leaseNumber,
  );
}

String formatLeasePickerLabel(Lease lease) {
  final home = _homeLabel(
    propertyName: lease.propertyName,
    unitNumber: lease.unitNumber,
  );
  final tenant = _clean(lease.tenantName);
  final number = _clean(lease.leaseNumber);
  if (home.isNotEmpty && tenant.isNotEmpty) return '$home — $tenant';
  if (home.isNotEmpty) return home;
  if (tenant.isNotEmpty) return tenant;
  if (number.isNotEmpty) return 'Lease $number';
  return 'Lease';
}
