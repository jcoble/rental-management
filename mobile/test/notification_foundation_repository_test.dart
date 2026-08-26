import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
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

    expect(adapter.requests[0].path, '/my-alerts');
    expect(adapter.requests[1].path, '/my-alerts');
    expect(adapter.requests[1].method, 'PUT');
    expect(adapter.requests[1].data, {
      'enableInApp': true,
      'enableMobilePush': true,
      'enableEmail': true,
      'enableSms': false,
    });
    expect(saved.enableEmail, isTrue);
  });

  test('My alerts save completing after disposal does not throw', () async {
    final saveResult = Completer<MyAlerts>();
    final container = ProviderContainer(
      overrides: [
        notificationFoundationRepositoryProvider.overrideWithValue(
          _BlockingAlertsRepository(saveResult),
        ),
      ],
    );
    final subscription = container.listen<AsyncValue<MyAlerts>>(
      myAlertsProvider,
      (_, _) {},
    );
    var disposed = false;
    void disposeContainer() {
      if (disposed) return;
      disposed = true;
      subscription.close();
      container.dispose();
    }
    addTearDown(disposeContainer);

    final notifier = container.read(myAlertsProvider.notifier);
    await Future<void>.delayed(Duration.zero);
    await Future<void>.delayed(Duration.zero);
    expect(container.read(myAlertsProvider).value, isNotNull);

    final save = notifier.save();
    await Future<void>.delayed(Duration.zero);
    disposeContainer();
    saveResult.complete(_testAlerts);

    await expectLater(save, completes);
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
      expect(adapter.requests[1].path, '/team-routing/31/recipients');
      expect(adapter.requests[2].path, '/team-routing');
      expect(adapter.requests[2].method, 'PUT');
      expect((adapter.requests[2].data as Map<String, dynamic>)['recipients'], [
        {'userId': 11, 'reason': 'Named rent contact.'},
      ]);
    },
  );

  test(
    'Morning Briefing schedule uses its canonical settings endpoint',
    () async {
      final adapter = _RecordingAdapter(
        (options) => {
          'enabled': options.method == 'PUT' ? false : true,
          'sendHourLocal': 7,
          'includeEmpty': true,
          'timeZone': 'America/New_York',
        },
      );
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
        everyElement('/team-routing/morning-briefing'),
      );
      expect(adapter.requests.last.method, 'PUT');
      expect(adapter.requests.last.data, {
        'enabled': false,
        'sendHourLocal': 7,
        'includeEmpty': true,
      });
      expect(saved.enabled, isFalse);
    },
  );

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

    expect(adapter.requests.single.path, '/tenant-notices/deliveries');
    expect(adapter.requests.single.queryParameters, {'take': 50});
    expect(statuses.single.status, 'Retrying');
    expect(statuses.single.nextAttemptAtUtc, isNotNull);
  });

  test('Tenant delivery status accepts only canonical queue states', () {
    expect(noticeDeliveryStatusValues, {
      'Queued',
      'Accepted',
      'Retrying',
      'Sent',
      'PermanentlyFailed',
    });
    final payload = <String, dynamic>{
      'evidenceId': 91,
      'renderedNoticeId': 81,
      'noticeDraftId': 71,
      'subject': 'Rent reminder',
      'leaseManagementId': 61,
      'recipientRole': 'PrimaryTenant',
      'channel': 'Email',
      'destination': 'tenant@example.test',
      'attemptCount': 1,
      'createdAtUtc': '2026-07-13T01:00:00Z',
    };

    for (final status in noticeDeliveryStatusValues) {
      expect(
        NoticeDeliveryStatus.fromJson({...payload, 'status': status}).status,
        status,
      );
    }
    for (final legacyStatus in ['Delivered', 'Failed']) {
      expect(
        () =>
            NoticeDeliveryStatus.fromJson({...payload, 'status': legacyStatus}),
        throwsFormatException,
      );
    }
  });

  test(
    'Notification previews expose effective recipients, destinations, and channels',
    () async {
      final adapter = _RecordingAdapter((options) {
        if (options.path.endsWith('/preview')) {
          return [
            {
              'userId': 11,
              'displayName': 'Pat Manager',
              'email': 'pat@example.test',
              'phoneNumber': '+15551234567',
              'enableInApp': true,
              'enableMobilePush': false,
              'enableEmail': true,
              'enableSms': true,
              'propertyId': null,
              'scope': 'All properties',
              'reason': 'Named rent contact.',
              'isAdministratorFallback': false,
            },
          ];
        }
        return [
          {
            'leaseManagementPartyId': 71,
            'tenantId': 81,
            'displayName': 'Taylor Tenant',
            'role': 'PrimaryTenant',
            'eligible': true,
            'availableChannels': ['TenantPortal', 'Email'],
            'email': 'taylor@example.test',
            'phone': null,
            'reason': 'Effective primary tenant.',
          },
        ];
      });
      final repository = NotificationFoundationRepository(_dio(adapter));

      final teamPreview = await repository.previewTeamRouting(31);
      final tenantPreview = await repository.previewTenantNoticeRecipients(
        automationKey: 'rent-reminder',
        leaseManagementId: 61,
      );

      expect(teamPreview.single.phoneNumber, '+15551234567');
      expect(teamPreview.single.enableEmail, isTrue);
      expect(teamPreview.single.enableSms, isTrue);
      expect(adapter.requests.first.path, '/team-routing/31/preview');
      expect(
        adapter.requests.last.path,
        '/tenant-notices/rent-reminder/recipients',
      );
      expect(adapter.requests.last.queryParameters, {'leaseManagementId': 61});
      expect(tenantPreview.single.availableChannels, ['TenantPortal', 'Email']);
      expect(tenantPreview.single.reason, 'Effective primary tenant.');
    },
  );

  test('Template merge-field help uses the server contract', () async {
    final adapter = _RecordingAdapter(
      (_) => [
        {
          'key': 'tenant_name',
          'token': '{{tenant_name}}',
          'label': 'Tenant name',
          'description': 'The effective primary tenant.',
          'example': 'Taylor Tenant',
        },
      ],
    );
    final repository = NotificationFoundationRepository(_dio(adapter));

    final fields = await repository.listTenantNoticeMergeFields(
      'rent-reminder',
    );

    expect(
      adapter.requests.single.path,
      '/tenant-notices/templates/rent-reminder/merge-fields',
    );
    expect(fields.single.token, '{{tenant_name}}');
    expect(fields.single.example, 'Taylor Tenant');
  });

  test('Tenant notice preview sends unsaved subject and body', () async {
    final adapter = _RecordingAdapter(
      (_) => {
        'systemKey': 'rent-reminder',
        'subject': 'Hello Taylor',
        'body': 'Rent is due August 1.',
        'exampleValues': {'tenant_name': 'Taylor Tenant'},
      },
    );
    final repository = NotificationFoundationRepository(_dio(adapter));

    final preview = await repository.previewTenantNotice(
      systemKey: 'rent-reminder',
      subject: 'Hello {{tenant_name}}',
      body: 'Rent is due {{rent_due_date}}.',
    );

    expect(
      adapter.requests.single.path,
      '/tenant-notices/templates/rent-reminder/preview',
    );
    expect(adapter.requests.single.data, {
      'systemKey': 'rent-reminder',
      'subject': 'Hello {{tenant_name}}',
      'body': 'Rent is due {{rent_due_date}}.',
    });
    expect(preview.subject, 'Hello Taylor');
    expect(preview.exampleValues['tenant_name'], 'Taylor Tenant');
  });

  test(
    'Tenant notice test send uses an explicit isolated destination',
    () async {
      final adapter = _RecordingAdapter(
        (_) => {
          'state': 'Suppressed',
          'message': 'No provider is configured.',
          'destination': 'owner+test@example.test',
          'provider': null,
          'providerMessageId': null,
        },
      );
      final repository = NotificationFoundationRepository(_dio(adapter));

      final result = await repository.sendTenantNoticeTest(
        systemKey: 'rent-reminder',
        subject: 'Hello {{tenant_name}}',
        body: 'Rent is due {{rent_due_date}}.',
        destination: 'owner+test@example.test',
      );

      expect(
        adapter.requests.single.path,
        '/tenant-notices/templates/rent-reminder/test-send',
      );
      expect(adapter.requests.single.method, 'POST');
      expect(
        adapter.requests.single.data,
        containsPair('destination', 'owner+test@example.test'),
      );
      expect(adapter.requests.single.headers, contains('Idempotency-Key'));
      expect(result.state, 'Suppressed');
      expect(noticeTestSendStates, {'Accepted', 'Suppressed', 'ProviderError'});
    },
  );

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
      expect(request.path, '/tenant-notices/rent-reminder');
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

class _BlockingAlertsRepository extends NotificationFoundationRepository {
  _BlockingAlertsRepository(this.saveResult) : super(Dio());

  final Completer<MyAlerts> saveResult;

  @override
  Future<MyAlerts> getMyAlerts() async => _testAlerts;

  @override
  Future<MyAlerts> updateMyAlerts(MyAlerts alerts) => saveResult.future;
}

const _testAlerts = MyAlerts(
  userId: 7,
  displayName: 'Pat Manager',
  email: 'pat@example.test',
  phoneNumber: null,
  enableInApp: true,
  enableMobilePush: true,
  enableEmail: false,
  enableSms: false,
);

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
  'templateProvenance': 'Rental Command supplied copy, workspace version 1',
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
