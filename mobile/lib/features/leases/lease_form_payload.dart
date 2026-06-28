Map<String, dynamic> buildLeaseSubmitPayload({
  required bool isEdit,
  required int propertyId,
  required int unitId,
  required List<int> tenantIds,
  required DateTime startDate,
  required DateTime endDate,
  required String monthlyRent,
  required String securityDeposit,
  required String lateFeeAmount,
  required String rentDueDay,
  required String status,
  required String leaseNumber,
}) {
  final body = <String, dynamic>{
    'unitId': unitId,
    'tenantId': tenantIds.first,
    'tenantIds': tenantIds,
    'startDate': _fmtIso(startDate),
    'endDate': _fmtIso(endDate),
    'monthlyRent': double.tryParse(monthlyRent) ?? 0.0,
    'securityDeposit': double.tryParse(securityDeposit) ?? 0.0,
    'lateFeeAmount': double.tryParse(lateFeeAmount) ?? 0.0,
    'rentDueDay': int.tryParse(rentDueDay) ?? 1,
    'status': status,
  };

  if (!isEdit) {
    body['propertyId'] = propertyId;
    body['leaseNumber'] = leaseNumber;
    body['rentTrackingStartMode'] = 'ForwardOnly';
  }

  return body;
}

String _fmtIso(DateTime d) =>
    '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';
