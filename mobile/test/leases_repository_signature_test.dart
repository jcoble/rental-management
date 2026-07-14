import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/leases/leases_repository.dart';

void main() {
  test('successor draft posts canonical route with caller-owned key', () async {
    final adapter = _RecordingAdapter();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = LeaseManagementsRepository(dio);

    final draft = await repo.createSuccessorDraft(
      leaseManagementId: 44,
      sourceAgreementId: 81,
      changeType: 'Renewal',
      termStartOn: DateTime(2027, 1, 1),
      termEndOn: DateTime(2027, 12, 31),
      governingFromOn: DateTime(2027, 1, 1),
      addendumDecisions: const [
        LeaseRenewalAddendumDecisionInput(
          sourceAddendumSeriesPublicId: '11111111-1111-1111-1111-111111111111',
          decision: 'ReissueAsAddendum',
        ),
      ],
      operationKey: 'stable-successor-44-81',
    );

    expect(adapter.method, 'POST');
    expect(
      adapter.path,
      '/lease-managements/44/agreements/81/successor-drafts',
    );
    expect(adapter.data, isA<Map<String, dynamic>>());
    final body = adapter.data! as Map<String, dynamic>;
    expect(body['changeType'], 'Renewal');
    expect(body['termStartOn'], '2027-01-01');
    expect(body['termEndOn'], '2027-12-31');
    expect(body['governingFromOn'], '2027-01-01');
    expect(body['addendumDecisions'], [
      {
        'sourceAddendumSeriesPublicId': '11111111-1111-1111-1111-111111111111',
        'decision': 'ReissueAsAddendum',
      },
    ]);
    expect(adapter.headers?['Idempotency-Key'], 'stable-successor-44-81');
    expect(draft.leaseManagementId, 44);
    expect(draft.leaseAgreementId, 82);
  });

  test(
    'effective addendum series GET preserves server order and counts',
    () async {
      final adapter = _RecordingAdapter();
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = LeaseManagementsRepository(dio);

      final context = await repo.effectiveAddendumSeries(
        leaseManagementId: 44,
        sourceAgreementId: 81,
      );

      expect(adapter.method, 'GET');
      expect(
        adapter.path,
        '/lease-managements/44/agreements/81/effective-addendum-series',
      );
      expect(context.requiredDecisionCount, 1);
      expect(context.businessDate, DateTime(2026, 7, 13));
      expect(
        context.series.single.seriesPublicId,
        '11111111-1111-1111-1111-111111111111',
      );
      expect(context.series.single.currentLeaseAddendumId, 91);
      expect(context.series.single.title, 'ADD-91');
      expect(context.series.single.financialEffectCount, 2);
      expect(context.series.single.financialEffects.first.id, 301);
      expect(context.series.single.financialEffects.last.chargeCode, 'PET');
      expect(
        context.series.single.financialEffects.map(
          (effect) => effect.effectType,
        ),
        ['RecurringRentDelta', 'OneTimeCharge'],
      );
    },
  );

  test('restatement posts no addendum decisions', () async {
    final adapter = _RecordingAdapter();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = LeaseManagementsRepository(dio);

    await repo.createSuccessorDraft(
      leaseManagementId: 44,
      sourceAgreementId: 81,
      changeType: 'Restatement',
      termStartOn: DateTime(2026, 1, 1),
      termEndOn: DateTime(2026, 12, 31),
      governingFromOn: DateTime(2026, 7, 13),
      addendumDecisions: const [],
      operationKey: 'stable-restatement-44-81',
    );

    expect(adapter.method, 'POST');
    expect(
      adapter.path,
      '/lease-managements/44/agreements/81/successor-drafts',
    );
    expect(adapter.headers?['Idempotency-Key'], 'stable-restatement-44-81');
    expect(
      (adapter.data! as Map<String, dynamic>)['addendumDecisions'],
      isEmpty,
    );
  });

  test(
    'correction sends its required reason and cancel-draft is explicit',
    () async {
      final adapter = _RecordingAdapter();
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = LeaseManagementsRepository(dio);

      await repo.createSuccessorDraft(
        leaseManagementId: 44,
        sourceAgreementId: 81,
        changeType: 'Correction',
        correctionReason: 'The monthly rent was transcribed incorrectly.',
        termStartOn: DateTime(2026, 1, 1),
        termEndOn: DateTime(2026, 12, 31),
        governingFromOn: DateTime(2026, 7, 13),
        addendumDecisions: const [],
        operationKey: 'stable-correction-44-81',
      );

      expect(
        (adapter.data! as Map<String, dynamic>)['correctionReason'],
        'The monthly rent was transcribed incorrectly.',
      );

      await repo.cancelSuccessorDraft(
        leaseManagementId: 44,
        leaseAgreementId: 82,
        cancellationReason: 'A newer draft replaced this attempt.',
        operationKey: 'stable-cancel-44-82',
      );

      expect(adapter.path, '/lease-managements/44/agreements/82/cancel-draft');
      expect(adapter.data, {
        'cancellationReason': 'A newer draft replaced this attempt.',
      });
      expect(adapter.headers?['Idempotency-Key'], 'stable-cancel-44-82');
    },
  );
}

class _RecordingAdapter implements HttpClientAdapter {
  String? method;
  String? path;
  Object? data;
  Map<String, dynamic>? headers;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    method = options.method;
    path = options.path;
    data = options.data;
    headers = Map<String, dynamic>.from(options.headers);

    final response = options.method == 'GET'
        ? {
            'leaseManagementId': 44,
            'sourceAgreementId': 81,
            'sourceAgreementNumber': 'AGR-81',
            'sourceTermStartOn': '2026-01-01',
            'sourceTermEndOn': '2026-12-31',
            'sourceGoverningFromOn': '2026-01-01',
            'businessDate': '2026-07-13',
            'decisionRequired': true,
            'requiredDecisionCount': 1,
            'series': [
              {
                'seriesPublicId': '11111111-1111-1111-1111-111111111111',
                'currentLeaseAddendumId': 91,
                'currentLeaseAddendumPublicId':
                    '22222222-2222-2222-2222-222222222222',
                'currentVersionNumber': 2,
                'baseAgreementId': 80,
                'baseAgreementNumber': 'AGR-80',
                'baseAgreementTermStartOn': '2025-01-01',
                'baseAgreementTermEndOn': '2025-12-31',
                'purpose': 'Financial',
                'title': 'ADD-91',
                'effectiveFromOn': '2026-01-01',
                'decisionRequired': true,
                'financialEffectCount': 2,
                'financialEffects': [
                  {
                    'leaseAddendumFinancialEffectId': 301,
                    'effectType': 'RecurringRentDelta',
                    'amount': 75,
                    'currency': 'USD',
                    'chargeCode': 'RENT',
                    'effectiveFromOn': '2026-01-01',
                    'description': 'Pet rent',
                  },
                  {
                    'leaseAddendumFinancialEffectId': 302,
                    'effectType': 'OneTimeCharge',
                    'amount': 200,
                    'currency': 'USD',
                    'chargeCode': 'PET',
                    'dueOn': '2026-01-01',
                    'description': 'Pet fee',
                  },
                ],
              },
            ],
          }
        : {
            'leaseManagementId': 44,
            'leaseAgreementId': 82,
            'versionNumber': 2,
            'draftRevision': 1,
          };
    return ResponseBody.fromString(
      jsonEncode(response),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}
