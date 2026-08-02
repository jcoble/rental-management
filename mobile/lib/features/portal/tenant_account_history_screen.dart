import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_exception.dart';
import '../../core/files/document_opener.dart';
import '../../core/theme/app_recipes.dart';
import '../money/money_format.dart';
import 'tenant_portal_repository.dart';

typedef TenantStatementSharer =
    Future<void> Function({
      required Uint8List bytes,
      required String fileName,
      String mimeType,
    });

final tenantStatementSharerProvider = Provider<TenantStatementSharer>(
  (ref) => DocumentOpener.shareBytes,
);

/// Tenant-facing account history. Balances, ordering, and paging are server
/// read-model fields; this screen only formats them for the tenant.
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
  bool _sharing = false;
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
      _showMessage(
        error.statusCode == 503
            ? "Online payments aren't set up yet."
            : error.message,
      );
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
      _showMessage(
        error.statusCode == 503
            ? "Autopay isn't available right now."
            : error.message,
      );
    } finally {
      if (mounted) setState(() => _autopayBusy = false);
    }
  }

  Future<void> _shareStatement(
    PortalTenantAccount account,
    PortalTenantAccountHistory history,
  ) async {
    if (_sharing) return;
    setState(() => _sharing = true);
    try {
      final html = tenantPortalStatementHtml(
        account: account,
        history: history,
      );
      await ref.read(tenantStatementSharerProvider)(
        bytes: Uint8List.fromList(utf8.encode(html)),
        fileName: 'tenant-account-statement.html',
        mimeType: 'text/html',
      );
      _showMessage('Statement ready to print or share.');
    } catch (_) {
      _showMessage("Couldn't prepare the statement. Please try again.");
    } finally {
      if (mounted) setState(() => _sharing = false);
    }
  }

  Future<void> _refreshAccounts(TenantAccountListPageRequest request) async {
    ref.invalidate(tenantPortalAccountsPageProvider(request));
  }

  Future<void> _refreshAccount(
    int accountId,
    TenantAccountListPageRequest accountRequest,
    TenantAccountHistoryRequest historyRequest,
  ) async {
    ref.invalidate(tenantPortalAccountsPageProvider(accountRequest));
    ref.invalidate(tenantPortalAccountProvider(accountId));
    ref.invalidate(tenantPortalAccountHistoryProvider(historyRequest));
    ref.invalidate(tenantAutopayStatusProvider(accountId));
  }

  void _showMessage(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(
      context,
    ).showSnackBar(SnackBar(content: Text(message)));
  }

  void _scheduleFocusedEntryScroll(PortalTenantAccountHistory history) {
    if (_focusedEntryScrolled ||
        !history.items.any((entry) => entry.isFocused)) {
      return;
    }
    _focusedEntryScrolled = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
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
        appBar: AppBar(title: const Text('Your account')),
        body: RefreshIndicator(
          onRefresh: () => _refreshAccounts(accountRequest),
          child: accounts.when(
            loading: () =>
                const _PortalLoadingState(key: Key('account-history-loading')),
            error: (_, _) => _PortalErrorState(
              key: const Key('account-history-error'),
              title: "Couldn't load your account.",
              onRetry: () => _refreshAccounts(accountRequest),
            ),
            data: (page) {
              if (page.totalCount == 0) {
                return const _PortalMessage(
                  key: Key('account-history-empty-account'),
                  title: 'No account history yet.',
                );
              }
              return ListView(
                padding: const EdgeInsets.all(20),
                children: [
                  _AccountPicker(
                    accounts: page.items,
                    totalCount: page.totalCount,
                    skip: page.skip,
                    take: page.take,
                    onPrevious: page.skip == 0
                        ? null
                        : () => setState(() {
                            _accountSkip = (_accountSkip - _accountPageSize)
                                .clamp(0, page.totalCount)
                                .toInt();
                          }),
                    onNext: page.skip + page.items.length >= page.totalCount
                        ? null
                        : () =>
                              setState(() => _accountSkip += _accountPageSize),
                    onChanged: (value) => setState(() {
                      _selectedAccountId = value;
                      _skip = 0;
                      _focusedEntryScrolled = false;
                    }),
                  ),
                ],
              );
            },
          ),
        ),
      );
    }

    final historyRequest = (
      tenantAccountId: accountId,
      period: _period,
      skip: _skip,
      take: _pageSize,
      focusedEntryId: widget.initialTenantLedgerEntryId,
    );
    final account = ref.watch(tenantPortalAccountProvider(accountId));
    final history = ref.watch(
      tenantPortalAccountHistoryProvider(historyRequest),
    );

    return Scaffold(
      appBar: AppBar(title: const Text('Your account')),
      body: RefreshIndicator(
        onRefresh: () =>
            _refreshAccount(accountId, accountRequest, historyRequest),
        child: account.when(
          loading: () => const _PortalLoadingState(
            key: Key('account-history-page-loading'),
          ),
          error: (_, _) => _PortalErrorState(
            key: const Key('account-history-page-error'),
            title: "Couldn't load your account.",
            onRetry: () =>
                ref.invalidate(tenantPortalAccountProvider(accountId)),
          ),
          data: (account) => history.when(
            loading: () => const _PortalLoadingState(
              key: Key('account-history-history-loading'),
            ),
            error: (_, _) => _PortalErrorState(
              key: const Key('account-history-history-error'),
              title: "Couldn't load your account history.",
              onRetry: () => ref.invalidate(
                tenantPortalAccountHistoryProvider(historyRequest),
              ),
            ),
            data: (page) {
              _scheduleFocusedEntryScroll(page);
              final selectedAccountInPage =
                  accountPage?.items.any(
                    (item) => item.tenantAccountId == accountId,
                  ) ??
                  false;
              return _AccountHistoryContent(
                account: account,
                history: page,
                accountPage: accountPage,
                accountId: accountId,
                selectedAccountId: selectedAccountInPage ? accountId : null,
                accountSkip: _accountSkip,
                accountPageSize: _accountPageSize,
                pageSize: _pageSize,
                period: _period,
                skip: _skip,
                payingEntryId: _payingEntryId,
                autopayBusy: _autopayBusy,
                sharing: _sharing,
                focusedEntryKey: _focusedEntryKey,
                onAccountPrevious: accountPage == null || accountPage.skip == 0
                    ? null
                    : () => setState(() {
                        _accountSkip = (_accountSkip - _accountPageSize)
                            .clamp(0, accountPage.totalCount)
                            .toInt();
                      }),
                onAccountNext:
                    accountPage == null ||
                        accountPage.skip + accountPage.items.length >=
                            accountPage.totalCount
                    ? null
                    : () => setState(() => _accountSkip += _accountPageSize),
                onAccountChanged: (value) => setState(() {
                  _selectedAccountId = value;
                  _skip = 0;
                  _focusedEntryScrolled = false;
                }),
                onPeriodChanged: (value) {
                  if (value == null) return;
                  setState(() {
                    _period = value;
                    _skip = 0;
                    _focusedEntryScrolled = false;
                  });
                },
                onPrevious: _skip == 0
                    ? null
                    : () => setState(() {
                        _skip = (_skip - _pageSize)
                            .clamp(0, page.totalCount)
                            .toInt();
                      }),
                onNext: _skip + _pageSize >= page.totalCount
                    ? null
                    : () => setState(() => _skip += _pageSize),
                onPay: (entry) => _payNow(accountId, entry),
                onAutopaySetup: () => _setUpAutopay(accountId),
                onShare: () => _shareStatement(account, page),
              );
            },
          ),
        ),
      ),
    );
  }
}

class _AccountHistoryContent extends StatelessWidget {
  const _AccountHistoryContent({
    required this.account,
    required this.history,
    required this.accountPage,
    required this.accountId,
    required this.selectedAccountId,
    required this.accountSkip,
    required this.accountPageSize,
    required this.pageSize,
    required this.period,
    required this.skip,
    required this.payingEntryId,
    required this.autopayBusy,
    required this.sharing,
    required this.focusedEntryKey,
    required this.onAccountPrevious,
    required this.onAccountNext,
    required this.onAccountChanged,
    required this.onPeriodChanged,
    required this.onPrevious,
    required this.onNext,
    required this.onPay,
    required this.onAutopaySetup,
    required this.onShare,
  });

  final PortalTenantAccount account;
  final PortalTenantAccountHistory history;
  final PortalTenantAccountPage? accountPage;
  final int accountId;
  final int? selectedAccountId;
  final int accountSkip;
  final int accountPageSize;
  final int pageSize;
  final TenantAccountHistoryPeriod period;
  final int skip;
  final int? payingEntryId;
  final bool autopayBusy;
  final bool sharing;
  final GlobalKey focusedEntryKey;
  final VoidCallback? onAccountPrevious;
  final VoidCallback? onAccountNext;
  final ValueChanged<int?> onAccountChanged;
  final ValueChanged<TenantAccountHistoryPeriod?> onPeriodChanged;
  final VoidCallback? onPrevious;
  final VoidCallback? onNext;
  final ValueChanged<PortalTenantAccountHistoryItem> onPay;
  final VoidCallback onAutopaySetup;
  final VoidCallback onShare;

  @override
  Widget build(BuildContext context) {
    final page = accountPage;
    return ListView(
      key: const Key('account-history-list'),
      padding: const EdgeInsets.fromLTRB(20, 12, 20, 32),
      children: [
        if ((page?.totalCount ?? 0) > 1) ...[
          _AccountPicker(
            accounts: page!.items,
            totalCount: page.totalCount,
            skip: accountSkip,
            take: accountPageSize,
            selectedAccountId: selectedAccountId,
            embedded: true,
            onPrevious: onAccountPrevious,
            onNext: onAccountNext,
            onChanged: onAccountChanged,
          ),
          const SizedBox(height: 20),
        ],
        _BalanceHeader(account: account),
        const SizedBox(height: 16),
        Row(
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(
              child: DropdownButtonFormField<TenantAccountHistoryPeriod>(
                key: const Key('account-history-period'),
                initialValue: period,
                decoration: const InputDecoration(
                  labelText: 'Statement period',
                  border: OutlineInputBorder(),
                ),
                items: [
                  for (final value in TenantAccountHistoryPeriod.values)
                    DropdownMenuItem(value: value, child: Text(value.label)),
                ],
                onChanged: onPeriodChanged,
              ),
            ),
            const SizedBox(width: 12),
            IconButton.filledTonal(
              key: const Key('tenant-portal-share-statement'),
              onPressed: sharing ? null : onShare,
              tooltip: 'Print or share statement',
              icon: sharing
                  ? const SizedBox.square(
                      dimension: 20,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.ios_share_outlined),
            ),
          ],
        ),
        const SizedBox(height: 16),
        _AutopayPanel(
          tenantAccountId: accountId,
          busy: autopayBusy,
          onSetup: onAutopaySetup,
        ),
        const SizedBox(height: 24),
        Text(
          'Account history',
          key: const Key('account-history-heading'),
          style: Theme.of(
            context,
          ).textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w700),
        ),
        const SizedBox(height: 12),
        if (history.items.isEmpty)
          const _PortalMessage(
            key: Key('account-history-empty'),
            title:
                'No charges or payments yet. Record the first payment or charge above.',
          )
        else
          for (var index = 0; index < history.items.length; index++) ...[
            _HistoryCard(
              focusKey: history.items[index].isFocused ? focusedEntryKey : null,
              entry: history.items[index],
              currency: history.currency,
              paying: payingEntryId == history.items[index].tenantLedgerEntryId,
              onPay: history.items[index].payable
                  ? () => onPay(history.items[index])
                  : null,
            ),
            if (index < history.items.length - 1) const SizedBox(height: 8),
          ],
        const SizedBox(height: 16),
        _ServerBalanceLine(
          key: const Key('account-history-closing-balance'),
          label: 'Balance after this period',
          value: _signedMoney(history.closingBalance, history.currency),
        ),
        if (history.totalCount > pageSize) ...[
          const SizedBox(height: 16),
          _HistoryPagination(
            skip: skip,
            pageSize: pageSize,
            totalCount: history.totalCount,
            onPrevious: onPrevious,
            onNext: onNext,
          ),
        ],
      ],
    );
  }
}

class _BalanceHeader extends StatelessWidget {
  const _BalanceHeader({required this.account});

  final PortalTenantAccount account;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return M3TonalCard(
      key: const Key('tenant-portal-balance-header'),
      family: M3TonalFamily.violet,
      padding: const EdgeInsets.all(20),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            '${account.propertyName} · Unit ${account.unitNumber}',
            style: theme.textTheme.labelLarge?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 12),
          Text(
            'Current balance',
            style: theme.textTheme.titleMedium?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 2),
          Semantics(
            header: true,
            child: Text(
              _signedMoney(account.receivableBalance, account.currency),
              key: const Key('account-history-current-due'),
              style: theme.textTheme.headlineMedium?.copyWith(
                fontWeight: FontWeight.w700,
                fontFeatures: const [FontFeature.tabularFigures()],
              ),
            ),
          ),
          const SizedBox(height: 20),
          GridView.count(
            crossAxisCount: 2,
            crossAxisSpacing: 12,
            mainAxisSpacing: 16,
            childAspectRatio: 2.15,
            shrinkWrap: true,
            physics: const NeverScrollableScrollPhysics(),
            children: [
              _BalanceMetric(
                key: const Key('tenant-portal-past-due'),
                label: 'Past due',
                value: _signedMoney(account.pastDueAmount, account.currency),
              ),
              _BalanceMetric(
                key: const Key('tenant-portal-next-due'),
                label: 'Next due',
                value: account.nextDueOn == null
                    ? '—'
                    : _signedMoney(account.nextDueAmount, account.currency),
                detail: account.nextDueOn == null
                    ? null
                    : _shortDate(account.nextDueOn!),
              ),
              _BalanceMetric(
                key: const Key('tenant-portal-deposit-held'),
                label: 'Deposit held',
                value: account.deposit == null
                    ? '—'
                    : _signedMoney(
                        account.deposit!.heldBalance,
                        account.deposit!.currency,
                      ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

class _BalanceMetric extends StatelessWidget {
  const _BalanceMetric({
    super.key,
    required this.label,
    required this.value,
    this.detail,
  });

  final String label;
  final String value;
  final String? detail;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        Text(
          label,
          style: theme.textTheme.labelMedium?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: 2),
        Text(
          value,
          style: theme.textTheme.titleMedium?.copyWith(
            fontWeight: FontWeight.w700,
            fontFeatures: const [FontFeature.tabularFigures()],
          ),
        ),
        if (detail != null)
          Text(
            detail!,
            style: theme.textTheme.bodySmall?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
      ],
    );
  }
}

class _HistoryCard extends StatelessWidget {
  const _HistoryCard({
    this.focusKey,
    required this.entry,
    required this.currency,
    required this.paying,
    this.onPay,
  });

  final GlobalKey? focusKey;
  final PortalTenantAccountHistoryItem entry;
  final String currency;
  final bool paying;
  final VoidCallback? onPay;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    return KeyedSubtree(
      key: Key('account-history-row-${entry.tenantLedgerEntryId}'),
      child: KeyedSubtree(
        key: focusKey,
        child: M3TonalCard(
          family: _historyFamily(entry.entryType),
          padding: const EdgeInsets.all(16),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      tenantPortalLedgerLabel(entry),
                      key: Key(
                        'tenant-portal-history-label-${entry.tenantLedgerEntryId}',
                      ),
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      _shortDate(entry.effectiveOn),
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color: scheme.onSurfaceVariant,
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
                    key: Key(
                      'tenant-portal-history-amount-${entry.tenantLedgerEntryId}',
                    ),
                    style: theme.textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                      color: entry.signedAmount < 0 ? scheme.tertiary : null,
                      fontFeatures: const [FontFeature.tabularFigures()],
                    ),
                  ),
                  const SizedBox(height: 4),
                  Text(
                    'Balance ${_signedMoney(entry.runningBalance, currency)}',
                    key: Key(
                      'tenant-portal-history-balance-${entry.tenantLedgerEntryId}',
                    ),
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: scheme.onSurfaceVariant,
                      fontFeatures: const [FontFeature.tabularFigures()],
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

class _AutopayPanel extends ConsumerWidget {
  const _AutopayPanel({
    required this.tenantAccountId,
    required this.busy,
    required this.onSetup,
  });

  final int tenantAccountId;
  final bool busy;
  final VoidCallback onSetup;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final status = ref.watch(tenantAutopayStatusProvider(tenantAccountId));
    return status.when(
      loading: () => const LinearProgressIndicator(
        key: Key('account-history-autopay-loading'),
      ),
      error: (_, _) => const Text(
        "Couldn't load autopay settings.",
        key: Key('account-history-autopay-error'),
      ),
      data: (value) => M3TonalCard(
        family: M3TonalFamily.sky,
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
        child: Row(
          children: [
            const Icon(Icons.autorenew_outlined),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                value.active
                    ? 'Autopay is on'
                    : 'Pay rent automatically each month.',
              ),
            ),
            if (!value.active)
              TextButton(
                key: const Key('account-history-autopay-setup'),
                onPressed: !value.onlinePaymentsAvailable || busy
                    ? null
                    : onSetup,
                child: Text(busy ? 'Opening…' : 'Set up'),
              ),
          ],
        ),
      ),
    );
  }
}

class _ServerBalanceLine extends StatelessWidget {
  const _ServerBalanceLine({
    super.key,
    required this.label,
    required this.value,
  });

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        Text(
          label,
          style: theme.textTheme.titleMedium?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        Text(
          value,
          style: theme.textTheme.titleMedium?.copyWith(
            fontWeight: FontWeight.w700,
            fontFeatures: const [FontFeature.tabularFigures()],
          ),
        ),
      ],
    );
  }
}

class _HistoryPagination extends StatelessWidget {
  const _HistoryPagination({
    required this.skip,
    required this.pageSize,
    required this.totalCount,
    required this.onPrevious,
    required this.onNext,
  });

  final int skip;
  final int pageSize;
  final int totalCount;
  final VoidCallback? onPrevious;
  final VoidCallback? onNext;

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        OutlinedButton(onPressed: onPrevious, child: const Text('Previous')),
        Text(
          '${skip + 1}–${(skip + pageSize).clamp(0, totalCount)} of $totalCount',
        ),
        OutlinedButton(onPressed: onNext, child: const Text('Next')),
      ],
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

class _PortalLoadingState extends StatelessWidget {
  const _PortalLoadingState({super.key});

  @override
  Widget build(BuildContext context) {
    final color = Theme.of(context).colorScheme.surfaceContainerHighest;
    return ListView(
      padding: const EdgeInsets.fromLTRB(20, 20, 20, 32),
      children: [
        _SkeletonBlock(color: color, height: 190),
        const SizedBox(height: 16),
        _SkeletonBlock(color: color, height: 56),
        const SizedBox(height: 24),
        for (var index = 0; index < 3; index++) ...[
          _SkeletonBlock(color: color, height: 94),
          if (index < 2) const SizedBox(height: 8),
        ],
      ],
    );
  }
}

class _SkeletonBlock extends StatelessWidget {
  const _SkeletonBlock({required this.color, required this.height});

  final Color color;
  final double height;

  @override
  Widget build(BuildContext context) {
    return DecoratedBox(
      decoration: BoxDecoration(
        color: color,
        borderRadius: BorderRadius.circular(28),
      ),
      child: SizedBox(height: height),
    );
  }
}

class _PortalErrorState extends StatelessWidget {
  const _PortalErrorState({
    super.key,
    required this.title,
    required this.onRetry,
  });

  final String title;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.fromLTRB(24, 96, 24, 24),
      children: [
        Text(title, textAlign: TextAlign.center),
        const SizedBox(height: 12),
        Center(
          child: OutlinedButton(
            onPressed: onRetry,
            child: const Text('Try again'),
          ),
        ),
      ],
    );
  }
}

class _PortalMessage extends StatelessWidget {
  const _PortalMessage({super.key, required this.title});

  final String title;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 32),
      child: Text(title, textAlign: TextAlign.center),
    );
  }
}

/// Maps wire entry types to the tenant-only vocabulary allowed in the portal.
String tenantPortalLedgerLabel(PortalTenantAccountHistoryItem entry) {
  return switch (entry.entryType.trim()) {
    'RentCharge' ||
    'AddendumCharge' ||
    'DepositCharge' ||
    'ManualCharge' ||
    'OpeningBalance' => 'Rent charge',
    'LateFeeCharge' => 'Late fee',
    'PaymentReceipt' => 'Payment received — thank you',
    'Credit' || 'TransferIn' => 'Credit',
    'Refund' => 'Refund',
    _ => 'Correction',
  };
}

String tenantPortalStatementHtml({
  required PortalTenantAccount account,
  required PortalTenantAccountHistory history,
}) {
  const escape = HtmlEscape(HtmlEscapeMode.element);
  final statement = StringBuffer()
    ..writeln('<!doctype html>')
    ..writeln('<html><head><meta charset="utf-8">')
    ..writeln('<meta name="viewport" content="width=device-width">')
    ..writeln('<title>Account statement</title>')
    ..writeln(
      '<style>body{font-family:Arial,sans-serif;color:#202124;margin:32px;}'
      'h1{margin-bottom:4px;}p{color:#5f6368;}table{border-collapse:collapse;'
      'width:100%;margin-top:24px;}th,td{border-bottom:1px solid #dadce0;'
      'padding:10px 6px;text-align:left;}th:nth-child(n+3),td:nth-child(n+3)'
      '{text-align:right;}dt{color:#5f6368;margin-top:12px;}dd{margin:2px 0;'
      'font-weight:700;}</style></head><body>',
    )
    ..writeln('<h1>Account statement</h1>')
    ..writeln(
      '<p>${escape.convert(account.propertyName)} · Unit '
      '${escape.convert(account.unitNumber)}</p>',
    )
    ..writeln('<dl>')
    ..writeln(
      '<dt>Current balance</dt><dd>${escape.convert(_signedMoney(account.receivableBalance, account.currency))}</dd>',
    )
    ..writeln(
      '<dt>Past due</dt><dd>${escape.convert(_signedMoney(account.pastDueAmount, account.currency))}</dd>',
    )
    ..writeln(
      '<dt>Next due</dt><dd>${account.nextDueOn == null ? '—' : escape.convert(_signedMoney(account.nextDueAmount, account.currency))}</dd>',
    )
    ..writeln(
      '<dt>Deposit held</dt><dd>${account.deposit == null ? '—' : escape.convert(_signedMoney(account.deposit!.heldBalance, account.deposit!.currency))}</dd>',
    )
    ..writeln('</dl>')
    ..writeln(
      '<p>${history.periodFrom == null ? 'Account opening' : _shortDate(history.periodFrom!)} – ${_shortDate(history.periodTo)}</p>',
    )
    ..writeln(
      '<table><thead><tr><th>Date</th><th>What happened</th><th>Amount</th><th>Balance</th></tr></thead><tbody>',
    );

  if (history.items.isEmpty) {
    statement.writeln(
      '<tr><td colspan="4">No charges or payments yet.</td></tr>',
    );
  } else {
    for (final entry in history.items) {
      statement.writeln(
        '<tr><td>${_shortDate(entry.effectiveOn)}</td>'
        '<td>${tenantPortalLedgerLabel(entry)}</td>'
        '<td>${escape.convert(_signedMoney(entry.signedAmount, history.currency))}</td>'
        '<td>${escape.convert(_signedMoney(entry.runningBalance, history.currency))}</td></tr>',
      );
    }
  }

  statement
    ..writeln('</tbody></table>')
    ..writeln(
      '<p>Balance after this period: '
      '${escape.convert(_signedMoney(history.closingBalance, history.currency))}</p>',
    )
    ..writeln('</body></html>');
  return statement.toString();
}

M3TonalFamily _historyFamily(String entryType) {
  return switch (entryType.trim()) {
    'PaymentReceipt' || 'Credit' || 'TransferIn' => M3TonalFamily.mint,
    'LateFeeCharge' => M3TonalFamily.amber,
    'Refund' => M3TonalFamily.sky,
    'Adjustment' || 'Reversal' || 'TransferOut' => M3TonalFamily.rose,
    _ => M3TonalFamily.violet,
  };
}

String _signedMoney(num value, String currency) {
  final amount = moneyFmt(value.abs());
  final display = currency.trim().isEmpty
      ? amount
      : '${currency.trim()} ${amount.substring(1)}';
  return value < 0 ? '−$display' : display;
}

String _shortDate(DateTime date) => dateFmt(date);
