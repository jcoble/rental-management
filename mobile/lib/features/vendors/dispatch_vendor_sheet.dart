import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'star_rating.dart';
import 'vendors_models.dart';
import 'vendors_repository.dart';

/// Opens the "Text a vendor" bottom sheet for a work order. Resolves to the
/// dispatched [Vendor] on success (so the caller can show a confirmation), or
/// `null` if dismissed.
Future<Vendor?> showDispatchVendorSheet(
  BuildContext context, {
  required int workOrderId,
}) {
  return showModalBottomSheet<Vendor>(
    context: context,
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) => _DispatchVendorSheet(workOrderId: workOrderId),
  );
}

/// Opens a vendor picker (no dispatch/text) and resolves to the chosen [Vendor],
/// or `null` if dismissed. Used by the work-order "Call a vendor" action so the
/// caller can dial the selected vendor's number.
Future<Vendor?> showSelectVendorSheet(BuildContext context) {
  return showModalBottomSheet<Vendor>(
    context: context,
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) => const _SelectVendorSheet(),
  );
}

class _DispatchVendorSheet extends ConsumerStatefulWidget {
  const _DispatchVendorSheet({required this.workOrderId});

  final int workOrderId;

  @override
  ConsumerState<_DispatchVendorSheet> createState() =>
      _DispatchVendorSheetState();
}

class _DispatchVendorSheetState extends ConsumerState<_DispatchVendorSheet> {
  final _noteCtrl = TextEditingController();
  int? _selectedVendorId;
  bool _sending = false;
  String? _error;

  @override
  void dispose() {
    _noteCtrl.dispose();
    super.dispose();
  }

  Future<void> _dispatch(List<Vendor> vendors) async {
    final id = _selectedVendorId;
    if (id == null) {
      setState(() => _error = 'Pick a vendor first.');
      return;
    }
    setState(() {
      _sending = true;
      _error = null;
    });
    try {
      await ref.read(vendorsRepositoryProvider).dispatch(
            widget.workOrderId,
            vendorId: id,
            note: _noteCtrl.text,
          );
      final vendor = vendors.firstWhere((v) => v.id == id);
      if (mounted) Navigator.of(context).pop(vendor);
    } on ApiException catch (e) {
      // Includes the 400 "Vendor has no phone number on file..." message.
      setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _sending = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;
    final vendorsAsync = ref.watch(vendorsProvider);

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottomPadding),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            'Text a vendor',
            style: theme.textTheme.titleLarge
                ?.copyWith(fontWeight: FontWeight.w700),
          ),
          const SizedBox(height: 4),
          Text(
            'They get the job by SMS and reply DONE to close it out.',
            style: theme.textTheme.bodyMedium
                ?.copyWith(color: cs.onSurfaceVariant),
          ),
          const SizedBox(height: 16),
          ConstrainedBox(
            constraints: BoxConstraints(
              maxHeight: MediaQuery.sizeOf(context).height * 0.4,
            ),
            child: vendorsAsync.when(
              loading: () => const Padding(
                padding: EdgeInsets.all(24),
                child: Center(child: CircularProgressIndicator()),
              ),
              error: (e, _) => Padding(
                padding: const EdgeInsets.all(16),
                child: Text(
                  e is ApiException ? e.message : e.toString(),
                  style: TextStyle(color: cs.error),
                ),
              ),
              data: (vendors) {
                if (vendors.isEmpty) {
                  return Padding(
                    padding: const EdgeInsets.symmetric(vertical: 24),
                    child: Text(
                      'No vendors yet. Add a vendor on the web first.',
                      textAlign: TextAlign.center,
                      style: TextStyle(color: cs.onSurfaceVariant),
                    ),
                  );
                }
                return ListView.separated(
                  shrinkWrap: true,
                  itemCount: vendors.length,
                  separatorBuilder: (_, _) => const SizedBox(height: 6),
                  itemBuilder: (_, i) => _VendorPickTile(
                    vendor: vendors[i],
                    selected: _selectedVendorId == vendors[i].id,
                    onTap: () => setState(() {
                      _selectedVendorId = vendors[i].id;
                      _error = null;
                    }),
                  ),
                );
              },
            ),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _noteCtrl,
            maxLines: 2,
            textCapitalization: TextCapitalization.sentences,
            decoration: const InputDecoration(
              labelText: 'Note (optional)',
              hintText: 'e.g. Gate code 1234, tenant home after 5pm',
            ),
          ),
          if (_error != null) ...[
            const SizedBox(height: 12),
            Container(
              padding:
                  const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
              decoration: BoxDecoration(
                color: cs.errorContainer,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Text(
                _error!,
                style: TextStyle(color: cs.onErrorContainer, fontSize: 13),
              ),
            ),
          ],
          const SizedBox(height: 20),
          Row(
            children: [
              Expanded(
                child: OutlinedButton(
                  onPressed:
                      _sending ? null : () => Navigator.of(context).pop(),
                  child: const Text('Cancel'),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: FilledButton.icon(
                  onPressed: _sending
                      ? null
                      : () => _dispatch(vendorsAsync.value ?? const []),
                  icon: _sending
                      ? const SizedBox(
                          height: 18,
                          width: 18,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Icons.sms_outlined, size: 18),
                  label: Text(_sending ? 'Sending...' : 'Send text'),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

/// Vendor picker that returns the chosen vendor (no dispatch). Tapping a vendor
/// resolves the sheet immediately so the caller can act on it (e.g. place a call).
class _SelectVendorSheet extends ConsumerWidget {
  const _SelectVendorSheet();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;
    final vendorsAsync = ref.watch(vendorsProvider);

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottomPadding),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            'Call a vendor',
            style: theme.textTheme.titleLarge
                ?.copyWith(fontWeight: FontWeight.w700),
          ),
          const SizedBox(height: 4),
          Text(
            'Pick a vendor to call from your phone.',
            style: theme.textTheme.bodyMedium
                ?.copyWith(color: cs.onSurfaceVariant),
          ),
          const SizedBox(height: 16),
          ConstrainedBox(
            constraints: BoxConstraints(
              maxHeight: MediaQuery.sizeOf(context).height * 0.45,
            ),
            child: vendorsAsync.when(
              loading: () => const Padding(
                padding: EdgeInsets.all(24),
                child: Center(child: CircularProgressIndicator()),
              ),
              error: (e, _) => Padding(
                padding: const EdgeInsets.all(16),
                child: Text(
                  e is ApiException ? e.message : e.toString(),
                  style: TextStyle(color: cs.error),
                ),
              ),
              data: (vendors) {
                if (vendors.isEmpty) {
                  return Padding(
                    padding: const EdgeInsets.symmetric(vertical: 24),
                    child: Text(
                      'No vendors yet. Add a vendor on the web first.',
                      textAlign: TextAlign.center,
                      style: TextStyle(color: cs.onSurfaceVariant),
                    ),
                  );
                }
                return ListView.separated(
                  shrinkWrap: true,
                  itemCount: vendors.length,
                  separatorBuilder: (_, _) => const SizedBox(height: 6),
                  itemBuilder: (_, i) => _VendorPickTile(
                    vendor: vendors[i],
                    selected: false,
                    onTap: () => Navigator.of(context).pop(vendors[i]),
                  ),
                );
              },
            ),
          ),
          const SizedBox(height: 16),
          OutlinedButton(
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('Cancel'),
          ),
        ],
      ),
    );
  }
}

class _VendorPickTile extends StatelessWidget {
  const _VendorPickTile({
    required this.vendor,
    required this.selected,
    required this.onTap,
  });

  final Vendor vendor;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final noPhone = !vendor.hasPhone;

    return Material(
      color: selected ? cs.primaryContainer : cs.surfaceContainerLowest,
      borderRadius: BorderRadius.circular(12),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(12),
            border: Border.all(
              color: selected ? cs.primary : cs.outlineVariant,
            ),
          ),
          child: Row(
            children: [
              Icon(
                selected
                    ? Icons.radio_button_checked
                    : Icons.radio_button_unchecked,
                size: 22,
                color: selected ? cs.primary : cs.onSurfaceVariant,
              ),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Flexible(
                          child: Text(
                            vendor.name,
                            style: theme.textTheme.titleSmall
                                ?.copyWith(fontWeight: FontWeight.w600),
                          ),
                        ),
                        if (vendor.preferred) ...[
                          const SizedBox(width: 6),
                          Icon(Icons.star_rounded,
                              size: 16, color: Colors.amber.shade600),
                        ],
                      ],
                    ),
                    const SizedBox(height: 2),
                    Row(
                      children: [
                        Text(
                          vendor.serviceType,
                          style: theme.textTheme.bodySmall
                              ?.copyWith(color: cs.onSurfaceVariant),
                        ),
                        const SizedBox(width: 8),
                        VendorRatingSummary(vendor: vendor, compact: true),
                      ],
                    ),
                    if (noPhone) ...[
                      const SizedBox(height: 4),
                      Text(
                        'No phone on file',
                        style: theme.textTheme.bodySmall
                            ?.copyWith(color: cs.error),
                      ),
                    ],
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Inline rating summary: stars + average + count, or "Not rated yet".
class VendorRatingSummary extends StatelessWidget {
  const VendorRatingSummary({
    super.key,
    required this.vendor,
    this.compact = false,
  });

  final Vendor vendor;
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final avg = vendor.averageRating;

    if (avg == null) {
      return Text(
        'Not rated yet',
        style: theme.textTheme.bodySmall?.copyWith(color: cs.onSurfaceVariant),
      );
    }

    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        StarRatingDisplay(rating: avg, size: compact ? 13 : 16),
        const SizedBox(width: 4),
        Text(
          '${avg.toStringAsFixed(1)} (${vendor.ratingCount})',
          style: theme.textTheme.bodySmall?.copyWith(
            color: cs.onSurfaceVariant,
            fontWeight: FontWeight.w500,
          ),
        ),
      ],
    );
  }
}
