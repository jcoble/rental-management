import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';

enum EvictionCaseStatus {
  draft('Draft', 'Draft'),
  noticeServed('NoticeServed', 'Notice served'),
  filed('Filed', 'Filed'),
  hearingScheduled('HearingScheduled', 'Hearing scheduled'),
  judgment('Judgment', 'Judgment'),
  moveOut('MoveOut', 'Move-out'),
  settled('Settled', 'Settled'),
  dismissed('Dismissed', 'Dismissed');

  const EvictionCaseStatus(this.wire, this.label);

  final String wire;
  final String label;

  static EvictionCaseStatus fromWire(String? wire) {
    return EvictionCaseStatus.values.firstWhere(
      (status) => status.wire == wire,
      orElse: () => EvictionCaseStatus.filed,
    );
  }
}

enum EvictionEventType {
  noticeServed('NoticeServed', 'Notice served'),
  filed('Filed', 'Filed'),
  hearingScheduled('HearingScheduled', 'Hearing scheduled'),
  judgment('Judgment', 'Judgment'),
  moveOut('MoveOut', 'Move-out'),
  settlement('Settlement', 'Settlement'),
  dismissal('Dismissal', 'Dismissal'),
  paymentPlan('PaymentPlan', 'Payment plan'),
  note('Note', 'Note');

  const EvictionEventType(this.wire, this.label);

  final String wire;
  final String label;

  static EvictionEventType fromWire(String? wire) {
    return EvictionEventType.values.firstWhere(
      (type) => type.wire == wire,
      orElse: () => EvictionEventType.note,
    );
  }
}

class EvictionCaseEvent {
  const EvictionCaseEvent({
    required this.id,
    required this.portfolioId,
    required this.evictionCaseId,
    required this.eventType,
    required this.eventDate,
    this.notes,
    required this.createdAt,
    required this.updatedAt,
    required this.testId,
  });

  final int id;
  final int portfolioId;
  final int evictionCaseId;
  final EvictionEventType eventType;
  final DateTime eventDate;
  final String? notes;
  final DateTime createdAt;
  final DateTime updatedAt;
  final String testId;

  factory EvictionCaseEvent.fromJson(Map<String, dynamic> json) {
    return EvictionCaseEvent(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num?)?.toInt() ?? 0,
      evictionCaseId: (json['evictionCaseId'] as num?)?.toInt() ?? 0,
      eventType: EvictionEventType.fromWire(json['eventType'] as String?),
      eventDate:
          DateTime.tryParse(json['eventDate'] as String? ?? '') ?? DateTime(0),
      notes: json['notes'] as String?,
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt:
          DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
      testId: json['testId'] as String? ?? 'eviction-event-${json['id']}',
    );
  }
}

class EvictionCase {
  const EvictionCase({
    required this.id,
    required this.portfolioId,
    required this.leaseId,
    this.leaseNumber,
    required this.propertyId,
    this.propertyName,
    required this.unitId,
    this.unitNumber,
    required this.tenantId,
    this.tenantName,
    required this.status,
    this.filedOnDate,
    this.hearingDate,
    this.resolvedOnDate,
    this.courtName,
    this.caseNumber,
    this.resolution,
    this.notes,
    required this.eventCount,
    this.latestEventDate,
    required this.events,
    required this.createdAt,
    required this.updatedAt,
    required this.testId,
  });

  final int id;
  final int portfolioId;
  final int leaseId;
  final String? leaseNumber;
  final int propertyId;
  final String? propertyName;
  final int unitId;
  final String? unitNumber;
  final int tenantId;
  final String? tenantName;
  final EvictionCaseStatus status;
  final DateTime? filedOnDate;
  final DateTime? hearingDate;
  final DateTime? resolvedOnDate;
  final String? courtName;
  final String? caseNumber;
  final String? resolution;
  final String? notes;
  final int eventCount;
  final DateTime? latestEventDate;
  final List<EvictionCaseEvent> events;
  final DateTime createdAt;
  final DateTime updatedAt;
  final String testId;

  factory EvictionCase.fromJson(Map<String, dynamic> json) {
    final rawEvents = json['events'];
    final events = rawEvents is List
        ? rawEvents
              .whereType<Map<String, dynamic>>()
              .map(EvictionCaseEvent.fromJson)
              .toList()
        : <EvictionCaseEvent>[];

    return EvictionCase(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num?)?.toInt() ?? 0,
      leaseId: (json['leaseId'] as num?)?.toInt() ?? 0,
      leaseNumber: json['leaseNumber'] as String?,
      propertyId: (json['propertyId'] as num?)?.toInt() ?? 0,
      propertyName: json['propertyName'] as String?,
      unitId: (json['unitId'] as num?)?.toInt() ?? 0,
      unitNumber: json['unitNumber'] as String?,
      tenantId: (json['tenantId'] as num?)?.toInt() ?? 0,
      tenantName: json['tenantName'] as String?,
      status: EvictionCaseStatus.fromWire(json['status'] as String?),
      filedOnDate: DateTime.tryParse(json['filedOnDate'] as String? ?? ''),
      hearingDate: DateTime.tryParse(json['hearingDate'] as String? ?? ''),
      resolvedOnDate: DateTime.tryParse(
        json['resolvedOnDate'] as String? ?? '',
      ),
      courtName: json['courtName'] as String?,
      caseNumber: json['caseNumber'] as String?,
      resolution: json['resolution'] as String?,
      notes: json['notes'] as String?,
      eventCount: (json['eventCount'] as num?)?.toInt() ?? events.length,
      latestEventDate: DateTime.tryParse(
        json['latestEventDate'] as String? ?? '',
      ),
      events: events,
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt:
          DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
      testId: json['testId'] as String? ?? 'eviction-case-${json['id']}',
    );
  }
}

class EvictionCaseListPage {
  const EvictionCaseListPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<EvictionCase> items;
  final int totalCount;
  final int skip;
  final int take;

  factory EvictionCaseListPage.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final items = rawItems is List
        ? rawItems
              .whereType<Map<String, dynamic>>()
              .map(EvictionCase.fromJson)
              .toList()
        : <EvictionCase>[];

    return EvictionCaseListPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

class EvictionCasesRepository {
  EvictionCasesRepository(this._dio);

  final Dio _dio;

  Future<EvictionCaseListPage> listCasesPage({
    required int leaseId,
    int skip = 0,
    int take = _leaseEvictionCasesPageSize,
    String sort = '-filedOnDate',
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/eviction-cases/page',
        queryParameters: {
          'leaseId': leaseId,
          'skip': skip,
          'take': take,
          if (sort.isNotEmpty) 'sort': sort,
        },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return EvictionCaseListPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<EvictionCase> getCase(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/eviction-cases/$id',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return EvictionCase.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<EvictionCase> createCase({
    required int leaseId,
    required Map<String, dynamic> data,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/eviction-cases',
        data: {...data, 'leaseId': leaseId},
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return EvictionCase.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<EvictionCase> updateCase(int id, Map<String, dynamic> data) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/eviction-cases/$id',
        data: data,
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return EvictionCase.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<EvictionCase> addEvent(
    int id, {
    required Map<String, dynamic> data,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/eviction-cases/$id/events',
        data: data,
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return EvictionCase.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> deleteCase(int id) async {
    try {
      await _dio.delete<dynamic>('/eviction-cases/$id');
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

const _leaseEvictionCasesPageSize = 20;

class LeaseEvictionCasesPage {
  const LeaseEvictionCasesPage({
    required this.cases,
    required this.hasMore,
    this.isLoadingMore = false,
    this.loadMoreError,
  });

  final List<EvictionCase> cases;
  final bool hasMore;
  final bool isLoadingMore;
  final Object? loadMoreError;
}

final evictionCasesRepositoryProvider = Provider<EvictionCasesRepository>((
  ref,
) {
  return EvictionCasesRepository(ref.watch(dioProvider));
});

class LeaseEvictionCasesNotifier
    extends Notifier<AsyncValue<LeaseEvictionCasesPage>> {
  LeaseEvictionCasesNotifier(this._leaseId);

  final int _leaseId;
  Future<void>? _initialLoad;

  EvictionCasesRepository get _repo =>
      ref.read(evictionCasesRepositoryProvider);

  @override
  AsyncValue<LeaseEvictionCasesPage> build() {
    _initialLoad = Future<void>.microtask(_loadFirstPage);
    return const AsyncValue.loading();
  }

  Future<void> refresh() {
    final pending = _initialLoad;
    if (pending != null && state.isLoading) return pending;
    return _loadFirstPage();
  }

  Future<void> _loadFirstPage() async {
    state = const AsyncValue.loading();
    try {
      final page = await _repo.listCasesPage(
        leaseId: _leaseId,
        skip: 0,
        take: _leaseEvictionCasesPageSize,
        sort: '-filedOnDate',
      );
      state = AsyncValue.data(
        LeaseEvictionCasesPage(
          cases: page.items,
          hasMore: page.items.length == _leaseEvictionCasesPageSize,
        ),
      );
    } catch (e, st) {
      state = AsyncValue.error(e, st);
    } finally {
      _initialLoad = null;
    }
  }

  Future<void> loadMore() async {
    final current = state.value;
    if (current == null || !current.hasMore || current.isLoadingMore) return;

    state = AsyncValue.data(
      LeaseEvictionCasesPage(
        cases: current.cases,
        hasMore: current.hasMore,
        isLoadingMore: true,
      ),
    );

    try {
      final next = await _repo.listCasesPage(
        leaseId: _leaseId,
        skip: current.cases.length,
        take: _leaseEvictionCasesPageSize,
        sort: '-filedOnDate',
      );
      state = AsyncValue.data(
        LeaseEvictionCasesPage(
          cases: [...current.cases, ...next.items],
          hasMore: next.items.length == _leaseEvictionCasesPageSize,
        ),
      );
    } catch (e) {
      state = AsyncValue.data(
        LeaseEvictionCasesPage(
          cases: current.cases,
          hasMore: current.hasMore,
          loadMoreError: e,
        ),
      );
    }
  }
}

final leaseEvictionCasesProvider =
    NotifierProvider.family<
      LeaseEvictionCasesNotifier,
      AsyncValue<LeaseEvictionCasesPage>,
      int
    >(LeaseEvictionCasesNotifier.new);
