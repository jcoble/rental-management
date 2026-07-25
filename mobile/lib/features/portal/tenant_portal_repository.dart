import 'dart:typed_data';
import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';
import '../../core/models/models.dart';

class PortalTenantAccount {
  const PortalTenantAccount({
    required this.tenantAccountId,
    required this.leaseManagementId,
    required this.propertyName,
    required this.unitNumber,
    required this.accountNumber,
    required this.relationshipNumber,
    required this.lifecycle,
    required this.currency,
    required this.receivableBalance,
    required this.unappliedCredit,
    required this.pastDueAmount,
    required this.pastDueCount,
    required this.nextDueAmount,
    required this.condition,
    this.nextDueOn,
  });

  final int tenantAccountId;
  final int leaseManagementId;
  final String propertyName;
  final String unitNumber;
  final String accountNumber;
  final String relationshipNumber;
  final String lifecycle;
  final String currency;
  final double receivableBalance;
  final double unappliedCredit;
  final double pastDueAmount;
  final int pastDueCount;
  final DateTime? nextDueOn;
  final double nextDueAmount;
  final String condition;

  factory PortalTenantAccount.fromJson(Map<String, dynamic> json) {
    return PortalTenantAccount(
      tenantAccountId: (json['tenantAccountId'] as num).toInt(),
      leaseManagementId: (json['leaseManagementId'] as num).toInt(),
      propertyName: json['propertyName'] as String,
      unitNumber: json['unitNumber'] as String,
      accountNumber: json['accountNumber'] as String,
      relationshipNumber: json['relationshipNumber'] as String,
      lifecycle: json['lifecycle'] as String,
      currency: json['currency'] as String,
      receivableBalance: (json['receivableBalance'] as num).toDouble(),
      unappliedCredit: (json['unappliedCredit'] as num).toDouble(),
      pastDueAmount: (json['pastDueAmount'] as num).toDouble(),
      pastDueCount: (json['pastDueCount'] as num).toInt(),
      nextDueOn: json['nextDueOn'] == null
          ? null
          : DateTime.parse(json['nextDueOn'] as String),
      nextDueAmount: (json['nextDueAmount'] as num).toDouble(),
      condition: json['condition'] as String,
    );
  }
}

class PortalTenantAccountPage {
  const PortalTenantAccountPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<PortalTenantAccount> items;
  final int totalCount;
  final int skip;
  final int take;

  factory PortalTenantAccountPage.fromJson(Map<String, dynamic> json) {
    return PortalTenantAccountPage(
      items: List<Map<String, dynamic>>.from(
        json['items'] as List<dynamic>,
      ).map(PortalTenantAccount.fromJson).toList(growable: false),
      totalCount: (json['totalCount'] as num).toInt(),
      skip: (json['skip'] as num).toInt(),
      take: (json['take'] as num).toInt(),
    );
  }
}

class TenantNotification {
  const TenantNotification({
    required this.id,
    required this.title,
    required this.message,
    required this.isRead,
  });

  final int id;
  final String title;
  final String message;
  final bool isRead;

  factory TenantNotification.fromJson(Map<String, dynamic> json) {
    return TenantNotification(
      id: (json['id'] as num).toInt(),
      title: json['title'] as String? ?? '',
      message: json['message'] as String? ?? '',
      isRead: json['isRead'] as bool? ?? false,
    );
  }
}

class PortalTenantCharge {
  const PortalTenantCharge({
    required this.tenantAccountId,
    required this.leaseManagementId,
    required this.tenantLedgerEntryId,
    required this.entryType,
    required this.description,
    required this.currency,
    required this.originalAmount,
    required this.openAmount,
    required this.isPastDue,
    this.dueOn,
  });

  final int tenantAccountId;
  final int leaseManagementId;
  final int tenantLedgerEntryId;
  final String entryType;
  final String description;
  final String currency;
  final double originalAmount;
  final double openAmount;
  final bool isPastDue;
  final DateTime? dueOn;

  factory PortalTenantCharge.fromJson(Map<String, dynamic> json) {
    return PortalTenantCharge(
      tenantAccountId: (json['tenantAccountId'] as num).toInt(),
      leaseManagementId: (json['leaseManagementId'] as num).toInt(),
      tenantLedgerEntryId: (json['tenantLedgerEntryId'] as num).toInt(),
      entryType: json['entryType'] as String,
      description: json['description'] as String,
      currency: json['currency'] as String,
      originalAmount: (json['originalAmount'] as num).toDouble(),
      openAmount: (json['openAmount'] as num).toDouble(),
      isPastDue: json['isPastDue'] as bool,
      dueOn: json['dueOn'] == null
          ? null
          : DateTime.parse(json['dueOn'] as String),
    );
  }
}

class PortalTenantChargePage {
  const PortalTenantChargePage({
    required this.tenantAccountId,
    required this.leaseManagementId,
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final int tenantAccountId;
  final int leaseManagementId;
  final List<PortalTenantCharge> items;
  final int totalCount;
  final int skip;
  final int take;

  factory PortalTenantChargePage.fromJson(Map<String, dynamic> json) {
    return PortalTenantChargePage(
      tenantAccountId: (json['tenantAccountId'] as num).toInt(),
      leaseManagementId: (json['leaseManagementId'] as num).toInt(),
      items: List<Map<String, dynamic>>.from(
        json['items'] as List<dynamic>,
      ).map(PortalTenantCharge.fromJson).toList(growable: false),
      totalCount: (json['totalCount'] as num).toInt(),
      skip: (json['skip'] as num).toInt(),
      take: (json['take'] as num).toInt(),
    );
  }
}

class PortalTenantLedgerEntry {
  const PortalTenantLedgerEntry({
    required this.tenantAccountId,
    required this.tenantLedgerEntryId,
    required this.leaseManagementId,
    required this.entryType,
    required this.direction,
    required this.amount,
    required this.currency,
    required this.effectiveOn,
    required this.description,
    this.dueOn,
  });

  final int tenantAccountId;
  final int tenantLedgerEntryId;
  final int leaseManagementId;
  final String entryType;
  final String direction;
  final double amount;
  final String currency;
  final DateTime effectiveOn;
  final DateTime? dueOn;
  final String description;

  factory PortalTenantLedgerEntry.fromJson(Map<String, dynamic> json) {
    return PortalTenantLedgerEntry(
      tenantAccountId: (json['tenantAccountId'] as num).toInt(),
      tenantLedgerEntryId: (json['tenantLedgerEntryId'] as num).toInt(),
      leaseManagementId: (json['leaseManagementId'] as num).toInt(),
      entryType: json['entryType'] as String,
      direction: json['direction'] as String,
      amount: (json['amount'] as num).toDouble(),
      currency: json['currency'] as String,
      effectiveOn: DateTime.parse(json['effectiveOn'] as String),
      dueOn: json['dueOn'] == null
          ? null
          : DateTime.parse(json['dueOn'] as String),
      description: json['description'] as String,
    );
  }
}

class PortalTenantLedgerEntryPage {
  const PortalTenantLedgerEntryPage({
    required this.tenantAccountId,
    required this.leaseManagementId,
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final int tenantAccountId;
  final int leaseManagementId;
  final List<PortalTenantLedgerEntry> items;
  final int totalCount;
  final int skip;
  final int take;

  factory PortalTenantLedgerEntryPage.fromJson(Map<String, dynamic> json) {
    return PortalTenantLedgerEntryPage(
      tenantAccountId: (json['tenantAccountId'] as num).toInt(),
      leaseManagementId: (json['leaseManagementId'] as num).toInt(),
      items: List<Map<String, dynamic>>.from(
        json['items'] as List<dynamic>,
      ).map(PortalTenantLedgerEntry.fromJson).toList(growable: false),
      totalCount: (json['totalCount'] as num).toInt(),
      skip: (json['skip'] as num).toInt(),
      take: (json['take'] as num).toInt(),
    );
  }
}

class PortalTenantAccountDeposit {
  const PortalTenantAccountDeposit({
    required this.tenantAccountId,
    required this.leaseManagementId,
    required this.securityDepositAccountId,
    required this.originatingAgreementId,
    required this.currency,
    required this.createdAtUtc,
    required this.effectiveNowUtc,
    required this.businessDate,
    required this.totalReceived,
    required this.totalDeductions,
    required this.totalRefunded,
    required this.totalTransferredIn,
    required this.totalTransferredOut,
    required this.netAdjustments,
    required this.heldBalance,
    required this.status,
  });

  final int tenantAccountId;
  final int leaseManagementId;
  final int securityDepositAccountId;
  final int originatingAgreementId;
  final String currency;
  final DateTime createdAtUtc;
  final DateTime effectiveNowUtc;
  final DateTime businessDate;
  final double totalReceived;
  final double totalDeductions;
  final double totalRefunded;
  final double totalTransferredIn;
  final double totalTransferredOut;
  final double netAdjustments;
  final double heldBalance;
  final String status;

  factory PortalTenantAccountDeposit.fromJson(Map<String, dynamic> json) {
    return PortalTenantAccountDeposit(
      tenantAccountId: (json['tenantAccountId'] as num).toInt(),
      leaseManagementId: (json['leaseManagementId'] as num).toInt(),
      securityDepositAccountId: (json['securityDepositAccountId'] as num)
          .toInt(),
      originatingAgreementId: (json['originatingAgreementId'] as num).toInt(),
      currency: json['currency'] as String,
      createdAtUtc: DateTime.parse(json['createdAtUtc'] as String),
      effectiveNowUtc: DateTime.parse(json['effectiveNowUtc'] as String),
      businessDate: DateTime.parse(json['businessDate'] as String),
      totalReceived: (json['totalReceived'] as num).toDouble(),
      totalDeductions: (json['totalDeductions'] as num).toDouble(),
      totalRefunded: (json['totalRefunded'] as num).toDouble(),
      totalTransferredIn: (json['totalTransferredIn'] as num).toDouble(),
      totalTransferredOut: (json['totalTransferredOut'] as num).toDouble(),
      netAdjustments: (json['netAdjustments'] as num).toDouble(),
      heldBalance: (json['heldBalance'] as num).toDouble(),
      status: json['status'] as String,
    );
  }
}

/// Autopay enrollment state for one of the tenant's canonical accounts.
class AutopayStatus {
  const AutopayStatus({
    required this.tenantAccountId,
    required this.active,
    required this.onlinePaymentsAvailable,
    this.enrolledAt,
  });

  final int tenantAccountId;
  final bool active;
  final bool onlinePaymentsAvailable;
  final DateTime? enrolledAt;

  factory AutopayStatus.fromJson(Map<String, dynamic> json) {
    return AutopayStatus(
      tenantAccountId: (json['tenantAccountId'] as num).toInt(),
      active: json['active'] as bool,
      onlinePaymentsAvailable: json['onlinePaymentsAvailable'] as bool,
      enrolledAt: json['enrolledAt'] == null
          ? null
          : DateTime.parse(json['enrolledAt'] as String),
    );
  }
}

class PortalTenantWorkOrderPage {
  const PortalTenantWorkOrderPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<WorkOrder> items;
  final int totalCount;
  final int skip;
  final int take;

  factory PortalTenantWorkOrderPage.fromJson(Map<String, dynamic> json) {
    return PortalTenantWorkOrderPage(
      items: List<Map<String, dynamic>>.from(
        json['items'] as List<dynamic>? ?? const [],
      ).map(WorkOrder.fromJson).toList(growable: false),
      totalCount: (json['totalCount'] as num?)?.toInt() ?? 0,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? 0,
    );
  }
}

class TenantPortalSnapshot {
  const TenantPortalSnapshot({
    required this.accounts,
    required this.workOrders,
    required this.leases,
    required this.notifications,
  });

  final PortalTenantAccountPage accounts;
  final PortalTenantWorkOrderPage workOrders;
  final List<PortalLeaseRelationship> leases;
  final List<TenantNotification> notifications;
}

class PortalLeaseDocumentDownload {
  const PortalLeaseDocumentDownload({
    required this.bytes,
    required this.fileName,
    required this.contentType,
  });

  final Uint8List bytes;
  final String fileName;
  final String contentType;
}

class TenantPortalRepository {
  TenantPortalRepository(this._dio);

  final Dio _dio;

  Future<TenantPortalSnapshot> snapshot() async {
    try {
      final results = await Future.wait<Response<dynamic>>([
        _dio.get<Map<String, dynamic>>(
          '/portal/tenant-accounts/page',
          queryParameters: {'take': 200, 'sort': 'propertyName'},
        ),
        _dio.get<Map<String, dynamic>>(
          '/portal/work-orders',
          queryParameters: {
            'openOnly': true,
            'take': 20,
            'sort': '-requestedAt',
          },
        ),
        _dio.get<List<dynamic>>('/portal/leases'),
        _dio.get<List<dynamic>>(
          '/notifications',
          queryParameters: {'take': 10},
        ),
      ]);

      return TenantPortalSnapshot(
        accounts: PortalTenantAccountPage.fromJson(
          (results[0].data as Map<String, dynamic>?) ?? const {},
        ),
        workOrders: PortalTenantWorkOrderPage.fromJson(
          (results[1].data as Map<String, dynamic>?) ?? const {},
        ),
        leases: ((results[2].data as List<dynamic>?) ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(PortalLeaseRelationship.fromJson)
            .toList(),
        notifications: ((results[3].data as List<dynamic>?) ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(TenantNotification.fromJson)
            .toList(),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<PortalLeaseDocumentDownload> executedAgreementDocument({
    required int leaseManagementId,
    required int leaseAgreementId,
    required String fileName,
    required String contentType,
  }) async {
    try {
      final response = await _dio.get<List<int>>(
        '/portal/leases/$leaseManagementId/agreements/'
        '$leaseAgreementId/executed-document',
        options: Options(responseType: ResponseType.bytes),
      );
      return PortalLeaseDocumentDownload(
        bytes: Uint8List.fromList(response.data ?? const []),
        fileName: fileName,
        contentType: contentType,
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<PortalTenantAccountPage> tenantAccountsPage({
    int skip = 0,
    int take = 200,
    String sort = 'propertyName',
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/portal/tenant-accounts/page',
        queryParameters: {'skip': skip, 'take': take, 'sort': sort},
      );
      return PortalTenantAccountPage.fromJson(response.data ?? const {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<PortalTenantAccount> tenantAccount(int tenantAccountId) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/portal/tenant-accounts/$tenantAccountId',
      );
      return PortalTenantAccount.fromJson(response.data ?? const {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<PortalTenantChargePage> tenantAccountChargesPage(
    int tenantAccountId, {
    int skip = 0,
    int take = 20,
    String sort = 'dueOn',
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/portal/tenant-accounts/$tenantAccountId/charges/page',
        queryParameters: {'skip': skip, 'take': take, 'sort': sort},
      );
      return PortalTenantChargePage.fromJson(response.data ?? const {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<PortalTenantLedgerEntryPage> tenantAccountEntriesPage(
    int tenantAccountId, {
    int skip = 0,
    int take = 20,
    String sort = '-effectiveOn',
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/portal/tenant-accounts/$tenantAccountId/entries/page',
        queryParameters: {'skip': skip, 'take': take, 'sort': sort},
      );
      return PortalTenantLedgerEntryPage.fromJson(response.data ?? const {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<PortalTenantAccountDeposit> tenantAccountDeposit(
    int tenantAccountId,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/portal/tenant-accounts/$tenantAccountId/deposit',
      );
      return PortalTenantAccountDeposit.fromJson(response.data ?? const {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<PortalTenantWorkOrderPage> workOrdersPage({
    int skip = 0,
    int take = 20,
    bool openOnly = false,
    String search = '',
    String? status,
    String sort = '-requestedAt',
    String? requestedFrom,
    String? requestedTo,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/portal/work-orders',
        queryParameters: {
          'skip': skip,
          'take': take,
          'sort': sort,
          'openOnly': openOnly,
          if (search.trim().isNotEmpty) 'search': search.trim(),
          if (status != null && status.isNotEmpty) 'status': status,
          if (requestedFrom != null) 'from': requestedFrom,
          if (requestedTo != null) 'to': requestedTo,
        },
      );
      return PortalTenantWorkOrderPage.fromJson(response.data ?? const {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<WorkOrder> createWorkOrder({
    required String title,
    required String description,
    String category = 'Resident Request',
    String priority = 'Normal',
  }) async {
    try {
      final body = {
        'title': title,
        'description': description,
        'category': category,
        'priority': priority,
      };
      final response = await IdempotentMutation.run(
        'portal:work-order:create:${jsonEncode(body)}',
        (key) => _dio.post<Map<String, dynamic>>(
          '/portal/tenant/work-orders',
          data: body,
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return WorkOrder.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// GET /portal/work-orders/{id} — the tenant's own work order PLUS its status
  /// timeline, so they can watch Received → … → Done.
  Future<WorkOrderDetail> getWorkOrderDetail(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/portal/work-orders/$id',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return WorkOrderDetail.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  // --- Online rent payment + autopay (GATED — Stripe-hosted Checkout) --------
  // No payment SDK: each method returns a hosted Checkout URL to open in the
  // browser. When Stripe is off the API replies 503, surfaced here as an
  // [ApiException] with statusCode 503 so the UI can show a gentle message
  // rather than an error. Success/cancel URLs are omitted; the API defaults
  // them server-side (the tenant just returns to the app and pulls to refresh).

  Future<String> payCheckout(
    int tenantAccountId,
    int chargeLedgerEntryId,
  ) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/portal/tenant-accounts/$tenantAccountId/charges/$chargeLedgerEntryId/checkout',
      );
      return response.data?['checkoutUrl'] as String? ?? '';
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<AutopayStatus> autopayStatus(int tenantAccountId) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/portal/tenant-accounts/$tenantAccountId/autopay',
      );
      return AutopayStatus.fromJson(response.data ?? const {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Starts hosted setup Checkout for this canonical tenant account.
  Future<String> autopayEnroll(int tenantAccountId) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/portal/tenant-accounts/$tenantAccountId/autopay/enroll',
        data: {
          'operationKey':
              'mobile-${DateTime.now().microsecondsSinceEpoch}-$tenantAccountId',
        },
      );
      return response.data?['checkoutUrl'] as String? ?? '';
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<AutopayStatus> autopayCancel(int tenantAccountId) async {
    return IdempotentMutation.run('portal:autopay:cancel:$tenantAccountId', (
      operationKey,
    ) async {
      try {
        final response = await _dio.post<Map<String, dynamic>>(
          '/portal/tenant-accounts/$tenantAccountId/autopay/cancel',
          data: const <String, dynamic>{},
          options: Options(headers: {'Idempotency-Key': operationKey}),
        );
        return AutopayStatus.fromJson(response.data ?? const {});
      } on DioException catch (e) {
        throw ApiException.fromDioException(e);
      }
    });
  }

  Future<void> uploadWorkOrderPhoto({
    required int workOrderId,
    required Uint8List bytes,
    required String fileName,
    required String contentType,
    required String clientOperationId,
  }) async {
    try {
      final formData = FormData.fromMap({
        'file': MultipartFile.fromBytes(
          bytes,
          filename: fileName,
          contentType: DioMediaType.parse(contentType),
        ),
        'entityType': 'WorkOrder',
        'entityId': workOrderId,
        'category': 'Tenant maintenance photo',
        'clientOperationId': clientOperationId,
      });
      await _dio.post<Map<String, dynamic>>('/documents', data: formData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final tenantPortalRepositoryProvider = Provider<TenantPortalRepository>((ref) {
  return TenantPortalRepository(ref.watch(dioProvider));
});

final tenantPortalSnapshotProvider =
    FutureProvider.autoDispose<TenantPortalSnapshot>((ref) {
      return ref.watch(tenantPortalRepositoryProvider).snapshot();
    });

typedef TenantWorkOrderPageRequest = ({
  int skip,
  int take,
  bool openOnly,
  String search,
  String? status,
  String sort,
  String? from,
  String? to,
});

final tenantPortalWorkOrdersPageProvider = FutureProvider.autoDispose
    .family<PortalTenantWorkOrderPage, TenantWorkOrderPageRequest>((
      ref,
      request,
    ) {
      return ref
          .watch(tenantPortalRepositoryProvider)
          .workOrdersPage(
            skip: request.skip,
            take: request.take,
            openOnly: request.openOnly,
            search: request.search,
            status: request.status,
            sort: request.sort,
            requestedFrom: request.from,
            requestedTo: request.to,
          );
    });

final tenantPortalAccountProvider = FutureProvider.autoDispose
    .family<PortalTenantAccount, int>((ref, tenantAccountId) {
      return ref
          .watch(tenantPortalRepositoryProvider)
          .tenantAccount(tenantAccountId);
    });

typedef TenantAccountPageRequest = ({int tenantAccountId, int skip, int take});

final tenantPortalChargesPageProvider = FutureProvider.autoDispose
    .family<PortalTenantChargePage, TenantAccountPageRequest>((ref, request) {
      return ref
          .watch(tenantPortalRepositoryProvider)
          .tenantAccountChargesPage(
            request.tenantAccountId,
            skip: request.skip,
            take: request.take,
          );
    });

final tenantPortalEntriesPageProvider = FutureProvider.autoDispose
    .family<PortalTenantLedgerEntryPage, TenantAccountPageRequest>((
      ref,
      request,
    ) {
      return ref
          .watch(tenantPortalRepositoryProvider)
          .tenantAccountEntriesPage(
            request.tenantAccountId,
            skip: request.skip,
            take: request.take,
          );
    });

/// The tenant's own work order + its status timeline, keyed by work-order id.
final tenantWorkOrderDetailProvider = FutureProvider.autoDispose
    .family<WorkOrderDetail, int>((ref, id) {
      return ref.watch(tenantPortalRepositoryProvider).getWorkOrderDetail(id);
    });

/// Autopay enrollment status for a single tenant account. Refresh by invalidating this
/// provider (the UI does so after enroll/cancel and on pull-to-refresh).
final tenantAutopayStatusProvider = FutureProvider.autoDispose
    .family<AutopayStatus, int>((ref, tenantAccountId) {
      return ref
          .watch(tenantPortalRepositoryProvider)
          .autopayStatus(tenantAccountId);
    });
