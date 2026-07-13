import 'dart:math';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/models/lease.dart';

class LeaseManagementListQuery {
  const LeaseManagementListQuery({
    this.skip = 0,
    this.take = 20,
    this.tenantId,
    this.propertyId,
    this.unitId,
    this.lifecycle,
    this.search,
    this.sort = '-updatedAt',
  });

  final int skip;
  final int take;
  final int? tenantId;
  final int? propertyId;
  final int? unitId;
  final String? lifecycle;
  final String? search;
  final String sort;

  @override
  bool operator ==(Object other) =>
      other is LeaseManagementListQuery &&
      other.skip == skip &&
      other.take == take &&
      other.tenantId == tenantId &&
      other.propertyId == propertyId &&
      other.unitId == unitId &&
      other.lifecycle == lifecycle &&
      other.search == search &&
      other.sort == sort;

  @override
  int get hashCode => Object.hash(
    skip,
    take,
    tenantId,
    propertyId,
    unitId,
    lifecycle,
    search,
    sort,
  );
}

class LeaseManagementListPage {
  const LeaseManagementListPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<LeaseManagementSummary> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory LeaseManagementListPage.fromJson(Map<String, dynamic> json) {
    final items = (json['items'] as List? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(LeaseManagementSummary.fromJson)
        .toList();
    return LeaseManagementListPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

class LeaseAgreementHistoryPage {
  const LeaseAgreementHistoryPage({required this.items});

  final List<LeaseAgreementHistory> items;

  factory LeaseAgreementHistoryPage.fromJson(Map<String, dynamic> json) =>
      LeaseAgreementHistoryPage(
        items: (json['items'] as List? ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(LeaseAgreementHistory.fromJson)
            .toList(),
      );
}

class LeaseSuccessorDraftResult {
  const LeaseSuccessorDraftResult({
    required this.leaseManagementId,
    required this.leaseAgreementId,
    required this.versionNumber,
    required this.draftRevision,
  });

  final int leaseManagementId;
  final int leaseAgreementId;
  final int versionNumber;
  final int draftRevision;

  factory LeaseSuccessorDraftResult.fromJson(Map<String, dynamic> json) =>
      LeaseSuccessorDraftResult(
        leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
        leaseAgreementId: (json['leaseAgreementId'] as num?)?.toInt() ?? 0,
        versionNumber: (json['versionNumber'] as num?)?.toInt() ?? 0,
        draftRevision: (json['draftRevision'] as num?)?.toInt() ?? 0,
      );
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

  factory LeaseQuestionResponse.fromJson(Map<String, dynamic> json) =>
      LeaseQuestionResponse(
        answer: json['answer'] as String? ?? '',
        llmEnhanced: json['llmEnhanced'] as bool? ?? false,
        sources: (json['sources'] as List? ?? const [])
            .whereType<String>()
            .toList(),
      );
}

class LeaseManagementsRepository {
  LeaseManagementsRepository(this._dio);

  final Dio _dio;

  Future<LeaseManagementListPage> listPage([
    LeaseManagementListQuery query = const LeaseManagementListQuery(),
  ]) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/page',
        queryParameters: <String, dynamic>{
          'skip': query.skip,
          'take': query.take,
          'tenantId': query.tenantId,
          'propertyId': query.propertyId,
          'unitId': query.unitId,
          'lifecycle': query.lifecycle,
          'search': query.search,
          'sort': query.sort,
        }..removeWhere((_, value) => value == null || value == ''),
      );
      return LeaseManagementListPage.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<List<LeaseManagementSummary>> list({
    int? tenantId,
    int? propertyId,
    int? unitId,
    String? lifecycle,
  }) async {
    final page = await listPage(
      LeaseManagementListQuery(
        take: 200,
        tenantId: tenantId,
        propertyId: propertyId,
        unitId: unitId,
        lifecycle: lifecycle,
      ),
    );
    return page.items;
  }

  Future<LeaseManagementDetail> get(int leaseManagementId) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId',
      );
      return LeaseManagementDetail.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseAgreementHistoryPage> agreementHistory(
    int leaseManagementId,
  ) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/agreements/page',
        queryParameters: const {'take': 200, 'sort': '-versionNumber'},
      );
      return LeaseAgreementHistoryPage.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseQuestionResponse> ask(
    int leaseManagementId,
    String question,
  ) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/ask',
        data: {'question': question},
      );
      return LeaseQuestionResponse.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseSuccessorDraftResult> createSuccessorDraft({
    required int leaseManagementId,
    required int sourceAgreementId,
    required String operation,
    required DateTime termStartOn,
    required DateTime governingFromOn,
    DateTime? termEndOn,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/agreements/'
        '$sourceAgreementId/$operation',
        data: {
          'termStartOn': _dateOnly(termStartOn),
          if (termEndOn != null) 'termEndOn': _dateOnly(termEndOn),
          'governingFromOn': _dateOnly(governingFromOn),
          'addendumDecisions': const [],
        },
        options: Options(headers: {'Idempotency-Key': _operationKey()}),
      );
      return LeaseSuccessorDraftResult.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<Uint8List> artifactBytes({
    required int leaseManagementId,
    required int leaseAgreementId,
    required int artifactId,
  }) async {
    try {
      final response = await _dio.get<List<int>>(
        '/lease-managements/$leaseManagementId/agreements/'
        '$leaseAgreementId/artifacts/$artifactId',
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<LeaseManagementLedger> ledger(
    int leaseManagementId, {
    int take = 200,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/lease-managements/$leaseManagementId/ledger',
        queryParameters: {'take': take},
      );
      return LeaseManagementLedger.fromJson(_required(response.data));
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  static Map<String, dynamic> _required(Map<String, dynamic>? data) {
    if (data == null) {
      throw const ApiException(
        statusCode: 0,
        message: 'Empty response from server.',
      );
    }
    return data;
  }

  static String _dateOnly(DateTime value) =>
      value.toIso8601String().split('T').first;

  static String _operationKey() {
    final random = Random.secure();
    return List<int>.generate(
      16,
      (_) => random.nextInt(256),
    ).map((value) => value.toRadixString(16).padLeft(2, '0')).join();
  }
}

class LeaseLedgerEntry {
  const LeaseLedgerEntry({
    required this.date,
    required this.type,
    required this.id,
    required this.description,
    required this.amount,
    required this.status,
    required this.explanation,
    required this.isProrated,
    this.propertyName,
    this.counterparty,
    this.category,
    this.sourceHref,
  });

  final DateTime date;
  final String type;
  final int id;
  final String description;
  final double amount;
  final String status;
  final String explanation;
  final bool isProrated;
  final String? propertyName;
  final String? counterparty;
  final String? category;
  final String? sourceHref;

  factory LeaseLedgerEntry.fromJson(Map<String, dynamic> json) =>
      LeaseLedgerEntry(
        date: DateTime.tryParse(json['date'] as String? ?? '') ?? DateTime(0),
        type: json['type'] as String? ?? '',
        id: (json['id'] as num?)?.toInt() ?? 0,
        description: json['description'] as String? ?? '',
        amount: (json['amount'] as num?)?.toDouble() ?? 0,
        status: json['status'] as String? ?? '',
        explanation: json['explanation'] as String? ?? '',
        isProrated: json['isProrated'] as bool? ?? false,
        propertyName: json['propertyName'] as String?,
        counterparty: json['counterparty'] as String?,
        category: json['category'] as String?,
        sourceHref: json['sourceHref'] as String?,
      );
}

class LeaseManagementLedger {
  const LeaseManagementLedger({
    required this.leaseManagementId,
    required this.tenantAccountId,
    required this.accountNumber,
    required this.totalCharged,
    required this.totalPaid,
    required this.balance,
    required this.entries,
    this.tenantName,
    this.propertyName,
  });

  final int leaseManagementId;
  final int tenantAccountId;
  final String accountNumber;
  final double totalCharged;
  final double totalPaid;
  final double balance;
  final List<LeaseLedgerEntry> entries;
  final String? tenantName;
  final String? propertyName;

  factory LeaseManagementLedger.fromJson(Map<String, dynamic> json) =>
      LeaseManagementLedger(
        leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
        tenantAccountId: (json['tenantAccountId'] as num?)?.toInt() ?? 0,
        accountNumber: json['accountNumber'] as String? ?? '',
        totalCharged: (json['totalCharged'] as num?)?.toDouble() ?? 0,
        totalPaid: (json['totalPaid'] as num?)?.toDouble() ?? 0,
        balance: (json['balance'] as num?)?.toDouble() ?? 0,
        entries: (json['entries'] as List? ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(LeaseLedgerEntry.fromJson)
            .toList(),
        tenantName: json['tenantName'] as String?,
        propertyName: json['propertyName'] as String?,
      );
}

final leaseManagementsRepositoryProvider = Provider<LeaseManagementsRepository>(
  (ref) => LeaseManagementsRepository(ref.watch(dioProvider)),
);

final leaseManagementsPageProvider = FutureProvider.autoDispose
    .family<LeaseManagementListPage, LeaseManagementListQuery>(
      (ref, query) =>
          ref.watch(leaseManagementsRepositoryProvider).listPage(query),
    );

final leaseManagementDetailProvider = FutureProvider.autoDispose
    .family<LeaseManagementDetail, int>(
      (ref, id) => ref.watch(leaseManagementsRepositoryProvider).get(id),
    );

final tenantLeaseManagementsProvider = FutureProvider.autoDispose
    .family<List<LeaseManagementSummary>, int>(
      (ref, tenantId) => ref
          .watch(leaseManagementsRepositoryProvider)
          .list(tenantId: tenantId),
    );

final leaseAgreementHistoryProvider = FutureProvider.autoDispose
    .family<LeaseAgreementHistoryPage, int>(
      (ref, id) =>
          ref.watch(leaseManagementsRepositoryProvider).agreementHistory(id),
    );

final leaseLedgerProvider = FutureProvider.autoDispose
    .family<LeaseManagementLedger, int>(
      (ref, id) => ref.watch(leaseManagementsRepositoryProvider).ledger(id),
    );
