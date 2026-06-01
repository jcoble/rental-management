import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';

import '../../core/api/api_exception.dart';
import 'scan_repository.dart';

/// Handles picking an image (camera or gallery), uploading it, then navigating
/// to the review screen for the newly created draft.
///
/// This is a helper widget — host it as a modal bottom sheet or call the static
/// helpers directly from [ScanListScreen].
class ScanCaptureSheet extends ConsumerStatefulWidget {
  const ScanCaptureSheet({super.key});

  @override
  ConsumerState<ScanCaptureSheet> createState() => _ScanCaptureSheetState();
}

class _ScanCaptureSheetState extends ConsumerState<ScanCaptureSheet> {
  bool _uploading = false;
  double _uploadProgress = 0;
  String? _error;

  /// What kind of record this scan will become: 'Expense' (receipt/bill) or
  /// 'Payment' (rent check). Drives the LLM extraction + confirm flow. Defaults
  /// to 'Expense' since receipts/bills are the most common capture.
  String _targetEntityType = 'Expense';

  Future<void> _pick(ImageSource source) async {
    final picker = ImagePicker();
    final XFile? picked = await picker.pickImage(
      source: source,
      // Documents only need enough resolution for legible text + extraction;
      // smaller files upload/store/retrieve faster and decode cheaper on-device.
      imageQuality: 80,
      maxWidth: 1600,
      maxHeight: 1600,
    );
    if (picked == null) return; // user cancelled

    setState(() {
      _uploading = true;
      _uploadProgress = 0;
      _error = null;
    });

    try {
      final bytes = Uint8List.fromList(await picked.readAsBytes());
      final contentType = _mimeFromExtension(picked.name);

      final created = await ref.read(scanRepositoryProvider).uploadImage(
            bytes,
            picked.name,
            contentType,
            targetEntityType: _targetEntityType,
            onSendProgress: (progress) {
              if (mounted) setState(() => _uploadProgress = progress);
            },
          );

      if (!mounted) return;
      // Close the sheet and pass the new draft id back.
      Navigator.of(context).pop(created.draftId);
    } on ApiException catch (e) {
      if (!mounted) return;
      setState(() {
        _uploading = false;
        _error = e.message;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _uploading = false;
        _error = 'Something went wrong. Please try again.';
      });
    }
  }

  String _mimeFromExtension(String filename) {
    final lower = filename.toLowerCase();
    if (lower.endsWith('.jpg') || lower.endsWith('.jpeg')) return 'image/jpeg';
    if (lower.endsWith('.png')) return 'image/png';
    if (lower.endsWith('.webp')) return 'image/webp';
    if (lower.endsWith('.heic')) return 'image/heic';
    if (lower.endsWith('.pdf')) return 'application/pdf';
    return 'image/jpeg'; // safe fallback for camera shots
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    if (_uploading) {
      return Padding(
        padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const SizedBox(height: 8),
            Text('Uploading…', style: theme.textTheme.titleMedium),
            const SizedBox(height: 20),
            LinearProgressIndicator(value: _uploadProgress > 0 ? _uploadProgress : null),
            const SizedBox(height: 8),
            Text(
              _uploadProgress > 0
                  ? '${(_uploadProgress * 100).toStringAsFixed(0)}%'
                  : 'Preparing…',
              style: theme.textTheme.bodySmall?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 16),
          ],
        ),
      );
    }

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              'Scan a document',
              style: theme.textTheme.titleLarge?.copyWith(
                fontWeight: FontWeight.w700,
              ),
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 8),
            Text(
              'Take a photo of a receipt, bill, or check.\nThe app will read the details for you.',
              style: theme.textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
              textAlign: TextAlign.center,
            ),
            if (_error != null) ...[
              const SizedBox(height: 12),
              Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: colorScheme.errorContainer,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Text(
                  _error!,
                  style: TextStyle(color: colorScheme.onErrorContainer),
                ),
              ),
            ],
            const SizedBox(height: 24),
            Text(
              'What are you scanning?',
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w600,
              ),
            ),
            const SizedBox(height: 8),
            _DocTypeSelector(
              selected: _targetEntityType,
              onChanged: (value) => setState(() => _targetEntityType = value),
            ),
            const SizedBox(height: 24),
            FilledButton.icon(
              icon: const Icon(Icons.camera_alt_outlined),
              label: const Text('Take a photo'),
              onPressed: () => _pick(ImageSource.camera),
            ),
            const SizedBox(height: 12),
            OutlinedButton.icon(
              icon: const Icon(Icons.photo_library_outlined),
              label: const Text('Choose from gallery'),
              onPressed: () => _pick(ImageSource.gallery),
            ),
            const SizedBox(height: 8),
          ],
        ),
      ),
    );
  }
}

/// Two large, thumb-friendly toggle buttons that pick what kind of record this
/// scan should become: a Receipt/Bill ('Expense') or a Rent Check/Payment
/// ('Payment'). The choice flows through to the upload's `targetEntityType`.
class _DocTypeSelector extends StatelessWidget {
  const _DocTypeSelector({required this.selected, required this.onChanged});

  final String selected;
  final ValueChanged<String> onChanged;

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Expanded(
          child: _DocTypeOption(
            icon: Icons.receipt_long_outlined,
            label: 'Receipt / Bill',
            selected: selected == 'Expense',
            onTap: () => onChanged('Expense'),
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: _DocTypeOption(
            icon: Icons.payments_outlined,
            label: 'Rent Check / Payment',
            selected: selected == 'Payment',
            onTap: () => onChanged('Payment'),
          ),
        ),
      ],
    );
  }
}

class _DocTypeOption extends StatelessWidget {
  const _DocTypeOption({
    required this.icon,
    required this.label,
    required this.selected,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final fg = selected ? colorScheme.onPrimaryContainer : colorScheme.onSurface;

    return Material(
      color: selected ? colorScheme.primaryContainer : colorScheme.surface,
      borderRadius: BorderRadius.circular(12),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Container(
          // Tall enough for a comfortable thumb target.
          constraints: const BoxConstraints(minHeight: 96),
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 14),
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(12),
            border: Border.all(
              color: selected ? colorScheme.primary : colorScheme.outlineVariant,
              width: selected ? 2 : 1,
            ),
          ),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Icon(icon, color: fg, size: 28),
              const SizedBox(height: 8),
              Text(
                label,
                textAlign: TextAlign.center,
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: fg,
                  fontWeight: selected ? FontWeight.w700 : FontWeight.w500,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Shows [ScanCaptureSheet] as a modal bottom sheet and returns the new
/// draft id, or null if the user cancelled.
Future<int?> showScanCaptureSheet(BuildContext context) {
  return showModalBottomSheet<int>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    builder: (_) => const ScanCaptureSheet(),
  );
}
