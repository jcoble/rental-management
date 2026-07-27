import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/models/lease.dart';
import 'package:rental_command/features/leases/lease_detail_screen.dart';
import 'package:rental_command/features/leases/leases_repository.dart';
import 'package:rental_command/features/leases/successor_agreement_sheet.dart';

void main() {
  test('successor defaults preserve correction and advance future terms', () {
    final fixed = _agreement(
      termType: 'FixedTerm',
      termStart: '2026-01-01',
      termEnd: '2026-12-31',
      governingFrom: '2026-01-01',
    );

    final renewal = initialLeaseSuccessorDates(
      fixed,
      LeaseSuccessorOperation.renewal,
      businessDate: DateTime(2026, 7, 1),
    );
    final monthToMonth = initialLeaseSuccessorDates(
      fixed,
      LeaseSuccessorOperation.monthToMonth,
      businessDate: DateTime(2026, 7, 1),
    );

    expect(_date(renewal.termStart), '2027-01-01');
    expect(_date(renewal.termEnd!), '2027-12-31');
    expect(_date(renewal.governingFrom), '2027-01-01');
    expect(_date(monthToMonth.termStart), '2027-01-01');
    expect(monthToMonth.termEnd, isNull);
    expect(LeaseSuccessorOperation.monthToMonth.apiChangeType, 'MonthToMonth');
    expect(LeaseSuccessorOperation.renewal.apiChangeType, 'Renewal');
    expect(
      LeaseSuccessorOperation.renewal.requiresEffectiveAddendumDecisions,
      isTrue,
    );
    expect(
      LeaseSuccessorOperation.monthToMonth.requiresEffectiveAddendumDecisions,
      isTrue,
    );
    expect(
      LeaseSuccessorOperation.correction.requiresEffectiveAddendumDecisions,
      isFalse,
    );
    expect(
      LeaseSuccessorOperation.restatement.requiresEffectiveAddendumDecisions,
      isFalse,
    );
  });

  test('month-to-month correction never invents a fixed end date', () {
    final source = _agreement(
      termType: 'MonthToMonth',
      termStart: '2026-01-01',
      governingFrom: '2026-01-01',
    );

    final correction = initialLeaseSuccessorDates(
      source,
      LeaseSuccessorOperation.correction,
      businessDate: DateTime(2026, 7, 1),
    );

    expect(_date(correction.termStart), '2026-01-01');
    expect(correction.termEnd, isNull);
    expect(_date(correction.governingFrom), '2026-07-01');

    final restatement = initialLeaseSuccessorDates(
      source,
      LeaseSuccessorOperation.restatement,
      businessDate: DateTime(2026, 7, 1),
    );
    expect(_date(restatement.termStart), '2026-01-01');
    expect(restatement.termEnd, isNull);
    expect(_date(restatement.governingFrom), '2026-07-01');
    expect(LeaseSuccessorOperation.restatement.apiChangeType, 'Restatement');
  });

  test('successor addendum decisions use only the canonical server series', () {
    final sheet = File(
      'lib/features/leases/successor_agreement_sheet.dart',
    ).readAsStringSync();
    final detail = File(
      'lib/features/leases/lease_detail_screen.dart',
    ).readAsStringSync();
    final repository = File(
      'lib/features/leases/leases_repository.dart',
    ).readAsStringSync();

    expect(sheet, contains("label: 'Addenda'"));
    expect(sheet, contains("value: 'End'"));
    expect(sheet, contains("value: 'IncorporateIntoBase'"));
    expect(sheet, contains("value: 'ReissueAsAddendum'"));
    expect(sheet, contains('series.financialEffects'));
    expect(sheet, contains('series.financialEffectCount'));
    expect(sheet, contains('if (!series.decisionRequired) continue;'));
    expect(
      sheet,
      contains('sourceAddendumSeriesPublicId: series.seriesPublicId'),
    );
    expect(sheet, contains('No addendum terms or effects carry forward'));
    expect(sheet, contains('folded into the new base agreement'));
    expect(sheet, contains('copies the terms, signers, and financial effects'));
    expect(sheet, isNot(contains('.sort(')));
    expect(sheet, isNot(contains('.where(')));
    expect(
      detail,
      contains('if (operation.requiresEffectiveAddendumDecisions)'),
    );
    expect(detail, contains('.effectiveAddendumSeries('));
    expect(detail, contains('addendumDecisions: result.addendumDecisions'));
    expect(detail, contains('LeaseSuccessorOperation.restatement'));
    expect(detail, isNot(contains('addendumCount')));
    expect(repository, contains('effective-addendum-series'));
    expect(repository, contains("'addendumDecisions': addendumDecisions"));
    expect(repository, contains(r'$sourceAgreementId/successor-drafts'));
    expect(repository, contains("'changeType': changeType"));
    expect(repository, contains("'Idempotency-Key': operationKey"));
    expect(detail, contains('action: () => ref'));
    expect(detail, contains('operationKey: result.operationKey'));
    expect(detail, contains('_runWithStableRetry('));
  });

  test(
    'correction successor UX explains governing state and supports cancel',
    () {
      final sheet = File(
        'lib/features/leases/successor_agreement_sheet.dart',
      ).readAsStringSync();
      final editor = File(
        'lib/features/leases/agreement_draft_action_sheets.dart',
      ).readAsStringSync();
      final detail = File(
        'lib/features/leases/lease_detail_screen.dart',
      ).readAsStringSync();
      final repository = File(
        'lib/features/leases/leases_repository.dart',
      ).readAsStringSync();

      expect(sheet, contains("labelText: 'Why is this correction needed?'"));
      expect(sheet, contains('_correctionReasonValid'));
      expect(
        sheet,
        contains(
          'keeps governing until the replacement is fully signed and executed',
        ),
      );
      expect(editor, contains('Old agreement vs correction'));
      expect(editor, contains('unchanged copied field'));
      expect(detail, contains('source: source'));
      expect(detail, contains('.agreementDraft('));
      expect(detail, contains("label: const Text('Cancel draft')"));
      expect(repository, contains("'correctionReason': correctionReason"));
      expect(repository, contains(r'$leaseAgreementId/cancel-draft'));
    },
  );

  test('agreement history keeps issued and executed artifacts distinct', () {
    final detail = File(
      'lib/features/leases/lease_detail_screen.dart',
    ).readAsStringSync();

    expect(detail, contains("label: const Text('Issued PDF')"));
    expect(detail, contains("label: const Text('Executed PDF')"));
    expect(detail, contains('agreement.issuedArtifact!'));
    expect(detail, contains('agreement.executedArtifact!'));
    expect(
      detail,
      isNot(contains('executedArtifact ?? agreement.issuedArtifact')),
    );
  });

  test(
    'give possession posts exact relationship and unit with idempotency',
    () async {
      final adapter = _GivePossessionAdapter();
      final repository = LeaseManagementsRepository(
        Dio(BaseOptions(baseUrl: 'https://example.test'))
          ..httpClientAdapter = adapter,
      );

      final result = await repository.givePossession(
        leaseManagementId: 44,
        unitId: 12,
        operationKey: 'give-possession-mobile-44',
      );

      expect(adapter.path, '/lease-managements/44/give-possession');
      expect(adapter.data, {'unitId': 12});
      expect(adapter.headers['Idempotency-Key'], 'give-possession-mobile-44');
      expect(result.leaseManagementId, 44);
      expect(result.unitId, 12);
      expect(result.possessionGivenAt.toUtc().year, 2026);
    },
  );

  testWidgets(
    'give possession action opens confirmation sheet without provider disposal assertion',
    (tester) async {
      tester.view.physicalSize = const Size(430, 3000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            authControllerProvider.overrideWith(
              () => _StaticAuthController(_managementAuthority()),
            ),
            leaseManagementDetailProvider.overrideWith(
              (ref, id) async => _activeManagementDetail(),
            ),
            leaseAgreementHistoryProvider.overrideWith(
              (ref, id) async => LeaseAgreementHistoryPage(
                items: [_executedGoverningAgreement()],
              ),
            ),
            leaseHouseholdContextProvider.overrideWith(
              (ref, id) async => const ReturnPossessionContext(
                parties: [],
                activeTenantUserAccesses: [],
              ),
            ),
            leaseAddendumHistoryProvider.overrideWith(
              (ref, query) async => const LeaseAddendumHistoryPage(
                items: [],
                totalCount: 0,
                skip: 0,
                take: 10,
              ),
            ),
          ],
          child: const MaterialApp(
            home: Scaffold(
              body: LeaseManagementDetailScreen(leaseManagementId: 44),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Give possession'), findsOneWidget);
      await tester.tap(find.text('Give possession'));
      await tester.pumpAndSettle();

      expect(find.text('Confirm possession'), findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );

  test('return possession is an explicit server-dated mobile action', () {
    final sheet = File(
      'lib/features/leases/return_possession_sheet.dart',
    ).readAsStringSync();
    final detail = File(
      'lib/features/leases/lease_detail_screen.dart',
    ).readAsStringSync();

    expect(sheet, contains('TabbedFormSheet('));
    expect(sheet, contains("label: 'Household'"));
    expect(sheet, contains("label: 'Access'"));
    expect(sheet, contains("label: 'Review'"));
    expect(sheet, contains("value: 'EndMembership'"));
    expect(sheet, contains("value: 'RetainGuarantor'"));
    expect(sheet, contains("value: 'RevokeNow'"));
    expect(sheet, contains("value: 'RetainHistorical'"));
    expect(sheet, contains('portfolio current business date'));
    expect(sheet, contains('no phone date is submitted'));
    expect(sheet, isNot(contains('DateTime.now')));
    expect(sheet, isNot(contains('effectiveOn')));
    expect(detail, contains("label: const Text('Return possession')"));
    expect(detail, contains('.returnPossessionContext(summary.id)'));
  });
}

LeaseAgreementHistory _agreement({
  required String termType,
  required String termStart,
  String? termEnd,
  required String governingFrom,
}) => LeaseAgreementHistory.fromJson({
  'leaseAgreementId': 81,
  'versionNumber': 1,
  'agreementNumber': 'AGR-81',
  'changeType': 'Initial',
  'termType': termType,
  'termStartOn': termStart,
  'termEndOn': termEnd,
  'governingFromOn': governingFrom,
  'baseRentAmount': 1500,
  'agreementStatus': 'Active',
  'isGoverning': true,
  'hasLiveReissue': false,
});

String _date(DateTime value) => value.toIso8601String().split('T').first;

LeaseManagementDetail _activeManagementDetail() => LeaseManagementDetail(
  summary: LeaseManagementSummary(
    id: 44,
    publicId: 'DEMO-LM-ACTIVE-044',
    relationshipNumber: 'DEMO-LM-ACTIVE-044',
    propertyId: 8,
    propertyName: 'Elm Haven',
    unitId: 12,
    unitNumber: 'Main',
    lifecycle: 'Active',
    businessDate: DateTime(2026, 7, 16),
    agreementId: 81,
    agreementNumber: 'AGR-81',
    agreementStatus: 'Active',
    tenantAccountId: 44,
    primaryTenantId: 44,
    primaryTenantName: 'Resident 44',
    currentPartyCount: 1,
    currentResidentCount: 1,
    hasReconciliationException: false,
    updatedAt: DateTime(2026, 7, 16),
  ),
  parties: const [],
  agreementCount: 1,
  addendumCount: 0,
  legalArtifactCount: 1,
);

LeaseAgreementHistory _executedGoverningAgreement() =>
    LeaseAgreementHistory.fromJson({
      'leaseAgreementId': 81,
      'versionNumber': 1,
      'agreementNumber': 'AGR-81',
      'changeType': 'Initial',
      'termType': 'Fixed',
      'termStartOn': '2026-01-01',
      'termEndOn': '2026-12-31',
      'governingFromOn': '2026-01-01',
      'baseRentAmount': 1500,
      'agreementStatus': 'Active',
      'isGoverning': true,
      'hasLiveReissue': false,
      'fullyExecutedAtUtc': '2025-12-20T12:00:00Z',
      'executedArtifact': {
        'legalDocumentArtifactId': 2,
        'fileName': 'executed-agreement.pdf',
        'contentType': 'application/pdf',
        'byteLength': 2048,
      },
    });

AuthStateAuthenticated _managementAuthority() {
  const experience = WorkspaceExperience.management;
  return AuthStateAuthenticated(
    const AuthUser(
      id: 1,
      email: 'manager@example.test',
      displayName: 'Test manager',
      emailVerified: true,
    ),
    const AccessEnvelope(
      identity: AccessIdentity(userId: 1, displayName: 'Test manager'),
      selectedContext: SelectedAccessContext(
        accessContextId: 1,
        portfolioId: 1,
        workspaceName: 'Test workspace',
        accessRevision: 1,
        activeExperience: experience,
      ),
      defaultExperience: experience,
      availableExperiences: [experience],
      assignments: [],
      navigation: [
        NavigationCapabilities(
          experience: experience,
          capabilityKeys: ['rentals.manage'],
        ),
      ],
    ),
    activeExperience: experience,
  );
}

class _StaticAuthController extends AuthController {
  _StaticAuthController(this.initialState);

  final AuthState initialState;

  @override
  AuthState build() => initialState;
}

class _GivePossessionAdapter implements HttpClientAdapter {
  String path = '';
  Map<String, dynamic> headers = const {};
  Map<String, dynamic> data = const {};

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    path = options.path;
    headers = Map<String, dynamic>.from(options.headers);
    data = Map<String, dynamic>.from(options.data as Map);
    return ResponseBody.fromString(
      jsonEncode({
        'leaseManagementId': 44,
        'unitId': 12,
        'possessionGivenAtUtc': '2026-07-13T14:00:00Z',
        'replayed': false,
      }),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}
