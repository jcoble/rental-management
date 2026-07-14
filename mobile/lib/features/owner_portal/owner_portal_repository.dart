import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../owner_reports/owner_reports_repository.dart';

class OwnerPortalOverview {
  const OwnerPortalOverview({
    required this.currentYear,
    required this.propertyCount,
    required this.unitCount,
    required this.distributedThisYear,
    required this.pendingApprovalCount,
    required this.unreadMessageCount,
  });

  final int currentYear;
  final int propertyCount;
  final int unitCount;
  final double distributedThisYear;
  final int pendingApprovalCount;
  final int unreadMessageCount;

  factory OwnerPortalOverview.fromJson(
    Map<String, dynamic> json,
  ) => OwnerPortalOverview(
    currentYear: (json['currentYear'] as num?)?.toInt() ?? 0,
    propertyCount: (json['propertyCount'] as num?)?.toInt() ?? 0,
    unitCount: (json['unitCount'] as num?)?.toInt() ?? 0,
    distributedThisYear: (json['distributedThisYear'] as num?)?.toDouble() ?? 0,
    pendingApprovalCount: (json['pendingApprovalCount'] as num?)?.toInt() ?? 0,
    unreadMessageCount: (json['unreadMessageCount'] as num?)?.toInt() ?? 0,
  );
}

class OwnerPortalProperty {
  const OwnerPortalProperty({
    required this.id,
    required this.name,
    required this.propertyType,
    required this.status,
    required this.addressLine1,
    required this.city,
    required this.state,
    required this.postalCode,
    required this.unitCount,
    this.addressLine2,
  });

  final int id;
  final String name;
  final String propertyType;
  final String status;
  final String addressLine1;
  final String? addressLine2;
  final String city;
  final String state;
  final String postalCode;
  final int unitCount;

  factory OwnerPortalProperty.fromJson(Map<String, dynamic> json) =>
      OwnerPortalProperty(
        id: (json['id'] as num).toInt(),
        name: json['name'] as String? ?? '',
        propertyType: json['propertyType'] as String? ?? '',
        status: json['status'] as String? ?? '',
        addressLine1: json['addressLine1'] as String? ?? '',
        addressLine2: json['addressLine2'] as String?,
        city: json['city'] as String? ?? '',
        state: json['state'] as String? ?? '',
        postalCode: json['postalCode'] as String? ?? '',
        unitCount: (json['unitCount'] as num?)?.toInt() ?? 0,
      );

  String get address {
    final second = addressLine2?.trim();
    final street = second == null || second.isEmpty
        ? addressLine1
        : '$addressLine1, $second';
    return '$street\n$city, $state $postalCode';
  }
}

class OwnerPortalItem {
  const OwnerPortalItem({
    required this.id,
    required this.title,
    required this.message,
    required this.severity,
    required this.isRead,
    required this.createdAt,
  });

  final int id;
  final String title;
  final String message;
  final String severity;
  final bool isRead;
  final DateTime createdAt;

  factory OwnerPortalItem.fromJson(Map<String, dynamic> json) =>
      OwnerPortalItem(
        id: (json['id'] as num).toInt(),
        title: json['title'] as String? ?? '',
        message: json['message'] as String? ?? '',
        severity: json['severity'] as String? ?? 'Info',
        isRead: json['isRead'] as bool? ?? false,
        createdAt:
            DateTime.tryParse(json['createdAt'] as String? ?? '') ??
            DateTime.fromMillisecondsSinceEpoch(0),
      );
}

class OwnerPortalPage<T> {
  const OwnerPortalPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<T> items;
  final int totalCount;
  final int skip;
  final int take;
}

class OwnerPortalRepository {
  OwnerPortalRepository(this._dio);

  final Dio _dio;

  Future<OwnerPortalOverview> overview() =>
      _getObject('/owner/overview', OwnerPortalOverview.fromJson);

  Future<OwnerPortalPage<OwnerPortalProperty>> propertiesPage({
    int skip = 0,
    int take = 20,
  }) => _getPage(
    '/owner/properties/page',
    OwnerPortalProperty.fromJson,
    skip: skip,
    take: take,
    sort: 'name',
  );

  Future<OwnerPortalPage<OwnerSummary>> statementsPage({
    required int year,
    int skip = 0,
    int take = 20,
  }) => _getPage(
    '/owner/statements',
    OwnerSummary.fromJson,
    skip: skip,
    take: take,
    sort: 'name',
    extra: {'year': year},
  );

  Future<OwnerStatement> statement(int ownerEntityId, int year) => _getObject(
    '/owner/statements/$ownerEntityId',
    OwnerStatement.fromJson,
    queryParameters: {'year': year},
  );

  Future<OwnerPortalPage<OwnerDistribution>> distributionsPage({
    required int year,
    int skip = 0,
    int take = 20,
  }) => _getPage(
    '/owner/distributions/page',
    OwnerDistribution.fromJson,
    skip: skip,
    take: take,
    sort: '-date',
    extra: {'from': '$year-01-01', 'to': '$year-12-31'},
  );

  Future<OwnerPortalPage<OwnerPortalItem>> approvalsPage({
    int skip = 0,
    int take = 20,
  }) => _getPage(
    '/owner/approvals/page',
    OwnerPortalItem.fromJson,
    skip: skip,
    take: take,
    sort: '-createdAt',
  );

  Future<OwnerPortalPage<OwnerPortalItem>> messagesPage({
    int skip = 0,
    int take = 20,
  }) => _getPage(
    '/owner/messages/page',
    OwnerPortalItem.fromJson,
    skip: skip,
    take: take,
    sort: '-createdAt',
  );

  Future<T> _getObject<T>(
    String path,
    T Function(Map<String, dynamic>) fromJson, {
    Map<String, dynamic>? queryParameters,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        path,
        queryParameters: queryParameters,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(statusCode: 0, message: 'Empty response.');
      }
      return fromJson(data);
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<OwnerPortalPage<T>> _getPage<T>(
    String path,
    T Function(Map<String, dynamic>) fromJson, {
    required int skip,
    required int take,
    required String sort,
    Map<String, dynamic>? extra,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        path,
        queryParameters: {'skip': skip, 'take': take, 'sort': sort, ...?extra},
      );
      final data = response.data ?? const <String, dynamic>{};
      final rawItems = data['items'];
      return OwnerPortalPage<T>(
        items: (rawItems is List ? rawItems : const [])
            .whereType<Map<String, dynamic>>()
            .map(fromJson)
            .toList(growable: false),
        totalCount: (data['totalCount'] as num?)?.toInt() ?? 0,
        skip: (data['skip'] as num?)?.toInt() ?? skip,
        take: (data['take'] as num?)?.toInt() ?? take,
      );
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }
}

final ownerPortalRepositoryProvider = Provider<OwnerPortalRepository>((ref) {
  return OwnerPortalRepository(ref.watch(dioProvider));
});
