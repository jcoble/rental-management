DateTime _date(dynamic value) =>
    DateTime.tryParse(value as String? ?? '') ?? DateTime(0);

DateTime? _optionalDate(dynamic value) =>
    DateTime.tryParse(value as String? ?? '');

/// One continuous household, possession, and tenant-account relationship.
///
/// This is deliberately not a mutable legal contract. Agreement versions live
/// below the relationship and may change while this identity and account stay
/// continuous.
class LeaseManagementSummary {
  const LeaseManagementSummary({
    required this.id,
    required this.publicId,
    required this.relationshipNumber,
    required this.propertyId,
    required this.propertyName,
    required this.unitId,
    required this.unitNumber,
    required this.lifecycle,
    required this.currentPartyCount,
    required this.currentResidentCount,
    required this.hasReconciliationException,
    required this.updatedAt,
    this.agreementId,
    this.agreementNumber,
    this.agreementStatus,
    this.termStartOn,
    this.termEndOn,
    this.baseRentAmount,
    this.upcomingAgreementId,
    this.tenantAccountId,
    this.primaryTenantId,
    this.primaryTenantName,
    this.plannedPossessionAt,
    this.possessionGivenAt,
    this.plannedMoveOutAt,
    this.possessionReturnedAt,
    this.accountClosedAt,
    this.canceledAt,
    this.endingDisposition = 'Undecided',
  });

  final int id;
  final String publicId;
  final String relationshipNumber;
  final int propertyId;
  final String propertyName;
  final int unitId;
  final String unitNumber;
  final String lifecycle;
  final int? agreementId;
  final String? agreementNumber;
  final String? agreementStatus;
  final DateTime? termStartOn;
  final DateTime? termEndOn;
  final double? baseRentAmount;
  final int? upcomingAgreementId;
  final int? tenantAccountId;
  final int? primaryTenantId;
  final String? primaryTenantName;
  final int currentPartyCount;
  final int currentResidentCount;
  final bool hasReconciliationException;
  final DateTime? plannedPossessionAt;
  final DateTime? possessionGivenAt;
  final DateTime? plannedMoveOutAt;
  final DateTime? possessionReturnedAt;
  final DateTime? accountClosedAt;
  final DateTime? canceledAt;
  final String endingDisposition;
  final DateTime updatedAt;

  bool get isOpen =>
      canceledAt == null &&
      (possessionReturnedAt == null || accountClosedAt == null);

  factory LeaseManagementSummary.fromJson(Map<String, dynamic> json) {
    return LeaseManagementSummary(
      id: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
      publicId: json['leaseManagementPublicId'] as String? ?? '',
      relationshipNumber: json['relationshipNumber'] as String? ?? '',
      propertyId: (json['propertyId'] as num?)?.toInt() ?? 0,
      propertyName: json['propertyName'] as String? ?? '',
      unitId: (json['unitId'] as num?)?.toInt() ?? 0,
      unitNumber: json['unitNumber'] as String? ?? '',
      lifecycle: json['lifecycle'] as String? ?? '',
      agreementId: (json['leaseAgreementId'] as num?)?.toInt(),
      agreementNumber: json['agreementNumber'] as String?,
      agreementStatus: json['agreementStatus'] as String?,
      termStartOn: _optionalDate(json['termStartOn']),
      termEndOn: _optionalDate(json['termEndOn']),
      baseRentAmount: (json['baseRentAmount'] as num?)?.toDouble(),
      upcomingAgreementId: (json['upcomingLeaseAgreementId'] as num?)?.toInt(),
      tenantAccountId: (json['tenantAccountId'] as num?)?.toInt(),
      primaryTenantId: (json['primaryTenantId'] as num?)?.toInt(),
      primaryTenantName: json['primaryTenantName'] as String?,
      currentPartyCount: (json['currentPartyCount'] as num?)?.toInt() ?? 0,
      currentResidentCount:
          (json['currentResidentCount'] as num?)?.toInt() ?? 0,
      hasReconciliationException:
          json['hasReconciliationException'] as bool? ?? false,
      plannedPossessionAt: _optionalDate(json['plannedPossessionAtUtc']),
      possessionGivenAt: _optionalDate(json['possessionGivenAtUtc']),
      plannedMoveOutAt: _optionalDate(json['plannedMoveOutAtUtc']),
      possessionReturnedAt: _optionalDate(json['possessionReturnedAtUtc']),
      accountClosedAt: _optionalDate(json['accountClosedAtUtc']),
      canceledAt: _optionalDate(json['canceledAtUtc']),
      endingDisposition: json['endingDisposition'] as String? ?? 'Undecided',
      updatedAt: _date(json['updatedAtUtc']),
    );
  }
}

class LeaseManagementParty {
  const LeaseManagementParty({
    required this.id,
    required this.tenantId,
    required this.tenantName,
    required this.role,
    required this.effectiveFrom,
    this.email,
    this.phone,
    this.effectiveThrough,
  });

  final int id;
  final int tenantId;
  final String tenantName;
  final String role;
  final String? email;
  final String? phone;
  final DateTime effectiveFrom;
  final DateTime? effectiveThrough;

  factory LeaseManagementParty.fromJson(Map<String, dynamic> json) =>
      LeaseManagementParty(
        id: (json['leaseManagementPartyId'] as num?)?.toInt() ?? 0,
        tenantId: (json['tenantId'] as num?)?.toInt() ?? 0,
        tenantName: json['tenantName'] as String? ?? '',
        role: json['role'] as String? ?? '',
        email: json['email'] as String?,
        phone: json['phone'] as String?,
        effectiveFrom: _date(json['effectiveFrom']),
        effectiveThrough: _optionalDate(json['effectiveThrough']),
      );
}

class LeaseManagementDetail {
  const LeaseManagementDetail({
    required this.summary,
    required this.parties,
    required this.agreementCount,
    required this.addendumCount,
    required this.legalArtifactCount,
  });

  final LeaseManagementSummary summary;
  final List<LeaseManagementParty> parties;
  final int agreementCount;
  final int addendumCount;
  final int legalArtifactCount;

  factory LeaseManagementDetail.fromJson(Map<String, dynamic> json) =>
      LeaseManagementDetail(
        summary: LeaseManagementSummary.fromJson(
          (json['summary'] as Map?)?.cast<String, dynamic>() ?? const {},
        ),
        parties: (json['parties'] as List? ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(LeaseManagementParty.fromJson)
            .toList(),
        agreementCount: (json['agreementCount'] as num?)?.toInt() ?? 0,
        addendumCount: (json['addendumCount'] as num?)?.toInt() ?? 0,
        legalArtifactCount: (json['legalArtifactCount'] as num?)?.toInt() ?? 0,
      );
}

class LegalArtifactSummary {
  const LegalArtifactSummary({
    required this.id,
    required this.fileName,
    required this.contentType,
    required this.byteLength,
  });

  final int id;
  final String fileName;
  final String contentType;
  final int byteLength;

  factory LegalArtifactSummary.fromJson(Map<String, dynamic> json) =>
      LegalArtifactSummary(
        id: (json['legalDocumentArtifactId'] as num?)?.toInt() ?? 0,
        fileName: json['fileName'] as String? ?? '',
        contentType: json['contentType'] as String? ?? '',
        byteLength: (json['byteLength'] as num?)?.toInt() ?? 0,
      );
}

class LeaseAgreementHistory {
  const LeaseAgreementHistory({
    required this.id,
    required this.versionNumber,
    required this.agreementNumber,
    required this.changeType,
    required this.termType,
    required this.termStartOn,
    required this.governingFromOn,
    required this.baseRentAmount,
    required this.status,
    required this.isGoverning,
    this.termEndOn,
    this.issuedArtifact,
    this.executedArtifact,
    this.issuedAt,
    this.fullyExecutedAt,
    this.voidedAt,
  });

  final int id;
  final int versionNumber;
  final String agreementNumber;
  final String changeType;
  final String termType;
  final DateTime termStartOn;
  final DateTime? termEndOn;
  final DateTime governingFromOn;
  final double baseRentAmount;
  final String status;
  final bool isGoverning;
  final LegalArtifactSummary? issuedArtifact;
  final LegalArtifactSummary? executedArtifact;
  final DateTime? issuedAt;
  final DateTime? fullyExecutedAt;
  final DateTime? voidedAt;

  bool get isDraft => status.toLowerCase() == 'draft';

  factory LeaseAgreementHistory.fromJson(Map<String, dynamic> json) {
    LegalArtifactSummary? artifact(String key) {
      final value = json[key];
      return value is Map<String, dynamic>
          ? LegalArtifactSummary.fromJson(value)
          : null;
    }

    return LeaseAgreementHistory(
      id: (json['leaseAgreementId'] as num?)?.toInt() ?? 0,
      versionNumber: (json['versionNumber'] as num?)?.toInt() ?? 0,
      agreementNumber: json['agreementNumber'] as String? ?? '',
      changeType: json['changeType'] as String? ?? '',
      termType: json['termType'] as String? ?? '',
      termStartOn: _date(json['termStartOn']),
      termEndOn: _optionalDate(json['termEndOn']),
      governingFromOn: _date(json['governingFromOn']),
      baseRentAmount: (json['baseRentAmount'] as num?)?.toDouble() ?? 0,
      status: json['agreementStatus'] as String? ?? '',
      isGoverning: json['isGoverning'] as bool? ?? false,
      issuedArtifact: artifact('issuedArtifact'),
      executedArtifact: artifact('executedArtifact'),
      issuedAt: _optionalDate(json['issuedAtUtc']),
      fullyExecutedAt: _optionalDate(json['fullyExecutedAtUtc']),
      voidedAt: _optionalDate(json['voidedAtUtc']),
    );
  }
}

/// Tenant-safe projection returned only from `/portal/leases`.
class PortalLeaseRelationship {
  const PortalLeaseRelationship({
    required this.leaseManagementId,
    required this.propertyName,
    required this.unitNumber,
    required this.lifecycle,
    this.tenantAccountId,
    this.agreement,
  });

  final int leaseManagementId;
  final int? tenantAccountId;
  final String propertyName;
  final String unitNumber;
  final String lifecycle;
  final PortalLeaseAgreement? agreement;

  factory PortalLeaseRelationship.fromJson(Map<String, dynamic> json) =>
      PortalLeaseRelationship(
        leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
        tenantAccountId: (json['tenantAccountId'] as num?)?.toInt(),
        propertyName: json['propertyName'] as String? ?? '',
        unitNumber: json['unitNumber'] as String? ?? '',
        lifecycle: json['lifecycle'] as String? ?? '',
        agreement: json['agreement'] is Map<String, dynamic>
            ? PortalLeaseAgreement.fromJson(
                json['agreement'] as Map<String, dynamic>,
              )
            : null,
      );
}

class PortalLeaseAgreement {
  const PortalLeaseAgreement({
    required this.id,
    required this.agreementNumber,
    required this.status,
    required this.termStartOn,
    required this.baseRentAmount,
    required this.securityDepositObligation,
    required this.lateFeeAmount,
    required this.rentDueDay,
    this.termEndOn,
  });

  final int id;
  final String agreementNumber;
  final String status;
  final DateTime termStartOn;
  final DateTime? termEndOn;
  final double baseRentAmount;
  final double securityDepositObligation;
  final double lateFeeAmount;
  final int rentDueDay;

  factory PortalLeaseAgreement.fromJson(Map<String, dynamic> json) =>
      PortalLeaseAgreement(
        id: (json['leaseAgreementId'] as num?)?.toInt() ?? 0,
        agreementNumber: json['agreementNumber'] as String? ?? '',
        status: json['agreementStatus'] as String? ?? '',
        termStartOn: _date(json['termStartOn']),
        termEndOn: _optionalDate(json['termEndOn']),
        baseRentAmount: (json['baseRentAmount'] as num?)?.toDouble() ?? 0,
        securityDepositObligation:
            (json['securityDepositObligation'] as num?)?.toDouble() ?? 0,
        lateFeeAmount: (json['lateFeeAmount'] as num?)?.toDouble() ?? 0,
        rentDueDay: (json['rentDueDay'] as num?)?.toInt() ?? 1,
      );
}
