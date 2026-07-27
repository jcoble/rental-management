import 'dart:math';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/models/lease.dart';

class LeaseManagementListQuery {
  const LeaseManagementListQuery({
    this.skip = 0,
    this.take = 20,
    this.tenantId,
    this.propertyId,
    this.unitId,
    this.lifecycle,
    this.search,
    this.sort = '-updatedAt',
  });

  final int skip;
  final int take;
  final int? tenantId;
  final int? propertyId;
  final int? unitId;
  final String? lifecycle;
  final String? search;
  final String sort;

  @override
  bool operator ==(Object other) =>
      other is LeaseManagementListQuery &&
      other.skip == skip &&
      other.take == take &&
      other.tenantId == tenantId &&
      other.propertyId == propertyId &&
      other.unitId == unitId &&
      other.lifecycle == lifecycle &&
      other.search == search &&
      other.sort == sort;

  @override
  int get hashCode => Object.hash(
    skip,
    take,
    tenantId,
    propertyId,
    unitId,
    lifecycle,
    search,
    sort,
  );
}

class LeaseManagementListPage {
  const LeaseManagementListPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<LeaseManagementSummary> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory LeaseManagementListPage.fromJson(Map<String, dynamic> json) {
    final items = (json['items'] as List? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(LeaseManagementSummary.fromJson)
        .toList();
    return LeaseManagementListPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

class LeaseAgreementHistoryPage {
  const LeaseAgreementHistoryPage({required this.items});

  final List<LeaseAgreementHistory> items;

  factory LeaseAgreementHistoryPage.fromJson(Map<String, dynamic> json) =>
      LeaseAgreementHistoryPage(
        items: (json['items'] as List? ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(LeaseAgreementHistory.fromJson)
            .toList(),
      );
}

class LeaseAddendumHistoryQuery {
  const LeaseAddendumHistoryQuery({
    required this.leaseManagementId,
    this.skip = 0,
    this.take = 10,
  });

  final int leaseManagementId;
  final int skip;
  final int take;

  @override
  bool operator ==(Object other) =>
      other is LeaseAddendumHistoryQuery &&
      other.leaseManagementId == leaseManagementId &&
      other.skip == skip &&
      other.take == take;

  @override
  int get hashCode => Object.hash(leaseManagementId, skip, take);
}

class LeaseAddendumHistoryPage {
  const LeaseAddendumHistoryPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<LeaseAddendumHistory> items;
  final int totalCount;
  final int skip;
  final int take;
  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory LeaseAddendumHistoryPage.fromJson(Map<String, dynamic> json) {
    final items = (json['items'] as List? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(LeaseAddendumHistory.fromJson)
        .toList();
    return LeaseAddendumHistoryPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

class LeaseAddendumEligibleBaseAgreementPage {
  const LeaseAddendumEligibleBaseAgreementPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<LeaseAddendumEligibleBaseAgreement> items;
  final int totalCount;
  final int skip;
  final int take;
  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory LeaseAddendumEligibleBaseAgreementPage.fromJson(
    Map<String, dynamic> json,
  ) {
    final items = (json['items'] as List? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(LeaseAddendumEligibleBaseAgreement.fromJson)
        .toList();
    return LeaseAddendumEligibleBaseAgreementPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

class LeaseAddendumDraftInput {
  const LeaseAddendumDraftInput({
    required this.baseAgreementId,
    required this.addendumNumber,
    required this.purpose,
    required this.effectiveFromOn,
    required this.termsSchemaVersion,
    required this.termsPayload,
    required this.documentTemplateId,
    required this.signers,
    required this.financialEffects,
    this.effectiveThroughOn,
    this.draftRevision,
  });

  final int baseAgreementId;
  final String addendumNumber;
  final String purpose;
  final DateTime effectiveFromOn;
  final DateTime? effectiveThroughOn;
  final int termsSchemaVersion;
  final Map<String, dynamic> termsPayload;
  final int documentTemplateId;
  final List<LeaseAddendumDraftSigner> signers;
  final List<LeaseAddendumFinancialEffect> financialEffects;
  final int? draftRevision;

  Map<String, dynamic> toJson() => {
    if (draftRevision != null) 'draftRevision': draftRevision,
    'baseAgreementId': baseAgreementId,
    'addendumNumber': addendumNumber,
    'purpose': purpose,
    'effectiveFromOn': LeaseManagementsRepository._dateOnly(effectiveFromOn),
    'effectiveThroughOn': effectiveThroughOn == null
        ? null
        : LeaseManagementsRepository._dateOnly(effectiveThroughOn!),
    'termsSchemaVersion': termsSchemaVersion,
    'termsPayload': termsPayload,
    'documentTemplateId': documentTemplateId,
    'signers': signers.map((signer) => signer.toRequestJson()).toList(),
    'financialEffects': financialEffects
        .map((effect) => effect.toRequestJson())
        .toList(),
  };
}

class LeaseAddendumDraftMutationResult {
  const LeaseAddendumDraftMutationResult({
    required this.leaseManagementId,
    required this.leaseAddendumId,
    required this.seriesPublicId,
    required this.versionNumber,
    required this.draftRevision,
    required this.replayed,
    this.sourceAddendumId,
  });

  final int leaseManagementId;
  final int leaseAddendumId;
  final String seriesPublicId;
  final int versionNumber;
  final int draftRevision;
  final int? sourceAddendumId;
  final bool replayed;

  factory LeaseAddendumDraftMutationResult.fromJson(
    Map<String, dynamic> json,
  ) => LeaseAddendumDraftMutationResult(
    leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
    leaseAddendumId: (json['leaseAddendumId'] as num?)?.toInt() ?? 0,
    seriesPublicId: json['seriesPublicId'] as String? ?? '',
    versionNumber: (json['versionNumber'] as num?)?.toInt() ?? 0,
    draftRevision: (json['draftRevision'] as num?)?.toInt() ?? 0,
    sourceAddendumId: (json['sourceAddendumId'] as num?)?.toInt(),
    replayed: json['replayed'] as bool? ?? false,
  );
}

class IssueAddendumResult {
  const IssueAddendumResult({
    required this.signatureRequestPublicId,
    required this.leaseManagementId,
    required this.leaseAddendumId,
    required this.signatureRequestId,
    required this.issuedArtifactId,
    required this.replayed,
  });

  final String signatureRequestPublicId;
  final int leaseManagementId;
  final int leaseAddendumId;
  final int signatureRequestId;
  final int issuedArtifactId;
  final bool replayed;

  factory IssueAddendumResult.fromJson(Map<String, dynamic> json) =>
      IssueAddendumResult(
        signatureRequestPublicId:
            json['signatureRequestPublicId'] as String? ?? '',
        leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
        leaseAddendumId: (json['leaseAddendumId'] as num?)?.toInt() ?? 0,
        signatureRequestId: (json['signatureRequestId'] as num?)?.toInt() ?? 0,
        issuedArtifactId: (json['issuedArtifactId'] as num?)?.toInt() ?? 0,
        replayed: json['replayed'] as bool? ?? false,
      );
}

class LeaseSuccessorDraftResult {
  const LeaseSuccessorDraftResult({
    required this.leaseManagementId,
    required this.leaseAgreementId,
    required this.versionNumber,
    required this.draftRevision,
    required this.sourceAgreementId,
    required this.leaseAgreementSignerIds,
    required this.addendumDecisionIds,
    required this.replacementAddendumIds,
    required this.replayed,
  });

  final int leaseManagementId;
  final int leaseAgreementId;
  final int versionNumber;
  final int draftRevision;
  final int? sourceAgreementId;
  final List<int> leaseAgreementSignerIds;
  final List<int> addendumDecisionIds;
  final List<int> replacementAddendumIds;
  final bool replayed;

  factory LeaseSuccessorDraftResult.fromJson(Map<String, dynamic> json) =>
      LeaseSuccessorDraftResult(
        leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
        leaseAgreementId: (json['leaseAgreementId'] as num?)?.toInt() ?? 0,
        versionNumber: (json['versionNumber'] as num?)?.toInt() ?? 0,
        draftRevision: (json['draftRevision'] as num?)?.toInt() ?? 0,
        sourceAgreementId: (json['sourceAgreementId'] as num?)?.toInt(),
        leaseAgreementSignerIds: (json['leaseAgreementSignerIds'] as List)
            .map((value) => (value as num).toInt())
            .toList(growable: false),
        addendumDecisionIds: (json['addendumDecisionIds'] as List)
            .map((value) => (value as num).toInt())
            .toList(growable: false),
        replacementAddendumIds: (json['replacementAddendumIds'] as List)
            .map((value) => (value as num).toInt())
            .toList(growable: false),
        replayed: json['replayed'] as bool,
      );
}

class CancelLeaseSuccessorDraftResult {
  const CancelLeaseSuccessorDraftResult({
    required this.leaseManagementId,
    required this.leaseAgreementId,
    required this.draftCanceledAt,
    required this.draftCancellationReason,
  });

  final int leaseManagementId;
  final int leaseAgreementId;
  final DateTime draftCanceledAt;
  final String draftCancellationReason;

  factory CancelLeaseSuccessorDraftResult.fromJson(Map<String, dynamic> json) =>
      CancelLeaseSuccessorDraftResult(
        leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
        leaseAgreementId: (json['leaseAgreementId'] as num?)?.toInt() ?? 0,
        draftCanceledAt:
            DateTime.tryParse(json['draftCanceledAtUtc'] as String? ?? '') ??
            DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
        draftCancellationReason:
            json['draftCancellationReason'] as String? ?? '',
      );
}

class LeaseRenewalAddendumDecisionInput {
  const LeaseRenewalAddendumDecisionInput({
    required this.sourceAddendumSeriesPublicId,
    required this.decision,
  });

  final String sourceAddendumSeriesPublicId;
  final String decision;

  Map<String, dynamic> toJson() => {
    'sourceAddendumSeriesPublicId': sourceAddendumSeriesPublicId,
    'decision': decision,
  };
}

class LeaseQuestionResponse {
  const LeaseQuestionResponse({
    required this.answer,
    required this.llmEnhanced,
    required this.sources,
  });

  final String answer;
  final bool llmEnhanced;
  final List<String> sources;

  factory LeaseQuestionResponse.fromJson(Map<String, dynamic> json) =>
      LeaseQuestionResponse(
        answer: json['answer'] as String? ?? '',
        llmEnhanced: json['llmEnhanced'] as bool? ?? false,
        sources: (json['sources'] as List? ?? const [])
            .whereType<String>()
            .toList(),
      );
}

class LeaseTemplateOption {
  const LeaseTemplateOption({
    required this.id,
    required this.name,
    this.propertyId,
  });
  final int id;
  final String name;
  final int? propertyId;

  factory LeaseTemplateOption.fromJson(Map<String, dynamic> json) =>
      LeaseTemplateOption(
        id: (json['id'] as num?)?.toInt() ?? 0,
        name: json['name'] as String? ?? 'Lease template',
        propertyId: (json['propertyId'] as num?)?.toInt(),
      );
}

class LeaseTemplateOptionPage {
  const LeaseTemplateOptionPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });
  final List<LeaseTemplateOption> items;
  final int totalCount;
  final int skip;
  final int take;
  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory LeaseTemplateOptionPage.fromJson(Map<String, dynamic> json) {
    final items = (json['items'] as List? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(LeaseTemplateOption.fromJson)
        .toList();
    return LeaseTemplateOptionPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

class PrepareMoveInPartyInput {
  const PrepareMoveInPartyInput({
    required this.tenantId,
    required this.role,
    required this.isAgreementSigner,
    this.guarantorLegalNoticeEligible = false,
    this.changeReason = 'Approved application move-in',
    this.signingOrder = 1,
  });
  final int tenantId;
  final String role;
  final bool guarantorLegalNoticeEligible;
  final String changeReason;
  final bool isAgreementSigner;
  final int? signingOrder;

  Map<String, dynamic> toJson() => {
    'tenantId': tenantId,
    'role': role,
    'guarantorLegalNoticeEligible': guarantorLegalNoticeEligible,
    'changeReason': changeReason,
    'isAgreementSigner': isAgreementSigner,
    'signingOrder': isAgreementSigner ? signingOrder : null,
    'isRequiredSigner': isAgreementSigner,
  };
}

class PrepareMoveInInput {
  const PrepareMoveInInput({
    required this.applicationId,
    required this.unitId,
    required this.plannedPossessionAtUtc,
    required this.partyEffectiveFrom,
    required this.parties,
    required this.documentTemplateId,
    required this.termType,
    required this.termStartOn,
    this.termEndOn,
    required this.baseRentAmount,
    required this.rentDueDay,
    required this.securityDepositObligation,
    required this.lateFeeAmount,
    required this.gracePeriodDays,
    this.rentTrackingStartMode = 'ForwardOnly',
    this.rentTrackingStartOn,
    required this.createSecurityDepositAccount,
    this.openingBalanceAmount,
    this.openingBalanceEffectiveOn,
    this.openingBalanceNote,
  });
  final int applicationId;
  final int unitId;
  final DateTime? plannedPossessionAtUtc;
  final DateTime partyEffectiveFrom;
  final List<PrepareMoveInPartyInput> parties;
  final int documentTemplateId;
  final String termType;
  final DateTime termStartOn;
  final DateTime? termEndOn;
  final double baseRentAmount;
  final int rentDueDay;
  final double securityDepositObligation;
  final double lateFeeAmount;
  final int gracePeriodDays;
  final String rentTrackingStartMode;
  final DateTime? rentTrackingStartOn;
  final bool createSecurityDepositAccount;
  final double? openingBalanceAmount;
  final DateTime? openingBalanceEffectiveOn;
  final String? openingBalanceNote;

  Map<String, dynamic> toJson() => {
    'applicationId': applicationId,
    'unitId': unitId,
    'plannedPossessionAtUtc': plannedPossessionAtUtc?.toUtc().toIso8601String(),
    'partyEffectiveFrom': LeaseManagementsRepository._dateOnly(
      partyEffectiveFrom,
    ),
    'parties': parties.map((party) => party.toJson()).toList(),
    'documentTemplateId': documentTemplateId,
    'termType': termType,
    'termStartOn': LeaseManagementsRepository._dateOnly(termStartOn),
    'termEndOn': termEndOn == null
        ? null
        : LeaseManagementsRepository._dateOnly(termEndOn!),
    'baseRentAmount': baseRentAmount,
    'rentDueDay': rentDueDay,
    'securityDepositObligation': securityDepositObligation,
    'lateFeeAmount': lateFeeAmount,
    'gracePeriodDays': gracePeriodDays,
    'rentTrackingStartMode': rentTrackingStartMode,
    'rentTrackingStartOn': rentTrackingStartOn == null
        ? null
        : LeaseManagementsRepository._dateOnly(rentTrackingStartOn!),
    'termsSchemaVersion': 1,
    'termsPayload': <String, dynamic>{},
    'createSecurityDepositAccount': createSecurityDepositAccount,
    'openingBalanceAmount': openingBalanceAmount,
    'openingBalanceEffectiveOn': openingBalanceEffectiveOn == null
        ? null
        : LeaseManagementsRepository._dateOnly(openingBalanceEffectiveOn!),
    'openingBalanceNote': openingBalanceNote,
  };
}

class PrepareMoveInResult {
  const PrepareMoveInResult({
    required this.leaseManagementId,
    required this.tenantAccountId,
    required this.leaseAgreementId,
    required this.unitId,
  });
  final int leaseManagementId;
  final int tenantAccountId;
  final int leaseAgreementId;
  final int unitId;
  factory PrepareMoveInResult.fromJson(
    Map<String, dynamic> json, {
    required int unitId,
  }) => PrepareMoveInResult(
    leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
    tenantAccountId: (json['tenantAccountId'] as num?)?.toInt() ?? 0,
    leaseAgreementId: (json['leaseAgreementId'] as num?)?.toInt() ?? 0,
    unitId: unitId,
  );
}

class GivePossessionResult {
  const GivePossessionResult({
    required this.leaseManagementId,
    required this.unitId,
    required this.possessionGivenAt,
    required this.replayed,
  });

  final int leaseManagementId;
  final int unitId;
  final DateTime possessionGivenAt;
  final bool replayed;

  factory GivePossessionResult.fromJson(Map<String, dynamic> json) =>
      GivePossessionResult(
        leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
        unitId: (json['unitId'] as num?)?.toInt() ?? 0,
        possessionGivenAt:
            DateTime.tryParse(json['possessionGivenAtUtc'] as String? ?? '') ??
            DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
        replayed: json['replayed'] as bool? ?? false,
      );
}

class RecordLeaseEndingDispositionResult {
  const RecordLeaseEndingDispositionResult({
    required this.leaseManagementId,
    required this.endingDisposition,
    required this.replayed,
    this.endingDispositionDecidedAt,
    this.noticeGivenAt,
    this.plannedMoveOutAt,
  });

  final int leaseManagementId;
  final String endingDisposition;
  final DateTime? endingDispositionDecidedAt;
  final DateTime? noticeGivenAt;
  final DateTime? plannedMoveOutAt;
  final bool replayed;

  factory RecordLeaseEndingDispositionResult.fromJson(
    Map<String, dynamic> json,
  ) => RecordLeaseEndingDispositionResult(
    leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
    endingDisposition: json['endingDisposition'] as String? ?? 'Undecided',
    endingDispositionDecidedAt: DateTime.tryParse(
      json['endingDispositionDecidedAtUtc'] as String? ?? '',
    ),
    noticeGivenAt: DateTime.tryParse(json['noticeGivenAtUtc'] as String? ?? ''),
    plannedMoveOutAt: DateTime.tryParse(
      json['plannedMoveOutAtUtc'] as String? ?? '',
    ),
    replayed: json['replayed'] as bool? ?? false,
  );
}

class EditAgreementDraftInput {
  const EditAgreementDraftInput({
    required this.draftRevision,
    required this.agreementNumber,
    required this.termType,
    required this.termStartOn,
    required this.termEndOn,
    required this.governingFromOn,
    required this.baseRentAmount,
    required this.rentDueDay,
    required this.securityDepositObligation,
    required this.lateFeeAmount,
    required this.gracePeriodDays,
    required this.termsSchemaVersion,
    required this.termsPayload,
    required this.documentTemplateId,
    required this.signers,
  });

  final int draftRevision;
  final String agreementNumber;
  final String termType;
  final DateTime termStartOn;
  final DateTime? termEndOn;
  final DateTime governingFromOn;
  final double baseRentAmount;
  final int rentDueDay;
  final double securityDepositObligation;
  final double lateFeeAmount;
  final int gracePeriodDays;
  final int termsSchemaVersion;
  final Map<String, dynamic> termsPayload;
  final int documentTemplateId;
  final List<LeaseAgreementDraftSigner> signers;

  Map<String, dynamic> toJson() => {
    'draftRevision': draftRevision,
    'agreementNumber': agreementNumber,
    'termType': termType,
    'termStartOn': LeaseManagementsRepository._dateOnly(termStartOn),
    'termEndOn': termEndOn == null
        ? null
        : LeaseManagementsRepository._dateOnly(termEndOn!),
    'governingFromOn': LeaseManagementsRepository._dateOnly(governingFromOn),
    'baseRentAmount': baseRentAmount,
    'rentDueDay': rentDueDay,
    'securityDepositObligation': securityDepositObligation,
    'lateFeeAmount': lateFeeAmount,
    'gracePeriodDays': gracePeriodDays,
    'termsSchemaVersion': termsSchemaVersion,
    'termsPayload': termsPayload,
    'documentTemplateId': documentTemplateId,
    'signers': signers.map((signer) => signer.toRequestJson()).toList(),
  };
}

class AgreementIssuancePreparation {
  const AgreementIssuancePreparation({
    required this.pendingUploadId,
    required this.draftRevision,
    required this.documentSourceVersionId,
    required this.issuanceFingerprint,
    required this.storageKey,
    required this.fileName,
    required this.fileSize,
    required this.contentSha256,
  });

  final String pendingUploadId;
  final int draftRevision;
  final int documentSourceVersionId;
  final String issuanceFingerprint;
  final String storageKey;
  final String fileName;
  final int fileSize;
  final String contentSha256;

  factory AgreementIssuancePreparation.fromJson(Map<String, dynamic> json) =>
      AgreementIssuancePreparation(
        pendingUploadId: json['pendingUploadId'] as String? ?? '',
        draftRevision: (json['draftRevision'] as num?)?.toInt() ?? 0,
        documentSourceVersionId:
            (json['documentSourceVersionId'] as num?)?.toInt() ?? 0,
        issuanceFingerprint: json['issuanceFingerprint'] as String? ?? '',
        storageKey: json['storageKey'] as String? ?? '',
        fileName: json['fileName'] as String? ?? '',
        fileSize: (json['fileSize'] as num?)?.toInt() ?? 0,
        contentSha256: json['contentSha256'] as String? ?? '',
      );
}

class IssueAgreementResult {
  const IssueAgreementResult({
    required this.signatureRequestPublicId,
    required this.leaseManagementId,
    required this.leaseAgreementId,
    required this.signatureRequestId,
    required this.issuedArtifactId,
    required this.replayed,
  });

  final String signatureRequestPublicId;
  final int leaseManagementId;
  final int leaseAgreementId;
  final int signatureRequestId;
  final int issuedArtifactId;
  final bool replayed;

  factory IssueAgreementResult.fromJson(Map<String, dynamic> json) =>
      IssueAgreementResult(
        signatureRequestPublicId:
            json['signatureRequestPublicId'] as String? ?? '',
        leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
        leaseAgreementId: (json['leaseAgreementId'] as num?)?.toInt() ?? 0,
        signatureRequestId: (json['signatureRequestId'] as num?)?.toInt() ?? 0,
        issuedArtifactId: (json['issuedArtifactId'] as num?)?.toInt() ?? 0,
        replayed: json['replayed'] as bool? ?? false,
      );
}

class ReturnPossessionPartyInput {
  const ReturnPossessionPartyInput({
    required this.leaseManagementPartyId,
    required this.disposition,
  });
  final int leaseManagementPartyId;
  final String disposition;
  Map<String, dynamic> toJson() => {
    'leaseManagementPartyId': leaseManagementPartyId,
    'disposition': disposition,
  };
}

class ReturnPossessionAccessInput {
  const ReturnPossessionAccessInput({
    required this.tenantUserAccessId,
    required this.disposition,
  });
  final int tenantUserAccessId;
  final String disposition;
  Map<String, dynamic> toJson() => {
    'tenantUserAccessId': tenantUserAccessId,
    'disposition': disposition,
  };
}

class ReturnPossessionResult {
  const ReturnPossessionResult({
    required this.leaseManagementId,
    required this.unitId,
    required this.turnoverPeriodId,
    required this.possessionReturnedAt,
    required this.replayed,
  });
  final int leaseManagementId;
  final int unitId;
  final int turnoverPeriodId;
  final DateTime possessionReturnedAt;
  final bool replayed;

  factory ReturnPossessionResult.fromJson(Map<String, dynamic> json) =>
      ReturnPossessionResult(
        leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
        unitId: (json['unitId'] as num?)?.toInt() ?? 0,
        turnoverPeriodId: (json['turnoverPeriodId'] as num?)?.toInt() ?? 0,
        possessionReturnedAt:
            DateTime.tryParse(
              json['possessionReturnedAtUtc'] as String? ?? '',
            ) ??
            DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
        replayed: json['replayed'] as bool? ?? false,
      );
}

class LeaseManagementsRepository {
  LeaseManagementsRepository(this._dio);

  final Dio _dio;

  Future<LeaseManagementListPage> listPage([
    LeaseManagementListQuery query = const LeaseManagementListQuery(),
  ]) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/page',
        queryParameters: <String, dynamic>{
          'skip': query.skip,
          'take': query.take,
          'tenantId': query.tenantId,
          'propertyId': query.propertyId,
          'unitId': query.unitId,
          'lifecycle': query.lifecycle,
          'search': query.search,
          'sort': query.sort,
        }..removeWhere((_, value) => value == null || value == ''),
      );
      return LeaseManagementListPage.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<List<LeaseManagementSummary>> list({
    int? tenantId,
    int? propertyId,
    int? unitId,
    String? lifecycle,
  }) async {
    final page = await listPage(
      LeaseManagementListQuery(
        take: 200,
        tenantId: tenantId,
        propertyId: propertyId,
        unitId: unitId,
        lifecycle: lifecycle,
      ),
    );
    return page.items;
  }

  Future<LeaseManagementDetail> get(int leaseManagementId) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId',
      );
      return LeaseManagementDetail.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<Map<String, dynamic>> addParty({
    required int leaseManagementId,
    required Map<String, dynamic> request,
    required String operationKey,
  }) => _partyMutation(
    '/lease-managements/$leaseManagementId/parties',
    request,
    operationKey,
  );

  Future<Map<String, dynamic>> changePartyRole({
    required int leaseManagementId,
    required int partyId,
    required Map<String, dynamic> request,
    required String operationKey,
  }) => _partyMutation(
    '/lease-managements/$leaseManagementId/parties/$partyId/change-role',
    request,
    operationKey,
  );

  Future<Map<String, dynamic>> endParty({
    required int leaseManagementId,
    required int partyId,
    required Map<String, dynamic> request,
    required String operationKey,
  }) => _partyMutation(
    '/lease-managements/$leaseManagementId/parties/$partyId/end',
    request,
    operationKey,
  );

  Future<Map<String, dynamic>> grantPartyAccess({
    required int leaseManagementId,
    required int partyId,
    required String reason,
    required String operationKey,
  }) => _partyMutation(
    '/lease-managements/$leaseManagementId/parties/$partyId/access',
    {'reason': reason},
    operationKey,
  );

  Future<Map<String, dynamic>> revokePartyAccess({
    required int leaseManagementId,
    required int partyId,
    required int tenantUserAccessId,
    required String reason,
    required String operationKey,
  }) => _partyMutation(
    '/lease-managements/$leaseManagementId/parties/$partyId/access/'
    '$tenantUserAccessId/revoke',
    {'reason': reason},
    operationKey,
  );

  Future<Map<String, dynamic>> _partyMutation(
    String path,
    Map<String, dynamic> request,
    String operationKey,
  ) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        path,
        data: request,
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return _required(response.data);
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseAgreementHistoryPage> agreementHistory(
    int leaseManagementId,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/agreements/page',
        queryParameters: const {'take': 200, 'sort': '-versionNumber'},
      );
      return LeaseAgreementHistoryPage.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseAddendumHistoryPage> addendumHistory(
    LeaseAddendumHistoryQuery query,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/${query.leaseManagementId}/addenda/page',
        queryParameters: {
          'skip': query.skip,
          'take': query.take,
          'sort': '-versionNumber',
        },
      );
      return LeaseAddendumHistoryPage.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseAddendumEligibleBaseAgreementPage>
  addendumEligibleBaseAgreementsPage({
    required int leaseManagementId,
    int skip = 0,
    int take = 50,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/addenda/'
        'eligible-base-agreements/page',
        queryParameters: {'skip': skip, 'take': take},
      );
      return LeaseAddendumEligibleBaseAgreementPage.fromJson(
        _required(response.data),
      );
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<List<LeaseManagementParty>> addendumSignerCandidates(
    int leaseManagementId,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/addenda/signer-candidates',
      );
      return (_required(response.data)['items'] as List? ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(LeaseManagementParty.fromJson)
          .toList();
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseQuestionResponse> ask(
    int leaseManagementId,
    String question,
  ) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/ask',
        data: {'question': question},
      );
      return LeaseQuestionResponse.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseTemplateOptionPage> leaseTemplatesPage({
    int skip = 0,
    int take = 20,
    String? search,
    int? propertyId,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/document-templates/page',
        queryParameters: {
          'kind': 'Lease',
          'status': 'Active',
          'skip': skip,
          'take': take,
          'sort': 'name',
          if (propertyId != null) 'propertyId': propertyId,
          if (search != null && search.trim().isNotEmpty)
            'search': search.trim(),
        },
      );
      return LeaseTemplateOptionPage.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseAddendumDraftDetail> addendumDraft({
    required int leaseManagementId,
    required int leaseAddendumId,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/addenda/'
        '$leaseAddendumId/draft',
      );
      return LeaseAddendumDraftDetail.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseAddendumDraftMutationResult> createAddendumDraft({
    required int leaseManagementId,
    required LeaseAddendumDraftInput input,
    required String operationKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/addenda',
        data: input.toJson(),
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return LeaseAddendumDraftMutationResult.fromJson(
        _required(response.data),
      );
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseAddendumDraftMutationResult> editAddendumDraft({
    required int leaseManagementId,
    required int leaseAddendumId,
    required LeaseAddendumDraftInput input,
    required String operationKey,
  }) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/addenda/'
        '$leaseAddendumId/draft',
        data: input.toJson(),
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return LeaseAddendumDraftMutationResult.fromJson(
        _required(response.data),
      );
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseAddendumDraftMutationResult> correctAddendumDraft({
    required int leaseManagementId,
    required int sourceAddendumId,
    required DateTime supersessionEffectiveOn,
    required String operationKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/addenda/'
        '$sourceAddendumId/correct',
        data: {'supersessionEffectiveOn': _dateOnly(supersessionEffectiveOn)},
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return LeaseAddendumDraftMutationResult.fromJson(
        _required(response.data),
      );
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<AgreementIssuancePreparation> prepareAddendumIssuance({
    required int leaseManagementId,
    required int leaseAddendumId,
    required int draftRevision,
    required String operationKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/addenda/'
        '$leaseAddendumId/issuance-preparations',
        data: {'draftRevision': draftRevision},
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return AgreementIssuancePreparation.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<IssueAddendumResult> issueAddendum({
    required int leaseManagementId,
    required int leaseAddendumId,
    required AgreementIssuancePreparation preparation,
    required String subject,
    required String operationKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/addenda/'
        '$leaseAddendumId/issue',
        data: {
          'draftRevision': preparation.draftRevision,
          'pendingUploadId': preparation.pendingUploadId,
          'documentSourceVersionId': preparation.documentSourceVersionId,
          'issuanceFingerprint': preparation.issuanceFingerprint,
          'storageKey': preparation.storageKey,
          'fileName': preparation.fileName,
          'fileSize': preparation.fileSize,
          'contentSha256': preparation.contentSha256,
          'subject': subject,
        },
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return IssueAddendumResult.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<Uint8List> addendumArtifactBytes({
    required int leaseManagementId,
    required int leaseAddendumId,
    required int artifactId,
  }) async {
    try {
      final response = await _dio.get<List<int>>(
        '/lease-managements/$leaseManagementId/addenda/'
        '$leaseAddendumId/artifacts/$artifactId',
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<PrepareMoveInResult> prepareMoveIn(
    PrepareMoveInInput input, {
    required String operationKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/prepare-move-in',
        data: input.toJson(),
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return PrepareMoveInResult.fromJson(
        _required(response.data),
        unitId: input.unitId,
      );
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<DateTime> prepareMoveInBusinessDate() async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/prepare-move-in-context',
      );
      final raw = _required(response.data)['businessDate'] as String?;
      final value = raw == null ? null : DateTime.tryParse(raw);
      if (value == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'The server did not return its canonical business date.',
        );
      }
      return value;
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<GivePossessionResult> givePossession({
    required int leaseManagementId,
    required int unitId,
    required String operationKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/give-possession',
        data: {'unitId': unitId},
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return GivePossessionResult.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<RecordLeaseEndingDispositionResult> recordEndingDisposition({
    required int leaseManagementId,
    required int unitId,
    required String disposition,
    required String decisionReason,
    required String operationKey,
    DateTime? noticeGivenAt,
    DateTime? plannedMoveOutAt,
  }) async {
    String? timestamp(DateTime? value) => value == null
        ? null
        : DateTime.utc(
            value.year,
            value.month,
            value.day,
            12,
          ).toIso8601String();
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/ending-disposition',
        data: {
          'unitId': unitId,
          'disposition': disposition,
          'noticeGivenAtUtc': timestamp(noticeGivenAt),
          'plannedMoveOutAtUtc': timestamp(plannedMoveOutAt),
          'decisionReason': decisionReason,
        },
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return RecordLeaseEndingDispositionResult.fromJson(
        _required(response.data),
      );
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseAgreementDraftDetail> agreementDraft({
    required int leaseManagementId,
    required int leaseAgreementId,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/agreements/'
        '$leaseAgreementId/draft',
      );
      return LeaseAgreementDraftDetail.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseSuccessorDraftResult> editAgreementDraft({
    required int leaseManagementId,
    required int leaseAgreementId,
    required EditAgreementDraftInput input,
    required String operationKey,
  }) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/agreements/'
        '$leaseAgreementId/draft',
        data: input.toJson(),
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return LeaseSuccessorDraftResult.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<AgreementIssuancePreparation> prepareAgreementIssuance({
    required int leaseManagementId,
    required int leaseAgreementId,
    required int draftRevision,
    required String operationKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/agreements/'
        '$leaseAgreementId/issuance-preparations',
        data: {'draftRevision': draftRevision},
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return AgreementIssuancePreparation.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<IssueAgreementResult> issueAgreement({
    required int leaseManagementId,
    required int leaseAgreementId,
    required AgreementIssuancePreparation preparation,
    required String subject,
    required String operationKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/agreements/'
        '$leaseAgreementId/issue',
        data: {
          'draftRevision': preparation.draftRevision,
          'pendingUploadId': preparation.pendingUploadId,
          'documentSourceVersionId': preparation.documentSourceVersionId,
          'issuanceFingerprint': preparation.issuanceFingerprint,
          'storageKey': preparation.storageKey,
          'fileName': preparation.fileName,
          'fileSize': preparation.fileSize,
          'contentSha256': preparation.contentSha256,
          'subject': subject,
        },
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return IssueAgreementResult.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<ReturnPossessionResult> returnPossession({
    required int leaseManagementId,
    required int unitId,
    required List<ReturnPossessionPartyInput> parties,
    required List<ReturnPossessionAccessInput> accesses,
    required String turnoverReason,
    required String operationKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/return-possession',
        data: {
          'unitId': unitId,
          'parties': parties.map((party) => party.toJson()).toList(),
          'accesses': accesses.map((access) => access.toJson()).toList(),
          'turnoverReason': turnoverReason,
        },
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return ReturnPossessionResult.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<ReturnPossessionContext> returnPossessionContext(
    int leaseManagementId,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/return-possession-context',
      );
      return ReturnPossessionContext.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseSuccessorDraftResult> createSuccessorDraft({
    required int leaseManagementId,
    required int sourceAgreementId,
    required String changeType,
    String? correctionReason,
    required DateTime termStartOn,
    required DateTime governingFromOn,
    required List<LeaseRenewalAddendumDecisionInput> addendumDecisions,
    required String operationKey,
    DateTime? termEndOn,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/agreements/'
        '$sourceAgreementId/successor-drafts',
        data: {
          'changeType': changeType,
          'correctionReason': correctionReason,
          'termStartOn': _dateOnly(termStartOn),
          if (termEndOn != null) 'termEndOn': _dateOnly(termEndOn),
          'governingFromOn': _dateOnly(governingFromOn),
          'addendumDecisions': addendumDecisions
              .map((decision) => decision.toJson())
              .toList(),
        },
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return LeaseSuccessorDraftResult.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseSuccessorDraftResult> replaceIssuedAgreementWithDraft({
    required int leaseManagementId,
    required int sourceAgreementId,
    String? voidNote,
    required String reissueReason,
    required String operationKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/agreements/'
        '$sourceAgreementId/issued-replacement-draft',
        data: {'voidNote': voidNote, 'reissueReason': reissueReason},
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return LeaseSuccessorDraftResult.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<CancelLeaseSuccessorDraftResult> cancelSuccessorDraft({
    required int leaseManagementId,
    required int leaseAgreementId,
    required String cancellationReason,
    required String operationKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/agreements/'
        '$leaseAgreementId/cancel-draft',
        data: {'cancellationReason': cancellationReason},
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      return CancelLeaseSuccessorDraftResult.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseAgreementEffectiveAddendumSeries> effectiveAddendumSeries({
    required int leaseManagementId,
    required int sourceAgreementId,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/agreements/'
        '$sourceAgreementId/effective-addendum-series',
      );
      return LeaseAgreementEffectiveAddendumSeries.fromJson(
        _required(response.data),
      );
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<Uint8List> artifactBytes({
    required int leaseManagementId,
    required int leaseAgreementId,
    required int artifactId,
  }) async {
    try {
      final response = await _dio.get<List<int>>(
        '/lease-managements/$leaseManagementId/agreements/'
        '$leaseAgreementId/artifacts/$artifactId',
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseManagementLedger> ledger(
    int leaseManagementId, {
    int take = 200,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/ledger',
        queryParameters: {'take': take},
      );
      return LeaseManagementLedger.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  static Map<String, dynamic> _required(Map<String, dynamic>? data) {
    if (data == null) {
      throw const ApiException(
        statusCode: 0,
        message: 'Empty response from server.',
      );
    }
    return data;
  }

  static String _dateOnly(DateTime value) =>
      value.toIso8601String().split('T').first;

  static String _operationKey() {
    final random = Random.secure();
    return List<int>.generate(
      16,
      (_) => random.nextInt(256),
    ).map((value) => value.toRadixString(16).padLeft(2, '0')).join();
  }

  static String newOperationKey() => _operationKey();
}

class LeaseLedgerEntry {
  const LeaseLedgerEntry({
    required this.date,
    required this.type,
    required this.id,
    required this.description,
    required this.amount,
    required this.status,
    required this.explanation,
    required this.isProrated,
    this.propertyName,
    this.counterparty,
    this.category,
    this.sourceHref,
  });

  final DateTime date;
  final String type;
  final int id;
  final String description;
  final double amount;
  final String status;
  final String explanation;
  final bool isProrated;
  final String? propertyName;
  final String? counterparty;
  final String? category;
  final String? sourceHref;

  factory LeaseLedgerEntry.fromJson(Map<String, dynamic> json) =>
      LeaseLedgerEntry(
        date: DateTime.tryParse(json['date'] as String? ?? '') ?? DateTime(0),
        type: json['type'] as String? ?? '',
        id: (json['id'] as num?)?.toInt() ?? 0,
        description: json['description'] as String? ?? '',
        amount: (json['amount'] as num?)?.toDouble() ?? 0,
        status: json['status'] as String? ?? '',
        explanation: json['explanation'] as String? ?? '',
        isProrated: json['isProrated'] as bool? ?? false,
        propertyName: json['propertyName'] as String?,
        counterparty: json['counterparty'] as String?,
        category: json['category'] as String?,
        sourceHref: json['sourceHref'] as String?,
      );
}

class LeaseManagementLedger {
  const LeaseManagementLedger({
    required this.leaseManagementId,
    required this.tenantAccountId,
    required this.accountNumber,
    required this.totalCharged,
    required this.totalPaid,
    required this.balance,
    required this.entries,
    this.tenantName,
    this.propertyName,
  });

  final int leaseManagementId;
  final int tenantAccountId;
  final String accountNumber;
  final double totalCharged;
  final double totalPaid;
  final double balance;
  final List<LeaseLedgerEntry> entries;
  final String? tenantName;
  final String? propertyName;

  factory LeaseManagementLedger.fromJson(Map<String, dynamic> json) =>
      LeaseManagementLedger(
        leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
        tenantAccountId: (json['tenantAccountId'] as num?)?.toInt() ?? 0,
        accountNumber: json['accountNumber'] as String? ?? '',
        totalCharged: (json['totalCharged'] as num?)?.toDouble() ?? 0,
        totalPaid: (json['totalPaid'] as num?)?.toDouble() ?? 0,
        balance: (json['balance'] as num?)?.toDouble() ?? 0,
        entries: (json['entries'] as List? ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(LeaseLedgerEntry.fromJson)
            .toList(),
        tenantName: json['tenantName'] as String?,
        propertyName: json['propertyName'] as String?,
      );
}

final leaseManagementsRepositoryProvider = Provider<LeaseManagementsRepository>(
  (ref) => LeaseManagementsRepository(ref.watch(dioProvider)),
);

final leaseManagementsPageProvider = FutureProvider.autoDispose
    .family<LeaseManagementListPage, LeaseManagementListQuery>(
      (ref, query) =>
          ref.watch(leaseManagementsRepositoryProvider).listPage(query),
    );

final leaseManagementDetailProvider = FutureProvider.autoDispose
    .family<LeaseManagementDetail, int>(
      (ref, id) => ref.watch(leaseManagementsRepositoryProvider).get(id),
    );

final leaseHouseholdContextProvider = FutureProvider.autoDispose
    .family<ReturnPossessionContext, int>(
      (ref, id) => ref
          .watch(leaseManagementsRepositoryProvider)
          .returnPossessionContext(id),
    );

final tenantLeaseManagementsProvider = FutureProvider.autoDispose
    .family<List<LeaseManagementSummary>, int>(
      (ref, tenantId) => ref
          .watch(leaseManagementsRepositoryProvider)
          .list(tenantId: tenantId),
    );

final leaseAgreementHistoryProvider = FutureProvider.autoDispose
    .family<LeaseAgreementHistoryPage, int>(
      (ref, id) =>
          ref.watch(leaseManagementsRepositoryProvider).agreementHistory(id),
    );

final leaseAddendumHistoryProvider = FutureProvider.autoDispose
    .family<LeaseAddendumHistoryPage, LeaseAddendumHistoryQuery>(
      (ref, query) =>
          ref.watch(leaseManagementsRepositoryProvider).addendumHistory(query),
    );

final leaseLedgerProvider = FutureProvider.autoDispose
    .family<LeaseManagementLedger, int>(
      (ref, id) => ref.watch(leaseManagementsRepositoryProvider).ledger(id),
    );
