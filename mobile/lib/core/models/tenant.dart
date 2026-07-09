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
  final bool canDelete;
  final String? deleteBlockedReason;

  /// Portal-login state, populated only on the single-tenant GET /tenants/{id}:
  /// 'none' (no login), 'active' (can sign in), or 'disabled' (login locked off).
  /// Null on list/create/update responses, which omit it.
  final String? portalAccess;

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
    this.canDelete = true,
    this.deleteBlockedReason,
    this.portalAccess,
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
      canDelete: json['canDelete'] as bool? ?? true,
      deleteBlockedReason: json['deleteBlockedReason'] as String?,
      portalAccess: json['portalAccess'] as String?,
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

  /// Returns a copy with [portalAccess] replaced. Used after the staff portal
  /// toggle, which returns just the new state rather than a full tenant.
  Tenant copyWith({String? portalAccess}) {
    return Tenant(
      id: id,
      portfolioId: portfolioId,
      firstName: firstName,
      lastName: lastName,
      fullName: fullName,
      email: email,
      phone: phone,
      emergencyContact: emergencyContact,
      dateOfBirth: dateOfBirth,
      notes: notes,
      activeLeaseCount: activeLeaseCount,
      leaseHistoryCount: leaseHistoryCount,
      canDelete: canDelete,
      deleteBlockedReason: deleteBlockedReason,
      portalAccess: portalAccess ?? this.portalAccess,
      createdAt: createdAt,
      updatedAt: updatedAt,
    );
  }
}
