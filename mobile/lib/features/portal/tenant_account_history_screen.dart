import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'tenant_portal_repository.dart';

/// Tenant-facing history from the canonical, tenant-scoped account ledger.
/// Filtering, ordering and paging stay on the server.
class TenantAccountHistoryScreen extends ConsumerStatefulWidget {
  const TenantAccountHistoryScreen({super.key, this.initialTenantAccountId});

  final int? initialTenantAccountId;

  @override
  ConsumerState<TenantAccountHistoryScreen> createState() =>
      _TenantAccountHistoryScreenState();
}

class _TenantAccountHistoryScreenState
    extends ConsumerState<TenantAccountHistoryScreen> {
  static const _pageSize = 20;
  int? _selectedAccountId;
  int _skip = 0;

  @override
  void initState() {
    super.initState();
    _selectedAccountId = widget.initialTenantAccountId;
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final snapshot = ref.watch(tenantPortalSnapshotProvider);

    return Scaffold(
      appBar: AppBar(title: const Text('Account history')),
      body: snapshot.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (err, _) => RefreshIndicator(
          onRefresh: () async => ref.invalidate(tenantPortalSnapshotProvider),
          child: ListView(
            padding: const EdgeInsets.all(20),
            children: [
              const SizedBox(height: 40),
              Text(
                "Couldn't load your account history.",
                textAlign: TextAlign.center,
                style: theme.textTheme.titleMedium,
              ),
              const SizedBox(height: 8),
              Text('$err', textAlign: TextAlign.center),
            ],
          ),
        ),
        data: (data) {
          if (data.accounts.totalCount == 0) {
            return const Center(child: Text('No tenant account is available.'));
          }

          final accountId =
              _selectedAccountId ??
              (data.accounts.totalCount == 1 && data.accounts.items.isNotEmpty
                  ? data.accounts.items.first.tenantAccountId
                  : null);
          final entries = accountId == null
              ? null
              : ref.watch(
                  tenantPortalEntriesPageProvider((
                    tenantAccountId: accountId,
                    skip: _skip,
                    take: _pageSize,
                  )),
                );

          return RefreshIndicator(
            onRefresh: () async {
              ref.invalidate(tenantPortalSnapshotProvider);
              if (accountId != null) {
                ref.invalidate(
                  tenantPortalEntriesPageProvider((
                    tenantAccountId: accountId,
                    skip: _skip,
                    take: _pageSize,
                  )),
                );
              }
            },
            child: ListView(
              padding: const EdgeInsets.all(16),
              children: [
                if (data.accounts.totalCount > 1) ...[
                  DropdownButtonFormField<int>(
                    initialValue: _selectedAccountId,
                    decoration: const InputDecoration(
                      labelText: 'Account',
                      border: OutlineInputBorder(),
                    ),
                    hint: const Text('Choose an account'),
                    items: [
                      for (final account in data.accounts.items)
                        DropdownMenuItem<int>(
                          value: account.tenantAccountId,
                          child: Text(
                            '${account.propertyName} · Unit ${account.unitNumber}',
                          ),
                        ),
                    ],
                    onChanged: (value) => setState(() {
                      _selectedAccountId = value;
                      _skip = 0;
                    }),
                  ),
                  const SizedBox(height: 16),
                ],
                if (accountId == null)
                  const Text('Choose an account to view its history.')
                else
                  entries!.when(
                    loading: () => const Center(
                      child: Padding(
                        padding: EdgeInsets.all(24),
                        child: CircularProgressIndicator(),
                      ),
                    ),
                    error: (err, _) => Text("Couldn't load entries: $err"),
                    data: (page) => Column(
                      children: [
                        for (final entry in page.items)
                          Card(
                            child: ListTile(
                              leading: Icon(
                                entry.direction == 'Debit'
                                    ? Icons.arrow_upward
                                    : Icons.arrow_downward,
                              ),
                              title: Text(
                                entry.description.isEmpty
                                    ? entry.entryType
                                    : entry.description,
                              ),
                              subtitle: Text(
                                '${_shortDate(entry.effectiveOn)} · ${entry.direction}',
                              ),
                              trailing: Text(
                                _money(entry.amount, entry.currency),
                                style: theme.textTheme.titleSmall,
                              ),
                            ),
                          ),
                        if (page.items.isEmpty)
                          const Padding(
                            padding: EdgeInsets.all(24),
                            child: Text('No account entries found.'),
                          ),
                        if (page.totalCount > _pageSize)
                          Row(
                            mainAxisAlignment: MainAxisAlignment.spaceBetween,
                            children: [
                              OutlinedButton(
                                onPressed: _skip == 0
                                    ? null
                                    : () => setState(
                                        () => _skip = _skip >= _pageSize
                                            ? _skip - _pageSize
                                            : 0,
                                      ),
                                child: const Text('Previous'),
                              ),
                              Text(
                                '${_skip + 1}–${(_skip + _pageSize).clamp(0, page.totalCount)} of ${page.totalCount}',
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
                    ),
                  ),
              ],
            ),
          );
        },
      ),
    );
  }
}

String _money(double value, String currency) =>
    '$currency ${value.toStringAsFixed(2)}';

String _shortDate(DateTime date) {
  if (date.year <= 1) return '';
  return '${date.month}/${date.day}/${date.year}';
}
