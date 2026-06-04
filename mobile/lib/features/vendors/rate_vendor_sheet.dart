import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'star_rating.dart';
import 'vendors_repository.dart';

/// Opens the rate-a-vendor bottom sheet. Resolves to `true` when a rating was
/// recorded (so callers can refresh), or `null`/`false` if dismissed.
Future<bool?> showRateVendorSheet(
  BuildContext context, {
  required int vendorId,
  required String vendorName,
  int? workOrderId,
}) {
  return showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) => _RateVendorSheet(
      vendorId: vendorId,
      vendorName: vendorName,
      workOrderId: workOrderId,
    ),
  );
}

class _RateVendorSheet extends ConsumerStatefulWidget {
  const _RateVendorSheet({
    required this.vendorId,
    required this.vendorName,
    this.workOrderId,
  });

  final int vendorId;
  final String vendorName;
  final int? workOrderId;

  @override
  ConsumerState<_RateVendorSheet> createState() => _RateVendorSheetState();
}

class _RateVendorSheetState extends ConsumerState<_RateVendorSheet> {
  final _commentCtrl = TextEditingController();
  int _stars = 0;
  bool _saving = false;
  String? _error;

  @override
  void dispose() {
    _commentCtrl.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (_stars < 1) {
      setState(() => _error = 'Pick a star rating first.');
      return;
    }
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      await ref.read(vendorsRepositoryProvider).rate(
            widget.vendorId,
            stars: _stars,
            comment: _commentCtrl.text,
            workOrderId: widget.workOrderId,
          );
      // Refresh anything keyed off this vendor's aggregates.
      ref.invalidate(vendorsProvider);
      ref.invalidate(vendorScorecardProvider(widget.vendorId));
      if (mounted) Navigator.of(context).pop(true);
    } on ApiException catch (e) {
      setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottomPadding),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            'Rate ${widget.vendorName}',
            style: theme.textTheme.titleLarge
                ?.copyWith(fontWeight: FontWeight.w700),
          ),
          const SizedBox(height: 4),
          Text(
            'How was the work? Your rating shapes their scorecard.',
            style: theme.textTheme.bodyMedium
                ?.copyWith(color: cs.onSurfaceVariant),
          ),
          const SizedBox(height: 16),
          Center(
            child: StarRatingInput(
              value: _stars,
              onChanged: (v) => setState(() {
                _stars = v;
                _error = null;
              }),
            ),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _commentCtrl,
            maxLines: 3,
            textCapitalization: TextCapitalization.sentences,
            decoration: const InputDecoration(
              labelText: 'Comment (optional)',
              hintText: 'e.g. Fast, tidy, fair price...',
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
                style:
                    TextStyle(color: cs.onErrorContainer, fontSize: 13),
              ),
            ),
          ],
          const SizedBox(height: 20),
          Row(
            children: [
              Expanded(
                child: OutlinedButton(
                  onPressed:
                      _saving ? null : () => Navigator.of(context).pop(),
                  child: const Text('Cancel'),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: FilledButton(
                  onPressed: _saving ? null : _submit,
                  child: _saving
                      ? const SizedBox(
                          height: 20,
                          width: 20,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Text('Submit rating'),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}
