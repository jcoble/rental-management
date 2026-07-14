DateTime _date(dynamic value) =>
    DateTime.tryParse(value as String? ?? '') ?? DateTime(0);

DateTime? _optionalDate(dynamic value) =>
    DateTime.tryParse(value as String? ?? '');

String _dateOnly(DateTime value) => value.toIso8601String().split('T').first;

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
    required this.businessDate,
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
    this.upcomingAgreementNumber,
    this.upcomingAgreementStatus,
    this.upcomingTermStartOn,
    this.upcomingTermEndOn,
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
    this.endingDispositionDecidedAt,
    this.endingDispositionDecidedByUserId,
    this.noticeGivenAt,
  });

  final int id;
  final String publicId;
  final String relationshipNumber;
  final int propertyId;
  final String propertyName;
  final int unitId;
  final String unitNumber;
  final String lifecycle;
  final DateTime businessDate;
  final int? agreementId;
  final String? agreementNumber;
  final String? agreementStatus;
  final DateTime? termStartOn;
  final DateTime? termEndOn;
  final double? baseRentAmount;
  final int? upcomingAgreementId;
  final String? upcomingAgreementNumber;
  final String? upcomingAgreementStatus;
  final DateTime? upcomingTermStartOn;
  final DateTime? upcomingTermEndOn;
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
  final DateTime? endingDispositionDecidedAt;
  final int? endingDispositionDecidedByUserId;
  final DateTime? noticeGivenAt;
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
      businessDate: _date(json['businessDate']),
      agreementId: (json['leaseAgreementId'] as num?)?.toInt(),
      agreementNumber: json['agreementNumber'] as String?,
      agreementStatus: json['agreementStatus'] as String?,
      termStartOn: _optionalDate(json['termStartOn']),
      termEndOn: _optionalDate(json['termEndOn']),
      baseRentAmount: (json['baseRentAmount'] as num?)?.toDouble(),
      upcomingAgreementId: (json['upcomingLeaseAgreementId'] as num?)?.toInt(),
      upcomingAgreementNumber: json['upcomingAgreementNumber'] as String?,
      upcomingAgreementStatus: json['upcomingAgreementStatus'] as String?,
      upcomingTermStartOn: _optionalDate(json['upcomingTermStartOn']),
      upcomingTermEndOn: _optionalDate(json['upcomingTermEndOn']),
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
      endingDispositionDecidedAt: _optionalDate(
        json['endingDispositionDecidedAtUtc'],
      ),
      endingDispositionDecidedByUserId:
          (json['endingDispositionDecidedByUserId'] as num?)?.toInt(),
      noticeGivenAt: _optionalDate(json['noticeGivenAtUtc']),
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
    required this.isCurrent,
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
  final bool isCurrent;

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
        isCurrent: json['isCurrent'] as bool? ?? false,
      );
}

class ActiveTenantUserAccess {
  const ActiveTenantUserAccess({
    required this.id,
    required this.publicId,
    required this.leaseManagementPartyId,
    required this.accessContextId,
    required this.applicationUserId,
    required this.tenantName,
    required this.userDisplayName,
    required this.userEmail,
    required this.grantedAt,
    required this.reason,
  });

  final int id;
  final String publicId;
  final int leaseManagementPartyId;
  final int accessContextId;
  final int applicationUserId;
  final String tenantName;
  final String userDisplayName;
  final String userEmail;
  final DateTime grantedAt;
  final String reason;

  factory ActiveTenantUserAccess.fromJson(Map<String, dynamic> json) =>
      ActiveTenantUserAccess(
        id: (json['tenantUserAccessId'] as num?)?.toInt() ?? 0,
        publicId: json['publicId'] as String? ?? '',
        leaseManagementPartyId:
            (json['leaseManagementPartyId'] as num?)?.toInt() ?? 0,
        accessContextId: (json['accessContextId'] as num?)?.toInt() ?? 0,
        applicationUserId: (json['applicationUserId'] as num?)?.toInt() ?? 0,
        tenantName: json['tenantName'] as String? ?? '',
        userDisplayName: json['userDisplayName'] as String? ?? '',
        userEmail: json['userEmail'] as String? ?? '',
        grantedAt: _date(json['grantedAtUtc']),
        reason: json['reason'] as String? ?? '',
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

class LeaseAgreementRenewalFinancialEffectSummary {
  const LeaseAgreementRenewalFinancialEffectSummary({
    required this.id,
    required this.effectType,
    required this.amount,
    required this.currency,
    required this.chargeCode,
    required this.description,
    this.effectiveFromOn,
    this.effectiveThroughOn,
    this.dueOn,
  });

  final int id;
  final String effectType;
  final double amount;
  final String currency;
  final String chargeCode;
  final DateTime? effectiveFromOn;
  final DateTime? effectiveThroughOn;
  final DateTime? dueOn;
  final String description;

  factory LeaseAgreementRenewalFinancialEffectSummary.fromJson(
    Map<String, dynamic> json,
  ) => LeaseAgreementRenewalFinancialEffectSummary(
    id: (json['leaseAddendumFinancialEffectId'] as num?)?.toInt() ?? 0,
    effectType: json['effectType'] as String? ?? '',
    amount: (json['amount'] as num?)?.toDouble() ?? 0,
    currency: json['currency'] as String? ?? '',
    chargeCode: json['chargeCode'] as String? ?? '',
    effectiveFromOn: _optionalDate(json['effectiveFromOn']),
    effectiveThroughOn: _optionalDate(json['effectiveThroughOn']),
    dueOn: _optionalDate(json['dueOn']),
    description: json['description'] as String? ?? '',
  );
}

class LeaseAgreementEffectiveAddendumSeriesItem {
  const LeaseAgreementEffectiveAddendumSeriesItem({
    required this.seriesPublicId,
    required this.currentLeaseAddendumId,
    required this.currentLeaseAddendumPublicId,
    required this.currentVersionNumber,
    required this.baseAgreementId,
    required this.baseAgreementNumber,
    required this.baseAgreementTermStartOn,
    required this.purpose,
    required this.title,
    required this.effectiveFromOn,
    required this.decisionRequired,
    required this.financialEffectCount,
    required this.financialEffects,
    this.baseAgreementTermEndOn,
    this.effectiveThroughOn,
  });

  final String seriesPublicId;
  final int currentLeaseAddendumId;
  final String currentLeaseAddendumPublicId;
  final int currentVersionNumber;
  final int baseAgreementId;
  final String baseAgreementNumber;
  final DateTime baseAgreementTermStartOn;
  final DateTime? baseAgreementTermEndOn;
  final String purpose;
  final String title;
  final DateTime effectiveFromOn;
  final DateTime? effectiveThroughOn;
  final bool decisionRequired;
  final int financialEffectCount;
  final List<LeaseAgreementRenewalFinancialEffectSummary> financialEffects;

  factory LeaseAgreementEffectiveAddendumSeriesItem.fromJson(
    Map<String, dynamic> json,
  ) => LeaseAgreementEffectiveAddendumSeriesItem(
    seriesPublicId: json['seriesPublicId'] as String? ?? '',
    currentLeaseAddendumId:
        (json['currentLeaseAddendumId'] as num?)?.toInt() ?? 0,
    currentLeaseAddendumPublicId:
        json['currentLeaseAddendumPublicId'] as String? ?? '',
    currentVersionNumber: (json['currentVersionNumber'] as num?)?.toInt() ?? 0,
    baseAgreementId: (json['baseAgreementId'] as num?)?.toInt() ?? 0,
    baseAgreementNumber: json['baseAgreementNumber'] as String? ?? '',
    baseAgreementTermStartOn: _date(json['baseAgreementTermStartOn']),
    baseAgreementTermEndOn: _optionalDate(json['baseAgreementTermEndOn']),
    purpose: json['purpose'] as String? ?? '',
    title: json['title'] as String? ?? '',
    effectiveFromOn: _date(json['effectiveFromOn']),
    effectiveThroughOn: _optionalDate(json['effectiveThroughOn']),
    decisionRequired: json['decisionRequired'] as bool? ?? false,
    financialEffectCount: (json['financialEffectCount'] as num?)?.toInt() ?? 0,
    financialEffects: (json['financialEffects'] as List? ?? const [])
        .map(
          (item) => LeaseAgreementRenewalFinancialEffectSummary.fromJson(
            (item as Map).cast<String, dynamic>(),
          ),
        )
        .toList(),
  );
}

class LeaseAgreementEffectiveAddendumSeries {
  const LeaseAgreementEffectiveAddendumSeries({
    required this.leaseManagementId,
    required this.sourceAgreementId,
    required this.sourceAgreementNumber,
    required this.sourceTermStartOn,
    required this.sourceGoverningFromOn,
    required this.businessDate,
    required this.decisionRequired,
    required this.requiredDecisionCount,
    required this.series,
    this.sourceTermEndOn,
  });

  final int leaseManagementId;
  final int sourceAgreementId;
  final String sourceAgreementNumber;
  final DateTime sourceTermStartOn;
  final DateTime? sourceTermEndOn;
  final DateTime sourceGoverningFromOn;
  final DateTime businessDate;
  final bool decisionRequired;
  final int requiredDecisionCount;
  final List<LeaseAgreementEffectiveAddendumSeriesItem> series;

  factory LeaseAgreementEffectiveAddendumSeries.fromJson(
    Map<String, dynamic> json,
  ) => LeaseAgreementEffectiveAddendumSeries(
    leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
    sourceAgreementId: (json['sourceAgreementId'] as num?)?.toInt() ?? 0,
    sourceAgreementNumber: json['sourceAgreementNumber'] as String? ?? '',
    sourceTermStartOn: _date(json['sourceTermStartOn']),
    sourceTermEndOn: _optionalDate(json['sourceTermEndOn']),
    sourceGoverningFromOn: _date(json['sourceGoverningFromOn']),
    businessDate: _date(json['businessDate']),
    decisionRequired: json['decisionRequired'] as bool? ?? false,
    requiredDecisionCount:
        (json['requiredDecisionCount'] as num?)?.toInt() ?? 0,
    series: (json['series'] as List? ?? const [])
        .map(
          (item) => LeaseAgreementEffectiveAddendumSeriesItem.fromJson(
            (item as Map).cast<String, dynamic>(),
          ),
        )
        .toList(),
  );
}

class ReturnPossessionContext {
  const ReturnPossessionContext({
    required this.parties,
    required this.activeTenantUserAccesses,
  });

  final List<LeaseManagementParty> parties;
  final List<ActiveTenantUserAccess> activeTenantUserAccesses;

  factory ReturnPossessionContext.fromJson(Map<String, dynamic> json) =>
      ReturnPossessionContext(
        parties: (json['parties'] as List? ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(LeaseManagementParty.fromJson)
            .toList(),
        activeTenantUserAccesses:
            (json['activeTenantUserAccesses'] as List? ?? const [])
                .whereType<Map<String, dynamic>>()
                .map(ActiveTenantUserAccess.fromJson)
                .toList(),
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
    this.correctionReason,
    this.replacesAgreementId,
    this.renewsAgreementId,
    this.termEndOn,
    this.issuedArtifact,
    this.executedArtifact,
    this.issuedAt,
    this.fullyExecutedAt,
    this.voidedAt,
    this.draftCanceledAt,
    this.draftCancellationReason,
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
  final String? correctionReason;
  final int? replacesAgreementId;
  final int? renewsAgreementId;
  final LegalArtifactSummary? issuedArtifact;
  final LegalArtifactSummary? executedArtifact;
  final DateTime? issuedAt;
  final DateTime? fullyExecutedAt;
  final DateTime? voidedAt;
  final DateTime? draftCanceledAt;
  final String? draftCancellationReason;

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
      correctionReason: json['correctionReason'] as String?,
      replacesAgreementId: (json['replacesAgreementId'] as num?)?.toInt(),
      renewsAgreementId: (json['renewsAgreementId'] as num?)?.toInt(),
      issuedArtifact: artifact('issuedArtifact'),
      executedArtifact: artifact('executedArtifact'),
      issuedAt: _optionalDate(json['issuedAtUtc']),
      fullyExecutedAt: _optionalDate(json['fullyExecutedAtUtc']),
      voidedAt: _optionalDate(json['voidedAtUtc']),
      draftCanceledAt: _optionalDate(json['draftCanceledAtUtc']),
      draftCancellationReason: json['draftCancellationReason'] as String?,
    );
  }
}

class LeaseAddendumHistory {
  const LeaseAddendumHistory({
    required this.id,
    required this.seriesPublicId,
    required this.baseAgreementId,
    required this.versionNumber,
    required this.addendumNumber,
    required this.purpose,
    required this.effectiveFromOn,
    required this.status,
    required this.financialEffectCount,
    required this.recurringRentDelta,
    required this.signerCount,
    required this.canCorrect,
    this.replacesAddendumId,
    this.effectiveThroughOn,
    this.supersededEffectiveOn,
    this.issuedArtifact,
    this.executedArtifact,
    this.issuedAt,
    this.fullyExecutedAt,
    this.voidedAt,
  });

  final int id;
  final String seriesPublicId;
  final int baseAgreementId;
  final int versionNumber;
  final String addendumNumber;
  final String purpose;
  final int? replacesAddendumId;
  final DateTime effectiveFromOn;
  final DateTime? effectiveThroughOn;
  final DateTime? supersededEffectiveOn;
  final String status;
  final int financialEffectCount;
  final double recurringRentDelta;
  final int signerCount;
  final bool canCorrect;
  final LegalArtifactSummary? issuedArtifact;
  final LegalArtifactSummary? executedArtifact;
  final DateTime? issuedAt;
  final DateTime? fullyExecutedAt;
  final DateTime? voidedAt;

  bool get isDraft => status.toLowerCase() == 'draft';
  factory LeaseAddendumHistory.fromJson(Map<String, dynamic> json) {
    LegalArtifactSummary? artifact(String key) {
      final value = json[key];
      return value is Map<String, dynamic>
          ? LegalArtifactSummary.fromJson(value)
          : null;
    }

    return LeaseAddendumHistory(
      id: (json['leaseAddendumId'] as num?)?.toInt() ?? 0,
      seriesPublicId: json['seriesPublicId'] as String? ?? '',
      baseAgreementId: (json['baseAgreementId'] as num?)?.toInt() ?? 0,
      versionNumber: (json['versionNumber'] as num?)?.toInt() ?? 0,
      addendumNumber: json['addendumNumber'] as String? ?? '',
      purpose: json['purpose'] as String? ?? '',
      replacesAddendumId: (json['replacesAddendumId'] as num?)?.toInt(),
      effectiveFromOn: _date(json['effectiveFromOn']),
      effectiveThroughOn: _optionalDate(json['effectiveThroughOn']),
      supersededEffectiveOn: _optionalDate(json['supersededEffectiveOn']),
      status: json['addendumStatus'] as String? ?? '',
      financialEffectCount:
          (json['financialEffectCount'] as num?)?.toInt() ?? 0,
      recurringRentDelta: (json['recurringRentDelta'] as num?)?.toDouble() ?? 0,
      signerCount: (json['signerCount'] as num?)?.toInt() ?? 0,
      canCorrect: json['canCorrect'] as bool? ?? false,
      issuedArtifact: artifact('issuedArtifact'),
      executedArtifact: artifact('executedArtifact'),
      issuedAt: _optionalDate(json['issuedAtUtc']),
      fullyExecutedAt: _optionalDate(json['fullyExecutedAtUtc']),
      voidedAt: _optionalDate(json['voidedAtUtc']),
    );
  }
}

class LeaseAddendumEligibleBaseAgreement {
  const LeaseAddendumEligibleBaseAgreement({
    required this.id,
    required this.publicId,
    required this.agreementNumber,
    required this.versionNumber,
    required this.termStartOn,
    required this.governingFromOn,
    required this.currency,
    this.termEndOn,
  });

  final int id;
  final String publicId;
  final String agreementNumber;
  final int versionNumber;
  final DateTime termStartOn;
  final DateTime? termEndOn;
  final DateTime governingFromOn;
  final String currency;

  factory LeaseAddendumEligibleBaseAgreement.fromJson(
    Map<String, dynamic> json,
  ) => LeaseAddendumEligibleBaseAgreement(
    id: (json['leaseAgreementId'] as num?)?.toInt() ?? 0,
    publicId: json['publicId'] as String? ?? '',
    agreementNumber: json['agreementNumber'] as String? ?? '',
    versionNumber: (json['versionNumber'] as num?)?.toInt() ?? 0,
    termStartOn: _date(json['termStartOn']),
    termEndOn: _optionalDate(json['termEndOn']),
    governingFromOn: _date(json['governingFromOn']),
    currency: json['currency'] as String? ?? '',
  );
}

class LeaseAddendumDraftSigner {
  const LeaseAddendumDraftSigner({
    required this.id,
    required this.signerRole,
    required this.nameSnapshot,
    required this.emailSnapshot,
    required this.signingOrder,
    required this.isRequired,
    this.leaseManagementPartyId,
    this.tenantId,
  });

  final int id;
  final int? leaseManagementPartyId;
  final int? tenantId;
  final String signerRole;
  final String nameSnapshot;
  final String emailSnapshot;
  final int signingOrder;
  final bool isRequired;

  bool get isComplete =>
      signerRole.isNotEmpty &&
      nameSnapshot.trim().isNotEmpty &&
      emailSnapshot.trim().isNotEmpty &&
      signingOrder > 0 &&
      isRequired &&
      (leaseManagementPartyId == null) == (tenantId == null);

  Map<String, dynamic> toRequestJson() => {
    'leaseManagementPartyId': leaseManagementPartyId,
    'tenantId': tenantId,
    'signerRole': signerRole,
    'nameSnapshot': nameSnapshot,
    'emailSnapshot': emailSnapshot,
    'signingOrder': signingOrder,
    'isRequired': isRequired,
  };

  factory LeaseAddendumDraftSigner.fromJson(Map<String, dynamic> json) =>
      LeaseAddendumDraftSigner(
        id: (json['leaseAddendumSignerId'] as num?)?.toInt() ?? 0,
        leaseManagementPartyId: (json['leaseManagementPartyId'] as num?)
            ?.toInt(),
        tenantId: (json['tenantId'] as num?)?.toInt(),
        signerRole: json['signerRole'] as String? ?? '',
        nameSnapshot: json['nameSnapshot'] as String? ?? '',
        emailSnapshot: json['emailSnapshot'] as String? ?? '',
        signingOrder: (json['signingOrder'] as num?)?.toInt() ?? 0,
        isRequired: json['isRequired'] as bool? ?? false,
      );
}

class LeaseAddendumFinancialEffect {
  const LeaseAddendumFinancialEffect({
    required this.id,
    required this.effectType,
    required this.amount,
    required this.currency,
    required this.chargeCode,
    required this.description,
    this.effectiveFromOn,
    this.effectiveThroughOn,
    this.dueOn,
  });

  final int id;
  final String effectType;
  final double amount;
  final String currency;
  final String chargeCode;
  final DateTime? effectiveFromOn;
  final DateTime? effectiveThroughOn;
  final DateTime? dueOn;
  final String description;

  Map<String, dynamic> toRequestJson() => {
    'effectType': effectType,
    'amount': amount,
    'currency': currency,
    'chargeCode': chargeCode,
    'effectiveFromOn': effectiveFromOn == null
        ? null
        : _dateOnly(effectiveFromOn!),
    'effectiveThroughOn': effectiveThroughOn == null
        ? null
        : _dateOnly(effectiveThroughOn!),
    'dueOn': dueOn == null ? null : _dateOnly(dueOn!),
    'description': description,
  };

  factory LeaseAddendumFinancialEffect.fromJson(Map<String, dynamic> json) =>
      LeaseAddendumFinancialEffect(
        id: (json['leaseAddendumFinancialEffectId'] as num?)?.toInt() ?? 0,
        effectType: json['effectType'] as String? ?? '',
        amount: (json['amount'] as num?)?.toDouble() ?? 0,
        currency: json['currency'] as String? ?? '',
        chargeCode: json['chargeCode'] as String? ?? '',
        effectiveFromOn: _optionalDate(json['effectiveFromOn']),
        effectiveThroughOn: _optionalDate(json['effectiveThroughOn']),
        dueOn: _optionalDate(json['dueOn']),
        description: json['description'] as String? ?? '',
      );
}

class LeaseAddendumDraftDetail {
  const LeaseAddendumDraftDetail({
    required this.leaseManagementId,
    required this.leaseAddendumId,
    required this.publicId,
    required this.seriesPublicId,
    required this.baseAgreementId,
    required this.baseAgreementNumber,
    required this.baseAgreementCurrency,
    required this.versionNumber,
    required this.draftRevision,
    required this.addendumNumber,
    required this.purpose,
    required this.effectiveFromOn,
    required this.termsSchemaVersion,
    required this.termsPayload,
    required this.documentSourceVersionId,
    required this.signers,
    required this.financialEffects,
    this.sourceAddendumId,
    this.effectiveThroughOn,
    this.documentTemplateId,
  });

  final int leaseManagementId;
  final int leaseAddendumId;
  final String publicId;
  final String seriesPublicId;
  final int baseAgreementId;
  final String baseAgreementNumber;
  final String baseAgreementCurrency;
  final int versionNumber;
  final int draftRevision;
  final String addendumNumber;
  final String purpose;
  final int? sourceAddendumId;
  final DateTime effectiveFromOn;
  final DateTime? effectiveThroughOn;
  final int termsSchemaVersion;
  final Map<String, dynamic> termsPayload;
  final int documentSourceVersionId;
  final int? documentTemplateId;
  final List<LeaseAddendumDraftSigner> signers;
  final List<LeaseAddendumFinancialEffect> financialEffects;

  bool get hasExactOrderedSigners {
    if (signers.isEmpty || signers.any((signer) => !signer.isComplete)) {
      return false;
    }
    return signers.map((signer) => signer.signingOrder).toSet().length ==
        signers.length;
  }

  factory LeaseAddendumDraftDetail.fromJson(Map<String, dynamic> json) =>
      LeaseAddendumDraftDetail(
        leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
        leaseAddendumId: (json['leaseAddendumId'] as num?)?.toInt() ?? 0,
        publicId: json['publicId'] as String? ?? '',
        seriesPublicId: json['seriesPublicId'] as String? ?? '',
        baseAgreementId: (json['baseAgreementId'] as num?)?.toInt() ?? 0,
        baseAgreementNumber: json['baseAgreementNumber'] as String? ?? '',
        baseAgreementCurrency: json['baseAgreementCurrency'] as String? ?? '',
        versionNumber: (json['versionNumber'] as num?)?.toInt() ?? 0,
        draftRevision: (json['draftRevision'] as num?)?.toInt() ?? 0,
        addendumNumber: json['addendumNumber'] as String? ?? '',
        purpose: json['purpose'] as String? ?? '',
        sourceAddendumId: (json['sourceAddendumId'] as num?)?.toInt(),
        effectiveFromOn: _date(json['effectiveFromOn']),
        effectiveThroughOn: _optionalDate(json['effectiveThroughOn']),
        termsSchemaVersion: (json['termsSchemaVersion'] as num?)?.toInt() ?? 0,
        termsPayload:
            (json['termsPayload'] as Map?)?.cast<String, dynamic>() ??
            <String, dynamic>{},
        documentSourceVersionId:
            (json['documentSourceVersionId'] as num?)?.toInt() ?? 0,
        documentTemplateId: (json['documentTemplateId'] as num?)?.toInt(),
        signers: (json['signers'] as List? ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(LeaseAddendumDraftSigner.fromJson)
            .toList(),
        financialEffects: (json['financialEffects'] as List? ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(LeaseAddendumFinancialEffect.fromJson)
            .toList(),
      );
}

class LeaseAgreementDraftSigner {
  const LeaseAgreementDraftSigner({
    required this.id,
    required this.signerRole,
    required this.nameSnapshot,
    required this.emailSnapshot,
    required this.signingOrder,
    required this.isRequired,
    this.leaseManagementPartyId,
    this.tenantId,
  });

  final int id;
  final int? leaseManagementPartyId;
  final int? tenantId;
  final String signerRole;
  final String nameSnapshot;
  final String emailSnapshot;
  final int signingOrder;
  final bool isRequired;

  bool get isComplete =>
      id > 0 &&
      signerRole.isNotEmpty &&
      nameSnapshot.trim().isNotEmpty &&
      emailSnapshot.trim().isNotEmpty &&
      signingOrder > 0 &&
      isRequired &&
      (leaseManagementPartyId == null) == (tenantId == null);

  Map<String, dynamic> toRequestJson() => {
    'leaseManagementPartyId': leaseManagementPartyId,
    'tenantId': tenantId,
    'signerRole': signerRole,
    'nameSnapshot': nameSnapshot,
    'emailSnapshot': emailSnapshot,
    'signingOrder': signingOrder,
    'isRequired': isRequired,
  };

  factory LeaseAgreementDraftSigner.fromJson(Map<String, dynamic> json) =>
      LeaseAgreementDraftSigner(
        id: (json['leaseAgreementSignerId'] as num?)?.toInt() ?? 0,
        leaseManagementPartyId: (json['leaseManagementPartyId'] as num?)
            ?.toInt(),
        tenantId: (json['tenantId'] as num?)?.toInt(),
        signerRole: json['signerRole'] as String? ?? '',
        nameSnapshot: json['nameSnapshot'] as String? ?? '',
        emailSnapshot: json['emailSnapshot'] as String? ?? '',
        signingOrder: (json['signingOrder'] as num?)?.toInt() ?? 0,
        isRequired: json['isRequired'] as bool? ?? false,
      );
}

class LeaseAgreementDraftDetail {
  const LeaseAgreementDraftDetail({
    required this.leaseManagementId,
    required this.leaseAgreementId,
    required this.publicId,
    required this.versionNumber,
    required this.draftRevision,
    required this.agreementNumber,
    required this.changeType,
    this.correctionReason,
    required this.termType,
    required this.termStartOn,
    required this.governingFromOn,
    required this.baseRentAmount,
    required this.rentDueDay,
    required this.securityDepositObligation,
    required this.lateFeeAmount,
    required this.gracePeriodDays,
    required this.currency,
    required this.termsSchemaVersion,
    required this.termsPayload,
    required this.documentSourceVersionId,
    required this.signers,
    required this.createdAt,
    required this.updatedAt,
    this.termEndOn,
    this.documentTemplateId,
    this.documentTemplateVersion,
  });

  final int leaseManagementId;
  final int leaseAgreementId;
  final String publicId;
  final int versionNumber;
  final int draftRevision;
  final String agreementNumber;
  final String changeType;
  final String? correctionReason;
  final String termType;
  final DateTime termStartOn;
  final DateTime? termEndOn;
  final DateTime governingFromOn;
  final double baseRentAmount;
  final int rentDueDay;
  final double securityDepositObligation;
  final double lateFeeAmount;
  final int gracePeriodDays;
  final String currency;
  final int termsSchemaVersion;
  final Map<String, dynamic> termsPayload;
  final int documentSourceVersionId;
  final int? documentTemplateId;
  final int? documentTemplateVersion;
  final List<LeaseAgreementDraftSigner> signers;
  final DateTime createdAt;
  final DateTime updatedAt;

  bool get hasExactOrderedSigners {
    if (signers.isEmpty || signers.any((signer) => !signer.isComplete)) {
      return false;
    }
    final orders = signers.map((signer) => signer.signingOrder).toSet();
    return orders.length == signers.length;
  }

  factory LeaseAgreementDraftDetail.fromJson(Map<String, dynamic> json) =>
      LeaseAgreementDraftDetail(
        leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
        leaseAgreementId: (json['leaseAgreementId'] as num?)?.toInt() ?? 0,
        publicId: json['publicId'] as String? ?? '',
        versionNumber: (json['versionNumber'] as num?)?.toInt() ?? 0,
        draftRevision: (json['draftRevision'] as num?)?.toInt() ?? 0,
        agreementNumber: json['agreementNumber'] as String? ?? '',
        changeType: json['changeType'] as String? ?? '',
        correctionReason: json['correctionReason'] as String?,
        termType: json['termType'] as String? ?? '',
        termStartOn: _date(json['termStartOn']),
        termEndOn: _optionalDate(json['termEndOn']),
        governingFromOn: _date(json['governingFromOn']),
        baseRentAmount: (json['baseRentAmount'] as num?)?.toDouble() ?? 0,
        rentDueDay: (json['rentDueDay'] as num?)?.toInt() ?? 0,
        securityDepositObligation:
            (json['securityDepositObligation'] as num?)?.toDouble() ?? 0,
        lateFeeAmount: (json['lateFeeAmount'] as num?)?.toDouble() ?? 0,
        gracePeriodDays: (json['gracePeriodDays'] as num?)?.toInt() ?? 0,
        currency: json['currency'] as String? ?? '',
        termsSchemaVersion: (json['termsSchemaVersion'] as num?)?.toInt() ?? 0,
        termsPayload:
            (json['termsPayload'] as Map?)?.cast<String, dynamic>() ??
            <String, dynamic>{},
        documentSourceVersionId:
            (json['documentSourceVersionId'] as num?)?.toInt() ?? 0,
        documentTemplateId: (json['documentTemplateId'] as num?)?.toInt(),
        documentTemplateVersion: (json['documentTemplateVersion'] as num?)
            ?.toInt(),
        signers:
            (json['signers'] as List? ?? const [])
                .whereType<Map<String, dynamic>>()
                .map(LeaseAgreementDraftSigner.fromJson)
                .toList()
              ..sort(
                (left, right) =>
                    left.signingOrder.compareTo(right.signingOrder),
              ),
        createdAt: _date(json['createdAtUtc']),
        updatedAt: _date(json['updatedAtUtc']),
      );
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
