class LeaseTenantSummary {
  final int id;
  final String name;
  final String? email;
  final String? phone;
  final bool isPrimary;

  const LeaseTenantSummary({
    required this.id,
    required this.name,
    this.email,
    this.phone,
    this.isPrimary = false,
  });

  factory LeaseTenantSummary.fromJson(Map<String, dynamic> json) {
    return LeaseTenantSummary(
      id: (json['id'] as num?)?.toInt() ?? 0,
      name: json['name'] as String? ?? '',
      email: json['email'] as String?,
      phone: json['phone'] as String?,
      isPrimary: json['isPrimary'] as bool? ?? false,
    );
  }
}

class Lease {
  final int id;
  final int portfolioId;
  final int propertyId;
  final int unitId;
  final int tenantId;
  final List<int> tenantIds;
  final List<LeaseTenantSummary> tenants;
  final String leaseNumber;
  final String status;
  final DateTime startDate;
  final DateTime endDate;
  final DateTime? moveInDate;
  final DateTime? moveOutDate;
  final double monthlyRent;
  final double securityDeposit;
  final double lateFeeAmount;
  final int rentDueDay;
  final DateTime? rentTrackingStartDate;
  final String? notes;
  final String? tenantName;
  final String? propertyName;
  final String? unitNumber;
  final DateTime createdAt;
  final DateTime updatedAt;

  const Lease({
    required this.id,
    required this.portfolioId,
    required this.propertyId,
    required this.unitId,
    required this.tenantId,
    this.tenantIds = const [],
    this.tenants = const [],
    required this.leaseNumber,
    required this.status,
    required this.startDate,
    required this.endDate,
    this.moveInDate,
    this.moveOutDate,
    required this.monthlyRent,
    required this.securityDeposit,
    required this.lateFeeAmount,
    required this.rentDueDay,
    this.rentTrackingStartDate,
    this.notes,
    this.tenantName,
    this.propertyName,
    this.unitNumber,
    required this.createdAt,
    required this.updatedAt,
  });

  factory Lease.fromJson(Map<String, dynamic> json) {
    final parsedTenants = (json['tenants'] as List<dynamic>? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(LeaseTenantSummary.fromJson)
        .where((tenant) => tenant.id > 0)
        .toList();
    final parsedTenantIds = (json['tenantIds'] as List<dynamic>? ?? const [])
        .whereType<num>()
        .map((id) => id.toInt())
        .where((id) => id > 0)
        .toList();
    final primaryTenantId =
        (json['tenantId'] as num?)?.toInt() ??
        (parsedTenantIds.isNotEmpty
            ? parsedTenantIds.first
            : (parsedTenants.isNotEmpty ? parsedTenants.first.id : 0));
    final normalizedTenantIds = parsedTenantIds.isNotEmpty
        ? parsedTenantIds
        : <int>[if (primaryTenantId > 0) primaryTenantId];
    final joinedTenantName = parsedTenants
        .map((tenant) => tenant.name.trim())
        .where((name) => name.isNotEmpty)
        .join(', ');

    return Lease(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num).toInt(),
      propertyId: (json['propertyId'] as num).toInt(),
      unitId: (json['unitId'] as num).toInt(),
      tenantId: primaryTenantId,
      tenantIds: normalizedTenantIds,
      tenants: parsedTenants,
      leaseNumber: json['leaseNumber'] as String? ?? '',
      status: json['status'] as String? ?? '',
      startDate:
          DateTime.tryParse(json['startDate'] as String? ?? '') ?? DateTime(0),
      endDate:
          DateTime.tryParse(json['endDate'] as String? ?? '') ?? DateTime(0),
      moveInDate: DateTime.tryParse(json['moveInDate'] as String? ?? ''),
      moveOutDate: DateTime.tryParse(json['moveOutDate'] as String? ?? ''),
      monthlyRent: (json['monthlyRent'] as num?)?.toDouble() ?? 0,
      securityDeposit: (json['securityDeposit'] as num?)?.toDouble() ?? 0,
      lateFeeAmount: (json['lateFeeAmount'] as num?)?.toDouble() ?? 0,
      rentDueDay: (json['rentDueDay'] as num?)?.toInt() ?? 1,
      rentTrackingStartDate: DateTime.tryParse(
        json['rentTrackingStartDate'] as String? ?? '',
      ),
      notes: json['notes'] as String?,
      tenantName:
          json['tenantName'] as String? ??
          (joinedTenantName.isEmpty ? null : joinedTenantName),
      propertyName: json['propertyName'] as String?,
      unitNumber: json['unitNumber'] as String?,
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt:
          DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
    );
  }

  Map<String, dynamic> toJson() {
    final outgoingTenantIds = tenantIds.isNotEmpty ? tenantIds : [tenantId];
    return {
      'portfolioId': portfolioId,
      'propertyId': propertyId,
      'unitId': unitId,
      'tenantId': outgoingTenantIds.first,
      'tenantIds': outgoingTenantIds,
      'status': status,
      'startDate': startDate.toIso8601String().split('T').first,
      'endDate': endDate.toIso8601String().split('T').first,
      if (moveInDate != null)
        'moveInDate': moveInDate!.toIso8601String().split('T').first,
      if (moveOutDate != null)
        'moveOutDate': moveOutDate!.toIso8601String().split('T').first,
      'monthlyRent': monthlyRent,
      'securityDeposit': securityDeposit,
      'lateFeeAmount': lateFeeAmount,
      'rentDueDay': rentDueDay,
      if (rentTrackingStartDate != null)
        'rentTrackingStartDate': rentTrackingStartDate!
            .toIso8601String()
            .split('T')
            .first,
      if (notes != null) 'notes': notes,
    };
  }
}
