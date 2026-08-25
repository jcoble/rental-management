import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/presentation/formatting.dart';
import '../scan/scan_repository.dart';
import 'payments_repository.dart';
import 'payments_screen.dart';

typedef _TenantReceiptAccountQuery = ({String search, int skip});
typedef _TenantReceiptChargeQuery = ({int tenantAccountId, int skip});

final _tenantReceiptAccountPageProvider = FutureProvider.autoDispose
    .family<TenantAccountOptionPage, _TenantReceiptAccountQuery>((ref, query) {
      return ref
          .read(scanRepositoryProvider)
          .listTenantAccountOptions(
            search: query.search,
            closed: false,
            skip: query.skip,
          );
    });

final _tenantReceiptChargePageProvider = FutureProvider.autoDispose
    .family<TenantChargePage, _TenantReceiptChargeQuery>((ref, query) {
      return ref
          .read(paymentsRepositoryProvider)
          .listTenantChargesPage(
            query.tenantAccountId,
            TenantChargeListQuery(skip: query.skip, take: 20, sort: 'dueOn'),
          );
    });

/// Starts the global manual-payment flow without losing the canonical rental
/// relationship. The picker is backed by the server-paged tenant-account query;
/// the selected account supplies both IDs required by the receipt command.
Future<RecordTenantReceiptResult?> showGlobalRecordTenantReceiptFlow(
  BuildContext context,
  WidgetRef ref,
) async {
  final account = await showModalBottomSheet<TenantAccountOption>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    useSafeArea: true,
    builder: (_) => const _TenantReceiptAccountPickerSheet(),
  );
  if (account == null || !context.mounted) return null;

  final rentalLabel = [
    account.propertyName,
    if (account.unitNumber.trim().isNotEmpty) 'Unit ${account.unitNumber}',
  ].where((value) => value.trim().isNotEmpty).join(' · ');
  final target = await showModalBottomSheet<_ReceiptChargeTarget>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    useSafeArea: true,
    builder: (_) => _TenantReceiptChargePickerSheet(account: account),
  );
  if (target == null || !context.mounted) return null;

  return showRecordTenantReceiptSheet(
    context,
    ref,
    tenantAccountId: account.tenantAccountId,
    leaseManagementId: account.leaseManagementId,
    tenantName: account.primaryTenantName,
    rentalLabel: rentalLabel,
    initialAmount: target.charge?.openAmount,
    targetChargeEntryId: target.charge?.tenantLedgerEntryId,
    initialLeaveUnapplied: target.unapplied,
  );
}

class _ReceiptChargeTarget {
  const _ReceiptChargeTarget._({this.charge, required this.unapplied});

  const _ReceiptChargeTarget.charge(TenantCharge charge)
    : this._(charge: charge, unapplied: false);

  const _ReceiptChargeTarget.unapplied() : this._(unapplied: true);

  final TenantCharge? charge;
  final bool unapplied;
}

class _TenantReceiptAccountPickerSheet extends ConsumerStatefulWidget {
  const _TenantReceiptAccountPickerSheet();

  @override
  ConsumerState<_TenantReceiptAccountPickerSheet> createState() =>
      _TenantReceiptAccountPickerSheetState();
}

class _TenantReceiptAccountPickerSheetState
    extends ConsumerState<_TenantReceiptAccountPickerSheet> {
  final _searchController = TextEditingController();
  Timer? _debounce;
  String _search = '';
  int _skip = 0;

  @override
  void dispose() {
    _debounce?.cancel();
    _searchController.dispose();
    super.dispose();
  }

  void _searchChanged(String value) {
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 300), () {
      if (!mounted) return;
      setState(() {
        _search = value.trim();
        _skip = 0;
      });
    });
  }

  @override
  Widget build(BuildContext context) {
    final pageAsync = ref.watch(
      _tenantReceiptAccountPageProvider((search: _search, skip: _skip)),
    );
    final bottomInset = MediaQuery.viewInsetsOf(context).bottom;
    final colorScheme = Theme.of(context).colorScheme;

    return Padding(
      padding: EdgeInsets.fromLTRB(16, 0, 16, 16 + bottomInset),
      child: SizedBox(
        height: MediaQuery.sizeOf(context).height * 0.72,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              'Add payment',
              style: Theme.of(
                context,
              ).textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 4),
            Text(
              'Choose the rental account that received the payment.',
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 16),
            TextField(
              controller: _searchController,
              onChanged: _searchChanged,
              textInputAction: TextInputAction.search,
              decoration: InputDecoration(
                labelText: 'Search rental accounts',
                hintText: 'Tenant, property, unit, or relationship',
                prefixIcon: const Icon(Icons.search),
                suffixIcon: _searchController.text.isEmpty
                    ? null
                    : IconButton(
                        tooltip: 'Clear search',
                        onPressed: () {
                          _debounce?.cancel();
                          _searchController.clear();
                          setState(() {
                            _search = '';
                            _skip = 0;
                          });
                        },
                        icon: const Icon(Icons.close),
                      ),
              ),
            ),
            const SizedBox(height: 12),
            Expanded(
              child: pageAsync.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (error, _) => _AccountPickerError(
                  message: error is ApiException
                      ? error.message
                      : 'Could not load rental accounts.',
                  onRetry: () => ref.invalidate(
                    _tenantReceiptAccountPageProvider((
                      search: _search,
                      skip: _skip,
                    )),
                  ),
                ),
                data: (page) => _TenantReceiptAccountResults(
                  page: page,
                  onSelected: (account) => Navigator.of(context).pop(account),
                  onPrevious: page.skip <= 0
                      ? null
                      : () => setState(
                          () => _skip = (page.skip - page.take)
                              .clamp(0, page.skip)
                              .toInt(),
                        ),
                  onNext: page.skip + page.items.length >= page.totalCount
                      ? null
                      : () => setState(() => _skip = page.skip + page.take),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _TenantReceiptChargePickerSheet extends ConsumerStatefulWidget {
  const _TenantReceiptChargePickerSheet({required this.account});

  final TenantAccountOption account;

  @override
  ConsumerState<_TenantReceiptChargePickerSheet> createState() =>
      _TenantReceiptChargePickerSheetState();
}

class _TenantReceiptChargePickerSheetState
    extends ConsumerState<_TenantReceiptChargePickerSheet> {
  int _skip = 0;

  @override
  Widget build(BuildContext context) {
    final pageAsync = ref.watch(
      _tenantReceiptChargePageProvider((
        tenantAccountId: widget.account.tenantAccountId,
        skip: _skip,
      )),
    );
    final bottomInset = MediaQuery.viewInsetsOf(context).bottom;
    final colorScheme = Theme.of(context).colorScheme;
    final rentalLabel = [
      widget.account.propertyName,
      if (widget.account.unitNumber.trim().isNotEmpty)
        'Unit ${widget.account.unitNumber}',
    ].where((value) => value.trim().isNotEmpty).join(' · ');

    return Padding(
      padding: EdgeInsets.fromLTRB(16, 0, 16, 16 + bottomInset),
      child: SizedBox(
        height: MediaQuery.sizeOf(context).height * 0.72,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              'Apply payment to',
              style: Theme.of(
                context,
              ).textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 4),
            Text(
              [
                if (widget.account.primaryTenantName?.trim().isNotEmpty == true)
                  widget.account.primaryTenantName!.trim(),
                if (rentalLabel.isNotEmpty) rentalLabel,
              ].join(' · '),
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 12),
            OutlinedButton.icon(
              key: const Key('tenant-receipt-unapplied-target'),
              onPressed: () => Navigator.of(
                context,
              ).pop(const _ReceiptChargeTarget.unapplied()),
              icon: const Icon(Icons.account_balance_wallet_outlined),
              label: const Text('Leave unapplied/advance receipt'),
            ),
            const SizedBox(height: 12),
            Expanded(
              child: pageAsync.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (error, _) => _AccountPickerError(
                  message: error is ApiException
                      ? error.message
                      : 'Could not load open charges.',
                  onRetry: () => ref.invalidate(
                    _tenantReceiptChargePageProvider((
                      tenantAccountId: widget.account.tenantAccountId,
                      skip: _skip,
                    )),
                  ),
                ),
                data: (page) => _TenantReceiptChargeResults(
                  page: page,
                  onSelected: (charge) => Navigator.of(
                    context,
                  ).pop(_ReceiptChargeTarget.charge(charge)),
                  onPrevious: page.hasPrevious
                      ? () => setState(
                          () => _skip = (page.skip - page.take)
                              .clamp(0, page.skip)
                              .toInt(),
                        )
                      : null,
                  onNext: page.hasNext
                      ? () => setState(() => _skip = page.skip + page.take)
                      : null,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _TenantReceiptChargeResults extends StatelessWidget {
  const _TenantReceiptChargeResults({
    required this.page,
    required this.onSelected,
    required this.onPrevious,
    required this.onNext,
  });

  final TenantChargePage page;
  final ValueChanged<TenantCharge> onSelected;
  final VoidCallback? onPrevious;
  final VoidCallback? onNext;

  @override
  Widget build(BuildContext context) {
    if (page.items.isEmpty) {
      return const Center(child: Text('No open charges found.'));
    }

    return Column(
      children: [
        Expanded(
          child: ListView.separated(
            itemCount: page.items.length,
            separatorBuilder: (_, _) => const Divider(height: 1),
            itemBuilder: (context, index) {
              final charge = page.items[index];
              return ListTile(
                leading: const Icon(Icons.receipt_long_outlined),
                title: Text(
                  charge.description,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
                subtitle: Text(
                  [
                    'Due ${dateFmt(charge.dueOn ?? charge.effectiveOn)}',
                    '${moneyFmt(charge.openAmount)} still owed',
                  ].join(' · '),
                ),
                trailing: const Icon(Icons.chevron_right),
                onTap: () => onSelected(charge),
              );
            },
          ),
        ),
        if (onPrevious != null || onNext != null)
          Padding(
            padding: const EdgeInsets.only(top: 8),
            child: Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                TextButton(
                  onPressed: onPrevious,
                  child: const Text('Previous'),
                ),
                TextButton(onPressed: onNext, child: const Text('Next')),
              ],
            ),
          ),
      ],
    );
  }
}

class _TenantReceiptAccountResults extends StatelessWidget {
  const _TenantReceiptAccountResults({
    required this.page,
    required this.onSelected,
    required this.onPrevious,
    required this.onNext,
  });

  final TenantAccountOptionPage page;
  final ValueChanged<TenantAccountOption> onSelected;
  final VoidCallback? onPrevious;
  final VoidCallback? onNext;

  @override
  Widget build(BuildContext context) {
    if (page.items.isEmpty) {
      return const Center(child: Text('No open rental accounts found.'));
    }

    return Column(
      children: [
        Expanded(
          child: ListView.separated(
            itemCount: page.items.length,
            separatorBuilder: (_, _) => const Divider(height: 1),
            itemBuilder: (context, index) {
              final account = page.items[index];
              final rentalLabel = [
                account.propertyName,
                if (account.unitNumber.trim().isNotEmpty)
                  'Unit ${account.unitNumber}',
              ].where((value) => value.trim().isNotEmpty).join(' · ');
              return ListTile(
                leading: const Icon(Icons.account_balance_wallet_outlined),
                title: Text(
                  account.primaryTenantName?.trim().isNotEmpty ?? false
                      ? account.primaryTenantName!.trim()
                      : account.relationshipNumber,
                ),
                subtitle: Text(rentalLabel),
                trailing: const Icon(Icons.chevron_right),
                onTap: () => onSelected(account),
              );
            },
          ),
        ),
        if (onPrevious != null || onNext != null)
          Padding(
            padding: const EdgeInsets.only(top: 8),
            child: Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                TextButton(
                  onPressed: onPrevious,
                  child: const Text('Previous'),
                ),
                TextButton(onPressed: onNext, child: const Text('Next')),
              ],
            ),
          ),
      ],
    );
  }
}

class _AccountPickerError extends StatelessWidget {
  const _AccountPickerError({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => Center(
    child: Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(message, textAlign: TextAlign.center),
        const SizedBox(height: 8),
        TextButton(onPressed: onRetry, child: const Text('Retry')),
      ],
    ),
  );
}
