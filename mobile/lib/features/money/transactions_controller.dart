import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'money_repository.dart';
import 'transaction_models.dart';

/// Paginated state for the unified ledger. Holds the accumulated rows, the
/// active filter, and whether another page can be loaded.
class TransactionsState {
  const TransactionsState({
    this.items = const [],
    this.filter = const TransactionsFilter(),
    this.totalCount = 0,
    this.loading = false,
    this.loadingMore = false,
    this.error,
  });

  final List<AccountingTransaction> items;
  final TransactionsFilter filter;
  final int totalCount;
  final bool loading;
  final bool loadingMore;
  final String? error;

  bool get hasMore => items.length < totalCount;

  TransactionsState copyWith({
    List<AccountingTransaction>? items,
    TransactionsFilter? filter,
    int? totalCount,
    bool? loading,
    bool? loadingMore,
    String? error,
    bool clearError = false,
  }) {
    return TransactionsState(
      items: items ?? this.items,
      filter: filter ?? this.filter,
      totalCount: totalCount ?? this.totalCount,
      loading: loading ?? this.loading,
      loadingMore: loadingMore ?? this.loadingMore,
      error: clearError ? null : (error ?? this.error),
    );
  }
}

/// Drives the unified ledger: first page, append-on-scroll, and filtering.
class TransactionsNotifier extends Notifier<TransactionsState> {
  static const _pageSize = 40;
  int _loadGeneration = 0;

  @override
  TransactionsState build() => const TransactionsState();

  MoneyRepository get _repo => ref.read(moneyRepositoryProvider);

  /// Loads (or reloads) the first page with the current filter.
  Future<void> load({
    TransactionsFilter? filter,
    bool clearItems = false,
  }) async {
    final nextFilter = filter ?? state.filter;
    final generation = ++_loadGeneration;

    state = state.copyWith(
      filter: nextFilter,
      items: clearItems ? const [] : state.items,
      totalCount: clearItems ? 0 : state.totalCount,
      loading: true,
      loadingMore: false,
      clearError: true,
    );
    try {
      final page = await _repo.transactions(
        skip: 0,
        take: _pageSize,
        filter: nextFilter,
      );
      if (generation != _loadGeneration) return;
      state = state.copyWith(
        items: page.items,
        totalCount: page.totalCount,
        loading: false,
        loadingMore: false,
      );
    } on ApiException catch (e) {
      if (generation != _loadGeneration) return;
      state = state.copyWith(loading: false, error: e.message);
    }
  }

  Future<void> refresh() => load();

  /// Appends the next page, if any.
  Future<void> loadMore() async {
    if (state.loadingMore || state.loading || !state.hasMore) return;
    final generation = _loadGeneration;
    final filter = state.filter;
    final skip = state.items.length;
    state = state.copyWith(loadingMore: true);
    try {
      final page = await _repo.transactions(
        skip: skip,
        take: _pageSize,
        filter: filter,
      );
      if (generation != _loadGeneration) return;
      state = state.copyWith(
        items: [...state.items, ...page.items],
        totalCount: page.totalCount,
        loadingMore: false,
      );
    } on ApiException catch (e) {
      if (generation != _loadGeneration) return;
      state = state.copyWith(loadingMore: false, error: e.message);
    }
  }

  /// Replaces the filter and reloads from the first page.
  Future<void> setFilter(TransactionsFilter filter) =>
      load(filter: filter, clearItems: true);
}

final transactionsProvider =
    NotifierProvider<TransactionsNotifier, TransactionsState>(
      TransactionsNotifier.new,
    );
