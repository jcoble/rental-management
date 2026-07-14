import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';

class LeasingToday {
  const LeasingToday({
    required this.applicationsToReview,
    required this.listingsNeedingAttention,
    required this.showingsToday,
    required this.upcomingMoveIns,
    required this.unreadConversations,
  });

  final int applicationsToReview;
  final int listingsNeedingAttention;
  final int showingsToday;
  final int upcomingMoveIns;
  final int unreadConversations;

  factory LeasingToday.fromJson(Map<String, dynamic> json) => LeasingToday(
    applicationsToReview: (json['applicationsToReview'] as num?)?.toInt() ?? 0,
    listingsNeedingAttention:
        (json['listingsNeedingAttention'] as num?)?.toInt() ?? 0,
    showingsToday: (json['showingsToday'] as num?)?.toInt() ?? 0,
    upcomingMoveIns: (json['upcomingMoveIns'] as num?)?.toInt() ?? 0,
    unreadConversations: (json['unreadConversations'] as num?)?.toInt() ?? 0,
  );
}

class LeasingPipelineItem {
  const LeasingPipelineItem({
    required this.kind,
    required this.recordId,
    required this.title,
    required this.stage,
    required this.updatedAtUtc,
    this.propertyId,
    this.unitId,
    this.propertyName,
    this.unitNumber,
    this.nextActionAtUtc,
  });

  final String kind;
  final int recordId;
  final int? propertyId;
  final int? unitId;
  final String title;
  final String? propertyName;
  final String? unitNumber;
  final String stage;
  final DateTime? nextActionAtUtc;
  final DateTime updatedAtUtc;

  factory LeasingPipelineItem.fromJson(Map<String, dynamic> json) =>
      LeasingPipelineItem(
        kind: json['kind'] as String? ?? '',
        recordId: (json['recordId'] as num).toInt(),
        propertyId: (json['propertyId'] as num?)?.toInt(),
        unitId: (json['unitId'] as num?)?.toInt(),
        title: json['title'] as String? ?? '',
        propertyName: json['propertyName'] as String?,
        unitNumber: json['unitNumber'] as String?,
        stage: json['stage'] as String? ?? '',
        nextActionAtUtc: _date(json['nextActionAtUtc']),
        updatedAtUtc:
            _date(json['updatedAtUtc']) ??
            DateTime.fromMillisecondsSinceEpoch(0),
      );
}

class LeasingRental {
  const LeasingRental({
    required this.propertyId,
    required this.unitId,
    required this.propertyName,
    required this.unitNumber,
    required this.address,
    required this.openApplicationCount,
    this.listingId,
    this.listingStatus,
    this.listingHeadline,
    this.askingRent,
    this.availableOn,
    this.nextShowingAtUtc,
  });

  final int propertyId;
  final int unitId;
  final String propertyName;
  final String unitNumber;
  final String address;
  final int? listingId;
  final String? listingStatus;
  final String? listingHeadline;
  final double? askingRent;
  final DateTime? availableOn;
  final int openApplicationCount;
  final DateTime? nextShowingAtUtc;

  factory LeasingRental.fromJson(Map<String, dynamic> json) => LeasingRental(
    propertyId: (json['propertyId'] as num).toInt(),
    unitId: (json['unitId'] as num).toInt(),
    propertyName: json['propertyName'] as String? ?? '',
    unitNumber: json['unitNumber'] as String? ?? '',
    address: json['address'] as String? ?? '',
    listingId: (json['listingId'] as num?)?.toInt(),
    listingStatus: json['listingStatus'] as String?,
    listingHeadline: json['listingHeadline'] as String?,
    askingRent: (json['askingRent'] as num?)?.toDouble(),
    availableOn: _date(json['availableOn']),
    openApplicationCount: (json['openApplicationCount'] as num?)?.toInt() ?? 0,
    nextShowingAtUtc: _date(json['nextShowingAtUtc']),
  );
}

class LeasingCalendarItem {
  const LeasingCalendarItem({
    required this.id,
    required this.title,
    required this.status,
    required this.scheduledStart,
    this.propertyId,
    this.unitId,
    this.prospectName,
    this.propertyName,
    this.unitNumber,
    this.scheduledEnd,
  });

  final int id;
  final int? propertyId;
  final int? unitId;
  final String title;
  final String? prospectName;
  final String? propertyName;
  final String? unitNumber;
  final String status;
  final DateTime scheduledStart;
  final DateTime? scheduledEnd;

  factory LeasingCalendarItem.fromJson(Map<String, dynamic> json) =>
      LeasingCalendarItem(
        id: (json['id'] as num).toInt(),
        propertyId: (json['propertyId'] as num?)?.toInt(),
        unitId: (json['unitId'] as num?)?.toInt(),
        title: json['title'] as String? ?? '',
        prospectName: json['prospectName'] as String?,
        propertyName: json['propertyName'] as String?,
        unitNumber: json['unitNumber'] as String?,
        status: json['status'] as String? ?? '',
        scheduledStart:
            _date(json['scheduledStart']) ??
            DateTime.fromMillisecondsSinceEpoch(0),
        scheduledEnd: _date(json['scheduledEnd']),
      );
}

class LeasingInboxItem {
  const LeasingInboxItem({
    required this.id,
    required this.tenantName,
    required this.subject,
    required this.lastMessageAt,
    required this.unreadCount,
    this.propertyName,
    this.lastMessagePreview,
  });

  final int id;
  final String tenantName;
  final String subject;
  final String? propertyName;
  final String? lastMessagePreview;
  final DateTime lastMessageAt;
  final int unreadCount;

  factory LeasingInboxItem.fromJson(Map<String, dynamic> json) =>
      LeasingInboxItem(
        id: (json['id'] as num).toInt(),
        tenantName: json['tenantName'] as String? ?? '',
        subject: json['subject'] as String? ?? '',
        propertyName: json['propertyName'] as String?,
        lastMessagePreview: json['lastMessagePreview'] as String?,
        lastMessageAt:
            _date(json['lastMessageAt']) ??
            DateTime.fromMillisecondsSinceEpoch(0),
        unreadCount: (json['unreadCount'] as num?)?.toInt() ?? 0,
      );
}

class LeasingPage<T> {
  const LeasingPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<T> items;
  final int totalCount;
  final int skip;
  final int take;
}

class LeasingWorkspaceRepository {
  LeasingWorkspaceRepository(this._dio);

  final Dio _dio;

  Future<LeasingToday> today() =>
      _getObject('/leasing/today', LeasingToday.fromJson);

  Future<LeasingPage<LeasingPipelineItem>> pipelinePage({
    int skip = 0,
    int take = 20,
    String? search,
  }) => _getPage(
    '/leasing/pipeline/page',
    LeasingPipelineItem.fromJson,
    skip: skip,
    take: take,
    search: search,
  );

  Future<LeasingPage<LeasingRental>> rentalsPage({
    int skip = 0,
    int take = 20,
    String? search,
  }) => _getPage(
    '/leasing/rentals/page',
    LeasingRental.fromJson,
    skip: skip,
    take: take,
    search: search,
  );

  Future<LeasingPage<LeasingCalendarItem>> calendarPage({
    int skip = 0,
    int take = 20,
    String? search,
  }) => _getPage(
    '/leasing/calendar/page',
    LeasingCalendarItem.fromJson,
    skip: skip,
    take: take,
    search: search,
  );

  Future<LeasingPage<LeasingInboxItem>> inboxPage({
    int skip = 0,
    int take = 20,
    String? search,
  }) => _getPage(
    '/leasing/inbox/page',
    LeasingInboxItem.fromJson,
    skip: skip,
    take: take,
    search: search,
    sort: '-lastMessageAt',
  );

  Future<T> _getObject<T>(
    String path,
    T Function(Map<String, dynamic>) fromJson,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(path);
      final data = response.data;
      if (data == null) {
        throw const ApiException(statusCode: 0, message: 'Empty response.');
      }
      return fromJson(data);
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeasingPage<T>> _getPage<T>(
    String path,
    T Function(Map<String, dynamic>) fromJson, {
    required int skip,
    required int take,
    String? search,
    String? sort,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        path,
        queryParameters: {
          'skip': skip,
          'take': take,
          if (search != null && search.trim().isNotEmpty)
            'search': search.trim(),
          if (sort != null) 'sort': sort,
        },
      );
      final data = response.data ?? const <String, dynamic>{};
      final rawItems = data['items'];
      return LeasingPage<T>(
        items: (rawItems is List ? rawItems : const [])
            .whereType<Map<String, dynamic>>()
            .map(fromJson)
            .toList(growable: false),
        totalCount: (data['totalCount'] as num?)?.toInt() ?? 0,
        skip: (data['skip'] as num?)?.toInt() ?? skip,
        take: (data['take'] as num?)?.toInt() ?? take,
      );
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }
}

DateTime? _date(Object? value) =>
    value is String ? DateTime.tryParse(value) : null;

final leasingWorkspaceRepositoryProvider = Provider<LeasingWorkspaceRepository>(
  (ref) => LeasingWorkspaceRepository(ref.watch(dioProvider)),
);
