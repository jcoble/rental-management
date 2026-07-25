import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import '../money/money_format.dart';
import 'payment_detail_screen.dart';
import 'payments_repository.dart';

Future<RecordTenantReceiptResult?> showRecordTenantReceiptSheet(
  BuildContext context,
  WidgetRef ref, {
  required int tenantAccountId,
  required int leaseManagementId,
  String? tenantName,
  String? rentalLabel,
  double? initialAmount,
}) {
  return showModalBottomSheet<RecordTenantReceiptResult>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    useSafeArea: true,
    builder: (_) => _RecordTenantReceiptSheet(
      tenantAccountId: tenantAccountId,
      leaseManagementId: leaseManagementId,
      tenantName: tenantName,
      rentalLabel: rentalLabel,
      initialAmount: initialAmount,
    ),
  );
}

class PaymentsScreen extends ConsumerStatefulWidget {
  const PaymentsScreen({super.key});

  @override
  ConsumerState<PaymentsScreen> createState() => _PaymentsScreenState();
}

class _PaymentsScreenState extends ConsumerState<PaymentsScreen> {
  final _searchController = TextEditingController();
  int _skip = 0;
  String? _search;

  TenantLedgerEntryListQuery get _query => TenantLedgerEntryListQuery(
    skip: _skip,
    take: 20,
    search: _search,
    sort: '-postedAtUtc',
  );

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  void _submitSearch(String value) {
    final normalized = value.trim();
    setState(() {
      _search = normalized.isEmpty ? null : normalized;
      _skip = 0;
    });
  }

  @override
  Widget build(BuildContext context) {
    final pageAsync = ref.watch(tenantLedgerEntriesPageProvider(_query));
    final body = RefreshIndicator(
      onRefresh: () async =>
          ref.invalidate(tenantLedgerEntriesPageProvider(_query)),
      child: ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.fromLTRB(16, 12, 16, 96),
        children: [
          TextField(
            controller: _searchController,
            textInputAction: TextInputAction.search,
            onSubmitted: _submitSearch,
            decoration: InputDecoration(
              labelText: 'Search receipts',
              hintText: 'Tenant, account, property, or unit',
              prefixIcon: const Icon(Icons.search),
              suffixIcon: _search == null
                  ? null
                  : IconButton(
                      tooltip: 'Clear search',
                      onPressed: () {
                        _searchController.clear();
                        _submitSearch('');
                      },
                      icon: const Icon(Icons.close),
                    ),
            ),
          ),
          const SizedBox(height: 16),
          pageAsync.when(
            loading: () => const Padding(
              padding: EdgeInsets.all(32),
              child: Center(child: CircularProgressIndicator()),
            ),
            error: (error, _) => _ErrorCard(
              message: error is ApiException ? error.message : error.toString(),
              onRetry: () =>
                  ref.invalidate(tenantLedgerEntriesPageProvider(_query)),
            ),
            data: (page) => Column(
              children: [
                if (page.items.isEmpty)
                  const Padding(
                    padding: EdgeInsets.symmetric(vertical: 48),
                    child: Text('No receipts found.'),
                  )
                else
                  for (final receipt in page.items)
                    _ReceiptCard(receipt: receipt),
                if (page.hasPrevious || page.hasNext) ...[
                  const SizedBox(height: 12),
                  Row(
                    mainAxisAlignment: MainAxisAlignment.spaceBetween,
                    children: [
                      OutlinedButton.icon(
                        onPressed: page.hasPrevious
                            ? () => setState(
                                () => _skip = _skip >= page.take
                                    ? _skip - page.take
                                    : 0,
                              )
                            : null,
                        icon: const Icon(Icons.chevron_left),
                        label: const Text('Previous'),
                      ),
                      Text('${page.totalCount} receipts'),
                      OutlinedButton.icon(
                        onPressed: page.hasNext
                            ? () => setState(() => _skip += page.take)
                            : null,
                        iconAlignment: IconAlignment.end,
                        icon: const Icon(Icons.chevron_right),
                        label: const Text('Next'),
                      ),
                    ],
                  ),
                ],
              ],
            ),
          ),
        ],
      ),
    );
    return Scaffold(
      appBar: AppBar(title: const Text('Receipts')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'receipts-fab',
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: body,
    );
  }
}

class _ReceiptCard extends StatelessWidget {
  const _ReceiptCard({required this.receipt});

  final StaffTenantLedgerEntry receipt;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final home = [
      receipt.propertyName,
      if (receipt.unitNumber.trim().isNotEmpty) 'Unit ${receipt.unitNumber}',
    ].where((value) => value.trim().isNotEmpty).join(' · ');
    return Card(
      margin: const EdgeInsets.only(bottom: 10),
      child: ListTile(
        leading: const Icon(Icons.receipt_long_outlined),
        title: Text(
          receipt.primaryTenantName?.trim().isNotEmpty == true
              ? receipt.primaryTenantName!
              : receipt.description,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
        subtitle: Text(
          [if (home.isNotEmpty) home, dateFmt(receipt.effectiveOn)].join(' · '),
          maxLines: 2,
          overflow: TextOverflow.ellipsis,
        ),
        trailing: Text(
          moneyFmt(receipt.amount),
          style: theme.textTheme.titleSmall?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        onTap: () => Navigator.of(context).push<void>(
          MaterialPageRoute<void>(
            builder: (_) => PaymentDetailScreen(
              tenantAccountId: receipt.tenantAccountId,
              tenantLedgerEntryId: receipt.tenantLedgerEntryId,
            ),
          ),
        ),
      ),
    );
  }
}

class _RecordTenantReceiptSheet extends ConsumerStatefulWidget {
  const _RecordTenantReceiptSheet({
    required this.tenantAccountId,
    required this.leaseManagementId,
    this.tenantName,
    this.rentalLabel,
    this.initialAmount,
  });

  final int tenantAccountId;
  final int leaseManagementId;
  final String? tenantName;
  final String? rentalLabel;
  final double? initialAmount;

  @override
  ConsumerState<_RecordTenantReceiptSheet> createState() =>
      _RecordTenantReceiptSheetState();
}

class _RecordTenantReceiptSheetState
    extends ConsumerState<_RecordTenantReceiptSheet> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _amount = TextEditingController(
    text: widget.initialAmount == null
        ? ''
        : widget.initialAmount!.toStringAsFixed(2),
  );
  final _description = TextEditingController(text: 'Tenant payment received');
  final _method = TextEditingController();
  final _reference = TextEditingController();
  final _payer = TextEditingController();
  late final String _operationKey =
      'mobile-receipt-${widget.tenantAccountId}-${DateTime.now().microsecondsSinceEpoch}';
  DateTime _receivedOn = DateTime.now();
  bool _saving = false;
  String? _error;

  @override
  void dispose() {
    _amount.dispose();
    _description.dispose();
    _method.dispose();
    _reference.dispose();
    _payer.dispose();
    super.dispose();
  }

  Future<void> _pickDate() async {
    final picked = await showDatePicker(
      context: context,
      initialDate: _receivedOn,
      firstDate: DateTime(_receivedOn.year - 3),
      lastDate: DateTime(_receivedOn.year + 1),
    );
    if (picked != null) setState(() => _receivedOn = picked);
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final result = await ref
          .read(paymentsRepositoryProvider)
          .recordReceipt(
            widget.tenantAccountId,
            RecordTenantReceiptInput(
              amount: double.parse(_amount.text.trim()),
              effectiveOn: _receivedOn,
              description: _description.text.trim(),
              paymentMethodSummary: _method.text.trim(),
              externalReference: _reference.text,
              payerName: _payer.text,
            ),
            operationKey: _operationKey,
          );
      ref.invalidate(tenantLedgerEntriesPageProvider);
      if (mounted) Navigator.of(context).pop(result);
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final bottom = MediaQuery.viewInsetsOf(context).bottom;
    final contextLabel = [
      widget.rentalLabel,
      widget.tenantName,
    ].whereType<String>().where((value) => value.trim().isNotEmpty).join(' · ');
    return SingleChildScrollView(
      padding: EdgeInsets.fromLTRB(20, 4, 20, 24 + bottom),
      child: Form(
        key: _formKey,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              'Record receipt',
              style: Theme.of(context).textTheme.titleLarge,
            ),
            if (contextLabel.isNotEmpty) ...[
              const SizedBox(height: 4),
              Text(contextLabel),
            ],
            const SizedBox(height: 16),
            TextFormField(
              controller: _amount,
              keyboardType: const TextInputType.numberWithOptions(
                decimal: true,
              ),
              decoration: const InputDecoration(
                labelText: 'Amount',
                prefixText: '\$',
              ),
              validator: (value) {
                final amount = double.tryParse(value?.trim() ?? '');
                return amount == null || amount <= 0
                    ? 'Enter an amount greater than zero'
                    : null;
              },
            ),
            const SizedBox(height: 12),
            TextFormField(
              controller: _method,
              decoration: const InputDecoration(labelText: 'Payment method'),
              validator: (value) => value == null || value.trim().isEmpty
                  ? 'Payment method is required'
                  : null,
            ),
            const SizedBox(height: 12),
            TextFormField(
              controller: _description,
              decoration: const InputDecoration(labelText: 'Description'),
              validator: (value) => value == null || value.trim().isEmpty
                  ? 'Description is required'
                  : null,
            ),
            const SizedBox(height: 12),
            ListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Received on'),
              subtitle: Text(dateFmt(_receivedOn)),
              trailing: const Icon(Icons.calendar_today_outlined),
              onTap: _pickDate,
            ),
            TextFormField(
              controller: _reference,
              decoration: const InputDecoration(
                labelText: 'Reference (optional)',
              ),
            ),
            const SizedBox(height: 12),
            TextFormField(
              controller: _payer,
              decoration: const InputDecoration(
                labelText: 'Payer name (optional)',
              ),
            ),
            if (_error != null) ...[
              const SizedBox(height: 12),
              Text(
                _error!,
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              ),
            ],
            const SizedBox(height: 20),
            FilledButton(
              onPressed: _saving ? null : _submit,
              child: Text(_saving ? 'Saving…' : 'Record receipt'),
            ),
          ],
        ),
      ),
    );
  }
}

class _ErrorCard extends StatelessWidget {
  const _ErrorCard({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        children: [
          Text(message, textAlign: TextAlign.center),
          const SizedBox(height: 8),
          TextButton(onPressed: onRetry, child: const Text('Retry')),
        ],
      ),
    ),
  );
}
