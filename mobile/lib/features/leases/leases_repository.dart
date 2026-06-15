import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/models/models.dart';

/// Repository for leases.
///
/// Endpoints used:
///   GET    /leases                              — list (JWT-scoped; optional tenantId / propertyId query params)
///   GET    /leases/{id}                         — single lease
///   POST   /leases                              — create
///   PATCH  /leases/{id}                         — update (partial)
///   DELETE /leases/{id}                         — delete
///
/// Create / update body shape:
///   {
///     unitId, tenantId, startDate, endDate, monthlyRent, securityDeposit,
///     lateFeeAmount, rentDueDay, status?,
///     (on create: propertyId is inferred server-side from unitId,
///      but we still pass it for completeness)
///   }
class LeasesRepository {
  LeasesRepository(this._dio);

  final Dio _dio;

  Future<List<Lease>> listLeases({int? tenantId, int? propertyId}) async {
    try {
      final params = <String, dynamic>{
        'tenantId': tenantId,
        'propertyId': propertyId,
      }..removeWhere((_, v) => v == null);
      final response = await _dio.get<List<dynamic>>(
        '/leases',
        queryParameters: params.isNotEmpty ? params : null,
      );
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(Lease.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<Lease> getLease(int id) async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/leases/$id');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Lease.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Create body:
  /// { unitId, tenantId, startDate (yyyy-MM-dd), endDate (yyyy-MM-dd),
  ///   monthlyRent, securityDeposit, lateFeeAmount, rentDueDay, status? }
  Future<Lease> createLease(Map<String, dynamic> data) async {
    try {
      final response =
          await _dio.post<Map<String, dynamic>>('/leases', data: data);
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Lease.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Update body: same optional fields as create (partial PATCH)
  Future<Lease> updateLease(int id, Map<String, dynamic> data) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/leases/$id',
        data: data,
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Lease.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Convenience wrapper: update only the status field.
  Future<Lease> updateStatus(int id, String status) async {
    return updateLease(id, {'status': status});
  }

  Future<LeaseQuestionResponse> ask(int id, String question) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/leases/$id/ask',
        data: {'question': question},
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return LeaseQuestionResponse.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Generates a standard residential lease-agreement PDF from the lease's
  /// captured terms, stores it against the lease, and returns its reference.
  ///
  /// POST /leases/{id}/generate-document → { storedFileId, leaseId, fileName, … }
  Future<LeaseDocument> generateDocument(int id) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/leases/$id/generate-document',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return LeaseDocument.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Downloads the latest generated lease-agreement PDF bytes (authed via the
  /// shared Dio interceptor). The API returns 404 until one has been generated.
  ///
  /// GET /leases/{id}/document → application/pdf
  Future<Uint8List> documentBytes(int id) async {
    try {
      final response = await _dio.get<List<int>>(
        '/leases/$id/document',
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Transparent ledger for a lease: every charge and payment, newest first,
  /// each with a plain-English explanation of what it is plus running totals.
  Future<LeaseLedger> ledger(int id) async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/leases/$id/ledger');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return LeaseLedger.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Kicks off the e-signature workflow for a lease by sending it to the
  /// configured e-sign provider. Returns the resulting signature status.
  ///
  /// The backend is gated: until provider keys are configured the call returns
  /// **503** (surfaced as an [ApiException] with statusCode 503), which the UI
  /// treats as "not set up yet" rather than a hard error.
  ///
  /// POST /leases/{id}/send-for-signature → { leaseId, esignStatus, … }
  Future<LeaseSignatureStatus> sendForSignature(
    int id, {
    String? signerName,
    String? signerEmail,
  }) async {
    try {
      final body = <String, dynamic>{
        'signerName': signerName,
        'signerEmail': signerEmail,
      }..removeWhere((_, v) => v == null);
      final response = await _dio.post<Map<String, dynamic>>(
        '/leases/$id/send-for-signature',
        data: body.isNotEmpty ? body : null,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return LeaseSignatureStatus.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Current e-signature status for a lease.
  ///
  /// GET /leases/{id}/signature-status → { leaseId, esignStatus, … }
  Future<LeaseSignatureStatus> signatureStatus(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/leases/$id/signature-status',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return LeaseSignatureStatus.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Downloads the signed lease PDF bytes (authed via the shared Dio
  /// interceptor). The API returns 404 until a signed document exists.
  ///
  /// GET /leases/{id}/signed-document → application/pdf
  Future<Uint8List> signedDocumentBytes(int id) async {
    try {
      final response = await _dio.get<List<int>>(
        '/leases/$id/signed-document',
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

/// One line in a lease ledger — a charge or a payment.
class LeaseLedgerEntry {
  const LeaseLedgerEntry({
    required this.date,
    required this.type,
    required this.id,
    required this.description,
    required this.amount,
    required this.status,
    required this.explanation,
    this.propertyName,
    this.counterparty,
    this.category,
    this.sourceHref,
  });

  final DateTime date;

  /// Entry kind (e.g. "Charge", "Payment").
  final String type;
  final int id;
  final String description;
  final double amount;
  final String status;

  /// Ready-to-show plain-English "why" for this entry.
  final String explanation;

  final String? propertyName;
  final String? counterparty;
  final String? category;
  final String? sourceHref;

  factory LeaseLedgerEntry.fromJson(Map<String, dynamic> json) {
    return LeaseLedgerEntry(
      date: DateTime.tryParse(json['date'] as String? ?? '') ?? DateTime(0),
      type: json['type'] as String? ?? '',
      id: (json['id'] as num?)?.toInt() ?? 0,
      description: json['description'] as String? ?? '',
      amount: (json['amount'] as num?)?.toDouble() ?? 0,
      status: json['status'] as String? ?? '',
      explanation: json['explanation'] as String? ?? '',
      propertyName: json['propertyName'] as String?,
      counterparty: json['counterparty'] as String?,
      category: json['category'] as String?,
      sourceHref: json['sourceHref'] as String?,
    );
  }
}

/// Full response from `GET /api/v1/leases/{id}/ledger`.
class LeaseLedger {
  const LeaseLedger({
    required this.leaseId,
    required this.leaseNumber,
    required this.totalCharged,
    required this.totalPaid,
    required this.balance,
    required this.entries,
    this.tenantName,
    this.propertyName,
  });

  final int leaseId;
  final String leaseNumber;

  /// Sum of charges owed on this lease.
  final double totalCharged;

  /// Sum of payments received on this lease.
  final double totalPaid;

  /// Outstanding balance (charges minus payments). Negative means a credit.
  final double balance;

  final List<LeaseLedgerEntry> entries;
  final String? tenantName;
  final String? propertyName;

  factory LeaseLedger.fromJson(Map<String, dynamic> json) {
    final rawEntries = json['entries'];
    final entries = rawEntries is List
        ? rawEntries
            .whereType<Map<String, dynamic>>()
            .map(LeaseLedgerEntry.fromJson)
            .toList()
        : <LeaseLedgerEntry>[];

    return LeaseLedger(
      leaseId: (json['leaseId'] as num?)?.toInt() ?? 0,
      leaseNumber: json['leaseNumber'] as String? ?? '',
      totalCharged: (json['totalCharged'] as num?)?.toDouble() ?? 0,
      totalPaid: (json['totalPaid'] as num?)?.toDouble() ?? 0,
      balance: (json['balance'] as num?)?.toDouble() ?? 0,
      entries: entries,
      tenantName: json['tenantName'] as String?,
      propertyName: json['propertyName'] as String?,
    );
  }
}

/// Reference to a generated lease-agreement PDF, returned by
/// `POST /leases/{id}/generate-document`.
class LeaseDocument {
  const LeaseDocument({
    required this.storedFileId,
    required this.leaseId,
    required this.fileName,
    required this.fileSize,
    this.downloadUrl,
    this.generatedAt,
  });

  final int storedFileId;
  final int leaseId;
  final String fileName;
  final int fileSize;
  final String? downloadUrl;
  final DateTime? generatedAt;

  factory LeaseDocument.fromJson(Map<String, dynamic> json) {
    return LeaseDocument(
      storedFileId: (json['storedFileId'] as num?)?.toInt() ?? 0,
      leaseId: (json['leaseId'] as num?)?.toInt() ?? 0,
      fileName: json['fileName'] as String? ?? 'lease-agreement.pdf',
      fileSize: (json['fileSize'] as num?)?.toInt() ?? 0,
      downloadUrl: json['downloadUrl'] as String?,
      generatedAt: DateTime.tryParse(json['generatedAt'] as String? ?? ''),
    );
  }
}

/// E-signature status for a lease, returned by the send-for-signature and
/// signature-status endpoints. Shared shape across both.
///
/// `esignStatus` is one of: None, Sent, Signed, Declined.
/// `leaseStatus` may be e.g. Draft, PendingSignature, Active.
class LeaseSignatureStatus {
  const LeaseSignatureStatus({
    required this.leaseId,
    required this.esignStatus,
    required this.leaseStatus,
    required this.hasSignedDocument,
    this.envelopeId,
  });

  final int leaseId;
  final String esignStatus;
  final String leaseStatus;
  final bool hasSignedDocument;
  final String? envelopeId;

  bool get isSent => esignStatus.toLowerCase() == 'sent';
  bool get isSigned => esignStatus.toLowerCase() == 'signed';
  bool get isDeclined => esignStatus.toLowerCase() == 'declined';

  factory LeaseSignatureStatus.fromJson(Map<String, dynamic> json) {
    return LeaseSignatureStatus(
      leaseId: (json['leaseId'] as num?)?.toInt() ?? 0,
      esignStatus: json['esignStatus'] as String? ?? 'None',
      leaseStatus: json['leaseStatus'] as String? ?? '',
      hasSignedDocument: json['hasSignedDocument'] as bool? ?? false,
      envelopeId: json['envelopeId'] as String?,
    );
  }
}

class LeaseQuestionResponse {
  const LeaseQuestionResponse({
    required this.answer,
    required this.llmEnhanced,
    required this.sources,
  });

  final String answer;
  final bool llmEnhanced;
  final List<String> sources;

  factory LeaseQuestionResponse.fromJson(Map<String, dynamic> json) {
    return LeaseQuestionResponse(
      answer: json['answer'] as String? ?? '',
      llmEnhanced: json['llmEnhanced'] as bool? ?? false,
      sources: (json['sources'] as List? ?? []).whereType<String>().toList(),
    );
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final leasesRepositoryProvider = Provider<LeasesRepository>((ref) {
  return LeasesRepository(ref.watch(dioProvider));
});

// ── All leases ────────────────────────────────────────────────────────────────

class LeasesNotifier extends Notifier<AsyncValue<List<Lease>>> {
  @override
  AsyncValue<List<Lease>> build() => const AsyncValue.loading();

  LeasesRepository get _repo => ref.read(leasesRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final list = await _repo.listLeases();
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final leasesProvider =
    NotifierProvider<LeasesNotifier, AsyncValue<List<Lease>>>(
  LeasesNotifier.new,
);

// ── Leases for a specific tenant ──────────────────────────────────────────────

class TenantLeasesNotifier extends Notifier<AsyncValue<List<Lease>>> {
  TenantLeasesNotifier(this._tenantId);

  final int _tenantId;

  @override
  AsyncValue<List<Lease>> build() {
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  LeasesRepository get _repo => ref.read(leasesRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final filtered = await _repo.listLeases(tenantId: _tenantId);
      state = AsyncValue.data(filtered);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final tenantLeasesProvider =
    NotifierProvider.family<TenantLeasesNotifier, AsyncValue<List<Lease>>, int>(
  TenantLeasesNotifier.new,
);

// ── Single lease detail ───────────────────────────────────────────────────────

class LeaseDetailNotifier extends Notifier<AsyncValue<Lease>> {
  LeaseDetailNotifier(this._leaseId);

  final int _leaseId;

  @override
  AsyncValue<Lease> build() {
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  LeasesRepository get _repo => ref.read(leasesRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final lease = await _repo.getLease(_leaseId);
      state = AsyncValue.data(lease);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final leaseDetailProvider =
    NotifierProvider.family<LeaseDetailNotifier, AsyncValue<Lease>, int>(
  LeaseDetailNotifier.new,
);

// ── Lease ledger ──────────────────────────────────────────────────────────────

/// Transparent ledger for a single lease, keyed by lease id. autoDispose so it
/// refreshes whenever the ledger view is reopened.
final leaseLedgerProvider =
    FutureProvider.autoDispose.family<LeaseLedger, int>((ref, leaseId) {
  return ref.watch(leasesRepositoryProvider).ledger(leaseId);
});
