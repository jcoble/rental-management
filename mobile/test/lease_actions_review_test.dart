import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/models/lease.dart';
import 'package:rental_command/features/applications/applications_models.dart';
import 'package:rental_command/features/leases/lease_detail_screen.dart';
import 'package:rental_command/features/leases/leases_repository.dart';
import 'package:rental_command/features/leases/prepare_move_in_sheet.dart';
import 'package:rental_command/features/leases/successor_agreement_sheet.dart';

void main() {
  testWidgets('actions card shows renew change end and a more sheet', (
    tester,
  ) async {
    await _pumpLeaseDetail(tester);

    expect(find.text('Renew'), findsOneWidget);
    expect(find.text('Change the lease'), findsOneWidget);
    expect(find.text('End the lease'), findsOneWidget);
    expect(find.text('More'), findsOneWidget);

    expect(find.text('Create restatement'), findsNothing);
    expect(find.text('Create correction'), findsNothing);
    expect(find.text('Create month-to-month'), findsNothing);
    expect(find.text('Ask this tenant & lease'), findsNothing);

    await tester.tap(find.text('More'));
    await tester.pumpAndSettle();

    expect(find.text('Ask about this tenant & lease'), findsOneWidget);
    expect(find.text('View tenant account'), findsOneWidget);
  });

  testWidgets(
    'change the lease gates fields by kind and posts the matching successor route',
    (tester) async {
      await _pumpOpener(
        tester,
        open: (context, ref) => showSuccessorAgreementSheet(
          context,
          source: _governingAgreement(),
          initialOperation: null,
          propertyName: 'Elm Haven',
          unitNumber: 'Main',
          businessDate: DateTime(2026, 7, 16),
          effectiveAddendumSeries: _noAddendumSeries(),
        ),
      );
      await tester.tap(find.text('Open'));
      await tester.pumpAndSettle();

      expect(find.text('Change the lease'), findsOneWidget);
      expect(find.text('What kind of change?'), findsOneWidget);
      expect(find.text('What was wrong?'), findsOneWidget);
      expect(find.text('New lease ends'), findsNothing);

      await tester.tap(find.byKey(const Key('change-kind')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Renew for another term').last);
      await tester.pumpAndSettle();

      expect(find.text('What was wrong?'), findsNothing);
      expect(find.text('New lease starts'), findsOneWidget);
      expect(find.text('New lease ends'), findsOneWidget);

      final adapter = _RecordingAdapter(
        status: 201,
        body: {
          'leaseManagementId': 44,
          'leaseAgreementId': 82,
          'versionNumber': 2,
          'draftRevision': 1,
          'sourceAgreementId': 81,
          'leaseAgreementSignerIds': <int>[],
          'addendumDecisionIds': <int>[],
          'replacementAddendumIds': <int>[],
          'replayed': false,
        },
      );
      final repository = LeaseManagementsRepository(
        Dio(BaseOptions(baseUrl: 'https://example.test'))
          ..httpClientAdapter = adapter,
      );

      await tester.runAsync(() async {
        await repository.createSuccessorDraft(
          leaseManagementId: 44,
          sourceAgreementId: 81,
          changeType: LeaseSuccessorOperation.correction.apiChangeType,
          correctionReason: 'The rent was typed wrong',
          termStartOn: DateTime(2026, 1, 1),
          termEndOn: DateTime(2026, 12, 31),
          governingFromOn: DateTime(2026, 7, 16),
          addendumDecisions: const [],
          operationKey: 'change-correction-1',
        );
      });

      expect(
        adapter.path,
        '/lease-managements/44/agreements/81/successor-drafts',
      );
      expect(adapter.data['changeType'], 'Correction');
      expect(adapter.data['correctionReason'], 'The rent was typed wrong');

      await tester.runAsync(() async {
        await repository.createSuccessorDraft(
          leaseManagementId: 44,
          sourceAgreementId: 81,
          changeType: LeaseSuccessorOperation.renewal.apiChangeType,
          termStartOn: DateTime(2027, 1, 1),
          termEndOn: DateTime(2027, 12, 31),
          governingFromOn: DateTime(2027, 1, 1),
          addendumDecisions: const [],
          operationKey: 'change-renewal-1',
        );
      });

      expect(adapter.data['changeType'], 'Renewal');
      expect(adapter.data['correctionReason'], isNull);
    },
  );

  testWidgets('prepare move in is one page and shows field errors on tap', (
    tester,
  ) async {
    final adapter = _PrepareMoveInFakeApi();
    await _pumpPrepareMoveIn(tester, adapter);

    expect(find.text('Send lease to sign'), findsOneWidget);
    expect(find.text('Who is moving in'), findsOneWidget);
    expect(find.text('Which rental'), findsOneWidget);
    expect(find.text('More details'), findsOneWidget);
    expect(
      find.text('Moving an existing lease into Rental Command?'),
      findsOneWidget,
    );

    final button = tester.widget<FilledButton>(
      find.widgetWithText(FilledButton, 'Send lease to sign'),
    );
    expect(button.onPressed, isNotNull);

    await tester.tap(find.text('Send lease to sign'));
    await tester.pumpAndSettle();

    expect(find.text('Enter the monthly rent'), findsOneWidget);
    expect(adapter.preparePath, isNull);
    expect(find.text('Send lease to sign'), findsOneWidget);
  });

  testWidgets('prepare move in posts primary tenant without a role picker', (
    tester,
  ) async {
    final adapter = _PrepareMoveInFakeApi();
    await _pumpPrepareMoveIn(tester, adapter);

    expect(find.text('Household role'), findsNothing);
    expect(find.text('Primary tenant'), findsNothing);

    await tester.enterText(find.byKey(const Key('move-in-rent')), '1500');
    await tester.pumpAndSettle();
    await tester.tap(find.text('Send lease to sign'));
    await tester.pumpAndSettle();

    expect(adapter.preparePath, '/lease-managements/prepare-move-in');
    expect(adapter.prepareData['parties'], [
      {
        'tenantId': 56,
        'role': 'PrimaryTenant',
        'guarantorLegalNoticeEligible': false,
        'changeReason': 'Approved application move-in',
        'isAgreementSigner': true,
        'signingOrder': 1,
        'isRequiredSigner': true,
      },
    ]);
    expect(adapter.prepareData['baseRentAmount'], 1500);
  });
}

Future<void> _pumpPrepareMoveIn(
  WidgetTester tester,
  _PrepareMoveInFakeApi adapter,
) async {
  await _pumpOpener(
    tester,
    adapter: adapter,
    open: (context, ref) =>
        showPrepareMoveInSheet(context, ref: ref, application: _application()),
  );
  await tester.tap(find.text('Open'));
  await tester.pumpAndSettle();
}

Future<void> _pumpOpener(
  WidgetTester tester, {
  required Future<Object?> Function(BuildContext context, WidgetRef ref) open,
  HttpClientAdapter? adapter,
}) async {
  tester.view.physicalSize = const Size(430, 2600);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  final dio = Dio(BaseOptions(baseUrl: 'https://example.test'));
  if (adapter != null) dio.httpClientAdapter = adapter;

  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        leaseManagementsRepositoryProvider.overrideWithValue(
          LeaseManagementsRepository(dio),
        ),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: Consumer(
            builder: (context, ref, _) => Center(
              child: ElevatedButton(
                onPressed: () => open(context, ref),
                child: const Text('Open'),
              ),
            ),
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

Future<void> _pumpLeaseDetail(WidgetTester tester) async {
  tester.view.physicalSize = const Size(430, 3200);
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
          (ref, id) async => _managementDetail(),
        ),
        leaseAgreementHistoryProvider.overrideWith(
          (ref, id) async =>
              LeaseAgreementHistoryPage(items: [_governingAgreement()]),
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
}

LeaseAgreementHistory _governingAgreement() => LeaseAgreementHistory.fromJson({
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

LeaseAgreementEffectiveAddendumSeries _noAddendumSeries() =>
    LeaseAgreementEffectiveAddendumSeries(
      leaseManagementId: 44,
      sourceAgreementId: 81,
      sourceAgreementNumber: 'AGR-81',
      sourceTermStartOn: DateTime(2026, 1, 1),
      sourceTermEndOn: DateTime(2026, 12, 31),
      sourceGoverningFromOn: DateTime(2026, 1, 1),
      businessDate: DateTime(2026, 7, 16),
      decisionRequired: false,
      requiredDecisionCount: 0,
      series: const [],
    );

LeaseManagementDetail _managementDetail() => LeaseManagementDetail(
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
    possessionGivenAt: DateTime(2026, 1, 1),
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

RentalApplication _application() => RentalApplication(
  id: 12,
  propertyId: 8,
  unitId: 34,
  firstName: 'Ada',
  lastName: 'Applicant',
  consentGiven: true,
  status: 'Approved',
  approvedTenantId: 56,
  desiredMoveInDate: DateTime(2026, 8, 1),
);

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

class _RecordingAdapter implements HttpClientAdapter {
  _RecordingAdapter({required this.status, required this.body});

  final int status;
  final Map<String, dynamic> body;

  String path = '';
  Map<String, dynamic> data = <String, dynamic>{};
  Map<String, dynamic> headers = <String, dynamic>{};

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    path = options.path;
    data = (options.data as Map).cast<String, dynamic>();
    headers = options.headers;
    return ResponseBody.fromString(
      jsonEncode(body),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

class _PrepareMoveInFakeApi implements HttpClientAdapter {
  String? preparePath;
  Map<String, dynamic> prepareData = <String, dynamic>{};

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    if (options.path == '/lease-managements/prepare-move-in-context') {
      return _json({'businessDate': '2026-07-16'});
    }
    if (options.path == '/document-templates/page') {
      return _json({
        'items': [
          {'id': 78, 'name': 'Standard lease'},
        ],
        'totalCount': 1,
        'skip': 0,
        'take': 20,
      });
    }
    preparePath = options.path;
    prepareData = (options.data as Map).cast<String, dynamic>();
    return _json({
      'leaseManagementId': 90,
      'tenantAccountId': 91,
      'leaseAgreementId': 92,
    }, status: 201);
  }

  ResponseBody _json(Map<String, dynamic> body, {int status = 200}) =>
      ResponseBody.fromString(
        jsonEncode(body),
        status,
        headers: {
          Headers.contentTypeHeader: [Headers.jsonContentType],
        },
      );

  @override
  void close({bool force = false}) {}
}
