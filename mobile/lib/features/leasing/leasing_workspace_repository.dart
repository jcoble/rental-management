import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';

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

class LeasingRentalDetail {
  const LeasingRentalDetail({
    required this.propertyId,
    required this.unitId,
    required this.propertyName,
    required this.unitNumber,
    required this.address,
    required this.canViewApplications,
    required this.openApplicationCount,
    required this.canViewShowings,
    this.listingId,
    this.listingStatus,
    this.listingHeadline,
    this.listingDescription,
    this.askingRent,
    this.securityDeposit,
    this.availableOn,
    this.leaseTerms,
    this.petPolicy,
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
  final String? listingDescription;
  final double? askingRent;
  final double? securityDeposit;
  final DateTime? availableOn;
  final String? leaseTerms;
  final String? petPolicy;
  final bool canViewApplications;
  final int openApplicationCount;
  final bool canViewShowings;
  final DateTime? nextShowingAtUtc;

  factory LeasingRentalDetail.fromJson(Map<String, dynamic> json) =>
      LeasingRentalDetail(
        propertyId: (json['propertyId'] as num).toInt(),
        unitId: (json['unitId'] as num).toInt(),
        propertyName: json['propertyName'] as String? ?? '',
        unitNumber: json['unitNumber'] as String? ?? '',
        address: json['address'] as String? ?? '',
        listingId: (json['listingId'] as num?)?.toInt(),
        listingStatus: json['listingStatus'] as String?,
        listingHeadline: json['listingHeadline'] as String?,
        listingDescription: json['listingDescription'] as String?,
        askingRent: (json['askingRent'] as num?)?.toDouble(),
        securityDeposit: (json['securityDeposit'] as num?)?.toDouble(),
        availableOn: _date(json['availableOn']),
        leaseTerms: json['leaseTerms'] as String?,
        petPolicy: json['petPolicy'] as String?,
        canViewApplications: json['canViewApplications'] as bool? ?? false,
        openApplicationCount:
            (json['openApplicationCount'] as num?)?.toInt() ?? 0,
        canViewShowings: json['canViewShowings'] as bool? ?? false,
        nextShowingAtUtc: _date(json['nextShowingAtUtc']),
      );
}

/// Assignment-scoped application projection for the Leasing experience.
///
/// This deliberately mirrors `/leasing/applications/{id}` instead of reusing
/// the management Applications API. A leasing agent must never gain a second,
/// broader read path merely because both screens happen to display an
/// application.
class LeasingApplicationDetail {
  const LeasingApplicationDetail({
    required this.id,
    required this.applicantName,
    required this.status,
    required this.consentGiven,
    required this.submittedAtUtc,
    this.propertyId,
    this.unitId,
    this.email,
    this.phone,
    this.propertyName,
    this.unitNumber,
    this.monthlyIncome,
    this.desiredMoveInDate,
    this.notes,
  });

  final int id;
  final int? propertyId;
  final int? unitId;
  final String applicantName;
  final String? email;
  final String? phone;
  final String? propertyName;
  final String? unitNumber;
  final String status;
  final double? monthlyIncome;
  final DateTime? desiredMoveInDate;
  final String? notes;
  final bool consentGiven;
  final DateTime submittedAtUtc;

  factory LeasingApplicationDetail.fromJson(Map<String, dynamic> json) =>
      LeasingApplicationDetail(
        id: (json['id'] as num).toInt(),
        propertyId: (json['propertyId'] as num?)?.toInt(),
        unitId: (json['unitId'] as num?)?.toInt(),
        applicantName: json['applicantName'] as String? ?? '',
        email: json['email'] as String?,
        phone: json['phone'] as String?,
        propertyName: json['propertyName'] as String?,
        unitNumber: json['unitNumber'] as String?,
        status: json['status'] as String? ?? 'Submitted',
        monthlyIncome: (json['monthlyIncome'] as num?)?.toDouble(),
        desiredMoveInDate: _date(json['desiredMoveInDate']),
        notes: json['notes'] as String?,
        consentGiven: json['consentGiven'] as bool? ?? false,
        submittedAtUtc:
            _date(json['submittedAtUtc']) ??
            DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
      );
}

class LeasingAppointmentDetail {
  const LeasingAppointmentDetail({
    required this.id,
    required this.title,
    required this.status,
    required this.scheduledStart,
    this.propertyId,
    this.unitId,
    this.rentalApplicationId,
    this.prospectName,
    this.prospectEmail,
    this.propertyName,
    this.unitNumber,
    this.scheduledEnd,
    this.assignedTo,
    this.notes,
  });

  final int id;
  final int? propertyId;
  final int? unitId;
  final int? rentalApplicationId;
  final String title;
  final String? prospectName;
  final String? prospectEmail;
  final String? propertyName;
  final String? unitNumber;
  final String status;
  final DateTime scheduledStart;
  final DateTime? scheduledEnd;
  final String? assignedTo;
  final String? notes;

  factory LeasingAppointmentDetail.fromJson(Map<String, dynamic> json) =>
      LeasingAppointmentDetail(
        id: (json['id'] as num).toInt(),
        propertyId: (json['propertyId'] as num?)?.toInt(),
        unitId: (json['unitId'] as num?)?.toInt(),
        rentalApplicationId: (json['rentalApplicationId'] as num?)?.toInt(),
        title: json['title'] as String? ?? '',
        prospectName: json['prospectName'] as String?,
        prospectEmail: json['prospectEmail'] as String?,
        propertyName: json['propertyName'] as String?,
        unitNumber: json['unitNumber'] as String?,
        status: json['status'] as String? ?? '',
        scheduledStart:
            _date(json['scheduledStart']) ??
            DateTime.fromMillisecondsSinceEpoch(0),
        scheduledEnd: _date(json['scheduledEnd']),
        assignedTo: json['assignedTo'] as String?,
        notes: json['notes'] as String?,
      );
}

class LeasingMoveInDetail {
  const LeasingMoveInDetail({
    required this.id,
    required this.propertyId,
    required this.unitId,
    required this.relationshipNumber,
    required this.tenantName,
    required this.propertyName,
    required this.unitNumber,
    required this.agreementFullyExecuted,
    required this.possessionGiven,
    this.plannedPossessionAtUtc,
  });

  final int id;
  final int propertyId;
  final int unitId;
  final String relationshipNumber;
  final String tenantName;
  final String propertyName;
  final String unitNumber;
  final DateTime? plannedPossessionAtUtc;
  final bool agreementFullyExecuted;
  final bool possessionGiven;

  factory LeasingMoveInDetail.fromJson(Map<String, dynamic> json) =>
      LeasingMoveInDetail(
        id: (json['id'] as num).toInt(),
        propertyId: (json['propertyId'] as num).toInt(),
        unitId: (json['unitId'] as num).toInt(),
        relationshipNumber: json['relationshipNumber'] as String? ?? '',
        tenantName: json['tenantName'] as String? ?? '',
        propertyName: json['propertyName'] as String? ?? '',
        unitNumber: json['unitNumber'] as String? ?? '',
        plannedPossessionAtUtc: _date(json['plannedPossessionAtUtc']),
        agreementFullyExecuted:
            json['agreementFullyExecuted'] as bool? ?? false,
        possessionGiven: json['possessionGiven'] as bool? ?? false,
      );
}

class LeasingConversationMessage {
  const LeasingConversationMessage({
    required this.id,
    required this.senderRole,
    required this.body,
    required this.createdAt,
  });

  final int id;
  final String senderRole;
  final String body;
  final DateTime createdAt;

  factory LeasingConversationMessage.fromJson(Map<String, dynamic> json) =>
      LeasingConversationMessage(
        id: (json['id'] as num).toInt(),
        senderRole: json['senderRole'] as String? ?? '',
        body: json['body'] as String? ?? '',
        createdAt:
            _date(json['createdAt']) ??
            DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
      );
}

class LeasingConversationDetail {
  const LeasingConversationDetail({
    required this.id,
    required this.tenantName,
    required this.subject,
    required this.messages,
    this.propertyName,
  });

  final int id;
  final String tenantName;
  final String subject;
  final String? propertyName;
  final List<LeasingConversationMessage> messages;

  factory LeasingConversationDetail.fromJson(Map<String, dynamic> json) =>
      LeasingConversationDetail(
        id: (json['id'] as num).toInt(),
        tenantName: json['tenantName'] as String? ?? '',
        subject: json['subject'] as String? ?? '',
        propertyName: json['propertyName'] as String?,
        messages: (json['messages'] as List? ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(LeasingConversationMessage.fromJson)
            .toList(growable: false),
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

  Future<LeasingRentalDetail> rental(int unitId) =>
      _getObject('/leasing/rentals/$unitId', LeasingRentalDetail.fromJson);

  Future<LeasingApplicationDetail> application(int applicationId) => _getObject(
    '/leasing/applications/$applicationId',
    LeasingApplicationDetail.fromJson,
  );

  Future<LeasingAppointmentDetail> appointment(int appointmentId) => _getObject(
    '/leasing/appointments/$appointmentId',
    LeasingAppointmentDetail.fromJson,
  );

  Future<LeasingConversationDetail> conversation(int conversationId) =>
      _getObject(
        '/leasing/conversations/$conversationId',
        LeasingConversationDetail.fromJson,
      );

  Future<void> replyToConversation(int conversationId, String body) =>
      IdempotentMutation.run('leasing:conversations:$conversationId:reply', (
        operationKey,
      ) async {
        try {
          await _dio.post<void>(
            '/leasing/conversations/$conversationId/messages',
            data: {
              'operationKey': operationKey,
              'body': body.trim(),
              'channels': const ['Portal'],
            },
            options: Options(headers: {'Idempotency-Key': operationKey}),
          );
        } on DioException catch (error) {
          throw ApiException.fromDioException(error);
        }
      });

  Future<LeasingMoveInDetail> moveIn(int leaseManagementId) => _getObject(
    '/leasing/move-ins/$leaseManagementId',
    LeasingMoveInDetail.fromJson,
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
          'sort': ?sort,
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
