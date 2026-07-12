import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:uuid/uuid.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'scan_models.dart';

class TenantAccountOption {
  const TenantAccountOption({
    required this.tenantAccountId,
    required this.relationshipNumber,
    required this.propertyName,
    required this.unitNumber,
    this.primaryTenantName,
  });

  final int tenantAccountId;
  final String relationshipNumber;
  final String propertyName;
  final String unitNumber;
  final String? primaryTenantName;

  factory TenantAccountOption.fromJson(Map<String, dynamic> json) =>
      TenantAccountOption(
        tenantAccountId: (json['tenantAccountId'] as num).toInt(),
        relationshipNumber: json['relationshipNumber'] as String? ?? '',
        propertyName: json['propertyName'] as String? ?? '',
        unitNumber: json['unitNumber'] as String? ?? '',
        primaryTenantName: json['primaryTenantName'] as String?,
      );
}

class TenantAccountOptionPage {
  const TenantAccountOptionPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<TenantAccountOption> items;
  final int totalCount;
  final int skip;
  final int take;
}

/// Repository for all scan-draft API calls.
///
/// Endpoints (all under /api/v1/scans):
///   GET    /scans?status=          — list drafts, newest first
///   GET    /scans/{id}             — single draft
///   GET    /scans/{id}/file        — stream the stored file (auth required)
///   POST   /scans                  — upload (multipart/form-data)
///   POST   /scans/{id}/confirm     — confirm → create Expense or Payment
///   POST   /scans/{id}/reject      — reject a draft
class ScanRepository {
  ScanRepository({required Dio dio}) : _dio = dio;

  final Dio _dio;
  final Map<int, String> _confirmOperationIds = {};

  /// Lists scan drafts for the caller's portfolio, newest first.
  Future<List<ScanDraft>> listDrafts({String? status}) async {
    try {
      final queryParams = <String, dynamic>{};
      if (status != null && status.isNotEmpty) queryParams['status'] = status;

      final response = await _dio.get<List<dynamic>>(
        '/scans',
        queryParameters: queryParams.isEmpty ? null : queryParams,
      );

      return (response.data ?? [])
          .cast<Map<String, dynamic>>()
          .map(ScanDraft.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Fetches a single draft by [id].
  Future<ScanDraft> getDraft(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/scans/$id');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ScanDraft.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Uploads an image/document and creates a new scan draft.
  ///
  /// [bytes]            — raw file bytes
  /// [filename]         — e.g. 'receipt.jpg'
  /// [contentType]      — MIME type, e.g. 'image/jpeg'
  /// [targetEntityType] — 'Expense' (default), 'Payment', 'WorkOrder', 'LeaseAgreement', or 'Loan'
  /// [onSendProgress]   — optional progress callback (0.0–1.0)
  Future<ScanCreatedResponse> uploadImage(
    Uint8List bytes,
    String filename,
    String contentType, {
    String targetEntityType = 'Expense',
    String? clientOperationId,
    int? propertyId,
    int? unitId,
    String? sourceLabel,
    void Function(double progress)? onSendProgress,
  }) async {
    try {
      final formData = FormData.fromMap({
        'file': MultipartFile.fromBytes(
          bytes,
          filename: filename,
          contentType: DioMediaType.parse(contentType),
        ),
        'targetEntityType': targetEntityType,
        'clientOperationId': clientOperationId ?? const Uuid().v4(),
        'propertyId': ?propertyId,
        'unitId': ?unitId,
        if (sourceLabel != null && sourceLabel.trim().isNotEmpty)
          'sourceLabel': sourceLabel.trim(),
      });

      final response = await _dio.post<Map<String, dynamic>>(
        '/scans',
        data: formData,
        onSendProgress: onSendProgress != null
            ? (sent, total) {
                if (total > 0) onSendProgress(sent / total);
              }
            : null,
      );

      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ScanCreatedResponse.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Uploads a recorded voice note and creates a reviewable AI draft.
  Future<ScanDraft> createVoiceDraft(
    Uint8List bytes,
    String filename,
    String contentType,
  ) async {
    try {
      final formData = FormData.fromMap({
        'audio': MultipartFile.fromBytes(
          bytes,
          filename: filename,
          contentType: DioMediaType.parse(contentType),
        ),
      });

      final response = await _dio.post<Map<String, dynamic>>(
        '/voice/drafts',
        data: formData,
      );

      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ScanDraft.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Downloads the stored scan file as bytes.
  ///
  /// The Dio auth interceptor attaches the Bearer token automatically, which is
  /// necessary because a plain network image widget cannot send auth headers.
  Future<Uint8List> downloadFile(int id) async {
    try {
      // Stream the response and accumulate Uint8List chunks. Dio's default
      // `ResponseType.bytes` inflates a multi-MB image into a boxed List<int>
      // (millions of integer objects) — seconds of CPU on-device. Streaming into
      // a BytesBuilder keeps the data as raw bytes the whole way.
      final response = await _dio.get<ResponseBody>(
        '/scans/$id/file',
        options: Options(responseType: ResponseType.stream),
      );
      final builder = BytesBuilder(copy: false);
      await for (final chunk in response.data!.stream) {
        builder.add(chunk);
      }
      return builder.takeBytes();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Confirms a draft, creating the target entity (Expense, Payment, WorkOrder,
  /// or Lease).
  ///
  /// [overrides] keys match what the API's [ConfirmScanRequest] expects:
  ///   — scalar fields: snake_case names as-is (vendor_name, total, etc.)
  ///   — legacy camelCase mappings: vendorName, amount, transactionDate, etc.
  ///   — Expense toggle: is_paid (bool)
  ///   — Payment account: tenantAccountId (int)
  ///   — Lease: propertyId + unitId (required), tenantId? (optional), plus the
  ///     edited lease terms (lease_number, start_date, monthly_rent, …).
  ///
  /// Returns the JSON response body so callers can read the created entity id
  /// (e.g. `leaseId`) for navigation. May be `null` for empty responses.
  Future<Map<String, dynamic>?> confirm(
    int id,
    Map<String, dynamic> overrides,
  ) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/scans/$id/confirm',
        data: {
          'overridesJson': jsonEncode(overrides),
          'clientOperationId': _confirmOperationIds.putIfAbsent(
            id,
            () => const Uuid().v4(),
          ),
        },
      );
      _confirmOperationIds.remove(id);
      return response.data;
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Rejects a draft with an optional [reason].
  Future<void> reject(int id, {String? reason}) async {
    try {
      await _dio.post<dynamic>(
        '/scans/$id/reject',
        data: {'reason': reason ?? ''},
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Fetches one server-paged set of canonical tenant accounts the caller may
  /// manage. The API applies current session, capability, and property scope.
  Future<TenantAccountOptionPage> listTenantAccountOptions({
    String? search,
    int skip = 0,
    int take = 25,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/payments/account-options',
        queryParameters: {
          'skip': skip,
          'take': take,
          if (search != null && search.trim().isNotEmpty)
            'search': search.trim(),
        },
      );
      final items = response.data?['items'] as List<dynamic>? ?? const [];
      return TenantAccountOptionPage(
        items: items
            .whereType<Map<String, dynamic>>()
            .map(TenantAccountOption.fromJson)
            .toList(),
        totalCount: (response.data?['totalCount'] as num?)?.toInt() ?? 0,
        skip: (response.data?['skip'] as num?)?.toInt() ?? skip,
        take: (response.data?['take'] as num?)?.toInt() ?? take,
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ---------------------------------------------------------------------------
// Riverpod providers
// ---------------------------------------------------------------------------

final scanRepositoryProvider = Provider<ScanRepository>((ref) {
  return ScanRepository(dio: ref.watch(dioProvider));
});

/// Public, auto-dispose scan-list provider keyed by an optional status filter.
///
/// Exposed at the repository level so the realtime watcher can invalidate all
/// active variants (e.g. [ScanListScreen] mounted with filter = null) without
/// importing the screen file.
final scanListFamilyProvider = FutureProvider.autoDispose
    .family<List<ScanDraft>, String?>(
      (ref, status) =>
          ref.read(scanRepositoryProvider).listDrafts(status: status),
    );
