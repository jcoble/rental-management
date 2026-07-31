import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_exception.dart';
import '../../core/presentation/plain_english_labels.dart';
import 'tenant_portal_repository.dart';

/// Tenant-facing account history. All period calculations, ordering, balances,
/// focus selection, and paging come from one tenant-scoped database statement.
class TenantAccountHistoryScreen extends ConsumerStatefulWidget {
  const TenantAccountHistoryScreen({
    super.key,
    this.initialTenantAccountId,
    this.initialTenantLedgerEntryId,
  });

  final int? initialTenantAccountId;
  final int? initialTenantLedgerEntryId;

  @override
  ConsumerState<TenantAccountHistoryScreen> createState() =>
      _TenantAccountHistoryScreenState();
}

class _TenantAccountHistoryScreenState
    extends ConsumerState<TenantAccountHistoryScreen> {
  static const _pageSize = 20;
  static const _accountPageSize = 20;
  int? _selectedAccountId;
  int _accountSkip = 0;
  int _skip = 0;
  int? _payingEntryId;
  bool _autopayBusy = false;
  bool _focusedEntryScrolled = false;
  final GlobalKey _focusedEntryKey = GlobalKey();
  TenantAccountHistoryPeriod _period = TenantAccountHistoryPeriod.currentMonth;

  @override
  void initState() {
    super.initState();
    _selectedAccountId = widget.initialTenantAccountId;
    if (widget.initialTenantLedgerEntryId != null) {
      _period = TenantAccountHistoryPeriod.all;
    }
  }

  Future<void> _payNow(
    int tenantAccountId,
    PortalTenantAccountHistoryItem entry,
  ) async {
    setState(() => _payingEntryId = entry.tenantLedgerEntryId);
    try {
      final url = await ref
          .read(tenantPortalRepositoryProvider)
          .payCheckout(tenantAccountId, entry.tenantLedgerEntryId);
      if (url.isNotEmpty) {
        await launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication);
      }
    } on ApiException catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              error.statusCode == 503
                  ? "Online payments aren't set up yet."
                  : error.message,
            ),
          ),
        );
      }
    } finally {
      if (mounted) setState(() => _payingEntryId = null);
    }
  }

  Future<void> _setUpAutopay(int tenantAccountId) async {
    setState(() => _autopayBusy = true);
    try {
      final url = await ref
          .read(tenantPortalRepositoryProvider)
          .autopayEnroll(tenantAccountId);
      if (url.isNotEmpty) {
        await launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication);
        ref.invalidate(tenantAutopayStatusProvider(tenantAccountId));
      }
    } on ApiException catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              error.statusCode == 503
                  ? "Autopay isn't available right now."
                  : error.message,
            ),
          ),
        );
      }
    } finally {
      if (mounted) setState(() => _autopayBusy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final accountRequest = (
      skip: _accountSkip,
      take: _accountPageSize,
      sort: 'propertyName',
    );
    final accounts = ref.watch(
      tenantPortalAccountsPageProvider(accountRequest),
    );
    final accountPage = accounts.asData?.value;
    final accountId =
        _selectedAccountId ??
        widget.initialTenantAccountId ??
        (accountPage?.totalCount == 1 && accountPage!.items.isNotEmpty
            ? accountPage.items.first.tenantAccountId
            : null);

    if (accountId == null) {
      return Scaffold(
        appBar: AppBar(title: const Text('Account history')),
        body: accounts.when(
          loading: () => const Center(
            key: Key('account-history-loading'),
            child: CircularProgressIndicator(),
          ),
          error: (error, _) => _HistoryMessage(
            key: const Key('account-history-error'),
            title: "Couldn't load your account history.",
            detail: '$error',
            onRefresh: () async => ref.invalidate(
              tenantPortalAccountsPageProvider(accountRequest),
            ),
          ),
          data: (page) {
            if (page.totalCount == 0) {
              return const _HistoryMessage(
                key: Key('account-history-empty-account'),
                title: 'No tenant account is available.',
              );
            }

            return _AccountPicker(
              accounts: page.items,
              totalCount: page.totalCount,
              skip: page.skip,
              take: page.take,
              onPrevious: page.skip == 0
                  ? null
                  : () => setState(() {
                      _accountSkip = (_accountSkip - _accountPageSize).clamp(
                        0,
                        page.totalCount,
                      );
                    }),
              onNext: page.skip + page.items.length >= page.totalCount
                  ? null
                  : () => setState(() => _accountSkip += _accountPageSize),
              onChanged: (value) => setState(() {
                _selectedAccountId = value;
                _skip = 0;
              }),
            );
          },
        ),
      );
    }

    final request = (
      tenantAccountId: accountId,
      period: _period,
      skip: _skip,
      take: _pageSize,
      focusedEntryId: widget.initialTenantLedgerEntryId,
    );
    final history = ref.watch(tenantPortalAccountHistoryProvider(request));
    final autopay = ref.watch(tenantAutopayStatusProvider(accountId));

    return Scaffold(
      appBar: AppBar(title: const Text('Account history')),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(tenantPortalAccountsPageProvider(accountRequest));
          ref.invalidate(tenantPortalAccountHistoryProvider(request));
          ref.invalidate(tenantAutopayStatusProvider(accountId));
        },
        child: history.when(
          loading: () => ListView(
            key: const Key('account-history-page-loading'),
            children: const [
              SizedBox(height: 160),
              Center(child: CircularProgressIndicator()),
            ],
          ),
          error: (error, _) => _HistoryMessage(
            key: const Key('account-history-page-error'),
            title: "Couldn't load your account history.",
            detail: '$error',
            onRefresh: () async =>
                ref.invalidate(tenantPortalAccountHistoryProvider(request)),
          ),
          data: (page) {
            if (page.items.any((entry) => entry.isFocused) &&
                !_focusedEntryScrolled) {
              _focusedEntryScrolled = true;
              WidgetsBinding.instance.addPostFrameCallback((_) {
                final focusedContext = _focusedEntryKey.currentContext;
                if (focusedContext != null) {
                  Scrollable.ensureVisible(
                    focusedContext,
                    duration: const Duration(milliseconds: 250),
                    alignment: 0.35,
                  );
                }
              });
            }
            return ListView(
              key: const Key('account-history-list'),
              padding: const EdgeInsets.symmetric(horizontal: 20),
              children: [
                if ((accountPage?.totalCount ?? 0) > 1) ...[
                  const SizedBox(height: 12),
                  _AccountPicker(
                    accounts: accountPage!.items,
                    totalCount: accountPage.totalCount,
                    skip: accountPage.skip,
                    take: accountPage.take,
                    selectedAccountId:
                        accountPage.items.any(
                          (account) => account.tenantAccountId == accountId,
                        )
                        ? accountId
                        : null,
                    embedded: true,
                    onPrevious: accountPage.skip == 0
                        ? null
                        : () => setState(() {
                            _accountSkip = (_accountSkip - _accountPageSize)
                                .clamp(0, accountPage.totalCount);
                          }),
                    onNext:
                        accountPage.skip + accountPage.items.length >=
                            accountPage.totalCount
                        ? null
                        : () =>
                              setState(() => _accountSkip += _accountPageSize),
                    onChanged: (value) => setState(() {
                      _selectedAccountId = value;
                      _skip = 0;
                    }),
                  ),
                ],
                const SizedBox(height: 24),
                Semantics(
                  header: true,
                  child: Text(
                    _money(page.currentDue, page.currency),
                    key: const Key('account-history-current-due'),
                    style: Theme.of(context).textTheme.displayMedium?.copyWith(
                      fontWeight: FontWeight.w600,
                      fontFeatures: const [FontFeature.tabularFigures()],
                    ),
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  page.currentDue < 0
                      ? 'Account credit — reduces what you owe next'
                      : 'Current due — includes unpaid rent, fees, and deposit '
                            'charges; held security deposits are not included',
                  style: Theme.of(context).textTheme.bodyMedium,
                ),
                const SizedBox(height: 24),
                DropdownButtonFormField<TenantAccountHistoryPeriod>(
                  key: const Key('account-history-period'),
                  initialValue: _period,
                  decoration: const InputDecoration(
                    labelText: 'Period',
                    border: OutlineInputBorder(),
                  ),
                  items: [
                    for (final value in TenantAccountHistoryPeriod.values)
                      DropdownMenuItem(value: value, child: Text(value.label)),
                  ],
                  onChanged: (value) {
                    if (value == null) return;
                    setState(() {
                      _period = value;
                      _skip = 0;
                    });
                  },
                ),
                const SizedBox(height: 16),
                autopay.when(
                  loading: () => const LinearProgressIndicator(
                    key: Key('account-history-autopay-loading'),
                  ),
                  error: (_, _) => const Text(
                    "Couldn't load autopay status.",
                    key: Key('account-history-autopay-error'),
                  ),
                  data: (status) => Row(
                    children: [
                      Expanded(
                        child: Text(
                          status.active ? 'Autopay is on' : 'Autopay is off',
                        ),
                      ),
                      if (!status.active)
                        TextButton(
                          key: const Key('account-history-autopay-setup'),
                          onPressed:
                              !status.onlinePaymentsAvailable || _autopayBusy
                              ? null
                              : () => _setUpAutopay(accountId),
                          child: Text(
                            _autopayBusy ? 'Opening…' : 'Set up autopay',
                          ),
                        ),
                    ],
                  ),
                ),
                const Divider(height: 32),
                _BalanceLine(
                  key: const Key('account-history-beginning-balance'),
                  label: 'Beginning balance',
                  value: _balanceMoney(page.beginningBalance, page.currency),
                ),
                const Divider(height: 1),
                for (final entry in page.items) ...[
                  _HistoryRow(
                    key: entry.isFocused ? _focusedEntryKey : null,
                    entry: entry,
                    currency: page.currency,
                    paying: _payingEntryId == entry.tenantLedgerEntryId,
                    onPay: entry.payable
                        ? () => _payNow(accountId, entry)
                        : null,
                  ),
                  const Divider(height: 1),
                ],
                if (page.items.isEmpty)
                  const Padding(
                    padding: EdgeInsets.symmetric(vertical: 32),
                    child: Text(
                      'No account activity in this period.',
                      key: Key('account-history-empty'),
                      textAlign: TextAlign.center,
                    ),
                  ),
                _BalanceLine(
                  key: const Key('account-history-closing-balance'),
                  label: 'Closing balance',
                  value: _balanceMoney(page.closingBalance, page.currency),
                ),
                if (page.totalCount > _pageSize) ...[
                  const SizedBox(height: 16),
                  Row(
                    mainAxisAlignment: MainAxisAlignment.spaceBetween,
                    children: [
                      OutlinedButton(
                        onPressed: _skip == 0
                            ? null
                            : () => setState(
                                () => _skip = (_skip - _pageSize).clamp(
                                  0,
                                  page.totalCount,
                                ),
                              ),
                        child: const Text('Previous'),
                      ),
                      Text(
                        '${_skip + 1}–'
                        '${(_skip + _pageSize).clamp(0, page.totalCount)} '
                        'of ${page.totalCount}',
                      ),
                      OutlinedButton(
                        onPressed: _skip + _pageSize >= page.totalCount
                            ? null
                            : () => setState(() => _skip += _pageSize),
                        child: const Text('Next'),
                      ),
                    ],
                  ),
                ],
                const SizedBox(height: 32),
              ],
            );
          },
        ),
      ),
    );
  }
}

class _HistoryRow extends StatelessWidget {
  const _HistoryRow({
    super.key,
    required this.entry,
    required this.currency,
    required this.paying,
    this.onPay,
  });

  final PortalTenantAccountHistoryItem entry;
  final String currency;
  final bool paying;
  final VoidCallback? onPay;

  @override
  Widget build(BuildContext context) {
    final description = entry.reversesEntryId != null
        ? '${entry.description.isEmpty ? 'Correction' : entry.description} · Reversal'
        : entry.reversedByEntryId != null
        ? '${entry.description} · Reversed'
        : entry.description.isEmpty
        ? tenantLedgerEntryLabel(entry.entryType)
        : entry.description;

    return Semantics(
      selected: entry.isFocused,
      child: DecoratedBox(
        key: Key('account-history-row-${entry.tenantLedgerEntryId}'),
        decoration: BoxDecoration(
          color: entry.isFocused
              ? Theme.of(context).colorScheme.primaryContainer.withAlpha(80)
              : null,
        ),
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 16),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      _shortDate(entry.effectiveOn),
                      style: Theme.of(context).textTheme.titleSmall,
                    ),
                    const SizedBox(height: 4),
                    Text(
                      description,
                      style: Theme.of(context).textTheme.bodyLarge,
                    ),
                    Text(
                      entry.displayType,
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: Theme.of(context).colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 12),
              Column(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  Text(
                    _signedMoney(entry.signedAmount, currency),
                    style: Theme.of(context).textTheme.titleSmall?.copyWith(
                      color: entry.signedAmount < 0
                          ? Theme.of(context).colorScheme.tertiary
                          : null,
                    ),
                  ),
                  const SizedBox(height: 4),
                  Text(
                    _balanceMoney(entry.runningBalance, currency),
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: Theme.of(context).colorScheme.onSurfaceVariant,
                    ),
                  ),
                  if (onPay != null)
                    TextButton(
                      key: Key(
                        'account-history-pay-${entry.tenantLedgerEntryId}',
                      ),
                      onPressed: paying ? null : onPay,
                      child: Text(paying ? 'Opening…' : 'Pay now'),
                    ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _BalanceLine extends StatelessWidget {
  const _BalanceLine({super.key, required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 16),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          Text(
            label,
            style: Theme.of(
              context,
            ).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w600),
          ),
          Text(
            value,
            style: Theme.of(
              context,
            ).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w600),
          ),
        ],
      ),
    );
  }
}

class _AccountPicker extends StatelessWidget {
  const _AccountPicker({
    required this.accounts,
    required this.totalCount,
    required this.skip,
    required this.take,
    required this.onChanged,
    required this.onPrevious,
    required this.onNext,
    this.selectedAccountId,
    this.embedded = false,
  });

  final List<PortalTenantAccount> accounts;
  final int totalCount;
  final int skip;
  final int take;
  final int? selectedAccountId;
  final ValueChanged<int?> onChanged;
  final VoidCallback? onPrevious;
  final VoidCallback? onNext;
  final bool embedded;

  @override
  Widget build(BuildContext context) {
    final picker = DropdownButtonFormField<int>(
      initialValue: selectedAccountId,
      decoration: const InputDecoration(
        labelText: 'Rental',
        border: OutlineInputBorder(),
      ),
      hint: const Text('Choose an account'),
      items: [
        for (final account in accounts)
          DropdownMenuItem(
            value: account.tenantAccountId,
            child: Text('${account.propertyName} · Unit ${account.unitNumber}'),
          ),
      ],
      onChanged: onChanged,
    );
    final content = Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        picker,
        if (totalCount > take) ...[
          const SizedBox(height: 8),
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              TextButton(onPressed: onPrevious, child: const Text('Previous')),
              Text(
                '${skip + 1}–${(skip + accounts.length).clamp(0, totalCount)} '
                'of $totalCount rentals',
              ),
              TextButton(onPressed: onNext, child: const Text('Next')),
            ],
          ),
        ],
      ],
    );
    if (embedded) return content;
    return Padding(padding: const EdgeInsets.all(20), child: content);
  }
}

class _HistoryMessage extends StatelessWidget {
  const _HistoryMessage({
    super.key,
    required this.title,
    this.detail,
    this.onRefresh,
  });

  final String title;
  final String? detail;
  final Future<void> Function()? onRefresh;

  @override
  Widget build(BuildContext context) {
    final content = ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SizedBox(height: 80),
        Text(
          title,
          textAlign: TextAlign.center,
          style: Theme.of(context).textTheme.titleMedium,
        ),
        if (detail != null) ...[
          const SizedBox(height: 8),
          Text(detail!, textAlign: TextAlign.center),
        ],
      ],
    );
    return onRefresh == null
        ? content
        : RefreshIndicator(onRefresh: onRefresh!, child: content);
  }
}

String _money(double value, String currency) =>
    '$currency ${value.abs().toStringAsFixed(2)}';

String _signedMoney(double value, String currency) =>
    value < 0 ? '−${_money(value, currency)}' : _money(value, currency);

String _balanceMoney(double value, String currency) =>
    value < 0 ? '(${_money(value, currency)})' : _money(value, currency);

String _shortDate(DateTime date) => '${date.month}/${date.day}/${date.year}';
