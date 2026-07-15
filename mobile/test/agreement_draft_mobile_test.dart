import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/lease.dart';
import 'package:rental_command/features/leases/leases_repository.dart';

void main() {
  test(
    'canonical draft and ordinary party reads preserve exact identities',
    () {
      final draft = LeaseAgreementDraftDetail.fromJson(_draftJson);
      final management = LeaseManagementDetail.fromJson({
        'summary': _summaryJson,
        'parties': [
          {
            'leaseManagementPartyId': 61,
            'tenantId': 71,
            'tenantName': 'Ada Tenant',
            'role': 'PrimaryTenant',
            'effectiveFrom': '2026-01-01',
            'isCurrent': true,
          },
        ],
        'agreementCount': 1,
        'addendumCount': 0,
        'legalArtifactCount': 0,
      });

      expect(draft.draftRevision, 4);
      expect(draft.documentSourceVersionId, 81);
      expect(draft.documentTemplateId, 82);
      expect(draft.termsPayload, {'pets': false});
      expect(draft.signers.map((signer) => signer.signingOrder), [1, 2]);
      expect(draft.hasExactOrderedSigners, isTrue);
      expect(management.parties.single.isCurrent, isTrue);
      expect(management.parties.single.id, 61);
    },
  );

  test(
    'draft edit preserves revision, terms, template, and ordered signers',
    () async {
      final adapter = _DraftAdapter();
      final repository = _repository(adapter);
      final draft = LeaseAgreementDraftDetail.fromJson(_draftJson);

      await repository.editAgreementDraft(
        leaseManagementId: 44,
        leaseAgreementId: 45,
        operationKey: 'edit-draft-45',
        input: EditAgreementDraftInput(
          draftRevision: draft.draftRevision,
          agreementNumber: draft.agreementNumber,
          termType: draft.termType,
          termStartOn: draft.termStartOn,
          termEndOn: draft.termEndOn,
          governingFromOn: draft.governingFromOn,
          baseRentAmount: 1600,
          rentDueDay: draft.rentDueDay,
          securityDepositObligation: draft.securityDepositObligation,
          lateFeeAmount: draft.lateFeeAmount,
          gracePeriodDays: draft.gracePeriodDays,
          termsSchemaVersion: draft.termsSchemaVersion,
          termsPayload: draft.termsPayload,
          documentTemplateId: draft.documentTemplateId!,
          signers: draft.signers,
        ),
      );

      final request = adapter.requests.single;
      expect(request.method, 'PATCH');
      expect(request.path, '/lease-managements/44/agreements/45/draft');
      expect(request.headers['Idempotency-Key'], 'edit-draft-45');
      expect(request.data['draftRevision'], 4);
      expect(request.data['termsPayload'], {'pets': false});
      expect(request.data['documentTemplateId'], 82);
      expect(
        (request.data['signers'] as List).map(
          (signer) => (signer as Map)['signingOrder'],
        ),
        [1, 2],
      );
    },
  );

  test(
    'issuance uses preparation metadata without signer token fabrication',
    () async {
      final adapter = _DraftAdapter();
      final repository = _repository(adapter);
      final preparation = await repository.prepareAgreementIssuance(
        leaseManagementId: 44,
        leaseAgreementId: 45,
        draftRevision: 4,
        operationKey: 'prepare-45',
      );
      final issued = await repository.issueAgreement(
        leaseManagementId: 44,
        leaseAgreementId: 45,
        preparation: preparation,
        subject: 'Agreement for signature',
        operationKey: 'issue-45',
      );

      expect(adapter.requests[0].path, contains('/issuance-preparations'));
      expect(adapter.requests[0].data, {'draftRevision': 4});
      expect(adapter.requests[1].path, endsWith('/45/issue'));
      expect(adapter.requests[1].data['pendingUploadId'], 'pending-45');
      expect(adapter.requests[1].data['issuanceFingerprint'], 'fingerprint');
      expect(adapter.requests[1].data, isNot(contains('signerToken')));
      expect(issued.signatureRequestId, 501);
    },
  );

  test('template selector sends property scope to the server', () async {
    final adapter = _DraftAdapter();
    final repository = _repository(adapter);

    await repository.leaseTemplatesPage(
      propertyId: 17,
      search: 'standard',
      skip: 20,
      take: 20,
    );

    final request = adapter.requests.single;
    expect(request.path, '/document-templates/page');
    expect(request.query['propertyId'], 17);
    expect(request.query['kind'], 'Lease');
    expect(request.query['status'], 'Active');
    expect(request.query['search'], 'standard');
  });

  test(
    'return possession context preserves the server-filtered current party set',
    () async {
      final adapter = _DraftAdapter();
      final repository = _repository(adapter);

      final context = await repository.returnPossessionContext(44);

      expect(
        adapter.requests.single.path,
        '/lease-managements/44/return-possession-context',
      );
      expect(context.parties.single.id, 61);
      expect(context.parties.single.isCurrent, isTrue);
      expect(context.activeTenantUserAccesses.single.id, 91);
      expect(context.activeTenantUserAccesses.single.tenantName, 'Ada Tenant');
    },
  );

  test(
    'return possession sends every explicit party and access disposition',
    () async {
      final adapter = _DraftAdapter();
      final repository = _repository(adapter);

      await repository.returnPossession(
        leaseManagementId: 44,
        unitId: 12,
        parties: const [
          ReturnPossessionPartyInput(
            leaseManagementPartyId: 61,
            disposition: 'EndMembership',
          ),
        ],
        accesses: const [
          ReturnPossessionAccessInput(
            tenantUserAccessId: 91,
            disposition: 'RevokeNow',
          ),
        ],
        turnoverReason: 'Keys returned after move-out inspection.',
        operationKey: 'return-44',
      );

      final request = adapter.requests.single;
      expect(request.data, isNot(contains('effectiveOn')));
      expect(request.data['parties'], [
        {'leaseManagementPartyId': 61, 'disposition': 'EndMembership'},
      ]);
      expect(request.data['accesses'], [
        {'tenantUserAccessId': 91, 'disposition': 'RevokeNow'},
      ]);
    },
  );
}

LeaseManagementsRepository _repository(HttpClientAdapter adapter) =>
    LeaseManagementsRepository(
      Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter,
    );

const _summaryJson = <String, dynamic>{
  'leaseManagementId': 44,
  'leaseManagementPublicId': 'management-public-id',
  'relationshipNumber': 'REL-44',
  'propertyId': 17,
  'propertyName': 'Oak House',
  'unitId': 12,
  'unitNumber': '2B',
  'lifecycle': 'Occupied',
  'currentPartyCount': 1,
  'currentResidentCount': 1,
  'hasReconciliationException': false,
  'updatedAtUtc': '2026-07-13T12:00:00Z',
};

const _draftJson = <String, dynamic>{
  'leaseManagementId': 44,
  'leaseAgreementId': 45,
  'publicId': 'agreement-public-id',
  'versionNumber': 2,
  'draftRevision': 4,
  'agreementNumber': 'AGR-45',
  'changeType': 'Renewal',
  'termType': 'FixedTerm',
  'termStartOn': '2027-01-01',
  'termEndOn': '2027-12-31',
  'governingFromOn': '2027-01-01',
  'baseRentAmount': 1500,
  'rentDueDay': 1,
  'securityDepositObligation': 1500,
  'lateFeeAmount': 50,
  'gracePeriodDays': 5,
  'currency': 'USD',
  'termsSchemaVersion': 1,
  'termsPayload': {'pets': false},
  'documentSourceVersionId': 81,
  'documentTemplateId': 82,
  'documentTemplateVersion': 3,
  'signers': [
    {
      'leaseAgreementSignerId': 202,
      'leaseManagementPartyId': 62,
      'tenantId': 72,
      'signerRole': 'CoTenant',
      'nameSnapshot': 'Ben Tenant',
      'emailSnapshot': 'ben@example.test',
      'signingOrder': 2,
      'isRequired': true,
    },
    {
      'leaseAgreementSignerId': 201,
      'leaseManagementPartyId': 61,
      'tenantId': 71,
      'signerRole': 'PrimaryTenant',
      'nameSnapshot': 'Ada Tenant',
      'emailSnapshot': 'ada@example.test',
      'signingOrder': 1,
      'isRequired': true,
    },
  ],
  'createdAtUtc': '2026-07-13T12:00:00Z',
  'updatedAtUtc': '2026-07-13T12:30:00Z',
};

class _RecordedRequest {
  const _RecordedRequest({
    required this.method,
    required this.path,
    required this.headers,
    required this.query,
    required this.data,
  });
  final String method;
  final String path;
  final Map<String, dynamic> headers;
  final Map<String, dynamic> query;
  final Map<String, dynamic> data;
}

class _DraftAdapter implements HttpClientAdapter {
  final requests = <_RecordedRequest>[];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(
      _RecordedRequest(
        method: options.method,
        path: options.path,
        headers: Map<String, dynamic>.from(options.headers),
        query: Map<String, dynamic>.from(options.queryParameters),
        data: options.data is Map
            ? Map<String, dynamic>.from(options.data as Map)
            : <String, dynamic>{},
      ),
    );
    final response = switch (options.path) {
      String path when path.endsWith('/issuance-preparations') => {
        'pendingUploadId': 'pending-45',
        'draftRevision': 4,
        'documentSourceVersionId': 81,
        'issuanceFingerprint': 'fingerprint',
        'storageKey': 'agreements/45.pdf',
        'fileName': 'agreement-45.pdf',
        'fileSize': 1200,
        'contentSha256': 'abc123',
      },
      String path when path.endsWith('/issue') => {
        'signatureRequestPublicId': 'signature-public-id',
        'leaseManagementId': 44,
        'leaseAgreementId': 45,
        'signatureRequestId': 501,
        'issuedArtifactId': 601,
        'replayed': false,
      },
      '/document-templates/page' => {
        'items': <dynamic>[],
        'totalCount': 0,
        'skip': 20,
        'take': 20,
      },
      String path when path.endsWith('/return-possession') => {
        'leaseManagementId': 44,
        'unitId': 12,
        'turnoverPeriodId': 301,
        'possessionReturnedAtUtc': '2026-07-13T18:00:00Z',
        'replayed': false,
      },
      String path when path.endsWith('/return-possession-context') => {
        'parties': [
          {
            'leaseManagementPartyId': 61,
            'tenantId': 71,
            'tenantName': 'Ada Tenant',
            'role': 'PrimaryTenant',
            'effectiveFrom': '2026-01-01',
            'isCurrent': true,
          },
        ],
        'activeTenantUserAccesses': [
          {
            'tenantUserAccessId': 91,
            'publicId': 'access-public-id',
            'leaseManagementPartyId': 61,
            'accessContextId': 101,
            'applicationUserId': 111,
            'tenantName': 'Ada Tenant',
            'userDisplayName': 'Ada Tenant',
            'userEmail': 'ada@example.test',
            'grantedAtUtc': '2026-01-01T12:00:00Z',
            'reason': 'Tenant portal',
          },
        ],
      },
      _ => {
        'leaseManagementId': 44,
        'leaseAgreementId': 45,
        'versionNumber': 2,
        'draftRevision': 5,
        'sourceAgreementId': 41,
        'leaseAgreementSignerIds': [201, 202],
        'addendumDecisionIds': <int>[],
        'replacementAddendumIds': <int>[],
        'replayed': false,
      },
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
