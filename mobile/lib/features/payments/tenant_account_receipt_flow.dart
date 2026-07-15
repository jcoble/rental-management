import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../scan/scan_repository.dart';
import 'payments_repository.dart';
import 'payments_screen.dart';

typedef _TenantReceiptAccountQuery = ({String search, int skip});

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

  return showRecordTenantReceiptSheet(
    context,
    ref,
    tenantAccountId: account.tenantAccountId,
    leaseManagementId: account.leaseManagementId,
    tenantName: account.primaryTenantName,
    rentalLabel: rentalLabel,
  );
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
