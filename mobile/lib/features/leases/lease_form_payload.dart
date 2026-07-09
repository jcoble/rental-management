Map<String, dynamic> buildLeaseSubmitPayload({
  required bool isEdit,
  required int propertyId,
  required int unitId,
  required List<int> tenantIds,
  Map<String, dynamic>? newTenant,
  required DateTime startDate,
  required DateTime endDate,
  required String monthlyRent,
  required String securityDeposit,
  required String lateFeeAmount,
  required String rentDueDay,
  required String status,
  required String leaseNumber,
  String? rentTrackingStartMode = 'ForwardOnly',
  DateTime? rentTrackingStartDate,
  String openingBalanceAmount = '',
  DateTime? openingBalanceAsOfDate,
  String openingBalanceNote = '',
  String notes = '',
}) {
  if (newTenant == null && tenantIds.isEmpty) {
    throw ArgumentError.value(
      tenantIds,
      'tenantIds',
      'Select existing tenants or supply one new tenant.',
    );
  }
  if (newTenant != null && tenantIds.isNotEmpty) {
    throw ArgumentError(
      'Existing tenant ids and a new tenant are mutually exclusive.',
    );
  }

  final body = <String, dynamic>{
    'unitId': unitId,
    'startDate': _fmtIso(startDate),
    'endDate': _fmtIso(endDate),
    'monthlyRent': double.tryParse(monthlyRent) ?? 0.0,
    'securityDeposit': double.tryParse(securityDeposit) ?? 0.0,
    'lateFeeAmount': double.tryParse(lateFeeAmount) ?? 0.0,
    'rentDueDay': int.tryParse(rentDueDay) ?? 1,
    'status': status,
    'leaseNumber': leaseNumber.trim(),
    'notes': isEdit
        ? notes.trim()
        : (notes.trim().isEmpty ? null : notes.trim()),
  };

  if (newTenant != null) {
    body['newTenant'] = newTenant;
  } else {
    body['tenantId'] = tenantIds.first;
    body['tenantIds'] = tenantIds;
  }

  if (rentTrackingStartMode != null) {
    body['rentTrackingStartMode'] = rentTrackingStartMode;
  }

  if (rentTrackingStartMode == 'CustomCutoffDate' &&
      rentTrackingStartDate != null) {
    body['rentTrackingStartDate'] = _fmtIso(rentTrackingStartDate);
  }

  if (rentTrackingStartMode == 'OpeningBalanceOnly') {
    final amount = double.tryParse(openingBalanceAmount);
    if (amount != null) body['openingBalanceAmount'] = amount;
    if (openingBalanceAsOfDate != null) {
      body['openingBalanceAsOfDate'] = _fmtIso(openingBalanceAsOfDate);
    }
    if (openingBalanceNote.trim().isNotEmpty) {
      body['openingBalanceNote'] = openingBalanceNote.trim();
    }
  }

  if (!isEdit) {
    body['propertyId'] = propertyId;
  }

  return body;
}

Map<String, dynamic> buildInlineTenantPayload({
  required String firstName,
  required String lastName,
  required String email,
  required String phone,
}) {
  return {
    'firstName': firstName.trim(),
    'lastName': lastName.trim(),
    'email': email.trim(),
    'phone': phone.trim(),
  };
}

String _fmtIso(DateTime d) =>
    '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';
