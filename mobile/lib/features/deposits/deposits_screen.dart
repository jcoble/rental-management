import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:uuid/uuid.dart';

import '../../core/api/api_exception.dart';
import '../../core/files/document_opener.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import '../accounting/accounting_book_models.dart';
import '../accounting/accounting_impact_card.dart';
import 'deposits_repository.dart';

String _fmtCurrency(double amount, [String currency = 'USD']) {
  final value = amount.toStringAsFixed(2);
  return currency == 'USD' ? '\$$value' : '$value $currency';
}

String _fmtDate(DateTime value) {
  const months = [
    '',
    'Jan',
    'Feb',
    'Mar',
    'Apr',
    'May',
    'Jun',
    'Jul',
    'Aug',
    'Sep',
    'Oct',
    'Nov',
    'Dec',
  ];
  return '${months[value.month]} ${value.day}, ${value.year}';
}

String _location(TenantAccountDeposit account) {
  final property = account.propertyName.trim();
  final unit = account.unitNumber.trim();
  if (property.isNotEmpty && unit.isNotEmpty) {
    return '$property · Unit $unit';
  }
  if (property.isNotEmpty) return property;
  if (unit.isNotEmpty) return 'Unit $unit';
  return 'Property ${account.propertyId} · Unit ${account.unitId}';
}

class DepositsScreen extends ConsumerStatefulWidget {
  const DepositsScreen({super.key});

  @override
  ConsumerState<DepositsScreen> createState() => _DepositsScreenState();
}

class _DepositsScreenState extends ConsumerState<DepositsScreen> {
  final _searchController = TextEditingController();
  Timer? _searchDebounce;
  String? _status;
  bool _loadingMore = false;

  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(depositsProvider.notifier).load());
  }

  @override
  void dispose() {
    _searchDebounce?.cancel();
    _searchController.dispose();
    super.dispose();
  }

  void _search(String value) {
    _searchDebounce?.cancel();
    _searchDebounce = Timer(const Duration(milliseconds: 350), () {
      ref.read(depositsProvider.notifier).load(search: value, status: _status);
    });
  }

  void _filterStatus(String? status) {
    setState(() => _status = status);
    ref
        .read(depositsProvider.notifier)
        .load(search: _searchController.text, status: status);
  }

  Future<void> _refresh() => ref.read(depositsProvider.notifier).refresh();

  Future<void> _loadMore() async {
    if (_loadingMore) return;
    setState(() => _loadingMore = true);
    try {
      await ref.read(depositsProvider.notifier).loadMore();
    } finally {
      if (mounted) {
        setState(() => _loadingMore = false);
      }
    }
  }

  void _showFundSheet({TenantAccountDeposit? account}) {
    final accounts =
        ref.read(depositsProvider).value?.items ??
        const <TenantAccountDeposit>[];
    if (accounts.isEmpty) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text(
            'A deposit account is created while preparing a tenant move-in.',
          ),
        ),
      );
      return;
    }
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => _FundDepositSheet(
        accounts: accounts,
        initialAccount: account,
        onSaved: _refresh,
      ),
    );
  }

  void _showDeductionSheet(TenantAccountDeposit account) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => _DeductDepositSheet(account: account, onSaved: _refresh),
    );
  }

  void _showRefundSheet(TenantAccountDeposit account) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => _RefundDepositSheet(account: account, onSaved: _refresh),
    );
  }

  Future<void> _showDetail(TenantAccountDeposit summary) async {
    final messenger = ScaffoldMessenger.of(context);
    TenantAccountDeposit account;
    try {
      account = await ref
          .read(depositsRepositoryProvider)
          .getDeposit(summary.tenantAccountId);
    } on ApiException catch (error) {
      if (mounted) {
        messenger.showSnackBar(SnackBar(content: Text(error.message)));
      }
      return;
    }
    if (!mounted) return;
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (sheetContext) => _DepositDetailSheet(
        account: account,
        onFund: () {
          Navigator.of(sheetContext).pop();
          _showFundSheet(account: account);
        },
        onDeduct: () {
          Navigator.of(sheetContext).pop();
          _showDeductionSheet(account);
        },
        onRefund: () {
          Navigator.of(sheetContext).pop();
          _showRefundSheet(account);
        },
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(depositsProvider);
    return Scaffold(
      appBar: mobileDomainRootAppBar(
        context,
        title: const Text('Security Deposits'),
      ),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'deposits-fab',
        primaryAction: MobileQuickAction(
          label: 'Fund deposit',
          icon: Icons.add_card_outlined,
          onPressed: () => _showFundSheet(),
        ),
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: CustomScrollView(
          physics: const AlwaysScrollableScrollPhysics(),
          slivers: [
            SliverToBoxAdapter(
              child: Padding(
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 8),
                child: Row(
                  children: [
                    Expanded(
                      child: SearchBar(
                        controller: _searchController,
                        leading: const Icon(Icons.search),
                        hintText: 'Search deposits',
                        onChanged: _search,
                      ),
                    ),
                    const SizedBox(width: 8),
                    PopupMenuButton<String>(
                      tooltip: 'Filter by status',
                      icon: Icon(
                        Icons.filter_list,
                        color: _status == null
                            ? null
                            : Theme.of(context).colorScheme.primary,
                      ),
                      onSelected: (value) =>
                          _filterStatus(value.isEmpty ? null : value),
                      itemBuilder: (_) => const [
                        PopupMenuItem(value: '', child: Text('All statuses')),
                        PopupMenuItem(
                          value: 'NotFunded',
                          child: Text('Not funded'),
                        ),
                        PopupMenuItem(value: 'Held', child: Text('Held')),
                        PopupMenuItem(
                          value: 'PartiallyReturned',
                          child: Text('Partially returned'),
                        ),
                        PopupMenuItem(
                          value: 'Returned',
                          child: Text('Returned'),
                        ),
                        PopupMenuItem(
                          value: 'Withheld',
                          child: Text('Withheld'),
                        ),
                      ],
                    ),
                  ],
                ),
              ),
            ),
            ...state.when(
              loading: () => const [_DepositsLoadingSliver()],
              error: (error, _) => [
                SliverFillRemaining(
                  hasScrollBody: false,
                  child: _ErrorBody(
                    message: error is ApiException
                        ? error.message
                        : error.toString(),
                    onRetry: _refresh,
                  ),
                ),
              ],
              data: (page) {
                final accounts = page.items;
                if (accounts.isEmpty) {
                  return const [
                    SliverFillRemaining(
                      hasScrollBody: false,
                      child: _EmptyBody(),
                    ),
                  ];
                }
                return [
                  SliverPadding(
                    padding: const EdgeInsets.fromLTRB(16, 4, 16, 8),
                    sliver: SliverList.separated(
                      itemCount: accounts.length,
                      separatorBuilder: (_, _) => const MobileM3ListDivider(),
                      itemBuilder: (_, index) => _DepositListItem(
                        account: accounts[index],
                        position: MobileM3ListItemPositionForIndex.forIndex(
                          index,
                          accounts.length,
                        ),
                        onTap: () => _showDetail(accounts[index]),
                      ),
                    ),
                  ),
                  if (page.hasMore)
                    SliverToBoxAdapter(
                      child: Padding(
                        padding: const EdgeInsets.fromLTRB(16, 4, 16, 100),
                        child: OutlinedButton(
                          onPressed: _loadingMore ? null : _loadMore,
                          child: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              if (_loadingMore) ...[
                                const SizedBox.square(
                                  key: Key('deposits-load-more-progress'),
                                  dimension: 18,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 2,
                                  ),
                                ),
                                const SizedBox(width: 8),
                              ],
                              Text(
                                'Load more (${page.items.length} of ${page.totalCount})',
                              ),
                            ],
                          ),
                        ),
                      ),
                    )
                  else
                    const SliverToBoxAdapter(child: SizedBox(height: 100)),
                ];
              },
            ),
          ],
        ),
      ),
    );
  }
}

class _DepositsLoadingSliver extends StatelessWidget {
  const _DepositsLoadingSliver();

  @override
  Widget build(BuildContext context) => const SliverFillRemaining(
    hasScrollBody: false,
    child: Center(
      child: SizedBox.square(
        key: Key('deposits-loading'),
        dimension: 32,
        child: CircularProgressIndicator(strokeWidth: 3),
      ),
    ),
  );
}

class _DepositListItem extends StatelessWidget {
  const _DepositListItem({
    required this.account,
    required this.position,
    required this.onTap,
  });

  final TenantAccountDeposit account;
  final MobileM3ListItemPosition position;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colors = theme.colorScheme;
    return MobileM3ListItem(
      position: position,
      leading: MobileM3LeadingIcon(
        icon: Icons.shield_outlined,
        backgroundColor: colors.primaryContainer,
        foregroundColor: colors.onPrimaryContainer,
      ),
      title: Row(
        children: [
          Expanded(
            child: Text(
              account.primaryTenantName?.trim().isNotEmpty == true
                  ? account.primaryTenantName!
                  : account.relationshipNumber,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
          ),
          const SizedBox(width: 8),
          _StatusChip(status: account.status),
        ],
      ),
      supporting: [
        Text(_location(account), maxLines: 1, overflow: TextOverflow.ellipsis),
        Text(
          '${_fmtCurrency(account.heldBalance, account.currency)} currently held',
          style: theme.textTheme.bodySmall?.copyWith(
            color: colors.onSurfaceVariant,
          ),
        ),
      ],
      meta: Text(
        account.accountNumber,
        style: theme.textTheme.labelSmall?.copyWith(
          color: colors.onSurfaceVariant,
        ),
      ),
      trailing: Icon(Icons.chevron_right, color: colors.onSurfaceVariant),
      onTap: onTap,
    );
  }
}

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.status});

  final String status;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    final lower = status.toLowerCase();
    final (background, foreground) = switch (lower) {
      'held' => (colors.primaryContainer, colors.onPrimaryContainer),
      'notfunded' => (colors.surfaceContainerHighest, colors.onSurfaceVariant),
      'returned' => (colors.secondaryContainer, colors.onSecondaryContainer),
      'partiallyreturned' => (
        colors.tertiaryContainer,
        colors.onTertiaryContainer,
      ),
      'withheld' => (colors.errorContainer, colors.onErrorContainer),
      _ => (colors.surfaceContainerHighest, colors.onSurfaceVariant),
    };
    final label = switch (status) {
      'NotFunded' => 'Not funded',
      'PartiallyReturned' => 'Partially returned',
      _ => status,
    };
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        label,
        style: TextStyle(
          color: foreground,
          fontSize: 11,
          fontWeight: FontWeight.w600,
        ),
      ),
    );
  }
}

class _DepositDetailSheet extends ConsumerWidget {
  const _DepositDetailSheet({
    required this.account,
    required this.onFund,
    required this.onDeduct,
    required this.onRefund,
  });

  final TenantAccountDeposit account;
  final VoidCallback onFund;
  final VoidCallback onDeduct;
  final VoidCallback onRefund;

  Future<void> _openStatement(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.of(context);
    messenger.showSnackBar(
      const SnackBar(content: Text('Preparing move-out statement…')),
    );
    try {
      final bytes = await ref
          .read(depositsRepositoryProvider)
          .moveOutStatementBytes(account.tenantAccountId);
      await DocumentOpener.openBytes(
        bytes: bytes,
        fileName:
            'deposit-${account.securityDepositAccountId}-move-out-statement.pdf',
      );
      messenger.hideCurrentSnackBar();
    } on ApiException catch (error) {
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(error.message)));
    }
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final canDispose = account.heldBalance > 0;
    final canFund = const {'NotFunded', 'Held'}.contains(account.status);
    return _SheetFrame(
      title: account.primaryTenantName?.trim().isNotEmpty == true
          ? account.primaryTenantName!
          : 'Security deposit',
      subtitle: _location(account),
      trailing: _StatusChip(status: account.status),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _AmountRow(
            label: 'Currently held',
            value: _fmtCurrency(account.heldBalance, account.currency),
            emphasized: true,
          ),
          _AmountRow(
            label: 'Total received',
            value: _fmtCurrency(account.totalReceived, account.currency),
          ),
          _AmountRow(
            label: 'Total deductions',
            value: _fmtCurrency(account.totalDeductions, account.currency),
          ),
          _AmountRow(
            label: 'Total refunded',
            value: _fmtCurrency(account.totalRefunded, account.currency),
          ),
          const Divider(height: 28),
          Text('Account', style: theme.textTheme.titleSmall),
          const SizedBox(height: 8),
          _InfoRow(label: 'Account number', value: account.accountNumber),
          _InfoRow(
            label: 'Rental relationship',
            value: account.relationshipNumber,
          ),
          _InfoRow(
            label: 'Created',
            value: _fmtDate(account.createdAtUtc.toLocal()),
          ),
          const SizedBox(height: 20),
          AccountingImpactCard(
            sourceType: JournalSourceType.securityDepositReceipt,
            sourceId: account.securityDepositAccountId,
          ),
          const SizedBox(height: 20),
          if (canFund) ...[
            FilledButton.icon(
              onPressed: onFund,
              icon: const Icon(Icons.add_card_outlined),
              label: const Text('Record deposit funds'),
            ),
            const SizedBox(height: 10),
          ],
          if (canDispose) ...[
            OutlinedButton.icon(
              onPressed: onDeduct,
              icon: const Icon(Icons.remove_circle_outline),
              label: const Text('Record deduction'),
            ),
            const SizedBox(height: 10),
            FilledButton.tonalIcon(
              onPressed: onRefund,
              icon: const Icon(Icons.assignment_return_outlined),
              label: const Text('Record refund'),
            ),
            const SizedBox(height: 10),
          ],
          OutlinedButton.icon(
            onPressed: () => _openStatement(context, ref),
            icon: const Icon(Icons.picture_as_pdf_outlined),
            label: const Text('Move-out statement (PDF)'),
          ),
        ],
      ),
    );
  }
}

class _FundDepositSheet extends ConsumerStatefulWidget {
  const _FundDepositSheet({
    required this.accounts,
    required this.initialAccount,
    required this.onSaved,
  });

  final List<TenantAccountDeposit> accounts;
  final TenantAccountDeposit? initialAccount;
  final Future<void> Function() onSaved;

  @override
  ConsumerState<_FundDepositSheet> createState() => _FundDepositSheetState();
}

class _FundDepositSheetState extends ConsumerState<_FundDepositSheet> {
  final _formKey = GlobalKey<FormState>();
  final _operationKey = const Uuid().v4();
  final _amount = TextEditingController();
  final _description = TextEditingController(text: 'Security deposit received');
  final _method = TextEditingController();
  final _reference = TextEditingController();
  late int _accountId;
  DateTime _effectiveOn = DateTime.now();
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _accountId =
        widget.initialAccount?.securityDepositAccountId ??
        widget.accounts.first.securityDepositAccountId;
  }

  @override
  void dispose() {
    _amount.dispose();
    _description.dispose();
    _method.dispose();
    _reference.dispose();
    super.dispose();
  }

  TenantAccountDeposit get _account => widget.accounts.firstWhere(
    (account) => account.securityDepositAccountId == _accountId,
  );

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      await ref
          .read(depositsRepositoryProvider)
          .fundDeposit(
            _account,
            FundSecurityDepositInput(
              amount: double.parse(_amount.text.trim()),
              effectiveOn: _effectiveOn,
              description: _description.text.trim(),
              paymentMethodSummary: _method.text.trim(),
              externalReference: _reference.text,
            ),
            operationKey: _operationKey,
          );
      await widget.onSaved();
      if (mounted) Navigator.of(context).pop();
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) => _MutationSheet(
    title: 'Record deposit funds',
    subtitle: 'Add money received to an existing deposit account.',
    formKey: _formKey,
    saving: _saving,
    error: _error,
    onSubmit: _submit,
    submitLabel: 'Record funds',
    children: [
      DropdownButtonFormField<int>(
        initialValue: _accountId,
        decoration: const InputDecoration(labelText: 'Deposit account'),
        items: widget.accounts
            .map(
              (account) => DropdownMenuItem(
                value: account.securityDepositAccountId,
                child: Text(
                  '${account.primaryTenantName ?? account.relationshipNumber} · ${_location(account)}',
                  overflow: TextOverflow.ellipsis,
                ),
              ),
            )
            .toList(),
        onChanged: (value) => setState(() => _accountId = value ?? _accountId),
      ),
      _MoneyField(controller: _amount),
      TextFormField(
        controller: _method,
        maxLength: 200,
        decoration: const InputDecoration(
          labelText: 'Payment method',
          hintText: 'Check, ACH, cash, or card',
        ),
        validator: _required,
      ),
      TextFormField(
        controller: _description,
        maxLength: 500,
        decoration: const InputDecoration(labelText: 'Description'),
        validator: _required,
      ),
      TextFormField(
        controller: _reference,
        maxLength: 200,
        decoration: const InputDecoration(
          labelText: 'Reference (optional)',
          hintText: 'Check or transaction number',
        ),
      ),
      _DateField(
        value: _effectiveOn,
        onChanged: (value) => setState(() => _effectiveOn = value),
      ),
    ],
  );
}

class _DeductDepositSheet extends ConsumerStatefulWidget {
  const _DeductDepositSheet({required this.account, required this.onSaved});

  final TenantAccountDeposit account;
  final Future<void> Function() onSaved;

  @override
  ConsumerState<_DeductDepositSheet> createState() =>
      _DeductDepositSheetState();
}

class _DeductDepositSheetState extends ConsumerState<_DeductDepositSheet> {
  final _formKey = GlobalKey<FormState>();
  final _operationKey = const Uuid().v4();
  final _amount = TextEditingController();
  final _reason = TextEditingController();
  final _notes = TextEditingController();
  DateTime _effectiveOn = DateTime.now();
  bool _saving = false;
  String? _error;

  @override
  void dispose() {
    _amount.dispose();
    _reason.dispose();
    _notes.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      await ref
          .read(depositsRepositoryProvider)
          .deductDeposit(
            widget.account,
            DeductSecurityDepositInput(
              amount: double.parse(_amount.text.trim()),
              effectiveOn: _effectiveOn,
              reason: _reason.text.trim(),
              notes: _notes.text,
            ),
            operationKey: _operationKey,
          );
      await widget.onSaved();
      if (mounted) Navigator.of(context).pop();
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) => _MutationSheet(
    title: 'Record deduction',
    subtitle:
        '${widget.account.primaryTenantName ?? widget.account.relationshipNumber} · '
        '${_fmtCurrency(widget.account.heldBalance, widget.account.currency)} held',
    formKey: _formKey,
    saving: _saving,
    error: _error,
    onSubmit: _submit,
    submitLabel: 'Record deduction',
    children: [
      _MoneyField(controller: _amount, maximum: widget.account.heldBalance),
      TextFormField(
        controller: _reason,
        maxLength: 500,
        decoration: const InputDecoration(
          labelText: 'Reason',
          hintText: 'Damage repair, cleaning, or unpaid rent',
        ),
        validator: _required,
      ),
      TextFormField(
        controller: _notes,
        maxLines: 3,
        maxLength: 2000,
        decoration: const InputDecoration(labelText: 'Notes (optional)'),
      ),
      _DateField(
        value: _effectiveOn,
        onChanged: (value) => setState(() => _effectiveOn = value),
      ),
    ],
  );
}

class _RefundDepositSheet extends ConsumerStatefulWidget {
  const _RefundDepositSheet({required this.account, required this.onSaved});

  final TenantAccountDeposit account;
  final Future<void> Function() onSaved;

  @override
  ConsumerState<_RefundDepositSheet> createState() =>
      _RefundDepositSheetState();
}

class _RefundDepositSheetState extends ConsumerState<_RefundDepositSheet> {
  final _formKey = GlobalKey<FormState>();
  final _operationKey = const Uuid().v4();
  late final TextEditingController _amount;
  final _description = TextEditingController(text: 'Security deposit refund');
  final _reference = TextEditingController();
  DateTime _effectiveOn = DateTime.now();
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _amount = TextEditingController(
      text: widget.account.heldBalance.toStringAsFixed(2),
    );
  }

  @override
  void dispose() {
    _amount.dispose();
    _description.dispose();
    _reference.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      await ref
          .read(depositsRepositoryProvider)
          .refundDeposit(
            widget.account,
            RefundSecurityDepositInput(
              amount: double.parse(_amount.text.trim()),
              effectiveOn: _effectiveOn,
              description: _description.text.trim(),
              externalReference: _reference.text,
            ),
            operationKey: _operationKey,
          );
      await widget.onSaved();
      if (mounted) Navigator.of(context).pop();
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) => _MutationSheet(
    title: 'Record refund',
    subtitle:
        '${widget.account.primaryTenantName ?? widget.account.relationshipNumber} · '
        '${_fmtCurrency(widget.account.heldBalance, widget.account.currency)} available',
    formKey: _formKey,
    saving: _saving,
    error: _error,
    onSubmit: _submit,
    submitLabel: 'Record refund',
    children: [
      _MoneyField(controller: _amount, maximum: widget.account.heldBalance),
      TextFormField(
        controller: _description,
        maxLength: 500,
        decoration: const InputDecoration(labelText: 'Description'),
        validator: _required,
      ),
      TextFormField(
        controller: _reference,
        maxLength: 200,
        decoration: const InputDecoration(
          labelText: 'Payout reference (optional)',
          hintText: 'Check or transaction number',
        ),
      ),
      _DateField(
        value: _effectiveOn,
        onChanged: (value) => setState(() => _effectiveOn = value),
      ),
    ],
  );
}

class _MutationSheet extends StatelessWidget {
  const _MutationSheet({
    required this.title,
    required this.subtitle,
    required this.formKey,
    required this.saving,
    required this.error,
    required this.onSubmit,
    required this.submitLabel,
    required this.children,
  });

  final String title;
  final String subtitle;
  final GlobalKey<FormState> formKey;
  final bool saving;
  final String? error;
  final VoidCallback onSubmit;
  final String submitLabel;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    return Padding(
      padding: EdgeInsets.fromLTRB(
        20,
        20,
        20,
        20 + MediaQuery.viewInsetsOf(context).bottom,
      ),
      child: Form(
        key: formKey,
        child: SingleChildScrollView(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          title,
                          style: Theme.of(context).textTheme.titleLarge
                              ?.copyWith(fontWeight: FontWeight.w700),
                        ),
                        const SizedBox(height: 4),
                        Text(
                          subtitle,
                          style: TextStyle(color: colors.onSurfaceVariant),
                        ),
                      ],
                    ),
                  ),
                  IconButton(
                    onPressed: () => Navigator.of(context).pop(),
                    icon: const Icon(Icons.close),
                  ),
                ],
              ),
              const SizedBox(height: 20),
              for (final child in children) ...[
                child,
                const SizedBox(height: 14),
              ],
              if (error != null) ...[
                Text(error!, style: TextStyle(color: colors.error)),
                const SizedBox(height: 12),
              ],
              FilledButton(
                onPressed: saving ? null : onSubmit,
                child: saving
                    ? const SizedBox(
                        width: 20,
                        height: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : Text(submitLabel),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _SheetFrame extends StatelessWidget {
  const _SheetFrame({
    required this.title,
    required this.subtitle,
    required this.trailing,
    required this.child,
  });

  final String title;
  final String subtitle;
  final Widget trailing;
  final Widget child;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.all(20),
    child: SingleChildScrollView(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      title,
                      style: Theme.of(context).textTheme.titleLarge?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    Text(
                      subtitle,
                      style: TextStyle(
                        color: Theme.of(context).colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
              ),
              trailing,
              IconButton(
                onPressed: () => Navigator.of(context).pop(),
                icon: const Icon(Icons.close),
              ),
            ],
          ),
          const SizedBox(height: 20),
          child,
        ],
      ),
    ),
  );
}

class _MoneyField extends StatelessWidget {
  const _MoneyField({required this.controller, this.maximum});

  final TextEditingController controller;
  final double? maximum;

  @override
  Widget build(BuildContext context) => TextFormField(
    controller: controller,
    keyboardType: const TextInputType.numberWithOptions(decimal: true),
    decoration: const InputDecoration(labelText: 'Amount', prefixText: '\$'),
    validator: (value) {
      final parsed = double.tryParse(value?.trim() ?? '');
      if (parsed == null || parsed <= 0) {
        return 'Enter an amount greater than zero';
      }
      if (maximum != null && parsed > maximum!) {
        return 'Amount cannot exceed ${_fmtCurrency(maximum!)}';
      }
      return null;
    },
  );
}

class _DateField extends StatelessWidget {
  const _DateField({required this.value, required this.onChanged});

  final DateTime value;
  final ValueChanged<DateTime> onChanged;

  @override
  Widget build(BuildContext context) => ListTile(
    contentPadding: EdgeInsets.zero,
    title: const Text('Effective date'),
    subtitle: Text(_fmtDate(value)),
    trailing: const Icon(Icons.calendar_today_outlined),
    onTap: () async {
      final selected = await showDatePicker(
        context: context,
        initialDate: value,
        firstDate: DateTime(2000),
        lastDate: DateTime.now().add(const Duration(days: 3650)),
      );
      if (selected != null) onChanged(selected);
    },
  );
}

class _AmountRow extends StatelessWidget {
  const _AmountRow({
    required this.label,
    required this.value,
    this.emphasized = false,
  });

  final String label;
  final String value;
  final bool emphasized;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 5),
    child: Row(
      children: [
        Expanded(child: Text(label)),
        Text(
          value,
          style: TextStyle(
            fontWeight: emphasized ? FontWeight.w700 : FontWeight.w500,
          ),
        ),
      ],
    ),
  );
}

class _InfoRow extends StatelessWidget {
  const _InfoRow({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 4),
    child: Row(
      children: [
        Expanded(
          child: Text(
            label,
            style: TextStyle(
              color: Theme.of(context).colorScheme.onSurfaceVariant,
            ),
          ),
        ),
        Flexible(child: Text(value, textAlign: TextAlign.end)),
      ],
    ),
  );
}

String? _required(String? value) =>
    value == null || value.trim().isEmpty ? 'Required' : null;

class _EmptyBody extends StatelessWidget {
  const _EmptyBody();

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(28),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              Icons.shield_outlined,
              size: 48,
              color: colors.onSurfaceVariant,
            ),
            const SizedBox(height: 12),
            Text(
              'No deposit accounts',
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 6),
            Text(
              'A deposit account is created automatically when you prepare a tenant move-in.',
              textAlign: TextAlign.center,
              style: TextStyle(color: colors.onSurfaceVariant),
            ),
          ],
        ),
      ),
    );
  }
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.message, required this.onRetry});

  final String message;
  final Future<void> Function() onRetry;

  @override
  Widget build(BuildContext context) => Center(
    child: Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.error_outline, color: Theme.of(context).colorScheme.error),
          const SizedBox(height: 12),
          Text(message, textAlign: TextAlign.center),
          const SizedBox(height: 16),
          FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
        ],
      ),
    ),
  );
}
