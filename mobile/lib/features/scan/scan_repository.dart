import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'scan_models.dart';

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
      return ScanDraft.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Uploads an image/document and creates a new scan draft.
  ///
  /// [bytes]            — raw file bytes
  /// [filename]         — e.g. 'receipt.jpg'
  /// [contentType]      — MIME type, e.g. 'image/jpeg'
  /// [targetEntityType] — 'Expense' (default) or 'Payment'
  /// [onSendProgress]   — optional progress callback (0.0–1.0)
  Future<ScanCreatedResponse> uploadImage(
    Uint8List bytes,
    String filename,
    String contentType, {
    String targetEntityType = 'Expense',
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

      return ScanCreatedResponse.fromJson(response.data!);
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

  /// Confirms a draft, creating the target entity (Expense or Payment).
  ///
  /// [overrides] keys match what the API's [ConfirmScanRequest] expects:
  ///   — scalar fields: snake_case names as-is (vendor_name, total, etc.)
  ///   — legacy camelCase mappings: vendorName, amount, transactionDate, etc.
  ///   — Expense toggle: is_paid (bool)
  ///   — Payment lease: leaseId (int)
  Future<void> confirm(int id, Map<String, dynamic> overrides) async {
    try {
      await _dio.post<dynamic>(
        '/scans/$id/confirm',
        data: {'overridesJson': jsonEncode(overrides)},
      );
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

  /// Fetches the active leases list for the caller's portfolio.
  ///
  /// Used by the review screen when [targetEntityType] == 'Payment'.
  Future<List<Map<String, dynamic>>> listLeases() async {
    try {
      final response = await _dio.get<List<dynamic>>('/leases');
      return (response.data ?? []).cast<Map<String, dynamic>>();
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
final scanListFamilyProvider =
    FutureProvider.autoDispose.family<List<ScanDraft>, String?>(
  (ref, status) => ref.read(scanRepositoryProvider).listDrafts(status: status),
);
