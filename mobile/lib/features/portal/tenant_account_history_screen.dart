import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_exception.dart';
import '../money/widgets/ledger_type_badge.dart';
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
    final range = _portalRange(_period);
    final ledgerRequest = (
      tenantAccountId: accountId,
      from: range.$1,
      to: range.$2,
    );
    final ledger = ref.watch(tenantPortalLedgerProvider(ledgerRequest));
    final monthSummaries = ref.watch(
      tenantPortalMonthSummaryProvider(ledgerRequest),
    );
    final autopay = ref.watch(tenantAutopayStatusProvider(accountId));

    return Scaffold(
      appBar: AppBar(title: const Text('Account history')),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(tenantPortalAccountsPageProvider(accountRequest));
          ref.invalidate(tenantPortalAccountHistoryProvider(request));
          ref.invalidate(tenantPortalLedgerProvider(ledgerRequest));
          ref.invalidate(tenantPortalMonthSummaryProvider(ledgerRequest));
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
                ledger.when(
                  loading: () =>
                      const Center(child: CircularProgressIndicator()),
                  error: (error, _) =>
                      Text("Couldn't load account activity: $error"),
                  data: (rows) => monthSummaries.when(
                    loading: () =>
                        const Center(child: CircularProgressIndicator()),
                    error: (error, _) =>
                        Text("Couldn't load monthly balances: $error"),
                    data: (summaries) => Column(
                      children: [
                        for (final summary in summaries)
                          _PortalMonthGroup(
                            summary: summary,
                            rows: rows
                                .where(
                                  (row) =>
                                      row.effectiveOn.year == summary.year &&
                                      row.effectiveOn.month == summary.month,
                                )
                                .toList(growable: false),
                            onTap: (row) => Navigator.of(context).push<void>(
                              MaterialPageRoute(
                                builder: (_) => _PortalLedgerDetailScreen(
                                  tenantAccountId: accountId,
                                  entryId: row.tenantLedgerEntryId,
                                ),
                              ),
                            ),
                          ),
                        if (summaries.isEmpty)
                          const Padding(
                            padding: EdgeInsets.symmetric(vertical: 32),
                            child: Text('No account activity in this period.'),
                          ),
                      ],
                    ),
                  ),
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

(String, String) _portalRange(TenantAccountHistoryPeriod period) {
  final now = DateTime.now().toUtc();
  final from = switch (period) {
    TenantAccountHistoryPeriod.currentMonth => DateTime.utc(
      now.year,
      now.month,
      1,
    ),
    TenantAccountHistoryPeriod.previousMonth => DateTime.utc(
      now.year,
      now.month - 1,
      1,
    ),
    TenantAccountHistoryPeriod.last3Months => DateTime.utc(
      now.year,
      now.month - 2,
      1,
    ),
    TenantAccountHistoryPeriod.thisYear => DateTime.utc(now.year, 1, 1),
    TenantAccountHistoryPeriod.all => DateTime.utc(2000, 1, 1),
  };
  final to = period == TenantAccountHistoryPeriod.previousMonth
      ? DateTime.utc(now.year, now.month, 0)
      : DateTime.utc(now.year, now.month + 1, 0);
  String date(DateTime value) =>
      '${value.year.toString().padLeft(4, '0')}-${value.month.toString().padLeft(2, '0')}-${value.day.toString().padLeft(2, '0')}';
  return (date(from), date(to));
}

class _PortalMonthGroup extends StatelessWidget {
  const _PortalMonthGroup({
    required this.summary,
    required this.rows,
    required this.onTap,
  });
  final PortalTenantMonthSummary summary;
  final List<PortalTenantLedgerRow> rows;
  final ValueChanged<PortalTenantLedgerRow> onTap;
  @override
  Widget build(BuildContext context) => Card.outlined(
    child: Column(
      children: [
        ListTile(
          title: Text('${_monthNames[summary.month - 1]} ${summary.year}'),
          subtitle: Text(
            'Opening amount owed ${_balanceMoney(summary.openingBalance, summary.currency)}',
          ),
        ),
        for (final row in rows)
          ListTile(
            onTap: () => onTap(row),
            leading: LedgerTypeBadge(type: row.type),
            title: Text(row.description),
            subtitle: Text(
              'Effective ${_shortDate(row.effectiveOn)} · Entered ${_shortDate(row.postedAtUtc)}',
            ),
            trailing: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                Text(
                  _signedMoney(
                    row.chargeAmount != 0
                        ? row.chargeAmount
                        : row.paymentAmount != 0
                        ? -row.paymentAmount
                        : -row.creditAmount,
                    row.currency,
                  ),
                ),
                Text(
                  'Owed ${_balanceMoney(row.runningAmountOwed, row.currency)}',
                ),
              ],
            ),
          ),
        const Divider(),
        Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            children: [
              _PortalTotal(
                label: 'Charges',
                value: _money(summary.chargeAmount, summary.currency),
              ),
              _PortalTotal(
                label: 'Payments',
                value: _money(summary.paymentAmount, summary.currency),
              ),
              _PortalTotal(
                label: 'Credits',
                value: _money(summary.creditAmount, summary.currency),
              ),
              _PortalTotal(
                label: 'Closing amount owed',
                value: _balanceMoney(summary.closingBalance, summary.currency),
              ),
            ],
          ),
        ),
      ],
    ),
  );
}

class _PortalTotal extends StatelessWidget {
  const _PortalTotal({required this.label, required this.value});
  final String label, value;
  @override
  Widget build(BuildContext context) => Row(
    mainAxisAlignment: MainAxisAlignment.spaceBetween,
    children: [Text(label), Text(value)],
  );
}

class _PortalLedgerDetailScreen extends ConsumerWidget {
  const _PortalLedgerDetailScreen({
    required this.tenantAccountId,
    required this.entryId,
  });
  final int tenantAccountId, entryId;
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final row = ref.watch(
      tenantPortalLedgerEntryProvider((
        tenantAccountId: tenantAccountId,
        entryId: entryId,
      )),
    );
    return Scaffold(
      appBar: AppBar(title: const Text('Money details')),
      body: row.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (error, _) =>
            Center(child: Text("Couldn't load money details: $error")),
        data: (entry) => ListView(
          padding: const EdgeInsets.all(16),
          children: [
            LedgerTypeBadge(type: entry.type),
            const SizedBox(height: 12),
            Text(
              entry.description,
              style: Theme.of(context).textTheme.headlineSmall,
            ),
            ListTile(
              title: const Text('Effective date'),
              subtitle: Text(_shortDate(entry.effectiveOn)),
            ),
            ListTile(
              title: const Text('Entered date'),
              subtitle: Text(_shortDate(entry.postedAtUtc)),
            ),
            if (entry.paymentMethod != null)
              ListTile(
                title: const Text('Payment method'),
                subtitle: Text(entry.paymentMethod!),
              ),
            if (entry.reference != null)
              ListTile(
                title: const Text('Receipt reference'),
                subtitle: Text(entry.reference!),
              ),
            if (entry.sourceDocumentContext != null)
              ListTile(
                title: const Text('Receipt or document'),
                subtitle: Text(entry.sourceDocumentContext!),
              ),
            if (entry.allocations.isNotEmpty) ...[
              Text(
                'Applied to',
                style: Theme.of(context).textTheme.titleMedium,
              ),
              for (final allocation in entry.allocations)
                ListTile(
                  title: Text(allocation.targetDescription),
                  subtitle: Text(_shortDate(allocation.effectiveOn)),
                  trailing: Text(_money(allocation.amount, entry.currency)),
                ),
            ],
            ListTile(
              title: const Text('Amount owed after this item'),
              trailing: Text(
                _balanceMoney(entry.runningAmountOwed, entry.currency),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

const _monthNames = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
];

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
