import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';

class SecurityDepositAccount {
  const SecurityDepositAccount({
    required this.id,
    required this.tenantAccountId,
    required this.leaseManagementId,
    required this.originatingAgreementId,
    required this.propertyId,
    required this.unitId,
    required this.accountNumber,
    required this.relationshipNumber,
    this.tenantName,
    this.propertyName,
    this.unitNumber,
    required this.currency,
    required this.totalReceived,
    required this.totalDeductions,
    required this.totalRefunded,
    required this.heldBalance,
    required this.status,
    required this.createdAtUtc,
  });

  final int id;
  final int tenantAccountId;
  final int leaseManagementId;
  final int originatingAgreementId;
  final int propertyId;
  final int unitId;
  final String accountNumber;
  final String relationshipNumber;
  final String? tenantName;
  final String? propertyName;
  final String? unitNumber;
  final String currency;
  final double totalReceived;
  final double totalDeductions;
  final double totalRefunded;
  final double heldBalance;
  final String status;
  final DateTime createdAtUtc;

  factory SecurityDepositAccount.fromJson(Map<String, dynamic> json) =>
      SecurityDepositAccount(
        id: (json['id'] as num).toInt(),
        tenantAccountId: (json['tenantAccountId'] as num).toInt(),
        leaseManagementId: (json['leaseManagementId'] as num).toInt(),
        originatingAgreementId: (json['originatingAgreementId'] as num).toInt(),
        propertyId: (json['propertyId'] as num).toInt(),
        unitId: (json['unitId'] as num).toInt(),
        accountNumber: json['accountNumber'] as String? ?? '',
        relationshipNumber: json['relationshipNumber'] as String? ?? '',
        tenantName: json['tenantName'] as String?,
        propertyName: json['propertyName'] as String?,
        unitNumber: json['unitNumber'] as String?,
        currency: json['currency'] as String? ?? 'USD',
        totalReceived: (json['totalReceived'] as num?)?.toDouble() ?? 0,
        totalDeductions: (json['totalDeductions'] as num?)?.toDouble() ?? 0,
        totalRefunded: (json['totalRefunded'] as num?)?.toDouble() ?? 0,
        heldBalance: (json['heldBalance'] as num?)?.toDouble() ?? 0,
        status: json['status'] as String? ?? 'NotFunded',
        createdAtUtc:
            DateTime.tryParse(json['createdAtUtc'] as String? ?? '') ??
            DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
      );
}

sealed class SecurityDepositMutationInput {
  const SecurityDepositMutationInput({
    required this.amount,
    required this.effectiveOn,
  });

  final double? amount;
  final DateTime effectiveOn;

  String get effectiveOnValue =>
      '${effectiveOn.year.toString().padLeft(4, '0')}-'
      '${effectiveOn.month.toString().padLeft(2, '0')}-'
      '${effectiveOn.day.toString().padLeft(2, '0')}';
}

class FundSecurityDepositInput extends SecurityDepositMutationInput {
  const FundSecurityDepositInput({
    required double amount,
    required super.effectiveOn,
    required this.description,
    required this.paymentMethodSummary,
    this.externalReference,
    this.sourceStoredFileId,
  }) : super(amount: amount);

  final String description;
  final String paymentMethodSummary;
  final String? externalReference;
  final int? sourceStoredFileId;
}

class DeductSecurityDepositInput extends SecurityDepositMutationInput {
  const DeductSecurityDepositInput({
    required double amount,
    required super.effectiveOn,
    required this.reason,
    this.notes,
    this.sourceStoredFileId,
  }) : super(amount: amount);

  final String reason;
  final String? notes;
  final int? sourceStoredFileId;
}

class RefundSecurityDepositInput extends SecurityDepositMutationInput {
  const RefundSecurityDepositInput({
    double? amount,
    required super.effectiveOn,
    required this.description,
    this.externalReference,
  }) : super(amount: amount);

  final String description;
  final String? externalReference;
}

class DepositsRepository {
  DepositsRepository(this._dio);

  final Dio _dio;

  Future<List<SecurityDepositAccount>> listDeposits({
    int? leaseManagementId,
  }) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/security-deposits',
        queryParameters: leaseManagementId == null
            ? null
            : {'leaseManagementId': leaseManagementId},
      );
      return (response.data ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(SecurityDepositAccount.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> fundDeposit(
    SecurityDepositAccount account,
    FundSecurityDepositInput input, {
    required String operationKey,
  }) => _postMutation(account, 'fund', operationKey, <String, dynamic>{
    'securityDepositAccountId': account.id,
    'amount': input.amount,
    'effectiveOn': input.effectiveOnValue,
    'description': input.description,
    'paymentMethodSummary': input.paymentMethodSummary,
    if (input.externalReference?.trim().isNotEmpty == true)
      'externalReference': input.externalReference!.trim(),
    if (input.sourceStoredFileId != null)
      'sourceStoredFileId': input.sourceStoredFileId,
  });

  Future<void> deductDeposit(
    SecurityDepositAccount account,
    DeductSecurityDepositInput input, {
    required String operationKey,
  }) => _postMutation(account, 'deductions', operationKey, <String, dynamic>{
    'securityDepositAccountId': account.id,
    'amount': input.amount,
    'effectiveOn': input.effectiveOnValue,
    'reason': input.reason,
    if (input.notes?.trim().isNotEmpty == true) 'notes': input.notes!.trim(),
    if (input.sourceStoredFileId != null)
      'sourceStoredFileId': input.sourceStoredFileId,
  });

  Future<void> refundDeposit(
    SecurityDepositAccount account,
    RefundSecurityDepositInput input, {
    required String operationKey,
  }) => _postMutation(account, 'refunds', operationKey, <String, dynamic>{
    'securityDepositAccountId': account.id,
    if (input.amount != null) 'amount': input.amount,
    'effectiveOn': input.effectiveOnValue,
    'description': input.description,
    if (input.externalReference?.trim().isNotEmpty == true)
      'externalReference': input.externalReference!.trim(),
  });

  Future<void> _postMutation(
    SecurityDepositAccount account,
    String route,
    String operationKey,
    Map<String, dynamic> body,
  ) async {
    try {
      await _dio.post<Map<String, dynamic>>(
        '/tenant-accounts/${account.tenantAccountId}/deposit/$route',
        data: body,
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

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
}

final depositsRepositoryProvider = Provider<DepositsRepository>((ref) {
  return DepositsRepository(ref.watch(dioProvider));
});

class DepositsNotifier
    extends Notifier<AsyncValue<List<SecurityDepositAccount>>> {
  @override
  AsyncValue<List<SecurityDepositAccount>> build() =>
      const AsyncValue.loading();

  DepositsRepository get _repo => ref.read(depositsRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      state = AsyncValue.data(await _repo.listDeposits());
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final depositsProvider =
    NotifierProvider<
      DepositsNotifier,
      AsyncValue<List<SecurityDepositAccount>>
    >(DepositsNotifier.new);
