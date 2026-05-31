import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'scan_models.dart';
import 'scan_repository.dart';

// ---------------------------------------------------------------------------
// Providers
// ---------------------------------------------------------------------------

/// Holds the draft being reviewed.
final _draftProvider =
    FutureProvider.autoDispose.family<ScanDraft, int>((ref, id) async {
  return ref.read(scanRepositoryProvider).getDraft(id);
});

/// Holds the authed image bytes for the document preview.
final _imageProvider =
    FutureProvider.autoDispose.family<Uint8List, int>((ref, id) async {
  // Cache the fetched bytes for the session so scrolling the preview out of and
  // back into view does not re-download the full image every time.
  ref.keepAlive();
  return ref.read(scanRepositoryProvider).downloadFile(id);
});

/// Holds the leases list (only fetched for Payment drafts).
final _leasesProvider =
    FutureProvider.autoDispose<List<Map<String, dynamic>>>((ref) async {
  return ref.read(scanRepositoryProvider).listLeases();
});

// ---------------------------------------------------------------------------
// Field groups (mirrors web review page)
// ---------------------------------------------------------------------------

const _fieldGroups = <({String label, List<String> fields})>[
  (
    label: 'Vendor',
    fields: [
      'vendor_name',
      'vendor_address',
      'vendor_phone',
      'vendor_website',
      'vendor_tax_id',
      'receipt_number',
    ]
  ),
  (
    label: 'Amounts',
    fields: [
      'subtotal',
      'tax',
      'tax_rate',
      'tip',
      'discount',
      'shipping',
      'total',
      'payment_method',
      'card_last4',
      'due_date',
    ]
  ),
  (
    label: 'Details',
    fields: ['document_kind', 'category', 'notes'],
  ),
];

const _knownScalarFields = {
  'vendor_name',
  'vendor_address',
  'vendor_phone',
  'vendor_website',
  'vendor_tax_id',
  'receipt_number',
  'subtotal',
  'tax',
  'tax_rate',
  'tip',
  'discount',
  'shipping',
  'total',
  'payment_method',
  'card_last4',
  'due_date',
  'document_kind',
  'category',
  'notes',
};

// ScheduleECategory values (mirrors RentalCommand.Core.Enums.ScheduleECategory)
const _scheduleECategories = [
  'Advertising',
  'AutoTravel',
  'CleaningMaintenance',
  'Commissions',
  'Insurance',
  'LegalProfessional',
  'ManagementFees',
  'MortgageInterest',
  'Repairs',
  'Supplies',
  'Taxes',
  'Utilities',
  'Depreciation',
  'Other',
];

// ---------------------------------------------------------------------------
// Overrides map builder (mirrors web's buildOverridesJson)
// ---------------------------------------------------------------------------

/// Key mapping: certain snake_case names get camelCase equivalents to match
/// the legacy override keys the API's ConfirmAndCreateAsync accepts.
const _keyMap = <String, String>{
  'vendor_name': 'vendorName',
  'amount': 'amount',
  'transaction_date': 'transactionDate',
  'category': 'category',
  'notes': 'notes',
};

Map<String, dynamic> buildOverridesMap({
  required Map<String, String> editedFields,
  required bool isPayment,
  required bool isPaid,
  required int? selectedLeaseId,
}) {
  final overrides = <String, dynamic>{};
  for (final entry in editedFields.entries) {
    if (entry.key == 'line_items') continue;
    final key = _keyMap[entry.key] ?? entry.key;
    overrides[key] = entry.value;
  }
  if (isPayment) {
    overrides['leaseId'] = selectedLeaseId;
  } else {
    overrides['is_paid'] = isPaid;
  }
  return overrides;
}

// ---------------------------------------------------------------------------
// ScanReviewScreen
// ---------------------------------------------------------------------------

class ScanReviewScreen extends ConsumerStatefulWidget {
  const ScanReviewScreen({super.key, required this.draftId});

  final int draftId;

  @override
  ConsumerState<ScanReviewScreen> createState() => _ScanReviewScreenState();
}

class _ScanReviewScreenState extends ConsumerState<ScanReviewScreen> {
  // Editable field values, keyed by field name.
  final Map<String, String> _editedFields = {};
  bool _fieldsInitialized = false;

  // Paid/unpaid toggle (Expense only).
  bool _isPaid = true;
  bool _isPaidInitialized = false;

  // Selected lease id (Payment only).
  int? _selectedLeaseId;

  // Polling timer — used while draft is Pending.
  Timer? _pollTimer;

  // Action states
  bool _confirming = false;
  bool _rejecting = false;

  @override
  void initState() {
    super.initState();
    _startPollingIfNeeded();
  }

  @override
  void dispose() {
    _pollTimer?.cancel();
    super.dispose();
  }

  void _startPollingIfNeeded() {
    // After the provider loads we check status; the timer is set up in _onDraftLoaded.
  }

  void _onDraftLoaded(ScanDraft draft) {
    // Initialize editable field values on first data.
    if (!_fieldsInitialized) {
      _fieldsInitialized = true;
      for (final f in draft.scalarFields) {
        _editedFields[f.name] = f.value;
      }
    }

    // Initialize isPaid from document_kind once.
    if (!_isPaidInitialized) {
      _isPaidInitialized = true;
      final kindField = draft.fields
          .where((f) => f.name == 'document_kind')
          .firstOrNull;
      _isPaid = _defaultIsPaidFromKind(kindField?.value);
    }

    // Start/stop polling based on status.
    if (draft.status == 'Pending') {
      _ensurePolling(draft.id);
    } else {
      _pollTimer?.cancel();
      _pollTimer = null;
    }
  }

  void _ensurePolling(int id) {
    if (_pollTimer != null && _pollTimer!.isActive) return;
    _pollTimer = Timer.periodic(const Duration(milliseconds: 1500), (_) {
      if (!mounted) {
        _pollTimer?.cancel();
        return;
      }
      // Invalidate the provider to trigger a re-fetch.
      ref.invalidate(_draftProvider(id));
    });
  }

  bool _defaultIsPaidFromKind(String? kind) {
    if (kind == null) return true;
    return !['Bill', 'Invoice', 'UtilityBill', 'PropertyTax'].contains(kind);
  }

  Future<void> _confirm(ScanDraft draft) async {
    if (_confirming) return;
    setState(() => _confirming = true);
    try {
      final overrides = buildOverridesMap(
        editedFields: _editedFields,
        isPayment: draft.isPayment,
        isPaid: _isPaid,
        selectedLeaseId: _selectedLeaseId,
      );
      await ref.read(scanRepositoryProvider).confirm(draft.id, overrides);
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            draft.isPayment ? 'Payment recorded!' : 'Expense created!',
          ),
          backgroundColor: Colors.green,
        ),
      );
      Navigator.of(context).pop();
    } on ApiException catch (e) {
      if (!mounted) return;
      _showError(e.message);
    } catch (_) {
      if (!mounted) return;
      _showError('Something went wrong. Please try again.');
    } finally {
      if (mounted) setState(() => _confirming = false);
    }
  }

  Future<void> _reject(ScanDraft draft) async {
    final reason = await _promptReason(context);
    if (!mounted) return;
    if (reason == null) return; // user cancelled

    setState(() => _rejecting = true);
    try {
      await ref
          .read(scanRepositoryProvider)
          .reject(draft.id, reason: reason.isEmpty ? null : reason);
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Scan rejected.')),
      );
      Navigator.of(context).pop();
    } on ApiException catch (e) {
      if (!mounted) return;
      _showError(e.message);
    } finally {
      if (mounted) setState(() => _rejecting = false);
    }
  }

  void _showError(String message) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(message),
        backgroundColor: Theme.of(context).colorScheme.error,
      ),
    );
  }

  static Future<String?> _promptReason(BuildContext context) {
    final controller = TextEditingController();
    return showDialog<String>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Reject scan'),
        content: TextField(
          controller: controller,
          decoration: const InputDecoration(
            hintText: 'Reason (optional)',
          ),
          autofocus: true,
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(ctx).pop(controller.text),
            child: const Text('Reject'),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final draftAsync = ref.watch(_draftProvider(widget.draftId));

    return draftAsync.when(
      loading: () => Scaffold(
        appBar: AppBar(title: Text('Review Scan #${widget.draftId}')),
        body: const Center(child: CircularProgressIndicator()),
      ),
      error: (e, _) => Scaffold(
        appBar: AppBar(title: Text('Review Scan #${widget.draftId}')),
        body: Center(
          child: Text(
            e is ApiException ? e.message : e.toString(),
            textAlign: TextAlign.center,
          ),
        ),
      ),
      data: (draft) {
        // Side-effect: initialize fields, start/stop polling.
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted) setState(() => _onDraftLoaded(draft));
        });

        return _ReviewBody(
          draft: draft,
          editedFields: _editedFields,
          onFieldChanged: (name, value) =>
              setState(() => _editedFields[name] = value),
          isPaid: _isPaid,
          onIsPaidChanged: (v) => setState(() => _isPaid = v),
          selectedLeaseId: _selectedLeaseId,
          onLeaseSelected: (id) => setState(() => _selectedLeaseId = id),
          confirming: _confirming,
          rejecting: _rejecting,
          onConfirm: () => _confirm(draft),
          onReject: () => _reject(draft),
        );
      },
    );
  }
}

// ---------------------------------------------------------------------------
// _ReviewBody
// ---------------------------------------------------------------------------

class _ReviewBody extends ConsumerWidget {
  const _ReviewBody({
    required this.draft,
    required this.editedFields,
    required this.onFieldChanged,
    required this.isPaid,
    required this.onIsPaidChanged,
    required this.selectedLeaseId,
    required this.onLeaseSelected,
    required this.confirming,
    required this.rejecting,
    required this.onConfirm,
    required this.onReject,
  });

  final ScanDraft draft;
  final Map<String, String> editedFields;
  final void Function(String name, String value) onFieldChanged;
  final bool isPaid;
  final ValueChanged<bool> onIsPaidChanged;
  final int? selectedLeaseId;
  final ValueChanged<int?> onLeaseSelected;
  final bool confirming;
  final bool rejecting;
  final VoidCallback onConfirm;
  final VoidCallback onReject;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    final isTerminal =
        draft.status == 'Confirmed' || draft.status == 'Rejected';
    final confirmEnabled = !confirming &&
        !rejecting &&
        !isTerminal &&
        (!draft.isPayment || selectedLeaseId != null);
    final rejectEnabled = !confirming && !rejecting && !isTerminal;

    return Scaffold(
      appBar: AppBar(
        title: Text('Review Scan #${draft.id}'),
        actions: [
          _StatusChip(status: draft.status),
          const SizedBox(width: 12),
        ],
      ),
      body: ListView(
        padding: const EdgeInsets.only(bottom: 140),
        children: [
          // ---- Status banners ----
          if (draft.status == 'Pending')
            _Banner(
              color: Colors.amber.shade50,
              borderColor: Colors.amber.shade200,
              textColor: Colors.amber.shade900,
              child: Row(
                children: [
                  const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  ),
                  const SizedBox(width: 10),
                  const Expanded(
                    child: Text(
                      'Processing your document… fields will appear once extraction completes.',
                    ),
                  ),
                ],
              ),
            ),

          if (draft.status == 'Failed')
            _Banner(
              color: colorScheme.errorContainer,
              borderColor: colorScheme.error.withValues(alpha: 0.4),
              textColor: colorScheme.onErrorContainer,
              child: const Text(
                'Extraction failed. You can still enter the values below and confirm.',
              ),
            ),

          if (draft.isNoOp)
            _Banner(
              color: Colors.amber.shade50,
              borderColor: Colors.amber.shade300,
              textColor: Colors.amber.shade900,
              child: const Text(
                'AI is off — no API key is configured. Enter the details below manually.',
              ),
            ),

          if (draft.status == 'Confirmed')
            _Banner(
              color: Colors.green.shade50,
              borderColor: Colors.green.shade300,
              textColor: Colors.green.shade900,
              child: const Text('This scan has already been confirmed.'),
            ),

          if (draft.status == 'Rejected')
            _Banner(
              color: colorScheme.surfaceContainerHighest,
              borderColor: colorScheme.outlineVariant,
              textColor: colorScheme.onSurfaceVariant,
              child: const Text('This scan has been rejected.'),
            ),

          // ---- Document preview ----
          _DocumentPreview(draftId: draft.id),

          const Divider(height: 1),

          // ---- Lease selector (Payment only) ----
          if (draft.isPayment)
            _LeaseSelector(
              selectedLeaseId: selectedLeaseId,
              onLeaseSelected: onLeaseSelected,
            ),

          // ---- Extracted field groups ----
          if (draft.scalarFields.isNotEmpty)
            _FieldsSection(
              draft: draft,
              editedFields: editedFields,
              onFieldChanged: onFieldChanged,
            )
          else if (draft.status != 'Pending')
            _ManualEntrySection(
              editedFields: editedFields,
              onFieldChanged: onFieldChanged,
            ),

          // ---- Line items (read-only) ----
          if (draft.lineItems.isNotEmpty)
            _LineItemsSection(items: draft.lineItems),

          // ---- Paid/Unpaid toggle (Expense only) ----
          if (!draft.isPayment)
            _PaidToggle(
              isPaid: isPaid,
              dueDate: editedFields['due_date'],
              onChanged: onIsPaidChanged,
            ),

          const SizedBox(height: 8),
        ],
      ),
      // ---- Bottom action bar ----
      bottomSheet: Container(
        padding: EdgeInsets.fromLTRB(
          16,
          12,
          16,
          12 + MediaQuery.of(context).padding.bottom,
        ),
        decoration: BoxDecoration(
          color: colorScheme.surface,
          border: Border(
            top: BorderSide(color: colorScheme.outlineVariant),
          ),
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (draft.isPayment && selectedLeaseId == null && !isTerminal)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Text(
                  'Select a lease above to enable payment creation.',
                  style: TextStyle(
                    fontSize: 12,
                    color: Colors.amber.shade700,
                  ),
                  textAlign: TextAlign.center,
                ),
              ),
            Row(
              children: [
                Expanded(
                  child: FilledButton(
                    onPressed: confirmEnabled ? onConfirm : null,
                    child: confirming
                        ? const SizedBox(
                            width: 18,
                            height: 18,
                            child: CircularProgressIndicator(
                              strokeWidth: 2,
                              color: Colors.white,
                            ),
                          )
                        : Text(
                            draft.isPayment
                                ? 'Create Payment'
                                : 'Confirm & Create Expense',
                          ),
                  ),
                ),
                const SizedBox(width: 12),
                OutlinedButton(
                  onPressed: rejectEnabled ? onReject : null,
                  child: rejecting
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Text('Reject'),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _DocumentPreview — fetches image bytes via Dio (bearer-authed)
// ---------------------------------------------------------------------------

class _DocumentPreview extends ConsumerWidget {
  const _DocumentPreview({required this.draftId});

  final int draftId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final imageAsync = ref.watch(_imageProvider(draftId));

    return Container(
      height: 240,
      color: Colors.grey.shade100,
      child: imageAsync.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(Icons.broken_image_outlined,
                  color: Colors.grey.shade400, size: 48),
              const SizedBox(height: 8),
              Text(
                'Preview unavailable',
                style: TextStyle(color: Colors.grey.shade500),
              ),
            ],
          ),
        ),
        data: (bytes) => InteractiveViewer(
          child: Image.memory(
            bytes,
            fit: BoxFit.contain,
            // Decode at preview scale (not full sensor resolution) so a large
            // stored image doesn't pin the CPU on lower-end / throttled devices.
            cacheHeight: 1080,
            errorBuilder: (ctx, e, _) => Center(
              child: Icon(Icons.picture_as_pdf_outlined,
                  size: 64, color: Colors.grey.shade400),
            ),
          ),
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _LeaseSelector
// ---------------------------------------------------------------------------

class _LeaseSelector extends ConsumerWidget {
  const _LeaseSelector({
    required this.selectedLeaseId,
    required this.onLeaseSelected,
  });

  final int? selectedLeaseId;
  final ValueChanged<int?> onLeaseSelected;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final leasesAsync = ref.watch(_leasesProvider);
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Which lease is this payment for?',
            style: theme.textTheme.labelLarge?.copyWith(
              fontWeight: FontWeight.w600,
            ),
          ),
          const SizedBox(height: 2),
          Text(
            'Required to record this payment.',
            style: theme.textTheme.bodySmall?.copyWith(
              color: colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 8),
          leasesAsync.when(
            loading: () => const LinearProgressIndicator(),
            error: (e, _) => Text(
              'Could not load leases.',
              style: TextStyle(color: colorScheme.error),
            ),
            data: (leases) => DropdownButtonFormField<int>(
              initialValue: selectedLeaseId,
              hint: const Text('Select a lease'),
              isExpanded: true,
              decoration: const InputDecoration(
                border: OutlineInputBorder(),
                contentPadding:
                    EdgeInsets.symmetric(horizontal: 12, vertical: 10),
              ),
              items: leases.map((l) {
                final id = (l['id'] as num).toInt();
                final number = l['leaseNumber'] as String? ?? '';
                final tenant = l['tenantName'] as String?;
                final unit = l['unitNumber'] as String?;
                final label = '#$number'
                    '${tenant != null ? ' — $tenant' : ''}'
                    '${unit != null ? ' · Unit $unit' : ''}';
                return DropdownMenuItem(value: id, child: Text(label));
              }).toList(),
              onChanged: onLeaseSelected,
            ),
          ),
          const SizedBox(height: 4),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _FieldsSection — grouped editable fields with confidence indicators
// ---------------------------------------------------------------------------

class _FieldsSection extends StatelessWidget {
  const _FieldsSection({
    required this.draft,
    required this.editedFields,
    required this.onFieldChanged,
  });

  final ScanDraft draft;
  final Map<String, String> editedFields;
  final void Function(String, String) onFieldChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final fieldMap = {for (final f in draft.scalarFields) f.name: f};

    // Build ordered groups
    final groups = <({String label, List<ScanField> fields})>[];
    for (final g in _fieldGroups) {
      final present =
          g.fields.map((n) => fieldMap[n]).whereType<ScanField>().toList();
      if (present.isNotEmpty) {
        groups.add((label: g.label, fields: present));
      }
    }
    // "Other" group
    final otherFields =
        draft.scalarFields.where((f) => !_knownScalarFields.contains(f.name)).toList();
    if (otherFields.isNotEmpty) {
      groups.add((label: 'Other', fields: otherFields));
    }

    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: groups.map((group) {
          return Padding(
            padding: const EdgeInsets.only(bottom: 24),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  group.label.toUpperCase(),
                  style: theme.textTheme.labelSmall?.copyWith(
                    fontWeight: FontWeight.w700,
                    letterSpacing: 0.8,
                    color: theme.colorScheme.onSurfaceVariant,
                  ),
                ),
                const SizedBox(height: 8),
                ...group.fields.map(
                  (field) => Padding(
                    padding: const EdgeInsets.only(bottom: 12),
                    child: _FieldInput(
                      field: field,
                      value: editedFields[field.name] ?? field.value,
                      onChanged: (v) => onFieldChanged(field.name, v),
                    ),
                  ),
                ),
              ],
            ),
          );
        }).toList(),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _FieldInput — editable field with confidence indicator
// ---------------------------------------------------------------------------

class _FieldInput extends StatefulWidget {
  const _FieldInput({
    required this.field,
    required this.value,
    required this.onChanged,
  });

  final ScanField field;
  final String value;
  final ValueChanged<String> onChanged;

  @override
  State<_FieldInput> createState() => _FieldInputState();
}

class _FieldInputState extends State<_FieldInput> {
  late TextEditingController _controller;

  @override
  void initState() {
    super.initState();
    _controller = TextEditingController(text: widget.value);
  }

  @override
  void didUpdateWidget(_FieldInput old) {
    super.didUpdateWidget(old);
    if (old.value != widget.value && _controller.text != widget.value) {
      _controller.text = widget.value;
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final level = widget.field.confidenceLevel;

    final labelColor = level == ConfidenceLevel.low
        ? colorScheme.error
        : level == ConfidenceLevel.medium
            ? Colors.amber.shade700
            : colorScheme.onSurface;

    final fieldLabel = widget.field.name.replaceAll('_', ' ');

    // Category field gets a dropdown
    if (widget.field.name == 'category') {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _FieldLabel(
            label: fieldLabel,
            level: level,
            labelColor: labelColor,
          ),
          DropdownButtonFormField<String>(
            initialValue: _scheduleECategories.contains(widget.value)
                ? widget.value
                : null,
            hint: const Text('Select category'),
            isExpanded: true,
            decoration: const InputDecoration(
              border: OutlineInputBorder(),
              contentPadding:
                  EdgeInsets.symmetric(horizontal: 12, vertical: 10),
            ),
            items: _scheduleECategories
                .map((c) => DropdownMenuItem(value: c, child: Text(c)))
                .toList(),
            onChanged: (v) {
              if (v != null) {
                _controller.text = v;
                widget.onChanged(v);
              }
            },
          ),
        ],
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _FieldLabel(
          label: fieldLabel,
          level: level,
          labelColor: labelColor,
        ),
        TextFormField(
          controller: _controller,
          onChanged: widget.onChanged,
          decoration: InputDecoration(
            border: const OutlineInputBorder(),
            contentPadding:
                const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
            enabledBorder: level == ConfidenceLevel.low
                ? OutlineInputBorder(
                    borderSide: BorderSide(color: colorScheme.error),
                  )
                : level == ConfidenceLevel.medium
                    ? OutlineInputBorder(
                        borderSide: BorderSide(color: Colors.amber.shade600),
                      )
                    : null,
          ),
        ),
      ],
    );
  }
}

class _FieldLabel extends StatelessWidget {
  const _FieldLabel({
    required this.label,
    required this.level,
    required this.labelColor,
  });

  final String label;
  final ConfidenceLevel level;
  final Color labelColor;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 4),
      child: Row(
        children: [
          Text(
            label,
            style: TextStyle(
              fontSize: 12,
              fontWeight: FontWeight.w500,
              color: labelColor,
            ),
          ),
          if (level != ConfidenceLevel.high) ...[
            const SizedBox(width: 6),
            Container(
              padding:
                  const EdgeInsets.symmetric(horizontal: 6, vertical: 1),
              decoration: BoxDecoration(
                color: level == ConfidenceLevel.low
                    ? Colors.red.shade100
                    : Colors.amber.shade100,
                borderRadius: BorderRadius.circular(4),
              ),
              child: Text(
                level == ConfidenceLevel.low ? 'Low confidence' : 'Medium confidence',
                style: TextStyle(
                  fontSize: 10,
                  color: level == ConfidenceLevel.low
                      ? Colors.red.shade800
                      : Colors.amber.shade800,
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _ManualEntrySection — shown when no fields were extracted
// ---------------------------------------------------------------------------

class _ManualEntrySection extends StatelessWidget {
  const _ManualEntrySection({
    required this.editedFields,
    required this.onFieldChanged,
  });

  final Map<String, String> editedFields;
  final void Function(String, String) onFieldChanged;

  static const _manualFields = [
    'vendor_name',
    'total',
    'subtotal',
    'tax',
    'transaction_date',
    'category',
    'payment_method',
    'notes',
  ];

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'ENTER DETAILS',
            style: Theme.of(context).textTheme.labelSmall?.copyWith(
                  fontWeight: FontWeight.w700,
                  letterSpacing: 0.8,
                  color: Theme.of(context).colorScheme.onSurfaceVariant,
                ),
          ),
          const SizedBox(height: 8),
          ..._manualFields.map((name) {
            // Synthesise a placeholder ScanField with high confidence.
            final field = ScanField(
              name: name,
              value: editedFields[name] ?? '',
              confidence: 1.0,
            );
            return Padding(
              padding: const EdgeInsets.only(bottom: 12),
              child: _FieldInput(
                field: field,
                value: editedFields[name] ?? '',
                onChanged: (v) => onFieldChanged(name, v),
              ),
            );
          }),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _LineItemsSection — read-only
// ---------------------------------------------------------------------------

class _LineItemsSection extends StatelessWidget {
  const _LineItemsSection({required this.items});

  final List<ScanLineItem> items;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'LINE ITEMS',
            style: theme.textTheme.labelSmall?.copyWith(
              fontWeight: FontWeight.w700,
              letterSpacing: 0.8,
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 8),
          Table(
            columnWidths: const {
              0: FlexColumnWidth(3),
              1: FlexColumnWidth(1),
              2: FlexColumnWidth(1.5),
              3: FlexColumnWidth(1.5),
            },
            children: [
              TableRow(
                decoration: BoxDecoration(
                  color: theme.colorScheme.surfaceContainerHighest,
                ),
                children: const [
                  _TableHeaderCell(text: 'Description'),
                  _TableHeaderCell(text: 'Qty', align: TextAlign.right),
                  _TableHeaderCell(text: 'Unit', align: TextAlign.right),
                  _TableHeaderCell(text: 'Amount', align: TextAlign.right),
                ],
              ),
              ...items.map(
                (item) => TableRow(
                  children: [
                    Padding(
                      padding: const EdgeInsets.all(8),
                      child:
                          Text(item.description ?? '', style: theme.textTheme.bodySmall),
                    ),
                    Padding(
                      padding: const EdgeInsets.all(8),
                      child: Text(
                        _fmtNum(item.quantity),
                        textAlign: TextAlign.right,
                        style: theme.textTheme.bodySmall,
                      ),
                    ),
                    Padding(
                      padding: const EdgeInsets.all(8),
                      child: Text(
                        _fmtMoney(item.unitPrice),
                        textAlign: TextAlign.right,
                        style: theme.textTheme.bodySmall,
                      ),
                    ),
                    Padding(
                      padding: const EdgeInsets.all(8),
                      child: Text(
                        _fmtMoney(item.amount),
                        textAlign: TextAlign.right,
                        style: theme.textTheme.bodySmall,
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }

  String _fmtNum(double? v) => v == null ? '' : v.toStringAsFixed(0);
  String _fmtMoney(double? v) =>
      v == null ? '' : v.toStringAsFixed(2);
}

class _TableHeaderCell extends StatelessWidget {
  const _TableHeaderCell({
    required this.text,
    this.align = TextAlign.left,
  });

  final String text;
  final TextAlign align;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
      child: Text(
        text,
        textAlign: align,
        style: const TextStyle(
          fontSize: 11,
          fontWeight: FontWeight.w700,
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _PaidToggle — Expense paid/unpaid selector
// ---------------------------------------------------------------------------

class _PaidToggle extends StatelessWidget {
  const _PaidToggle({
    required this.isPaid,
    required this.dueDate,
    required this.onChanged,
  });

  final bool isPaid;
  final String? dueDate;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Payment status',
            style: theme.textTheme.labelLarge?.copyWith(
              fontWeight: FontWeight.w600,
            ),
          ),
          const SizedBox(height: 8),
          SegmentedButton<bool>(
            segments: [
              const ButtonSegment(
                value: true,
                icon: Icon(Icons.check_circle_outline, size: 16),
                label: Text('Already paid'),
              ),
              ButtonSegment(
                value: false,
                icon: const Icon(Icons.schedule, size: 16),
                label: Text(
                  dueDate != null && dueDate!.isNotEmpty
                      ? 'Unpaid — due $dueDate'
                      : 'Unpaid bill',
                ),
              ),
            ],
            selected: {isPaid},
            onSelectionChanged: (s) => onChanged(s.first),
          ),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _Banner — coloured informational strip
// ---------------------------------------------------------------------------

class _Banner extends StatelessWidget {
  const _Banner({
    required this.color,
    required this.borderColor,
    required this.textColor,
    required this.child,
  });

  final Color color;
  final Color borderColor;
  final Color textColor;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.all(16),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: color,
        border: Border.all(color: borderColor),
        borderRadius: BorderRadius.circular(8),
      ),
      child: DefaultTextStyle(
        style: TextStyle(fontSize: 13, color: textColor),
        child: child,
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _StatusChip
// ---------------------------------------------------------------------------

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.status});

  final String status;

  String _label(String s) {
    switch (s) {
      case 'Pending':
        return 'Processing';
      case 'Reviewing':
        return 'Ready to review';
      case 'Confirmed':
        return 'Confirmed';
      case 'Failed':
        return 'Failed';
      case 'Rejected':
        return 'Rejected';
      default:
        return s;
    }
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final (bg, fg) = switch (status) {
      'Pending' => (Colors.amber.shade100, Colors.amber.shade900),
      'Reviewing' => (Colors.blue.shade100, Colors.blue.shade900),
      'Confirmed' => (Colors.green.shade100, Colors.green.shade900),
      'Failed' || 'Rejected' => (cs.errorContainer, cs.onErrorContainer),
      _ => (cs.surfaceContainerHighest, cs.onSurface),
    };

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Text(
        _label(status),
        style: TextStyle(
            fontSize: 11, fontWeight: FontWeight.w600, color: fg),
      ),
    );
  }
}
