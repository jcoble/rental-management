import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
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
    required this.leaseNumber,
    required this.status,
    required this.startDate,
    required this.endDate,
    required this.monthlyRent,
    required this.securityDeposit,
  });

  final int id;
  final String leaseNumber;
  final String status;
  final DateTime startDate;
  final DateTime endDate;
  final double monthlyRent;
  final double securityDeposit;

  factory UnitLeaseSummary.fromJson(Map<String, dynamic> json) {
    return UnitLeaseSummary(
      id: (json['id'] as num?)?.toInt() ?? 0,
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
    required this.leaseId,
    required this.type,
    required this.status,
    required this.amount,
    required this.dueDate,
    this.paidDate,
  });

  final int id;
  final int leaseId;
  final String type;
  final String status;
  final double amount;
  final DateTime dueDate;
  final DateTime? paidDate;

  factory UnitPaymentSummary.fromJson(Map<String, dynamic> json) {
    return UnitPaymentSummary(
      id: (json['id'] as num?)?.toInt() ?? 0,
      leaseId: (json['leaseId'] as num?)?.toInt() ?? 0,
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

class UnitListing {
  const UnitListing({
    required this.id,
    required this.portfolioId,
    required this.propertyId,
    required this.unitId,
    required this.channel,
    required this.status,
    required this.headline,
    required this.description,
    required this.rent,
    required this.bedrooms,
    required this.bathrooms,
    required this.isPosted,
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
    this.photoNotes,
    this.zillowListingUrl,
    this.zillowApplicationUrl,
    this.postedAtUtc,
  });

  final int id;
  final int portfolioId;
  final int propertyId;
  final int unitId;
  final String channel;
  final String status;
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
  final String? photoNotes;
  final String? zillowListingUrl;
  final String? zillowApplicationUrl;
  final DateTime? postedAtUtc;
  final bool isPosted;
  final DateTime createdAt;
  final DateTime updatedAt;

  factory UnitListing.fromJson(Map<String, dynamic> json) {
    return UnitListing(
      id: (json['id'] as num?)?.toInt() ?? 0,
      portfolioId: (json['portfolioId'] as num?)?.toInt() ?? 0,
      propertyId: (json['propertyId'] as num?)?.toInt() ?? 0,
      unitId: (json['unitId'] as num?)?.toInt() ?? 0,
      channel: json['channel'] as String? ?? '',
      status: json['status'] as String? ?? 'Draft',
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
      photoNotes: json['photoNotes'] as String?,
      zillowListingUrl: json['zillowListingUrl'] as String?,
      zillowApplicationUrl: json['zillowApplicationUrl'] as String?,
      postedAtUtc: _parseOptionalDate(json['postedAtUtc']),
      isPosted: json['isPosted'] as bool? ?? false,
      createdAt: _parseDate(json['createdAt']),
      updatedAt: _parseDate(json['updatedAt']),
    );
  }
}

class SaveUnitListingRequest {
  const SaveUnitListingRequest({
    this.status,
    this.headline,
    this.description,
    this.rent,
    this.securityDeposit,
    this.bedrooms,
    this.bathrooms,
    this.squareFeet,
    this.availableOn,
    this.leaseTerms,
    this.petPolicy,
    this.utilities,
    this.parking,
    this.amenities,
    this.photoNotes,
    this.zillowListingUrl,
    this.zillowApplicationUrl,
    this.postedAtUtc,
  });

  final String? status;
  final String? headline;
  final String? description;
  final double? rent;
  final double? securityDeposit;
  final double? bedrooms;
  final double? bathrooms;
  final int? squareFeet;
  final DateTime? availableOn;
  final String? leaseTerms;
  final String? petPolicy;
  final String? utilities;
  final String? parking;
  final String? amenities;
  final String? photoNotes;
  final String? zillowListingUrl;
  final String? zillowApplicationUrl;
  final DateTime? postedAtUtc;

  Map<String, dynamic> toJson() => {
    if (status != null) 'status': status,
    if (headline != null) 'headline': headline,
    if (description != null) 'description': description,
    if (rent != null) 'rent': rent,
    if (securityDeposit != null) 'securityDeposit': securityDeposit,
    if (bedrooms != null) 'bedrooms': bedrooms,
    if (bathrooms != null) 'bathrooms': bathrooms,
    if (squareFeet != null) 'squareFeet': squareFeet,
    if (availableOn != null)
      'availableOn': availableOn!.toUtc().toIso8601String(),
    if (leaseTerms != null) 'leaseTerms': leaseTerms,
    if (petPolicy != null) 'petPolicy': petPolicy,
    if (utilities != null) 'utilities': utilities,
    if (parking != null) 'parking': parking,
    if (amenities != null) 'amenities': amenities,
    if (photoNotes != null) 'photoNotes': photoNotes,
    if (zillowListingUrl != null) 'zillowListingUrl': zillowListingUrl,
    if (zillowApplicationUrl != null)
      'zillowApplicationUrl': zillowApplicationUrl,
    if (postedAtUtc != null)
      'postedAtUtc': postedAtUtc!.toUtc().toIso8601String(),
  };
}

typedef UnitHealthListArgs = ({String? search, int skip, int take});

class UnitsRepository {
  UnitsRepository(this._dio);

  final Dio _dio;

  Future<UnitHealthPage> listWithHealthPage({
    String? search,
    int skip = 0,
    int take = 100,
  }) async {
    final query = <String, dynamic>{
      'skip': skip,
      'take': take,
      'sort': 'propertyName',
      if (search != null && search.trim().isNotEmpty) 'search': search.trim(),
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

  Future<UnitListing?> listing(int unitId) async {
    try {
      final response = await _dio.get<Map<String, dynamic>?>(
        '/units/$unitId/listing',
      );
      final data = response.data;
      return data == null ? null : UnitListing.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<UnitListing> generateListing(int unitId) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/units/$unitId/listing/generate',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return UnitListing.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<UnitListing> saveListing(
    int unitId,
    SaveUnitListingRequest request,
  ) async {
    try {
      final response = await _dio.put<Map<String, dynamic>>(
        '/units/$unitId/listing',
        data: request.toJson(),
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return UnitListing.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final unitsRepositoryProvider = Provider<UnitsRepository>((ref) {
  return UnitsRepository(ref.watch(dioProvider));
});

final unitHealthPageProvider = FutureProvider.autoDispose
    .family<UnitHealthPage, UnitHealthListArgs>((ref, args) {
      return ref
          .watch(unitsRepositoryProvider)
          .listWithHealthPage(
            search: args.search,
            skip: args.skip,
            take: args.take,
          );
    });

final unitDashboardProvider = FutureProvider.autoDispose
    .family<UnitDashboard, int>((ref, unitId) {
      return ref.watch(unitsRepositoryProvider).dashboard(unitId);
    });

final unitListingProvider = FutureProvider.autoDispose
    .family<UnitListing?, int>((ref, unitId) {
      return ref.watch(unitsRepositoryProvider).listing(unitId);
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
