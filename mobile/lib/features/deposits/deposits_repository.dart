import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';

class TenantAccountDeposit {
  const TenantAccountDeposit({
    required this.securityDepositAccountId,
    required this.tenantAccountId,
    required this.leaseManagementId,
    required this.originatingAgreementId,
    required this.propertyId,
    required this.unitId,
    required this.accountNumber,
    required this.relationshipNumber,
    this.primaryTenantName,
    required this.propertyName,
    required this.unitNumber,
    required this.currency,
    required this.totalReceived,
    required this.totalDeductions,
    required this.totalRefunded,
    required this.totalTransferredIn,
    required this.totalTransferredOut,
    required this.netAdjustments,
    required this.heldBalance,
    required this.status,
    required this.createdAtUtc,
    required this.effectiveNowUtc,
    required this.businessDate,
    this.accountId,
    this.accountName,
    this.journalEntryPublicId,
  });

  final int securityDepositAccountId;
  final int tenantAccountId;
  final int leaseManagementId;
  final int originatingAgreementId;
  final int propertyId;
  final int unitId;
  final String accountNumber;
  final String relationshipNumber;
  final String? primaryTenantName;
  final String propertyName;
  final String unitNumber;
  final String currency;
  final double totalReceived;
  final double totalDeductions;
  final double totalRefunded;
  final double totalTransferredIn;
  final double totalTransferredOut;
  final double netAdjustments;
  final double heldBalance;
  final String status;
  final DateTime createdAtUtc;
  final DateTime effectiveNowUtc;
  final String businessDate;
  final int? accountId;
  final String? accountName, journalEntryPublicId;

  factory TenantAccountDeposit.fromJson(
    Map<String, dynamic> json,
  ) => TenantAccountDeposit(
    securityDepositAccountId: (json['securityDepositAccountId'] as num).toInt(),
    tenantAccountId: (json['tenantAccountId'] as num).toInt(),
    leaseManagementId: (json['leaseManagementId'] as num).toInt(),
    originatingAgreementId: (json['originatingAgreementId'] as num).toInt(),
    propertyId: (json['propertyId'] as num).toInt(),
    unitId: (json['unitId'] as num).toInt(),
    accountNumber: json['accountNumber'] as String? ?? '',
    relationshipNumber: json['relationshipNumber'] as String? ?? '',
    primaryTenantName: json['primaryTenantName'] as String?,
    propertyName: json['propertyName'] as String? ?? '',
    unitNumber: json['unitNumber'] as String? ?? '',
    currency: json['currency'] as String? ?? 'USD',
    totalReceived: (json['totalReceived'] as num?)?.toDouble() ?? 0,
    totalDeductions: (json['totalDeductions'] as num?)?.toDouble() ?? 0,
    totalRefunded: (json['totalRefunded'] as num?)?.toDouble() ?? 0,
    totalTransferredIn: (json['totalTransferredIn'] as num?)?.toDouble() ?? 0,
    totalTransferredOut: (json['totalTransferredOut'] as num?)?.toDouble() ?? 0,
    netAdjustments: (json['netAdjustments'] as num?)?.toDouble() ?? 0,
    heldBalance: (json['heldBalance'] as num?)?.toDouble() ?? 0,
    status: json['status'] as String? ?? 'NotFunded',
    createdAtUtc:
        DateTime.tryParse(json['createdAtUtc'] as String? ?? '') ??
        DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
    effectiveNowUtc:
        DateTime.tryParse(json['effectiveNowUtc'] as String? ?? '') ??
        DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
    businessDate: json['businessDate'] as String? ?? '',
    accountId: (json['accountId'] as num?)?.toInt(),
    accountName: json['accountName'] as String?,
    journalEntryPublicId: json['journalEntryPublicId'] as String?,
  );
}

class TenantAccountDepositQuery {
  const TenantAccountDepositQuery({
    this.skip = 0,
    this.take = 20,
    this.search = '',
    this.sort = '-createdAtUtc',
    this.tenantAccountId,
    this.propertyId,
    this.status,
  });

  final int skip;
  final int take;
  final String search;
  final String sort;
  final int? tenantAccountId;
  final int? propertyId;
  final String? status;

  Map<String, dynamic> get queryParameters => {
    'skip': skip,
    'take': take,
    'sort': sort,
    if (search.trim().isNotEmpty) 'search': search.trim(),
    if (tenantAccountId != null) 'tenantAccountId': tenantAccountId,
    if (propertyId != null) 'propertyId': propertyId,
    if (status?.trim().isNotEmpty == true) 'status': status!.trim(),
  };

  TenantAccountDepositQuery copyWith({
    int? skip,
    int? take,
    String? search,
    String? sort,
    int? tenantAccountId,
    int? propertyId,
    String? status,
    bool clearStatus = false,
  }) => TenantAccountDepositQuery(
    skip: skip ?? this.skip,
    take: take ?? this.take,
    search: search ?? this.search,
    sort: sort ?? this.sort,
    tenantAccountId: tenantAccountId ?? this.tenantAccountId,
    propertyId: propertyId ?? this.propertyId,
    status: clearStatus ? null : status ?? this.status,
  );
}

class TenantAccountDepositPage {
  const TenantAccountDepositPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
    required this.query,
  });

  final List<TenantAccountDeposit> items;
  final int totalCount;
  final int skip;
  final int take;
  final TenantAccountDepositQuery query;

  bool get hasMore => items.length < totalCount;

  factory TenantAccountDepositPage.fromJson(
    Map<String, dynamic> json, {
    required TenantAccountDepositQuery query,
  }) => TenantAccountDepositPage(
    items: (json['items'] as List<dynamic>? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(TenantAccountDeposit.fromJson)
        .toList(growable: false),
    totalCount: (json['totalCount'] as num?)?.toInt() ?? 0,
    skip: (json['skip'] as num?)?.toInt() ?? query.skip,
    take: (json['take'] as num?)?.toInt() ?? query.take,
    query: query,
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
    super.amount,
    required super.effectiveOn,
    required this.description,
    this.externalReference,
  });

  final String description;
  final String? externalReference;
}

class DepositsRepository {
  DepositsRepository(this._dio);

  final Dio _dio;

  Future<TenantAccountDepositPage> listDepositsPage([
    TenantAccountDepositQuery query = const TenantAccountDepositQuery(),
  ]) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/tenant-accounts/deposits/page',
        queryParameters: query.queryParameters,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return TenantAccountDepositPage.fromJson(data, query: query);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<TenantAccountDeposit> getDeposit(int tenantAccountId) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/tenant-accounts/$tenantAccountId/deposit',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return TenantAccountDeposit.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> fundDeposit(
    TenantAccountDeposit account,
    FundSecurityDepositInput input, {
    required String operationKey,
  }) => _postMutation(account, 'fund', operationKey, <String, dynamic>{
    'securityDepositAccountId': account.securityDepositAccountId,
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
    TenantAccountDeposit account,
    DeductSecurityDepositInput input, {
    required String operationKey,
  }) => _postMutation(account, 'deductions', operationKey, <String, dynamic>{
    'securityDepositAccountId': account.securityDepositAccountId,
    'amount': input.amount,
    'effectiveOn': input.effectiveOnValue,
    'reason': input.reason,
    if (input.notes?.trim().isNotEmpty == true) 'notes': input.notes!.trim(),
    if (input.sourceStoredFileId != null)
      'sourceStoredFileId': input.sourceStoredFileId,
  });

  Future<void> refundDeposit(
    TenantAccountDeposit account,
    RefundSecurityDepositInput input, {
    required String operationKey,
  }) => _postMutation(account, 'refunds', operationKey, <String, dynamic>{
    'securityDepositAccountId': account.securityDepositAccountId,
    if (input.amount != null) 'amount': input.amount,
    'effectiveOn': input.effectiveOnValue,
    'description': input.description,
    if (input.externalReference?.trim().isNotEmpty == true)
      'externalReference': input.externalReference!.trim(),
  });

  Future<void> _postMutation(
    TenantAccountDeposit account,
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

  Future<Uint8List> moveOutStatementBytes(int tenantAccountId) async {
    try {
      final response = await _dio.get<List<int>>(
        '/tenant-accounts/$tenantAccountId/deposit/move-out-statement',
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

class DepositsNotifier extends Notifier<AsyncValue<TenantAccountDepositPage>> {
  static const _pageSize = 20;

  @override
  AsyncValue<TenantAccountDepositPage> build() => const AsyncValue.loading();

  DepositsRepository get _repo => ref.read(depositsRepositoryProvider);

  Future<void> load({String search = '', String? status}) async {
    state = const AsyncValue.loading();
    state = await AsyncValue.guard(
      () => _repo.listDepositsPage(
        TenantAccountDepositQuery(
          take: _pageSize,
          search: search,
          status: status,
        ),
      ),
    );
  }

  Future<void> refresh() => load(
    search: state.value?.query.search ?? '',
    status: state.value?.query.status,
  );

  Future<void> loadMore() async {
    final current = state.value;
    if (current == null || !current.hasMore) return;
    final nextQuery = current.query.copyWith(skip: current.items.length);
    final next = await _repo.listDepositsPage(nextQuery);
    state = AsyncValue.data(
      TenantAccountDepositPage(
        items: [...current.items, ...next.items],
        totalCount: next.totalCount,
        skip: 0,
        take: current.take,
        query: current.query,
      ),
    );
  }
}

final depositsProvider =
    NotifierProvider<DepositsNotifier, AsyncValue<TenantAccountDepositPage>>(
      DepositsNotifier.new,
    );
