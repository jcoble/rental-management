import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';
import '../../core/models/unit.dart';

class UnitHealth {
  const UnitHealth({
    required this.id,
    required this.propertyId,
    required this.propertyName,
    required this.unitNumber,
    required this.status,
    required this.marketRent,
    required this.openWorkOrderCount,
    required this.docsNeedingReviewCount,
    required this.simpleStage,
    this.leaseEndsInDays,
  });

  final int id;
  final int propertyId;
  final String propertyName;
  final String unitNumber;
  final String status;
  final double marketRent;
  final int openWorkOrderCount;
  final int docsNeedingReviewCount;
  final String simpleStage;
  final int? leaseEndsInDays;

  factory UnitHealth.fromJson(Map<String, dynamic> json) {
    return UnitHealth(
      id: (json['id'] as num).toInt(),
      propertyId: (json['propertyId'] as num).toInt(),
      propertyName: json['propertyName'] as String? ?? '',
      unitNumber: json['unitNumber'] as String? ?? '',
      status: json['status'] as String? ?? '',
      marketRent: (json['marketRent'] as num?)?.toDouble() ?? 0,
      openWorkOrderCount: (json['openWorkOrderCount'] as num?)?.toInt() ?? 0,
      docsNeedingReviewCount:
          (json['docsNeedingReviewCount'] as num?)?.toInt() ?? 0,
      simpleStage: json['simpleStage'] as String? ?? '',
      leaseEndsInDays: (json['leaseEndsInDays'] as num?)?.toInt(),
    );
  }
}

class UnitHealthPage {
  const UnitHealthPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<UnitHealth> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory UnitHealthPage.fromJson(Map<String, dynamic> json) {
    final items = (json['items'] as List<dynamic>? ?? [])
        .whereType<Map<String, dynamic>>()
        .map(UnitHealth.fromJson)
        .toList();

    return UnitHealthPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

class UnitDashboard {
  const UnitDashboard({
    required this.unit,
    required this.propertyName,
    required this.lifecycleStage,
    required this.nextBestAction,
    required this.header,
    required this.overview,
    this.currentLease,
    this.currentTenant,
    this.currentTenants = const [],
    this.turnover = const UnitTurnoverSummary(),
  });

  final Unit unit;
  final String propertyName;
  final String lifecycleStage;
  final UnitNextBestAction nextBestAction;
  final UnitDashboardHeader header;
  final UnitLeaseSummary? currentLease;
  final UnitTenantSummary? currentTenant;
  final List<UnitTenantSummary> currentTenants;
  final UnitDashboardOverview overview;
  final UnitTurnoverSummary turnover;

  factory UnitDashboard.fromJson(Map<String, dynamic> json) {
    final leaseJson = _jsonObjectOrNull(json['currentLease']);
    final tenantJson = _jsonObjectOrNull(json['currentTenant']);
    final tenants = _jsonList(
      json['currentTenants'],
    ).map(UnitTenantSummary.fromJson).where((tenant) => tenant.id > 0).toList();

    return UnitDashboard(
      unit: Unit.fromJson(_jsonObject(json['unit'])),
      propertyName: json['propertyName'] as String? ?? '',
      lifecycleStage: json['lifecycleStage'] as String? ?? '',
      nextBestAction: UnitNextBestAction.fromJson(
        _jsonObject(json['nextBestAction']),
      ),
      header: UnitDashboardHeader.fromJson(_jsonObject(json['header'])),
      currentLease: leaseJson == null
          ? null
          : UnitLeaseSummary.fromJson(leaseJson),
      currentTenant: tenantJson == null
          ? null
          : UnitTenantSummary.fromJson(tenantJson),
      currentTenants: tenants,
      overview: UnitDashboardOverview.fromJson(_jsonObject(json['overview'])),
      turnover: UnitTurnoverSummary.fromJson(_jsonObject(json['turnover'])),
    );
  }
}

class UnitTurnoverSummary {
  const UnitTurnoverSummary({
    this.status = 'NotStarted',
    this.totalTaskCount = 0,
    this.openTaskCount = 0,
    this.completedTaskCount = 0,
    this.receiptCount = 0,
    this.estimatedCost = 0,
    this.actualCost = 0,
    this.startedAt,
    this.targetReadyDate,
    this.lastActivityAt,
    this.daysInTurnover,
  });

  final String status;
  final int totalTaskCount;
  final int openTaskCount;
  final int completedTaskCount;
  final int receiptCount;
  final double estimatedCost;
  final double actualCost;
  final DateTime? startedAt;
  final DateTime? targetReadyDate;
  final DateTime? lastActivityAt;
  final int? daysInTurnover;

  factory UnitTurnoverSummary.fromJson(Map<String, dynamic> json) {
    return UnitTurnoverSummary(
      status: json['status'] as String? ?? 'NotStarted',
      totalTaskCount: (json['totalTaskCount'] as num?)?.toInt() ?? 0,
      openTaskCount: (json['openTaskCount'] as num?)?.toInt() ?? 0,
      completedTaskCount: (json['completedTaskCount'] as num?)?.toInt() ?? 0,
      receiptCount: (json['receiptCount'] as num?)?.toInt() ?? 0,
      estimatedCost: (json['estimatedCost'] as num?)?.toDouble() ?? 0,
      actualCost: (json['actualCost'] as num?)?.toDouble() ?? 0,
      startedAt: _parseOptionalDate(json['startedAt']),
      targetReadyDate: _parseOptionalDate(json['targetReadyDate']),
      lastActivityAt: _parseOptionalDate(json['lastActivityAt']),
      daysInTurnover: (json['daysInTurnover'] as num?)?.toInt(),
    );
  }
}

class UnitNextBestAction {
  const UnitNextBestAction({required this.label, required this.href});

  final String label;
  final String href;

  factory UnitNextBestAction.fromJson(Map<String, dynamic> json) {
    return UnitNextBestAction(
      label: json['label'] as String? ?? '',
      href: json['href'] as String? ?? '',
    );
  }
}

class UnitDashboardHeader {
  const UnitDashboardHeader({
    required this.rentState,
    required this.outstandingRentBalance,
    required this.openWorkOrderCount,
    required this.docsNeedingReviewCount,
    this.leaseEndsInDays,
    this.currentTenantName,
  });

  final String rentState;
  final double outstandingRentBalance;
  final int openWorkOrderCount;
  final int docsNeedingReviewCount;
  final int? leaseEndsInDays;
  final String? currentTenantName;

  factory UnitDashboardHeader.fromJson(Map<String, dynamic> json) {
    return UnitDashboardHeader(
      rentState: json['rentState'] as String? ?? 'NoLease',
      outstandingRentBalance:
          (json['outstandingRentBalance'] as num?)?.toDouble() ?? 0,
      openWorkOrderCount: (json['openWorkOrderCount'] as num?)?.toInt() ?? 0,
      docsNeedingReviewCount:
          (json['docsNeedingReviewCount'] as num?)?.toInt() ?? 0,
      leaseEndsInDays: (json['leaseEndsInDays'] as num?)?.toInt(),
      currentTenantName: json['currentTenantName'] as String?,
    );
  }
}

class UnitLeaseSummary {
  const UnitLeaseSummary({
    required this.id,
    required this.leaseManagementId,
    this.tenantAccountId,
    required this.leaseNumber,
    required this.status,
    required this.startDate,
    required this.endDate,
    required this.monthlyRent,
    required this.securityDeposit,
  });

  final int id;
  final int leaseManagementId;
  final int? tenantAccountId;
  final String leaseNumber;
  final String status;
  final DateTime startDate;
  final DateTime endDate;
  final double monthlyRent;
  final double securityDeposit;

  factory UnitLeaseSummary.fromJson(Map<String, dynamic> json) {
    return UnitLeaseSummary(
      id: (json['id'] as num?)?.toInt() ?? 0,
      leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
      tenantAccountId: (json['tenantAccountId'] as num?)?.toInt(),
      leaseNumber: json['leaseNumber'] as String? ?? '',
      status: json['status'] as String? ?? '',
      startDate: _parseDate(json['startDate']),
      endDate: _parseDate(json['endDate']),
      monthlyRent: (json['monthlyRent'] as num?)?.toDouble() ?? 0,
      securityDeposit: (json['securityDeposit'] as num?)?.toDouble() ?? 0,
    );
  }
}

class UnitTenantSummary {
  const UnitTenantSummary({
    required this.id,
    required this.name,
    this.email,
    this.phone,
  });

  final int id;
  final String name;
  final String? email;
  final String? phone;

  factory UnitTenantSummary.fromJson(Map<String, dynamic> json) {
    return UnitTenantSummary(
      id: (json['id'] as num?)?.toInt() ?? 0,
      name: json['name'] as String? ?? '',
      email: json['email'] as String?,
      phone: json['phone'] as String?,
    );
  }
}

class UnitDashboardOverview {
  const UnitDashboardOverview({
    required this.recentPayments,
    required this.openWorkOrders,
    required this.pendingDocs,
    required this.upcomingAppointments,
  });

  final List<UnitPaymentSummary> recentPayments;
  final List<UnitWorkOrderSummary> openWorkOrders;
  final List<UnitDocumentSummary> pendingDocs;
  final List<UnitAppointmentSummary> upcomingAppointments;

  factory UnitDashboardOverview.fromJson(Map<String, dynamic> json) {
    return UnitDashboardOverview(
      recentPayments: _jsonList(
        json['recentPayments'],
      ).map(UnitPaymentSummary.fromJson).toList(),
      openWorkOrders: _jsonList(
        json['openWorkOrders'],
      ).map(UnitWorkOrderSummary.fromJson).toList(),
      pendingDocs: _jsonList(
        json['pendingDocs'],
      ).map(UnitDocumentSummary.fromJson).toList(),
      upcomingAppointments: _jsonList(
        json['upcomingAppointments'],
      ).map(UnitAppointmentSummary.fromJson).toList(),
    );
  }
}

class UnitPaymentSummary {
  const UnitPaymentSummary({
    required this.id,
    required this.tenantAccountId,
    required this.leaseManagementId,
    this.leaseAgreementId,
    required this.type,
    required this.status,
    required this.amount,
    required this.dueDate,
    this.paidDate,
  });

  final int id;
  final int tenantAccountId;
  final int leaseManagementId;

  /// Immutable agreement provenance for the receipt; never an account selector.
  final int? leaseAgreementId;
  final String type;
  final String status;
  final double amount;
  final DateTime dueDate;
  final DateTime? paidDate;

  factory UnitPaymentSummary.fromJson(Map<String, dynamic> json) {
    return UnitPaymentSummary(
      id: (json['id'] as num?)?.toInt() ?? 0,
      tenantAccountId: (json['tenantAccountId'] as num).toInt(),
      leaseManagementId: (json['leaseManagementId'] as num).toInt(),
      leaseAgreementId: (json['leaseAgreementId'] as num?)?.toInt(),
      type: json['type'] as String? ?? '',
      status: json['status'] as String? ?? '',
      amount: (json['amount'] as num?)?.toDouble() ?? 0,
      dueDate: _parseDate(json['dueDate']),
      paidDate: _parseOptionalDate(json['paidDate']),
    );
  }
}

class UnitWorkOrderSummary {
  const UnitWorkOrderSummary({
    required this.id,
    required this.title,
    required this.status,
    required this.priority,
    required this.requestedAt,
  });

  final int id;
  final String title;
  final String status;
  final String priority;
  final DateTime requestedAt;

  factory UnitWorkOrderSummary.fromJson(Map<String, dynamic> json) {
    return UnitWorkOrderSummary(
      id: (json['id'] as num?)?.toInt() ?? 0,
      title: json['title'] as String? ?? '',
      status: json['status'] as String? ?? '',
      priority: json['priority'] as String? ?? '',
      requestedAt: _parseDate(json['requestedAt']),
    );
  }
}

class UnitDocumentSummary {
  const UnitDocumentSummary({
    required this.id,
    required this.fileName,
    required this.contentType,
    required this.uploadedAt,
    this.entityType,
    this.entityId,
  });

  final int id;
  final String fileName;
  final String contentType;
  final String? entityType;
  final int? entityId;
  final DateTime uploadedAt;

  factory UnitDocumentSummary.fromJson(Map<String, dynamic> json) {
    return UnitDocumentSummary(
      id: (json['id'] as num?)?.toInt() ?? 0,
      fileName: json['fileName'] as String? ?? '',
      contentType: json['contentType'] as String? ?? '',
      entityType: json['entityType'] as String?,
      entityId: (json['entityId'] as num?)?.toInt(),
      uploadedAt: _parseDate(json['uploadedAt']),
    );
  }
}

class UnitAppointmentSummary {
  const UnitAppointmentSummary({
    required this.id,
    required this.title,
    required this.type,
    required this.status,
    required this.scheduledStart,
    this.assignedTo,
  });

  final int id;
  final String title;
  final String type;
  final String status;
  final DateTime scheduledStart;
  final String? assignedTo;

  factory UnitAppointmentSummary.fromJson(Map<String, dynamic> json) {
    return UnitAppointmentSummary(
      id: (json['id'] as num?)?.toInt() ?? 0,
      title: json['title'] as String? ?? '',
      type: json['type'] as String? ?? '',
      status: json['status'] as String? ?? '',
      scheduledStart: _parseDate(json['scheduledStart']),
      assignedTo: json['assignedTo'] as String?,
    );
  }
}

class ListingPhoto {
  const ListingPhoto({
    required this.id,
    required this.position,
    required this.category,
    this.caption,
    this.storedFileId,
    this.fileName,
    this.sha256,
  });

  final int id;
  final int position;
  final String category;
  final String? caption;
  final int? storedFileId;
  final String? fileName;
  final String? sha256;

  factory ListingPhoto.fromJson(Map<String, dynamic> json) => ListingPhoto(
    id: (json['id'] as num?)?.toInt() ?? 0,
    position: (json['position'] as num?)?.toInt() ?? 0,
    category: json['category'] as String? ?? '',
    caption: json['caption'] as String?,
    storedFileId: (json['storedFileId'] as num?)?.toInt(),
    fileName: json['fileName'] as String?,
    sha256: json['sha256'] as String?,
  );
}

class ExternalListingSignal {
  const ExternalListingSignal({
    required this.id,
    required this.signalType,
    required this.disposition,
    required this.receivedAtUtc,
    this.suggestedExternalListingId,
    this.suggestedListingUrl,
    this.suggestedExternalStatus,
  });

  final int id;
  final String signalType;
  final String? suggestedExternalListingId;
  final String? suggestedListingUrl;
  final String? suggestedExternalStatus;
  final String disposition;
  final DateTime receivedAtUtc;

  factory ExternalListingSignal.fromJson(Map<String, dynamic> json) =>
      ExternalListingSignal(
        id: (json['id'] as num?)?.toInt() ?? 0,
        signalType: json['signalType'] as String? ?? '',
        suggestedExternalListingId:
            json['suggestedExternalListingId'] as String?,
        suggestedListingUrl: json['suggestedListingUrl'] as String?,
        suggestedExternalStatus: json['suggestedExternalStatus'] as String?,
        disposition: json['disposition'] as String? ?? 'Unconfirmed',
        receivedAtUtc: _parseDate(json['receivedAtUtc']),
      );
}

class ListingPublication {
  const ListingPublication({
    required this.id,
    required this.providerKey,
    required this.mode,
    required this.status,
    required this.copyConfirmed,
    required this.termsConfirmed,
    required this.photosConfirmed,
    required this.providerWorkspaceOpened,
    required this.needsRepublish,
    required this.channelAvailable,
    required this.channelState,
    required this.unconfirmedSignals,
    this.externalListingId,
    this.listingUrl,
    this.applicationUrl,
    this.managementUrl,
    this.lastConfirmedExternalStatus,
    this.lastConfirmedAtUtc,
    this.publishedContentVersion,
    this.lastDeliveryKey,
    this.lastDeliveryStatus,
    this.lastDeliveryError,
    this.lastDeliveryAttemptAtUtc,
    this.channelUnavailableReason,
  });

  final int id;
  final String providerKey;
  final String mode;
  final String status;
  final String? externalListingId;
  final String? listingUrl;
  final String? applicationUrl;
  final String? managementUrl;
  final String? lastConfirmedExternalStatus;
  final DateTime? lastConfirmedAtUtc;
  final bool copyConfirmed;
  final bool termsConfirmed;
  final bool photosConfirmed;
  final bool providerWorkspaceOpened;
  final bool needsRepublish;
  final bool channelAvailable;
  final String channelState;
  final String? channelUnavailableReason;
  final int? publishedContentVersion;
  final String? lastDeliveryKey;
  final String? lastDeliveryStatus;
  final String? lastDeliveryError;
  final DateTime? lastDeliveryAttemptAtUtc;
  final List<ExternalListingSignal> unconfirmedSignals;

  factory ListingPublication.fromJson(
    Map<String, dynamic> json,
  ) => ListingPublication(
    id: (json['id'] as num?)?.toInt() ?? 0,
    providerKey: json['providerKey'] as String? ?? '',
    mode: json['mode'] as String? ?? 'Guided',
    status: json['status'] as String? ?? 'Draft',
    externalListingId: json['externalListingId'] as String?,
    listingUrl: json['listingUrl'] as String?,
    applicationUrl: json['applicationUrl'] as String?,
    managementUrl: json['managementUrl'] as String?,
    lastConfirmedExternalStatus: json['lastConfirmedExternalStatus'] as String?,
    lastConfirmedAtUtc: _parseOptionalDate(json['lastConfirmedAtUtc']),
    copyConfirmed: json['copyConfirmed'] as bool? ?? false,
    termsConfirmed: json['termsConfirmed'] as bool? ?? false,
    photosConfirmed: json['photosConfirmed'] as bool? ?? false,
    providerWorkspaceOpened: json['providerWorkspaceOpened'] as bool? ?? false,
    needsRepublish: json['needsRepublish'] as bool? ?? false,
    channelAvailable: json['channelAvailable'] as bool? ?? false,
    channelState: json['channelState'] as String? ?? 'Unavailable',
    channelUnavailableReason: json['channelUnavailableReason'] as String?,
    publishedContentVersion: (json['publishedContentVersion'] as num?)?.toInt(),
    lastDeliveryKey: json['lastDeliveryKey'] as String?,
    lastDeliveryStatus: json['lastDeliveryStatus'] as String?,
    lastDeliveryError: json['lastDeliveryError'] as String?,
    lastDeliveryAttemptAtUtc: _parseOptionalDate(
      json['lastDeliveryAttemptAtUtc'],
    ),
    unconfirmedSignals: _jsonList(
      json['unconfirmedSignals'],
    ).map(ExternalListingSignal.fromJson).toList(),
  );
}

class ListingWorkspace {
  const ListingWorkspace({
    required this.id,
    required this.portfolioId,
    required this.propertyId,
    required this.unitId,
    required this.status,
    required this.contentVersion,
    required this.headline,
    required this.description,
    required this.rent,
    required this.bedrooms,
    required this.bathrooms,
    required this.photoManifest,
    required this.publications,
    required this.signedLeaseImportUrl,
    required this.createdAt,
    required this.updatedAt,
    this.securityDeposit,
    this.squareFeet,
    this.availableOn,
    this.leaseTerms,
    this.petPolicy,
    this.utilities,
    this.parking,
    this.amenities,
  });

  final int id;
  final int portfolioId;
  final int propertyId;
  final int unitId;
  final String status;
  final int contentVersion;
  final String headline;
  final String description;
  final double rent;
  final double? securityDeposit;
  final double bedrooms;
  final double bathrooms;
  final int? squareFeet;
  final DateTime? availableOn;
  final String? leaseTerms;
  final String? petPolicy;
  final String? utilities;
  final String? parking;
  final String? amenities;
  final List<ListingPhoto> photoManifest;
  final List<ListingPublication> publications;
  final String signedLeaseImportUrl;
  final DateTime createdAt;
  final DateTime updatedAt;

  factory ListingWorkspace.fromJson(Map<String, dynamic> json) {
    return ListingWorkspace(
      id: (json['id'] as num?)?.toInt() ?? 0,
      portfolioId: (json['portfolioId'] as num?)?.toInt() ?? 0,
      propertyId: (json['propertyId'] as num?)?.toInt() ?? 0,
      unitId: (json['unitId'] as num?)?.toInt() ?? 0,
      status: json['status'] as String? ?? 'Draft',
      contentVersion: (json['contentVersion'] as num?)?.toInt() ?? 0,
      headline: json['headline'] as String? ?? '',
      description: json['description'] as String? ?? '',
      rent: (json['rent'] as num?)?.toDouble() ?? 0,
      securityDeposit: (json['securityDeposit'] as num?)?.toDouble(),
      bedrooms: (json['bedrooms'] as num?)?.toDouble() ?? 0,
      bathrooms: (json['bathrooms'] as num?)?.toDouble() ?? 0,
      squareFeet: (json['squareFeet'] as num?)?.toInt(),
      availableOn: _parseOptionalDate(json['availableOn']),
      leaseTerms: json['leaseTerms'] as String?,
      petPolicy: json['petPolicy'] as String?,
      utilities: json['utilities'] as String?,
      parking: json['parking'] as String?,
      amenities: json['amenities'] as String?,
      photoManifest: _jsonList(
        json['photoManifest'],
      ).map(ListingPhoto.fromJson).toList(),
      publications: _jsonList(
        json['publications'],
      ).map(ListingPublication.fromJson).toList(),
      signedLeaseImportUrl: json['signedLeaseImportUrl'] as String? ?? '',
      createdAt: _parseDate(json['createdAt']),
      updatedAt: _parseDate(json['updatedAt']),
    );
  }
}

class SaveGuidedPublicationRequest {
  const SaveGuidedPublicationRequest({
    this.status,
    this.externalListingId,
    this.listingUrl,
    this.applicationUrl,
    this.managementUrl,
    this.lastConfirmedExternalStatus,
    this.lastConfirmedAtUtc,
    this.copyConfirmed,
    this.termsConfirmed,
    this.photosConfirmed,
    this.providerWorkspaceOpened,
    this.markCurrentVersionPublished,
  });

  final String? status;
  final String? externalListingId;
  final String? listingUrl;
  final String? applicationUrl;
  final String? managementUrl;
  final String? lastConfirmedExternalStatus;
  final DateTime? lastConfirmedAtUtc;
  final bool? copyConfirmed;
  final bool? termsConfirmed;
  final bool? photosConfirmed;
  final bool? providerWorkspaceOpened;
  final bool? markCurrentVersionPublished;

  Map<String, dynamic> toJson() => {
    if (status != null) 'status': status,
    if (externalListingId != null) 'externalListingId': externalListingId,
    if (listingUrl != null) 'listingUrl': listingUrl,
    if (applicationUrl != null) 'applicationUrl': applicationUrl,
    if (managementUrl != null) 'managementUrl': managementUrl,
    if (lastConfirmedExternalStatus != null)
      'lastConfirmedExternalStatus': lastConfirmedExternalStatus,
    if (lastConfirmedAtUtc != null)
      'lastConfirmedAtUtc': lastConfirmedAtUtc!.toUtc().toIso8601String(),
    if (copyConfirmed != null) 'copyConfirmed': copyConfirmed,
    if (termsConfirmed != null) 'termsConfirmed': termsConfirmed,
    if (photosConfirmed != null) 'photosConfirmed': photosConfirmed,
    if (providerWorkspaceOpened != null)
      'providerWorkspaceOpened': providerWorkspaceOpened,
    if (markCurrentVersionPublished != null)
      'markCurrentVersionPublished': markCurrentVersionPublished,
  };
}

class SaveListingWorkspaceRequest {
  const SaveListingWorkspaceRequest({
    this.status,
    this.headline,
    this.description,
    this.rent,
    this.securityDeposit,
    this.availableOn,
    this.leaseTerms,
    this.petPolicy,
    this.utilities,
    this.parking,
    this.amenities,
    this.zillowGuided,
  });

  final String? status;
  final String? headline;
  final String? description;
  final double? rent;
  final double? securityDeposit;
  final DateTime? availableOn;
  final String? leaseTerms;
  final String? petPolicy;
  final String? utilities;
  final String? parking;
  final String? amenities;
  final SaveGuidedPublicationRequest? zillowGuided;

  Map<String, dynamic> toJson() => {
    if (status != null) 'status': status,
    if (headline != null) 'headline': headline,
    if (description != null) 'description': description,
    if (rent != null) 'rent': rent,
    if (securityDeposit != null) 'securityDeposit': securityDeposit,
    if (availableOn != null)
      'availableOn': availableOn!.toUtc().toIso8601String(),
    if (leaseTerms != null) 'leaseTerms': leaseTerms,
    if (petPolicy != null) 'petPolicy': petPolicy,
    if (utilities != null) 'utilities': utilities,
    if (parking != null) 'parking': parking,
    if (amenities != null) 'amenities': amenities,
    if (zillowGuided != null) 'zillowGuided': zillowGuided!.toJson(),
  };
}

class UnitHealthListQuery {
  const UnitHealthListQuery({
    this.skip = 0,
    this.take = 20,
    this.search,
    this.sort = 'propertyName',
    this.status,
    this.stage,
  });

  final int skip;
  final int take;
  final String? search;
  final String sort;
  final String? status;
  final String? stage;

  @override
  bool operator ==(Object other) {
    return other is UnitHealthListQuery &&
        other.skip == skip &&
        other.take == take &&
        other.search == search &&
        other.sort == sort &&
        other.status == status &&
        other.stage == stage;
  }

  @override
  int get hashCode => Object.hash(skip, take, search, sort, status, stage);
}

class UnitsRepository {
  UnitsRepository(this._dio);

  final Dio _dio;

  Future<UnitHealthPage> listWithHealthPage({
    String? search,
    String sort = 'propertyName',
    String? status,
    String? stage,
    int skip = 0,
    int take = 20,
  }) async {
    final query = <String, dynamic>{
      'skip': skip,
      'take': take,
      'sort': sort,
      if (search != null && search.trim().isNotEmpty) 'search': search.trim(),
      if (status != null && status.trim().isNotEmpty) 'status': status.trim(),
      if (stage != null && stage.trim().isNotEmpty) 'stage': stage.trim(),
    };

    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/units/list-with-health/page',
        queryParameters: query,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return UnitHealthPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<UnitDashboard> dashboard(int unitId) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/units/$unitId/dashboard',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return UnitDashboard.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ListingWorkspace?> listingWorkspace(int unitId) async {
    try {
      final response = await _dio.get<Map<String, dynamic>?>(
        '/units/$unitId/listing-workspace',
      );
      final data = response.data;
      return data == null ? null : ListingWorkspace.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ListingWorkspace> generateListingWorkspace(int unitId) async {
    try {
      final response = await IdempotentMutation.run(
        'listing:$unitId:generate',
        (operationKey) => _dio.post<Map<String, dynamic>>(
          '/units/$unitId/listing-workspace/generate',
          options: _listingMutationOptions(operationKey),
        ),
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ListingWorkspace.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ListingWorkspace> saveListingWorkspace(
    int unitId,
    SaveListingWorkspaceRequest request,
  ) async {
    try {
      final response = await IdempotentMutation.run(
        'listing:$unitId:save:${request.toJson()}',
        (operationKey) => _dio.put<Map<String, dynamic>>(
          '/units/$unitId/listing-workspace',
          data: request.toJson(),
          options: _listingMutationOptions(operationKey),
        ),
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ListingWorkspace.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ListingWorkspace> attachListingPhoto({
    required int unitId,
    required int photoId,
    required List<int> bytes,
    required String fileName,
    required String contentType,
    required String clientOperationId,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/units/$unitId/listing-workspace/photos/$photoId/content',
        data: FormData.fromMap({
          'file': MultipartFile.fromBytes(
            bytes,
            filename: fileName,
            contentType: DioMediaType.parse(contentType),
          ),
        }),
        options: _listingMutationOptions(clientOperationId),
      );
      return ListingWorkspace.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ListingWorkspace> removeListingPhoto(int unitId, int photoId) async {
    try {
      final response = await IdempotentMutation.run(
        'listing:$unitId:photo:$photoId:remove',
        (operationKey) => _dio.delete<Map<String, dynamic>>(
          '/units/$unitId/listing-workspace/photos/$photoId/content',
          options: _listingMutationOptions(operationKey),
        ),
      );
      return ListingWorkspace.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ListingWorkspace> reorderListingPhotos(
    int unitId,
    List<int> photoIds,
  ) async {
    try {
      final response = await IdempotentMutation.run(
        "listing:$unitId:photos:reorder:${photoIds.join(',')}",
        (operationKey) => _dio.put<Map<String, dynamic>>(
          '/units/$unitId/listing-workspace/photos/order',
          data: {'photoIds': photoIds},
          options: _listingMutationOptions(operationKey),
        ),
      );
      return ListingWorkspace.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ListingWorkspace> confirmListingSignal(
    int unitId,
    int signalId, {
    required bool accept,
  }) async {
    try {
      final response = await IdempotentMutation.run(
        'listing:$unitId:signal:$signalId:confirm:$accept',
        (operationKey) => _dio.post<Map<String, dynamic>>(
          '/units/$unitId/listing-workspace/signals/$signalId/confirm',
          queryParameters: {'accept': accept},
          options: _listingMutationOptions(operationKey),
        ),
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ListingWorkspace.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ListingWorkspace> prepareConnectedListing(
    int unitId,
    int publicationId,
  ) async {
    try {
      final response = await IdempotentMutation.run(
        'listing:$unitId:publication:$publicationId:prepare',
        (operationKey) => _dio.post<Map<String, dynamic>>(
          '/units/$unitId/listing-workspace/publications/$publicationId/connected/prepare',
          options: _listingMutationOptions(operationKey),
        ),
      );
      return ListingWorkspace.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ListingWorkspace> runConnectedListingCommand(
    int unitId,
    int publicationId,
    String action,
    String clientOperationId,
  ) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/units/$unitId/listing-workspace/publications/$publicationId/connected/$action',
        options: _listingMutationOptions(clientOperationId),
      );
      return ListingWorkspace.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  static Options _listingMutationOptions(String operationKey) =>
      Options(headers: {'Idempotency-Key': operationKey});
}

final unitsRepositoryProvider = Provider<UnitsRepository>((ref) {
  return UnitsRepository(ref.watch(dioProvider));
});

final unitHealthPageProvider = FutureProvider.autoDispose
    .family<UnitHealthPage, UnitHealthListQuery>((ref, query) {
      return ref
          .watch(unitsRepositoryProvider)
          .listWithHealthPage(
            search: query.search,
            sort: query.sort,
            status: query.status,
            stage: query.stage,
            skip: query.skip,
            take: query.take,
          );
    });

final unitDashboardProvider = FutureProvider.autoDispose
    .family<UnitDashboard, int>((ref, unitId) {
      return ref.watch(unitsRepositoryProvider).dashboard(unitId);
    });

final unitListingWorkspaceProvider = FutureProvider.autoDispose
    .family<ListingWorkspace?, int>((ref, unitId) {
      return ref.watch(unitsRepositoryProvider).listingWorkspace(unitId);
    });

Map<String, dynamic> _jsonObject(Object? value) {
  if (value is Map<String, dynamic>) return value;
  if (value is Map) return Map<String, dynamic>.from(value);
  return <String, dynamic>{};
}

Map<String, dynamic>? _jsonObjectOrNull(Object? value) {
  if (value == null) return null;
  return _jsonObject(value);
}

List<Map<String, dynamic>> _jsonList(Object? value) {
  final raw = value is List ? value : const [];
  return [
    for (final item in raw)
      if (item is Map<String, dynamic>)
        item
      else if (item is Map)
        Map<String, dynamic>.from(item),
  ];
}

DateTime _parseDate(Object? value) {
  if (value is String && value.isNotEmpty) {
    return DateTime.tryParse(value)?.toLocal() ?? DateTime(0);
  }
  return DateTime(0);
}

DateTime? _parseOptionalDate(Object? value) {
  if (value is String && value.isNotEmpty) {
    return DateTime.tryParse(value)?.toLocal();
  }
  return null;
}
