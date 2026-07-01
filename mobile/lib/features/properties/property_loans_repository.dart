import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';

class PropertyLoan {
  const PropertyLoan({
    required this.id,
    required this.portfolioId,
    required this.propertyId,
    this.propertyName,
    required this.lender,
    required this.originalAmount,
    required this.currentBalance,
    required this.annualInterestRatePct,
    required this.termMonths,
    required this.startDate,
    required this.dayOfMonthDue,
    required this.monthlyPrincipalInterest,
    required this.monthlyEscrow,
    required this.escrowCoversTaxes,
    required this.escrowCoversInsurance,
    required this.status,
    this.notes,
    required this.createdAt,
    required this.updatedAt,
    required this.testId,
  });

  final int id;
  final int portfolioId;
  final int propertyId;
  final String? propertyName;
  final String lender;
  final double originalAmount;
  final double currentBalance;
  final double annualInterestRatePct;
  final int termMonths;
  final DateTime startDate;
  final int dayOfMonthDue;
  final double monthlyPrincipalInterest;
  final double monthlyEscrow;
  final bool escrowCoversTaxes;
  final bool escrowCoversInsurance;
  final String status;
  final String? notes;
  final DateTime createdAt;
  final DateTime updatedAt;
  final String testId;

  factory PropertyLoan.fromJson(Map<String, dynamic> json) {
    return PropertyLoan(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num).toInt(),
      propertyId: (json['propertyId'] as num).toInt(),
      propertyName: json['propertyName'] as String?,
      lender: json['lender'] as String? ?? '',
      originalAmount: (json['originalAmount'] as num?)?.toDouble() ?? 0,
      currentBalance: (json['currentBalance'] as num?)?.toDouble() ?? 0,
      annualInterestRatePct:
          (json['annualInterestRatePct'] as num?)?.toDouble() ?? 0,
      termMonths: (json['termMonths'] as num?)?.toInt() ?? 0,
      startDate:
          DateTime.tryParse(json['startDate'] as String? ?? '') ?? DateTime(0),
      dayOfMonthDue: (json['dayOfMonthDue'] as num?)?.toInt() ?? 1,
      monthlyPrincipalInterest:
          (json['monthlyPrincipalInterest'] as num?)?.toDouble() ?? 0,
      monthlyEscrow: (json['monthlyEscrow'] as num?)?.toDouble() ?? 0,
      escrowCoversTaxes: json['escrowCoversTaxes'] as bool? ?? false,
      escrowCoversInsurance: json['escrowCoversInsurance'] as bool? ?? false,
      status: json['status'] as String? ?? 'Active',
      notes: json['notes'] as String?,
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt:
          DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
      testId: json['testId'] as String? ?? 'loan-${json['id']}',
    );
  }
}

class PropertyLoansRepository {
  PropertyLoansRepository(this._dio);

  final Dio _dio;

  Future<List<PropertyLoan>> listLoans({
    required int propertyId,
    int skip = 0,
    int? take,
    String sort = '-createdAt',
    String? search,
  }) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/loans',
        queryParameters: {
          'propertyId': propertyId,
          'skip': skip,
          'take': ?take,
          if (sort.isNotEmpty) 'sort': sort,
          if (search != null && search.trim().isNotEmpty)
            'search': search.trim(),
        },
      );
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(PropertyLoan.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<PropertyLoan> createLoan({
    required int propertyId,
    required Map<String, dynamic> data,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/loans',
        data: {...data, 'propertyId': propertyId},
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return PropertyLoan.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<PropertyLoan> updateLoan(int id, Map<String, dynamic> data) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/loans/$id',
        data: data,
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return PropertyLoan.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> deleteLoan(int id) async {
    try {
      await _dio.delete<dynamic>('/loans/$id');
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

const _propertyLoansPageSize = 20;

class PropertyLoansPage {
  const PropertyLoansPage({
    required this.loans,
    required this.hasMore,
    this.isLoadingMore = false,
    this.loadMoreError,
  });

  final List<PropertyLoan> loans;
  final bool hasMore;
  final bool isLoadingMore;
  final Object? loadMoreError;
}

final propertyLoansRepositoryProvider = Provider<PropertyLoansRepository>((
  ref,
) {
  return PropertyLoansRepository(ref.watch(dioProvider));
});

class PropertyLoansNotifier extends Notifier<AsyncValue<PropertyLoansPage>> {
  PropertyLoansNotifier(this._propertyId);

  final int _propertyId;
  Future<void>? _initialLoad;

  PropertyLoansRepository get _repo =>
      ref.read(propertyLoansRepositoryProvider);

  @override
  AsyncValue<PropertyLoansPage> build() {
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
      final loans = await _repo.listLoans(
        propertyId: _propertyId,
        skip: 0,
        take: _propertyLoansPageSize,
        sort: '-createdAt',
      );
      state = AsyncValue.data(
        PropertyLoansPage(
          loans: loans,
          hasMore: loans.length == _propertyLoansPageSize,
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
      PropertyLoansPage(
        loans: current.loans,
        hasMore: current.hasMore,
        isLoadingMore: true,
      ),
    );

    try {
      final next = await _repo.listLoans(
        propertyId: _propertyId,
        skip: current.loans.length,
        take: _propertyLoansPageSize,
        sort: '-createdAt',
      );
      state = AsyncValue.data(
        PropertyLoansPage(
          loans: [...current.loans, ...next],
          hasMore: next.length == _propertyLoansPageSize,
        ),
      );
    } catch (e) {
      state = AsyncValue.data(
        PropertyLoansPage(
          loans: current.loans,
          hasMore: current.hasMore,
          loadMoreError: e,
        ),
      );
    }
  }
}

final propertyLoansProvider =
    NotifierProvider.family<
      PropertyLoansNotifier,
      AsyncValue<PropertyLoansPage>,
      int
    >(PropertyLoansNotifier.new);
