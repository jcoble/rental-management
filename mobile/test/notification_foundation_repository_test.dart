import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/settings/notification_foundation_repository.dart';

void main() {
  test('My alerts uses the canonical personal endpoint and payload', () async {
    final adapter = _RecordingAdapter((options) {
      return {
        'userId': 7,
        'displayName': 'Alex Owner',
        'email': 'alex@example.test',
        'phoneNumber': null,
        'enableInApp': true,
        'enableMobilePush': true,
        'enableEmail': options.method == 'PUT',
        'enableSms': false,
      };
    });
    final repository = NotificationFoundationRepository(_dio(adapter));

    final current = await repository.getMyAlerts();
    final saved = await repository.updateMyAlerts(
      current.copyWith(enableEmail: true),
    );

    expect(adapter.requests[0].path, '/notification-settings/my-alerts');
    expect(adapter.requests[1].path, '/notification-settings/my-alerts');
    expect(adapter.requests[1].method, 'PUT');
    expect(adapter.requests[1].data, {
      'enableInApp': true,
      'enableMobilePush': true,
      'enableEmail': true,
      'enableSms': false,
    });
    expect(saved.enableEmail, isTrue);
  });

  test(
    'Team routing preserves server-shaped rows and recipient reasons',
    () async {
      final adapter = _RecordingAdapter((options) {
        if (options.path.endsWith('/recipients')) {
          return [
            {
              'userId': 11,
              'displayName': 'Pat Manager',
              'email': 'pat@example.test',
              'reason': 'Named rent contact.',
            },
          ];
        }
        if (options.method == 'PUT') {
          return _routingRuleJson;
        }
        return [_routingRuleJson];
      });
      final repository = NotificationFoundationRepository(_dio(adapter));

      final rules = await repository.listTeamRouting();
      final recipients = await repository.listTeamRoutingRecipients(31);
      await repository.replaceTeamRouting(
        topic: 'RentAndMoney',
        propertyId: null,
        useWorkspaceAdministratorFallback: true,
        recipients: const [
          TeamRoutingRecipientUpdate(userId: 11, reason: 'Named rent contact.'),
        ],
      );

      expect(rules.single.scope, 'All properties');
      expect(recipients.single.reason, 'Named rent contact.');
      expect(
        adapter.requests[1].path,
        '/notification-settings/team-routing/31/recipients',
      );
      expect(adapter.requests[2].path, '/notification-settings/team-routing');
      expect(adapter.requests[2].method, 'PUT');
      expect((adapter.requests[2].data as Map<String, dynamic>)['recipients'], [
        {'userId': 11, 'reason': 'Named rent contact.'},
      ]);
    },
  );

  test('Morning Briefing schedule uses its canonical settings endpoint', () async {
    final adapter = _RecordingAdapter((options) => {
      'enabled': options.method == 'PUT' ? false : true,
      'sendHourLocal': 7,
      'includeEmpty': true,
      'timeZone': 'America/New_York',
    });
    final repository = NotificationFoundationRepository(_dio(adapter));

    final current = await repository.getMorningBriefingSettings();
    final saved = await repository.updateMorningBriefingSettings(
      MorningBriefingSettings(
        enabled: false,
        sendHourLocal: current.sendHourLocal,
        includeEmpty: current.includeEmpty,
        timeZone: current.timeZone,
      ),
    );

    expect(
      adapter.requests.map((request) => request.path),
      everyElement('/notification-settings/morning-briefing'),
    );
    expect(adapter.requests.last.method, 'PUT');
    expect(adapter.requests.last.data, {
      'enabled': false,
      'sendHourLocal': 7,
      'includeEmpty': true,
    });
    expect(saved.enabled, isFalse);
  });

  test('Tenant delivery status requests a server-side page of 50', () async {
    final adapter = _RecordingAdapter(
      (_) => [
        {
          'evidenceId': 91,
          'renderedNoticeId': 81,
          'noticeDraftId': 71,
          'subject': 'Rent reminder',
          'leaseManagementId': 61,
          'recipientRole': 'PrimaryTenant',
          'channel': 'Email',
          'destination': 'tenant@example.test',
          'status': 'Retrying',
          'attemptCount': 2,
          'createdAtUtc': '2026-07-13T01:00:00Z',
          'lastAttemptAtUtc': '2026-07-13T01:05:00Z',
          'nextAttemptAtUtc': '2026-07-13T01:15:00Z',
          'acceptedAtUtc': null,
          'deliveredAtUtc': null,
          'failedAtUtc': null,
          'provider': 'SendGrid',
          'providerMessageId': null,
          'lastError': 'Temporary provider error',
        },
      ],
    );
    final repository = NotificationFoundationRepository(_dio(adapter));

    final statuses = await repository.listTenantNoticeDeliveries(take: 50);

    expect(
      adapter.requests.single.path,
      '/notification-settings/tenant-notices/deliveries',
    );
    expect(adapter.requests.single.queryParameters, {'take': 50});
    expect(statuses.single.status, 'Retrying');
    expect(statuses.single.nextAttemptAtUtc, isNotNull);
  });

  test(
    'Tenant policy update never sends a legacy global tenant switch',
    () async {
      final policyJson = _policyJson;
      final adapter = _RecordingAdapter((_) => policyJson);
      final repository = NotificationFoundationRepository(_dio(adapter));
      final policy = TenantNoticePolicy.fromJson(policyJson);

      await repository.updateTenantNoticePolicy(
        policy,
        policy.toUpdateJson(
          updatedMode: 'Off',
          updatedLeadDays: 3,
          updatedSendHourLocal: 9,
          updatedSendTenantPortal: true,
          updatedSendMobilePush: true,
          updatedSendEmail: true,
          updatedSendSms: false,
          updatedIncludePrimaryTenant: true,
          updatedIncludeCoTenant: true,
          updatedIncludeEligibleGuarantor: false,
          updatedIncludeOccupant: true,
          updatedFailureBehavior: 'RetryThenDraft',
        ),
      );

      final request = adapter.requests.single;
      expect(
        request.path,
        '/notification-settings/tenant-notices/rent-reminder',
      );
      expect(request.method, 'PUT');
      expect(request.data, isNot(contains('notifyTenants')));
      expect(request.data, isNot(contains('leaseEndAutoAction')));
      expect(request.data, containsPair('mode', 'Off'));
    },
  );
}

Dio _dio(HttpClientAdapter adapter) =>
    Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;

class _RecordingAdapter implements HttpClientAdapter {
  _RecordingAdapter(this.responseFor);

  final dynamic Function(RequestOptions options) responseFor;
  final requests = <RequestOptions>[];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(options);
    return ResponseBody.fromString(
      jsonEncode(responseFor(options)),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

const _routingRuleJson = <String, dynamic>{
  'id': 31,
  'topic': 'RentAndMoney',
  'propertyId': null,
  'scope': 'All properties',
  'useWorkspaceAdministratorFallback': true,
  'namedRecipientCount': 1,
  'namedRecipientSummary': 'Pat Manager',
  'routingExplanation': 'Pat receives rent and money updates.',
  'updatedAtUtc': '2026-07-13T01:00:00Z',
};

const _policyJson = <String, dynamic>{
  'id': 41,
  'automationKey': 'rent-reminder',
  'mode': 'Draft',
  'classification': 'Courtesy',
  'leadDays': 3,
  'sendHourLocal': 9,
  'sendTenantPortal': true,
  'sendMobilePush': true,
  'sendEmail': true,
  'sendSms': false,
  'includePrimaryTenant': true,
  'includeCoTenant': true,
  'includeEligibleGuarantor': false,
  'includeOccupant': true,
  'failureBehavior': 'RetryThenDraft',
  'workspaceNoticeTemplateVersionId': 51,
  'templateSystemKey': 'rent-reminder',
  'templateVersion': 1,
  'templateSubject': 'Your rent is due soon',
  'templateBody': 'Hello {{tenant_name}}',
  'templateIsCustomized': false,
  'templateBasedOnSystemTemplateVersionId': 2,
  'templateUpdateAvailable': false,
  'templateCreatedAtUtc': '2026-07-13T01:00:00Z',
  'templateJurisdictionCode': null,
  'templateJurisdictionReviewedAtUtc': null,
  'reviewedJurisdictionCode': null,
  'jurisdictionReviewedAtUtc': null,
  'canAutoSend': false,
  'updatedAtUtc': '2026-07-13T01:00:00Z',
};
