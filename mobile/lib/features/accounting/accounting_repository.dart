import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';
import 'accounting_models.dart';

/// Repository for the accounting endpoints.
///
/// Endpoints:
///   GET /api/v1/accounting/snapshot
///   GET /api/v1/accounting/year-end-packet?year={year}  — PDF bytes
class AccountingRepository {
  const AccountingRepository(this._dio);

  final Dio _dio;

  /// Fetches the plain-English money snapshot for the caller's portfolio.
  Future<MoneySnapshot> snapshot() async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/accounting/snapshot',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return MoneySnapshot.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Fetches a bounded "Who's behind" page (one row per tenant account). Shares
  /// the snapshot's past-due definition, so the count matches the dashboard KPI.
  Future<PastDueResult> pastDue({int skip = 0, int take = 20}) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/accounting/past-due',
        queryParameters: {'skip': skip, 'take': take},
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return PastDueResult.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Fetches the year-end accountant packet PDF bytes (Schedule E, P&L, cash
  /// flow, rent roll) for [year], authed via the shared Dio interceptor.
  Future<Uint8List> yearEndPacketBytes(int year) async {
    try {
      final response = await _dio.get<List<int>>(
        '/accounting/year-end-packet',
        queryParameters: {'year': year},
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Disconnects one provider. The retained operation key makes an ambiguous
  /// transport failure safe to retry without replaying the lifecycle command.
  Future<void> disconnectIntegration(String provider) async {
    try {
      await IdempotentMutation.run(
        'accounting:disconnect:$provider',
        (operationKey) => _dio.post<void>(
          '/integrations/accounting/$provider/disconnect',
          options: Options(headers: {'Idempotency-Key': operationKey}),
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Updates both accounting directions as one atomic command.
  Future<void> setIntegrationDirection(
    String provider, {
    required bool pullEnabled,
    required bool pushEnabled,
  }) async {
    try {
      await IdempotentMutation.run(
        'accounting:direction:$provider:$pullEnabled:$pushEnabled',
        (operationKey) => _dio.post<void>(
          '/integrations/accounting/$provider/direction',
          data: {
            'pullEnabled': pullEnabled,
            'pushEnabled': pushEnabled,
          },
          options: Options(headers: {'Idempotency-Key': operationKey}),
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final accountingRepositoryProvider = Provider<AccountingRepository>((ref) {
  return AccountingRepository(ref.watch(dioProvider));
});

/// The landlord home money snapshot. autoDispose so it refreshes on each visit.
final moneySnapshotProvider = FutureProvider.autoDispose<MoneySnapshot>((ref) {
  return ref.watch(accountingRepositoryProvider).snapshot();
});

class PastDueState {
  const PastDueState({
    this.items = const [],
    this.totalCount = 0,
    this.totalPastDueAmount = 0,
    this.businessDate,
    this.loading = false,
    this.loadingMore = false,
    this.error,
  });

  final List<PastDueLease> items;
  final int totalCount;
  final double totalPastDueAmount;
  final DateTime? businessDate;
  final bool loading;
  final bool loadingMore;
  final String? error;

  bool get hasMore => items.length < totalCount;

  PastDueState copyWith({
    List<PastDueLease>? items,
    int? totalCount,
    double? totalPastDueAmount,
    DateTime? businessDate,
    bool? loading,
    bool? loadingMore,
    String? error,
    bool clearError = false,
  }) => PastDueState(
    items: items ?? this.items,
    totalCount: totalCount ?? this.totalCount,
    totalPastDueAmount: totalPastDueAmount ?? this.totalPastDueAmount,
    businessDate: businessDate ?? this.businessDate,
    loading: loading ?? this.loading,
    loadingMore: loadingMore ?? this.loadingMore,
    error: clearError ? null : error ?? this.error,
  );
}

/// Accumulates bounded server pages without re-filtering, grouping, sorting, or
/// deriving totals on the client. The exact count and amount always come from SQL.
class PastDueNotifier extends Notifier<PastDueState> {
  static const _pageSize = 20;
  int _requestGeneration = 0;

  @override
  PastDueState build() {
    Future.microtask(refresh);
    return const PastDueState(loading: true);
  }

  AccountingRepository get _repo => ref.read(accountingRepositoryProvider);

  Future<void> refresh() async {
    final generation = ++_requestGeneration;
    state = state.copyWith(loading: true, loadingMore: false, clearError: true);
    try {
      final page = await _repo.pastDue(take: _pageSize);
      if (generation != _requestGeneration || !ref.mounted) return;
      state = PastDueState(
        items: page.items,
        totalCount: page.totalCount,
        totalPastDueAmount: page.totalPastDueAmount,
        businessDate: page.businessDate,
      );
    } on ApiException catch (error) {
      if (generation != _requestGeneration || !ref.mounted) return;
      state = state.copyWith(
        loading: false,
        loadingMore: false,
        error: error.message,
      );
    } catch (_) {
      if (generation != _requestGeneration || !ref.mounted) return;
      state = state.copyWith(
        loading: false,
        loadingMore: false,
        error: "Couldn't load who's behind.",
      );
    }
  }

  Future<void> loadMore() async {
    if (state.loading || state.loadingMore || !state.hasMore) return;
    final generation = _requestGeneration;
    final skip = state.items.length;
    state = state.copyWith(loadingMore: true, clearError: true);
    try {
      final page = await _repo.pastDue(skip: skip, take: _pageSize);
      if (generation != _requestGeneration || !ref.mounted) return;
      if (page.businessDate != state.businessDate) {
        await refresh();
        return;
      }
      state = PastDueState(
        items: [...state.items, ...page.items],
        totalCount: page.totalCount,
        totalPastDueAmount: page.totalPastDueAmount,
        businessDate: page.businessDate,
      );
    } on ApiException catch (error) {
      if (generation != _requestGeneration || !ref.mounted) return;
      state = state.copyWith(loadingMore: false, error: error.message);
    } catch (_) {
      if (generation != _requestGeneration || !ref.mounted) return;
      state = state.copyWith(
        loadingMore: false,
        error: "Couldn't load more accounts.",
      );
    }
  }
}

final pastDueProvider =
    NotifierProvider.autoDispose<PastDueNotifier, PastDueState>(
      PastDueNotifier.new,
    );
