import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/models/models.dart';

// ── Models ────────────────────────────────────────────────────────────────────

class DepositDeduction {
  const DepositDeduction({
    required this.reason,
    required this.amount,
    this.notes,
  });

  final String reason;
  final double amount;
  final String? notes;

  factory DepositDeduction.fromJson(Map<String, dynamic> json) =>
      DepositDeduction(
        reason: json['reason'] as String? ?? '',
        amount: (json['amount'] as num?)?.toDouble() ?? 0,
        notes: json['notes'] as String?,
      );
}

class SecurityDeposit {
  const SecurityDeposit({
    required this.id,
    required this.leaseId,
    this.leaseNumber,
    required this.amount,
    required this.status,
    required this.heldAt,
    this.returnedAt,
    this.returnedAmount,
    required this.deductions,
    required this.totalDeductions,
    required this.netRefund,
    this.notes,
  });

  final int id;
  final int leaseId;
  final String? leaseNumber;
  final double amount;
  final String status;
  final DateTime heldAt;
  final DateTime? returnedAt;
  final double? returnedAmount;
  final List<DepositDeduction> deductions;
  final double totalDeductions;
  final double netRefund;
  final String? notes;

  factory SecurityDeposit.fromJson(Map<String, dynamic> json) {
    final deductions = (json['deductions'] as List<dynamic>? ?? [])
        .whereType<Map<String, dynamic>>()
        .map(DepositDeduction.fromJson)
        .toList();
    return SecurityDeposit(
      id: (json['id'] as num).toInt(),
      leaseId: (json['leaseId'] as num).toInt(),
      leaseNumber: json['leaseNumber'] as String?,
      amount: (json['amount'] as num?)?.toDouble() ?? 0,
      status: json['status'] as String? ?? '',
      heldAt: DateTime.tryParse(json['heldAt'] as String? ?? '') ??
          DateTime(0),
      returnedAt: DateTime.tryParse(json['returnedAt'] as String? ?? ''),
      returnedAmount: (json['returnedAmount'] as num?)?.toDouble(),
      deductions: deductions,
      totalDeductions: (json['totalDeductions'] as num?)?.toDouble() ?? 0,
      netRefund: (json['netRefund'] as num?)?.toDouble() ?? 0,
      notes: json['notes'] as String?,
    );
  }
}

// ── Repository ────────────────────────────────────────────────────────────────

/// Manages security deposit API calls.
///
/// Endpoints:
///   GET  /security-deposits              — list (JWT-scoped, ?leaseId=)
///   POST /security-deposits              — create { leaseId, amount?, notes? }
///   POST /security-deposits/{id}/deductions { reason, amount, notes? }
///   POST /security-deposits/{id}/return  { notes? }
///   GET  /security-deposits/{id}/move-out-statement — PDF bytes
///   GET  /leases                         — for the lease picker
class DepositsRepository {
  DepositsRepository(this._dio);

  final Dio _dio;

  Future<List<SecurityDeposit>> listDeposits({int? leaseId}) async {
    try {
      final params = <String, dynamic>{};
      if (leaseId != null) params['leaseId'] = leaseId;
      final response = await _dio.get<List<dynamic>>(
        '/security-deposits',
        queryParameters: params.isEmpty ? null : params,
      );
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(SecurityDeposit.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<SecurityDeposit> createDeposit(Map<String, dynamic> data) async {
    try {
      final response =
          await _dio.post<Map<String, dynamic>>('/security-deposits',
              data: data);
      return SecurityDeposit.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<SecurityDeposit> addDeduction(
      int id, Map<String, dynamic> data) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/security-deposits/$id/deductions',
        data: data,
      );
      return SecurityDeposit.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<SecurityDeposit> processReturn(int id, {String? notes}) async {
    try {
      final body = <String, dynamic>{};
      if (notes != null && notes.isNotEmpty) body['notes'] = notes;
      final response = await _dio.post<Map<String, dynamic>>(
        '/security-deposits/$id/return',
        data: body,
      );
      return SecurityDeposit.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Fetches the security-deposit move-out statement PDF bytes (authed via the
  /// shared Dio interceptor).
  Future<Uint8List> moveOutStatementBytes(int id) async {
    try {
      final response = await _dio.get<List<int>>(
        '/security-deposits/$id/move-out-statement',
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<Lease>> listLeases() async {
    try {
      final response = await _dio.get<List<dynamic>>('/leases');
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(Lease.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final depositsRepositoryProvider = Provider<DepositsRepository>((ref) {
  return DepositsRepository(ref.watch(dioProvider));
});

class DepositsNotifier extends Notifier<AsyncValue<List<SecurityDeposit>>> {
  @override
  AsyncValue<List<SecurityDeposit>> build() => const AsyncValue.loading();

  DepositsRepository get _repo => ref.read(depositsRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final list = await _repo.listDeposits();
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();

  void _replace(SecurityDeposit updated) {
    state.whenData((list) {
      state = AsyncValue.data([
        for (final d in list)
          if (d.id == updated.id) updated else d,
      ]);
    });
  }

  Future<void> addDeduction(int depositId, Map<String, dynamic> data) async {
    try {
      final updated = await _repo.addDeduction(depositId, data);
      _replace(updated);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> processReturn(int depositId, {String? notes}) async {
    try {
      final updated =
          await _repo.processReturn(depositId, notes: notes);
      _replace(updated);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }
}

final depositsProvider =
    NotifierProvider<DepositsNotifier, AsyncValue<List<SecurityDeposit>>>(
  DepositsNotifier.new,
);

// ── Leases picker ─────────────────────────────────────────────────────────────

class LeasesForDepositNotifier extends Notifier<AsyncValue<List<Lease>>> {
  @override
  AsyncValue<List<Lease>> build() => const AsyncValue.loading();

  DepositsRepository get _repo => ref.read(depositsRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final list = await _repo.listLeases();
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }
}

final leasesForDepositProvider =
    NotifierProvider<LeasesForDepositNotifier, AsyncValue<List<Lease>>>(
  LeasesForDepositNotifier.new,
);
