import 'dart:math';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';
import 'applications_models.dart';

class ApplicationListQuery {
  const ApplicationListQuery({
    this.skip = 0,
    this.take = 20,
    this.status,
    this.unitId,
    this.search,
    this.sort = '-submittedAt',
  });

  final int skip;
  final int take;
  final String? status;
  final int? unitId;
  final String? search;
  final String sort;

  @override
  bool operator ==(Object other) {
    return other is ApplicationListQuery &&
        other.skip == skip &&
        other.take == take &&
        other.status == status &&
        other.unitId == unitId &&
        other.search == search &&
        other.sort == sort;
  }

  @override
  int get hashCode => Object.hash(skip, take, status, unitId, search, sort);
}

class ApplicationListPage {
  const ApplicationListPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<RentalApplication> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory ApplicationListPage.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final items = rawItems is List
        ? rawItems
              .whereType<Map<String, dynamic>>()
              .map(RentalApplication.fromJson)
              .toList()
        : <RentalApplication>[];

    return ApplicationListPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

/// Repository for rental applications (landlord-facing review).
///
/// Endpoints used (all JWT-scoped, portfolio from claim):
///   GET    /applications?status=     — list, optionally filtered by status
///   GET    /applications/{id}        — single application
///   PATCH  /applications/{id}        — landlord corrections before decision
///   DELETE /applications/{id}        — soft-delete a landlord application
///   POST   /applications/{id}/approve  — approve (also creates a Tenant)
///   POST   /applications/{id}/decline  — decline with optional { reason }
///   POST   /applications/{id}/withdraw — withdraw
///   POST   /applications/link        — mint a shareable apply link
class ApplicationsRepository {
  ApplicationsRepository(this._dio);

  final Dio _dio;

  Future<List<RentalApplication>> list({String? status}) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/applications',
        queryParameters: (status == null || status.isEmpty)
            ? null
            : {'status': status},
      );
      return (response.data ?? [])
          .whereType<Map<String, dynamic>>()
          .map(RentalApplication.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ApplicationListPage> listPage([
    ApplicationListQuery query = const ApplicationListQuery(),
  ]) async {
    final parameters = <String, dynamic>{
      'skip': query.skip,
      'take': query.take,
      'status': query.status,
      'unitId': query.unitId,
      'search': query.search,
      'sort': query.sort,
    }..removeWhere((_, value) => value == null || value == '');

    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/applications/page',
        queryParameters: parameters,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ApplicationListPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<RentalApplication> get(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/applications/$id',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return RentalApplication.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Correct landlord-editable details while the application is still open.
  Future<RentalApplication> update(int id, UpdateApplicationInput input) async {
    final payload = input.toJson();
    return IdempotentMutation.run('applications:update:$id:$payload', (
      operationKey,
    ) async {
      try {
        final response = await _dio.patch<Map<String, dynamic>>(
          '/applications/$id',
          options: Options(headers: {'Idempotency-Key': operationKey}),
          data: payload,
        );
        final data = response.data;
        if (data == null) {
          throw const ApiException(
            statusCode: 0,
            message: 'Empty response from server.',
          );
        }
        return RentalApplication.fromJson(data);
      } on DioException catch (e) {
        throw ApiException.fromDioException(e);
      }
    });
  }

  Future<void> delete(int id) async {
    await IdempotentMutation.run('applications:delete:$id', (
      operationKey,
    ) async {
      try {
        await _dio.delete<void>(
          '/applications/$id',
          options: Options(headers: {'Idempotency-Key': operationKey}),
        );
      } on DioException catch (e) {
        throw ApiException.fromDioException(e);
      }
    });
  }

  /// Approve — returns { applicationId, status, tenantId }. Also creates a Tenant.
  Future<ApplicationApproval> approve(int id) async {
    return IdempotentMutation.run('applications:approve:$id', (
      operationKey,
    ) async {
      try {
        final response = await _dio.post<Map<String, dynamic>>(
          '/applications/$id/approve',
          options: Options(headers: {'Idempotency-Key': operationKey}),
          data: {},
        );
        final data = response.data;
        if (data == null) {
          throw const ApiException(
            statusCode: 0,
            message: 'Empty response from server.',
          );
        }
        return ApplicationApproval.fromJson(data);
      } on DioException catch (e) {
        throw ApiException.fromDioException(e);
      }
    });
  }

  /// Decline with an optional reason — returns the updated application.
  Future<RentalApplication> decline(int id, {String? reason}) async {
    return IdempotentMutation.run('applications:decline:$id:${reason ?? ''}', (
      operationKey,
    ) async {
      try {
        final response = await _dio.post<Map<String, dynamic>>(
          '/applications/$id/decline',
          options: Options(headers: {'Idempotency-Key': operationKey}),
          data: {if (reason != null && reason.isNotEmpty) 'reason': reason},
        );
        final data = response.data;
        if (data == null) {
          throw const ApiException(
            statusCode: 0,
            message: 'Empty response from server.',
          );
        }
        return RentalApplication.fromJson(data);
      } on DioException catch (e) {
        throw ApiException.fromDioException(e);
      }
    });
  }

  /// Withdraw — returns the updated application.
  Future<RentalApplication> withdraw(int id) async {
    return IdempotentMutation.run('applications:withdraw:$id', (
      operationKey,
    ) async {
      try {
        final response = await _dio.post<Map<String, dynamic>>(
          '/applications/$id/withdraw',
          options: Options(headers: {'Idempotency-Key': operationKey}),
          data: {},
        );
        final data = response.data;
        if (data == null) {
          throw const ApiException(
            statusCode: 0,
            message: 'Empty response from server.',
          );
        }
        return RentalApplication.fromJson(data);
      } on DioException catch (e) {
        throw ApiException.fromDioException(e);
      }
    });
  }

  /// Records an immutable fee collection in the application's pre-tenancy
  /// financial account. Dio reuses this request's operation key if an
  /// interceptor retries the same submission.
  Future<ApplicationFinanceMutation> recordFee(
    int id, {
    required String operationKey,
    required double amount,
    String? method,
    DateTime? effectiveOn,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/applications/$id/fee',
        options: Options(headers: {'Idempotency-Key': operationKey}),
        data: {
          'amount': amount,
          if (method != null && method.isNotEmpty) 'method': method,
          'currency': 'USD',
          if (effectiveOn != null)
            'effectiveOn': effectiveOn.toIso8601String().split('T').first,
        },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ApplicationFinanceMutation.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Mint a shareable apply link.
  Future<ApplicationLink> createLink() async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/applications/link',
        data: {},
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ApplicationLink.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ApplicantScreening> startIntegratedScreening(
    int id, {
    required String operationKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/applications/$id/screening/integrated',
        data: {'operationKey': operationKey},
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ApplicantScreening.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ApplicantScreening> trackExternalScreening(
    int id, {
    required String operationKey,
    required String providerDisplayName,
    String? providerReference,
    String? providerHostedUrl,
    String? creditReportingAgencyName,
    String? creditReportingAgencyAddress,
    String? creditReportingAgencyPhone,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/applications/$id/screening/external',
        data: {
          'operationKey': operationKey,
          'providerDisplayName': providerDisplayName,
          if (providerReference != null && providerReference.isNotEmpty)
            'providerReference': providerReference,
          if (providerHostedUrl != null && providerHostedUrl.isNotEmpty)
            'providerHostedUrl': providerHostedUrl,
          if (creditReportingAgencyName != null &&
              creditReportingAgencyName.isNotEmpty)
            'creditReportingAgencyName': creditReportingAgencyName,
          if (creditReportingAgencyAddress != null &&
              creditReportingAgencyAddress.isNotEmpty)
            'creditReportingAgencyAddress': creditReportingAgencyAddress,
          if (creditReportingAgencyPhone != null &&
              creditReportingAgencyPhone.isNotEmpty)
            'creditReportingAgencyPhone': creditReportingAgencyPhone,
          'status': 'InProgress',
        },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ApplicantScreening.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ApplicantScreening> markExternalScreeningComplete(
    int applicationId,
    int screeningId, {
    required String operationKey,
  }) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/applications/$applicationId/screening/$screeningId/external',
        data: {'operationKey': operationKey, 'status': 'Completed'},
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ApplicantScreening.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ApplicantScreening> updateExternalScreeningAgency(
    int applicationId,
    int screeningId, {
    required String operationKey,
    required String creditReportingAgencyName,
    required String creditReportingAgencyAddress,
    required String creditReportingAgencyPhone,
  }) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/applications/$applicationId/screening/$screeningId/external',
        data: {
          'operationKey': operationKey,
          'creditReportingAgencyName': creditReportingAgencyName,
          'creditReportingAgencyAddress': creditReportingAgencyAddress,
          'creditReportingAgencyPhone': creditReportingAgencyPhone,
        },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ApplicantScreening.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ScreeningWorkspace> screening(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/applications/$id/screening',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ScreeningWorkspace.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<ApplicantScreening> recordScreeningDecision(
    int applicationId,
    int screeningId, {
    required String operationKey,
    required String decision,
    required bool consumerReportUsed,
    String? reason,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/applications/$applicationId/screening/$screeningId/decision',
        data: {
          'operationKey': operationKey,
          'decision': decision,
          'consumerReportUsed': consumerReportUsed,
          if (reason != null && reason.isNotEmpty) 'reason': reason,
        },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return ApplicantScreening.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Generates an FCRA adverse-action notice for a declined application.
  Future<AdverseActionNotice> adverseAction(
    int id, {
    required String operationKey,
    String? reason,
    required bool sendToApplicant,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/applications/$id/adverse-action',
        data: {
          'operationKey': operationKey,
          if (reason != null && reason.isNotEmpty) 'reason': reason,
          'sendToApplicant': sendToApplicant,
        },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return AdverseActionNotice.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  static String newOperationKey() {
    final random = Random.secure();
    final bytes = List<int>.generate(16, (_) => random.nextInt(256));
    return bytes.map((value) => value.toRadixString(16).padLeft(2, '0')).join();
  }

  /// Raw bytes for a stored file (authed via the shared interceptor) — used to
  /// open the generated adverse-action PDF.
  Future<Uint8List> documentBytes(int storedFileId) async {
    try {
      final response = await _dio.get<List<int>>(
        '/documents/$storedFileId/file',
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

class UpdateApplicationInput {
  const UpdateApplicationInput({
    this.firstName,
    this.lastName,
    this.email,
    this.phone,
    this.dateOfBirth,
    this.clearDateOfBirth = false,
    this.currentAddress,
    this.employer,
    this.monthlyIncome,
    this.clearMonthlyIncome = false,
    this.desiredMoveInDate,
    this.clearDesiredMoveInDate = false,
    this.notes,
  });

  final String? firstName;
  final String? lastName;
  final String? email;
  final String? phone;
  final String? dateOfBirth;
  final bool clearDateOfBirth;
  final String? currentAddress;
  final String? employer;
  final double? monthlyIncome;
  final bool clearMonthlyIncome;
  final String? desiredMoveInDate;
  final bool clearDesiredMoveInDate;
  final String? notes;

  Map<String, dynamic> toJson() {
    return {
      if (firstName != null) 'firstName': firstName,
      if (lastName != null) 'lastName': lastName,
      if (email != null) 'email': email,
      if (phone != null) 'phone': phone,
      if (dateOfBirth != null) 'dateOfBirth': dateOfBirth,
      if (clearDateOfBirth) 'clearDateOfBirth': true,
      if (currentAddress != null) 'currentAddress': currentAddress,
      if (employer != null) 'employer': employer,
      if (monthlyIncome != null) 'monthlyIncome': monthlyIncome,
      if (clearMonthlyIncome) 'clearMonthlyIncome': true,
      if (desiredMoveInDate != null) 'desiredMoveInDate': desiredMoveInDate,
      if (clearDesiredMoveInDate) 'clearDesiredMoveInDate': true,
      if (notes != null) 'notes': notes,
    };
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final applicationsRepositoryProvider = Provider<ApplicationsRepository>((ref) {
  return ApplicationsRepository(ref.watch(dioProvider));
});

/// List of applications filtered by [status] (null/empty = all statuses).
final applicationsProvider = FutureProvider.autoDispose
    .family<List<RentalApplication>, String?>((ref, status) {
      return ref.watch(applicationsRepositoryProvider).list(status: status);
    });

final applicationsPageProvider = FutureProvider.autoDispose
    .family<ApplicationListPage, ApplicationListQuery>((ref, query) {
      return ref.watch(applicationsRepositoryProvider).listPage(query);
    });

/// A single application by id.
final applicationDetailProvider = FutureProvider.autoDispose
    .family<RentalApplication, int>((ref, id) {
      return ref.watch(applicationsRepositoryProvider).get(id);
    });

/// Screening results for an application (auto-disposes so it re-fetches on open).
final applicationScreeningProvider = FutureProvider.autoDispose
    .family<ScreeningWorkspace, int>((ref, id) {
      return ref.watch(applicationsRepositoryProvider).screening(id);
    });
