import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';

class MyAlerts {
  const MyAlerts({
    required this.userId,
    required this.displayName,
    required this.email,
    required this.phoneNumber,
    required this.enableInApp,
    required this.enableMobilePush,
    required this.enableEmail,
    required this.enableSms,
  });

  final int userId;
  final String displayName;
  final String? email;
  final String? phoneNumber;
  final bool enableInApp;
  final bool enableMobilePush;
  final bool enableEmail;
  final bool enableSms;

  factory MyAlerts.fromJson(Map<String, dynamic> json) => MyAlerts(
    userId: (json['userId'] as num).toInt(),
    displayName: json['displayName'] as String? ?? '',
    email: json['email'] as String?,
    phoneNumber: json['phoneNumber'] as String?,
    enableInApp: json['enableInApp'] as bool? ?? false,
    enableMobilePush: json['enableMobilePush'] as bool? ?? false,
    enableEmail: json['enableEmail'] as bool? ?? false,
    enableSms: json['enableSms'] as bool? ?? false,
  );

  MyAlerts copyWith({
    bool? enableInApp,
    bool? enableMobilePush,
    bool? enableEmail,
    bool? enableSms,
  }) => MyAlerts(
    userId: userId,
    displayName: displayName,
    email: email,
    phoneNumber: phoneNumber,
    enableInApp: enableInApp ?? this.enableInApp,
    enableMobilePush: enableMobilePush ?? this.enableMobilePush,
    enableEmail: enableEmail ?? this.enableEmail,
    enableSms: enableSms ?? this.enableSms,
  );

  Map<String, dynamic> toUpdateJson() => {
    'enableInApp': enableInApp,
    'enableMobilePush': enableMobilePush,
    'enableEmail': enableEmail,
    'enableSms': enableSms,
  };
}

class MorningBriefingSettings {
  const MorningBriefingSettings({
    required this.enabled,
    required this.sendHourLocal,
    required this.includeEmpty,
    required this.timeZone,
  });

  final bool enabled;
  final int sendHourLocal;
  final bool includeEmpty;
  final String timeZone;

  factory MorningBriefingSettings.fromJson(Map<String, dynamic> json) =>
      MorningBriefingSettings(
        enabled: json['enabled'] as bool? ?? false,
        sendHourLocal: (json['sendHourLocal'] as num?)?.toInt() ?? 8,
        includeEmpty: json['includeEmpty'] as bool? ?? false,
        timeZone: json['timeZone'] as String? ?? 'America/New_York',
      );

  Map<String, dynamic> toUpdateJson() => {
    'enabled': enabled,
    'sendHourLocal': sendHourLocal,
    'includeEmpty': includeEmpty,
  };
}

class TeamRoutingRecipient {
  const TeamRoutingRecipient({
    required this.userId,
    required this.displayName,
    required this.email,
    required this.reason,
  });

  final int userId;
  final String displayName;
  final String? email;
  final String reason;

  factory TeamRoutingRecipient.fromJson(Map<String, dynamic> json) =>
      TeamRoutingRecipient(
        userId: (json['userId'] as num).toInt(),
        displayName: json['displayName'] as String? ?? '',
        email: json['email'] as String?,
        reason: json['reason'] as String? ?? '',
      );
}

class TeamRoutingRecipientPreview extends TeamRoutingRecipient {
  const TeamRoutingRecipientPreview({
    required super.userId,
    required super.displayName,
    required super.email,
    required super.reason,
    required this.propertyId,
    required this.scope,
    required this.isAdministratorFallback,
  });

  final int? propertyId;
  final String scope;
  final bool isAdministratorFallback;

  factory TeamRoutingRecipientPreview.fromJson(Map<String, dynamic> json) =>
      TeamRoutingRecipientPreview(
        userId: (json['userId'] as num).toInt(),
        displayName: json['displayName'] as String? ?? '',
        email: json['email'] as String?,
        reason: json['reason'] as String? ?? '',
        propertyId: (json['propertyId'] as num?)?.toInt(),
        scope: json['scope'] as String? ?? '',
        isAdministratorFallback:
            json['isAdministratorFallback'] as bool? ?? false,
      );
}

class TeamRoutingRule {
  const TeamRoutingRule({
    required this.id,
    required this.topic,
    required this.propertyId,
    required this.scope,
    required this.useWorkspaceAdministratorFallback,
    required this.namedRecipientCount,
    required this.namedRecipientSummary,
    required this.routingExplanation,
    required this.updatedAtUtc,
  });

  final int id;
  final String topic;
  final int? propertyId;
  final String scope;
  final bool useWorkspaceAdministratorFallback;
  final int namedRecipientCount;
  final String namedRecipientSummary;
  final String routingExplanation;
  final DateTime updatedAtUtc;

  factory TeamRoutingRule.fromJson(Map<String, dynamic> json) =>
      TeamRoutingRule(
        id: (json['id'] as num).toInt(),
        topic: json['topic'] as String? ?? '',
        propertyId: (json['propertyId'] as num?)?.toInt(),
        scope: json['scope'] as String? ?? '',
        useWorkspaceAdministratorFallback:
            json['useWorkspaceAdministratorFallback'] as bool? ?? false,
        namedRecipientCount:
            (json['namedRecipientCount'] as num?)?.toInt() ?? 0,
        namedRecipientSummary: json['namedRecipientSummary'] as String? ?? '',
        routingExplanation: json['routingExplanation'] as String? ?? '',
        updatedAtUtc: _date(json['updatedAtUtc']),
      );
}

class TeamRoutingRecipientUpdate {
  const TeamRoutingRecipientUpdate({
    required this.userId,
    required this.reason,
  });

  final int userId;
  final String reason;

  Map<String, dynamic> toJson() => {'userId': userId, 'reason': reason};
}

class TenantNoticePolicy {
  const TenantNoticePolicy({
    required this.id,
    required this.automationKey,
    required this.mode,
    required this.classification,
    required this.leadDays,
    required this.sendHourLocal,
    required this.sendTenantPortal,
    required this.sendMobilePush,
    required this.sendEmail,
    required this.sendSms,
    required this.includePrimaryTenant,
    required this.includeCoTenant,
    required this.includeEligibleGuarantor,
    required this.includeOccupant,
    required this.failureBehavior,
    required this.workspaceNoticeTemplateVersionId,
    required this.templateSystemKey,
    required this.templateVersion,
    required this.templateSubject,
    required this.templateBody,
    required this.templateIsCustomized,
    required this.templateBasedOnSystemTemplateVersionId,
    required this.templateUpdateAvailable,
    required this.templateCreatedAtUtc,
    required this.templateJurisdictionCode,
    required this.templateJurisdictionReviewedAtUtc,
    required this.reviewedJurisdictionCode,
    required this.jurisdictionReviewedAtUtc,
    required this.canAutoSend,
    required this.updatedAtUtc,
  });

  final int id;
  final String automationKey;
  final String mode;
  final String classification;
  final int leadDays;
  final int sendHourLocal;
  final bool sendTenantPortal;
  final bool sendMobilePush;
  final bool sendEmail;
  final bool sendSms;
  final bool includePrimaryTenant;
  final bool includeCoTenant;
  final bool includeEligibleGuarantor;
  final bool includeOccupant;
  final String failureBehavior;
  final int workspaceNoticeTemplateVersionId;
  final String templateSystemKey;
  final int templateVersion;
  final String templateSubject;
  final String templateBody;
  final bool templateIsCustomized;
  final int templateBasedOnSystemTemplateVersionId;
  final bool templateUpdateAvailable;
  final DateTime templateCreatedAtUtc;
  final String? templateJurisdictionCode;
  final DateTime? templateJurisdictionReviewedAtUtc;
  final String? reviewedJurisdictionCode;
  final DateTime? jurisdictionReviewedAtUtc;
  final bool canAutoSend;
  final DateTime updatedAtUtc;

  bool get automaticDeliveryIsEligible =>
      classification != 'Legal' || jurisdictionReviewedAtUtc != null;

  factory TenantNoticePolicy.fromJson(
    Map<String, dynamic> json,
  ) => TenantNoticePolicy(
    id: (json['id'] as num).toInt(),
    automationKey: json['automationKey'] as String? ?? '',
    mode: json['mode'] as String? ?? 'Draft',
    classification: json['classification'] as String? ?? 'Courtesy',
    leadDays: (json['leadDays'] as num?)?.toInt() ?? 0,
    sendHourLocal: (json['sendHourLocal'] as num?)?.toInt() ?? 8,
    sendTenantPortal: json['sendTenantPortal'] as bool? ?? false,
    sendMobilePush: json['sendMobilePush'] as bool? ?? false,
    sendEmail: json['sendEmail'] as bool? ?? false,
    sendSms: json['sendSms'] as bool? ?? false,
    includePrimaryTenant: json['includePrimaryTenant'] as bool? ?? false,
    includeCoTenant: json['includeCoTenant'] as bool? ?? false,
    includeEligibleGuarantor:
        json['includeEligibleGuarantor'] as bool? ?? false,
    includeOccupant: json['includeOccupant'] as bool? ?? false,
    failureBehavior:
        json['failureBehavior'] as String? ?? 'StopAndRequireReview',
    workspaceNoticeTemplateVersionId:
        (json['workspaceNoticeTemplateVersionId'] as num).toInt(),
    templateSystemKey: json['templateSystemKey'] as String? ?? '',
    templateVersion: (json['templateVersion'] as num?)?.toInt() ?? 0,
    templateSubject: json['templateSubject'] as String? ?? '',
    templateBody: json['templateBody'] as String? ?? '',
    templateIsCustomized: json['templateIsCustomized'] as bool? ?? false,
    templateBasedOnSystemTemplateVersionId:
        (json['templateBasedOnSystemTemplateVersionId'] as num?)?.toInt() ?? 0,
    templateUpdateAvailable: json['templateUpdateAvailable'] as bool? ?? false,
    templateCreatedAtUtc: _date(json['templateCreatedAtUtc']),
    templateJurisdictionCode: json['templateJurisdictionCode'] as String?,
    templateJurisdictionReviewedAtUtc: _nullableDate(
      json['templateJurisdictionReviewedAtUtc'],
    ),
    reviewedJurisdictionCode: json['reviewedJurisdictionCode'] as String?,
    jurisdictionReviewedAtUtc: _nullableDate(json['jurisdictionReviewedAtUtc']),
    canAutoSend: json['canAutoSend'] as bool? ?? false,
    updatedAtUtc: _date(json['updatedAtUtc']),
  );

  Map<String, dynamic> toUpdateJson({
    required String updatedMode,
    required int updatedLeadDays,
    required int updatedSendHourLocal,
    required bool updatedSendTenantPortal,
    required bool updatedSendMobilePush,
    required bool updatedSendEmail,
    required bool updatedSendSms,
    required bool updatedIncludePrimaryTenant,
    required bool updatedIncludeCoTenant,
    required bool updatedIncludeEligibleGuarantor,
    required bool updatedIncludeOccupant,
    required String updatedFailureBehavior,
  }) => {
    'automationKey': automationKey,
    'mode': updatedMode,
    'classification': classification,
    'leadDays': updatedLeadDays,
    'sendHourLocal': updatedSendHourLocal,
    'sendTenantPortal': updatedSendTenantPortal,
    'sendMobilePush': updatedSendMobilePush,
    'sendEmail': updatedSendEmail,
    'sendSms': updatedSendSms,
    'includePrimaryTenant': updatedIncludePrimaryTenant,
    'includeCoTenant': updatedIncludeCoTenant,
    'includeEligibleGuarantor': updatedIncludeEligibleGuarantor,
    'includeOccupant': classification == 'Legal'
        ? false
        : updatedIncludeOccupant,
    'failureBehavior': updatedFailureBehavior,
    'workspaceNoticeTemplateVersionId': workspaceNoticeTemplateVersionId,
    'reviewedJurisdictionCode': reviewedJurisdictionCode,
    'confirmJurisdictionReviewed': jurisdictionReviewedAtUtc != null,
  };
}

class NoticeDeliveryStatus {
  const NoticeDeliveryStatus({
    required this.evidenceId,
    required this.renderedNoticeId,
    required this.noticeDraftId,
    required this.subject,
    required this.leaseManagementId,
    required this.recipientRole,
    required this.channel,
    required this.destination,
    required this.status,
    required this.attemptCount,
    required this.createdAtUtc,
    required this.lastAttemptAtUtc,
    required this.nextAttemptAtUtc,
    required this.acceptedAtUtc,
    required this.deliveredAtUtc,
    required this.failedAtUtc,
    required this.provider,
    required this.providerMessageId,
    required this.lastError,
  });

  final int evidenceId;
  final int renderedNoticeId;
  final int noticeDraftId;
  final String subject;
  final int leaseManagementId;
  final String recipientRole;
  final String channel;
  final String destination;
  final String status;
  final int attemptCount;
  final DateTime createdAtUtc;
  final DateTime? lastAttemptAtUtc;
  final DateTime? nextAttemptAtUtc;
  final DateTime? acceptedAtUtc;
  final DateTime? deliveredAtUtc;
  final DateTime? failedAtUtc;
  final String? provider;
  final String? providerMessageId;
  final String? lastError;

  factory NoticeDeliveryStatus.fromJson(Map<String, dynamic> json) =>
      NoticeDeliveryStatus(
        evidenceId: (json['evidenceId'] as num).toInt(),
        renderedNoticeId: (json['renderedNoticeId'] as num).toInt(),
        noticeDraftId: (json['noticeDraftId'] as num).toInt(),
        subject: json['subject'] as String? ?? '',
        leaseManagementId: (json['leaseManagementId'] as num).toInt(),
        recipientRole: json['recipientRole'] as String? ?? '',
        channel: json['channel'] as String? ?? '',
        destination: json['destination'] as String? ?? '',
        status: json['status'] as String? ?? 'Queued',
        attemptCount: (json['attemptCount'] as num?)?.toInt() ?? 0,
        createdAtUtc: _date(json['createdAtUtc']),
        lastAttemptAtUtc: _nullableDate(json['lastAttemptAtUtc']),
        nextAttemptAtUtc: _nullableDate(json['nextAttemptAtUtc']),
        acceptedAtUtc: _nullableDate(json['acceptedAtUtc']),
        deliveredAtUtc: _nullableDate(json['deliveredAtUtc']),
        failedAtUtc: _nullableDate(json['failedAtUtc']),
        provider: json['provider'] as String?,
        providerMessageId: json['providerMessageId'] as String?,
        lastError: json['lastError'] as String?,
      );
}

class NotificationFoundationRepository {
  NotificationFoundationRepository(this._dio);

  final Dio _dio;

  Future<MyAlerts> getMyAlerts() => _request(
    () => _dio.get<Map<String, dynamic>>('/notification-settings/my-alerts'),
    (data) => MyAlerts.fromJson(_map(data)),
  );

  Future<MyAlerts> updateMyAlerts(MyAlerts alerts) {
    final request = alerts.toUpdateJson();
    return IdempotentMutation.run(
      'notification-settings:my-alerts:$request',
      (operationKey) => _request(
        () => _dio.put<Map<String, dynamic>>(
          '/notification-settings/my-alerts',
          data: request,
          options: Options(headers: {'Idempotency-Key': operationKey}),
        ),
        (data) => MyAlerts.fromJson(_map(data)),
      ),
    );
  }

  Future<MorningBriefingSettings> getMorningBriefingSettings() => _request(
    () => _dio.get<Map<String, dynamic>>(
      '/notification-settings/morning-briefing',
    ),
    (data) => MorningBriefingSettings.fromJson(_map(data)),
  );

  Future<MorningBriefingSettings> updateMorningBriefingSettings(
    MorningBriefingSettings settings,
  ) {
    final request = settings.toUpdateJson();
    return IdempotentMutation.run(
      'notification-settings:morning-briefing:$request',
      (operationKey) => _request(
        () => _dio.put<Map<String, dynamic>>(
          '/notification-settings/morning-briefing',
          data: request,
          options: Options(headers: {'Idempotency-Key': operationKey}),
        ),
        (data) => MorningBriefingSettings.fromJson(_map(data)),
      ),
    );
  }

  Future<List<TeamRoutingRule>> listTeamRouting() => _request(
    () => _dio.get<List<dynamic>>('/notification-settings/team-routing'),
    (data) => _list(data, TeamRoutingRule.fromJson),
  );

  Future<List<TeamRoutingRecipient>> listTeamRoutingRecipients(int ruleId) =>
      _request(
        () => _dio.get<List<dynamic>>(
          '/notification-settings/team-routing/$ruleId/recipients',
        ),
        (data) => _list(data, TeamRoutingRecipient.fromJson),
      );

  Future<List<TeamRoutingRecipientPreview>> previewTeamRouting(int ruleId) =>
      _request(
        () => _dio.get<List<dynamic>>(
          '/notification-settings/team-routing/$ruleId/preview',
        ),
        (data) => _list(data, TeamRoutingRecipientPreview.fromJson),
      );

  Future<TeamRoutingRule> replaceTeamRouting({
    required String topic,
    required int? propertyId,
    required bool useWorkspaceAdministratorFallback,
    required List<TeamRoutingRecipientUpdate> recipients,
  }) {
    final request = {
      'topic': topic,
      'propertyId': propertyId,
      'useWorkspaceAdministratorFallback': useWorkspaceAdministratorFallback,
      'recipients': recipients.map((item) => item.toJson()).toList(),
    };
    return IdempotentMutation.run(
      'notification-settings:team-routing:$request',
      (operationKey) => _request(
        () => _dio.put<Map<String, dynamic>>(
          '/notification-settings/team-routing',
          data: request,
          options: Options(headers: {'Idempotency-Key': operationKey}),
        ),
        (data) => TeamRoutingRule.fromJson(_map(data)),
      ),
    );
  }

  Future<List<TenantNoticePolicy>> listTenantNoticePolicies() => _request(
    () => _dio.get<List<dynamic>>('/notification-settings/tenant-notices'),
    (data) => _list(data, TenantNoticePolicy.fromJson),
  );

  Future<TenantNoticePolicy> updateTenantNoticePolicy(
    TenantNoticePolicy policy,
    Map<String, dynamic> request,
  ) => IdempotentMutation.run(
    'notification-settings:tenant-notice:${policy.automationKey}:$request',
    (operationKey) => _request(
      () => _dio.put<Map<String, dynamic>>(
        '/notification-settings/tenant-notices/${Uri.encodeComponent(policy.automationKey)}',
        data: request,
        options: Options(headers: {'Idempotency-Key': operationKey}),
      ),
      (data) => TenantNoticePolicy.fromJson(_map(data)),
    ),
  );

  Future<void> seedTenantNoticeTemplates() => IdempotentMutation.run(
    'notification-settings:tenant-notices:seed',
    (operationKey) => _request(
      () => _dio.post<void>(
        '/notification-settings/tenant-notices/templates/seed',
        options: Options(headers: {'Idempotency-Key': operationKey}),
      ),
      (_) {},
    ),
  );

  Future<TenantNoticePolicy> createTenantNoticeTemplateVersion({
    required String systemKey,
    required String subject,
    required String body,
    required String? jurisdictionCode,
    required bool confirmJurisdictionReviewed,
  }) {
    final request = {
      'subject': subject,
      'body': body,
      'jurisdictionCode': jurisdictionCode,
      'confirmJurisdictionReviewed': confirmJurisdictionReviewed,
    };
    return IdempotentMutation.run(
      'notification-settings:tenant-notice-template:$systemKey:$request',
      (operationKey) => _request(
        () => _dio.post<Map<String, dynamic>>(
          '/notification-settings/tenant-notices/templates/${Uri.encodeComponent(systemKey)}/versions',
          data: request,
          options: Options(headers: {'Idempotency-Key': operationKey}),
        ),
        (data) => TenantNoticePolicy.fromJson(_map(data)),
      ),
    );
  }

  Future<TenantNoticePolicy> restoreTenantNoticeTemplate(
    String systemKey,
  ) => IdempotentMutation.run(
    'notification-settings:tenant-notice-template:$systemKey:restore',
    (operationKey) => _request(
      () => _dio.post<Map<String, dynamic>>(
        '/notification-settings/tenant-notices/templates/${Uri.encodeComponent(systemKey)}/restore-default',
        options: Options(headers: {'Idempotency-Key': operationKey}),
      ),
      (data) => TenantNoticePolicy.fromJson(_map(data)),
    ),
  );

  Future<List<NoticeDeliveryStatus>> listTenantNoticeDeliveries({
    int take = 50,
  }) => _request(
    () => _dio.get<List<dynamic>>(
      '/notification-settings/tenant-notices/deliveries',
      queryParameters: {'take': take},
    ),
    (data) => _list(data, NoticeDeliveryStatus.fromJson),
  );

  Future<T> _request<T, R>(
    Future<Response<R>> Function() request,
    T Function(R? data) parse,
  ) async {
    try {
      final response = await request();
      return parse(response.data);
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }
}

final notificationFoundationRepositoryProvider =
    Provider<NotificationFoundationRepository>((ref) {
      return NotificationFoundationRepository(ref.watch(dioProvider));
    });

final myAlertsProvider =
    NotifierProvider.autoDispose<MyAlertsNotifier, AsyncValue<MyAlerts>>(
      MyAlertsNotifier.new,
    );

class MyAlertsNotifier extends Notifier<AsyncValue<MyAlerts>> {
  @override
  AsyncValue<MyAlerts> build() {
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  NotificationFoundationRepository get _repository =>
      ref.read(notificationFoundationRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    state = await AsyncValue.guard(_repository.getMyAlerts);
  }

  void update(MyAlerts Function(MyAlerts current) edit) {
    final current = state.value;
    if (current != null) state = AsyncValue.data(edit(current));
  }

  Future<void> save() async {
    final current = state.value;
    if (current == null) return;
    final saved = await _repository.updateMyAlerts(current);
    state = AsyncValue.data(saved);
  }
}

DateTime _date(dynamic value) =>
    DateTime.tryParse(value?.toString() ?? '')?.toUtc() ??
    DateTime.fromMillisecondsSinceEpoch(0, isUtc: true);

DateTime? _nullableDate(dynamic value) =>
    value == null ? null : DateTime.tryParse(value.toString())?.toUtc();

Map<String, dynamic> _map(dynamic data) {
  if (data is Map<String, dynamic>) return data;
  throw const ApiException(
    statusCode: 0,
    message: 'Empty response from server.',
  );
}

List<T> _list<T>(dynamic data, T Function(Map<String, dynamic>) parse) {
  if (data is! List<dynamic>) return const [];
  return data
      .map((item) => parse(item as Map<String, dynamic>))
      .toList(growable: false);
}
