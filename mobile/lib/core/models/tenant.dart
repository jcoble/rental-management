class Tenant {
  final int id;
  final int portfolioId;
  final String firstName;
  final String lastName;
  final String? fullName;
  final String? email;
  final String? phone;
  final String? emergencyContact;
  final DateTime? dateOfBirth;
  final String? notes;
  final int? activeLeaseCount;
  final int? leaseHistoryCount;
  final int? currentPropertyId;
  final String? currentPropertyName;
  final int? currentUnitId;
  final String? currentUnitNumber;
  final bool canDelete;
  final String? deleteBlockedReason;

  final DateTime createdAt;
  final DateTime updatedAt;

  const Tenant({
    required this.id,
    required this.portfolioId,
    required this.firstName,
    required this.lastName,
    this.fullName,
    this.email,
    this.phone,
    this.emergencyContact,
    this.dateOfBirth,
    this.notes,
    this.activeLeaseCount,
    this.leaseHistoryCount,
    this.currentPropertyId,
    this.currentPropertyName,
    this.currentUnitId,
    this.currentUnitNumber,
    this.canDelete = true,
    this.deleteBlockedReason,
    required this.createdAt,
    required this.updatedAt,
  });

  factory Tenant.fromJson(Map<String, dynamic> json) {
    return Tenant(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num).toInt(),
      firstName: json['firstName'] as String? ?? '',
      lastName: json['lastName'] as String? ?? '',
      fullName: json['fullName'] as String?,
      email: json['email'] as String?,
      phone: json['phone'] as String?,
      emergencyContact: json['emergencyContact'] as String?,
      dateOfBirth: DateTime.tryParse(json['dateOfBirth'] as String? ?? ''),
      notes: json['notes'] as String?,
      activeLeaseCount: (json['activeLeaseCount'] as num?)?.toInt(),
      leaseHistoryCount: (json['leaseHistoryCount'] as num?)?.toInt(),
      currentPropertyId: (json['currentPropertyId'] as num?)?.toInt(),
      currentPropertyName: json['currentPropertyName'] as String?,
      currentUnitId: (json['currentUnitId'] as num?)?.toInt(),
      currentUnitNumber: json['currentUnitNumber'] as String?,
      canDelete: json['canDelete'] as bool? ?? true,
      deleteBlockedReason: json['deleteBlockedReason'] as String?,
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt:
          DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
    );
  }

  Map<String, dynamic> toJson() {
    return {
      'portfolioId': portfolioId,
      'firstName': firstName,
      'lastName': lastName,
      if (email != null) 'email': email,
      if (phone != null) 'phone': phone,
      if (emergencyContact != null) 'emergencyContact': emergencyContact,
      if (dateOfBirth != null)
        'dateOfBirth': dateOfBirth!.toIso8601String().split('T').first,
      if (notes != null) 'notes': notes,
    };
  }
}
