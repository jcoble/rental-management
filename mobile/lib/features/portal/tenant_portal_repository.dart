import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/models/models.dart';

class TenantBalance {
  const TenantBalance({
    required this.outstanding,
    required this.overdue,
    required this.overdueCount,
  });

  final double outstanding;
  final double overdue;
  final int overdueCount;

  factory TenantBalance.fromJson(Map<String, dynamic> json) {
    return TenantBalance(
      outstanding: (json['outstanding'] as num?)?.toDouble() ?? 0,
      overdue: (json['overdue'] as num?)?.toDouble() ?? 0,
      overdueCount: (json['overdueCount'] as num?)?.toInt() ?? 0,
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

/// Tenant-portal payable obligation. This is intentionally distinct from the
/// management app's immutable [PaymentReceipt] projection.
class TenantPortalPayment {
  const TenantPortalPayment({
    required this.id,
    required this.tenantAccountId,
    required this.type,
    required this.status,
    required this.amount,
    required this.dueDate,
  });

  final int id;
  final int tenantAccountId;
  final String type;
  final String status;
  final double amount;
  final DateTime dueDate;

  factory TenantPortalPayment.fromJson(Map<String, dynamic> json) {
    return TenantPortalPayment(
      id: (json['id'] as num).toInt(),
      tenantAccountId: (json['tenantAccountId'] as num).toInt(),
      type: json['paymentType'] as String? ?? json['type'] as String? ?? '',
      status: json['status'] as String? ?? '',
      amount: (json['amount'] as num?)?.toDouble() ?? 0,
      dueDate:
          DateTime.tryParse(json['dueDate'] as String? ?? '') ?? DateTime(0),
    );
  }
}

/// Autopay enrollment state for one of the tenant's canonical accounts.
class AutopayStatus {
  const AutopayStatus({
    required this.tenantAccountId,
    required this.active,
    this.enrolledAt,
  });

  final int tenantAccountId;
  final bool active;
  final DateTime? enrolledAt;

  factory AutopayStatus.fromJson(Map<String, dynamic> json) {
    return AutopayStatus(
      tenantAccountId: (json['tenantAccountId'] as num?)?.toInt() ?? 0,
      active: json['active'] as bool? ?? false,
      enrolledAt: DateTime.tryParse(json['enrolledAt'] as String? ?? ''),
    );
  }
}

class TenantPortalSnapshot {
  const TenantPortalSnapshot({
    required this.balance,
    required this.payments,
    required this.workOrders,
    required this.leases,
    required this.notifications,
  });

  final TenantBalance balance;
  final List<TenantPortalPayment> payments;
  final List<WorkOrder> workOrders;
  final List<Lease> leases;
  final List<TenantNotification> notifications;
}

class TenantPortalRepository {
  TenantPortalRepository(this._dio);

  final Dio _dio;

  Future<TenantPortalSnapshot> snapshot() async {
    try {
      final results = await Future.wait<Response<dynamic>>([
        _dio.get<Map<String, dynamic>>('/portal/balance'),
        _dio.get<List<dynamic>>('/portal/payments'),
        _dio.get<List<dynamic>>('/portal/work-orders'),
        _dio.get<List<dynamic>>('/portal/leases'),
        _dio.get<List<dynamic>>(
          '/notifications',
          queryParameters: {'take': 10},
        ),
      ]);

      return TenantPortalSnapshot(
        balance: TenantBalance.fromJson(
          (results[0].data as Map<String, dynamic>?) ?? const {},
        ),
        payments: ((results[1].data as List<dynamic>?) ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(TenantPortalPayment.fromJson)
            .toList(),
        workOrders: ((results[2].data as List<dynamic>?) ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(WorkOrder.fromJson)
            .toList(),
        leases: ((results[3].data as List<dynamic>?) ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(Lease.fromJson)
            .toList(),
        notifications: ((results[4].data as List<dynamic>?) ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(TenantNotification.fromJson)
            .toList(),
      );
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
      final response = await _dio.post<Map<String, dynamic>>(
        '/portal/tenant/work-orders',
        data: {
          'title': title,
          'description': description,
          'category': category,
          'priority': priority,
        },
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

  Future<String> payCheckout(int tenantAccountId, int chargeLedgerEntryId) async {
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
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/portal/tenant-accounts/$tenantAccountId/autopay/cancel',
      );
      return AutopayStatus.fromJson(response.data ?? const {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
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

/// The tenant's own work order + its status timeline, keyed by work-order id.
final tenantWorkOrderDetailProvider = FutureProvider.autoDispose
    .family<WorkOrderDetail, int>((ref, id) {
      return ref.watch(tenantPortalRepositoryProvider).getWorkOrderDetail(id);
    });

/// Autopay enrollment status for a single tenant account. Refresh by invalidating this
/// provider (the UI does so after enroll/cancel and on pull-to-refresh).
final tenantAutopayStatusProvider = FutureProvider.autoDispose
    .family<AutopayStatus, int>((ref, tenantAccountId) {
      return ref.watch(tenantPortalRepositoryProvider).autopayStatus(tenantAccountId);
    });
