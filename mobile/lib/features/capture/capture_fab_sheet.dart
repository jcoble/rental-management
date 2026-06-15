import 'dart:typed_data';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/api/api_exception.dart';
import '../../core/theme/app_recipes.dart';
import '../payments/payments_screen.dart';
import '../scan/scan_repository.dart';
import '../scan/scan_review_screen.dart';
import '../voice/tell_me_screen.dart';

/// The TSK-138 Material 3 expressive capture menu, opened by the center-docked
/// Capture FAB. Every item routes into a real flow — no dead `(){}` handlers.
///
/// Items:
///   Scan (camera)  → photo → scan draft → review
///   Gallery        → pick image → scan draft → review
///   PDF / file     → file picker (pdf/image) → scan draft → review
///   Tell me        → voice capture screen
///   Type it        → record a payment manually (no-paper intake)
class CaptureFabSheet extends ConsumerStatefulWidget {
  const CaptureFabSheet({super.key});

  @override
  ConsumerState<CaptureFabSheet> createState() => _CaptureFabSheetState();
}

class _CaptureFabSheetState extends ConsumerState<CaptureFabSheet> {
  bool _busy = false;
  String? _error;

  /// Uploads picked image bytes as a scan draft, closes the sheet, then opens
  /// the review screen for the new draft.
  Future<void> _uploadAndReview(
    Uint8List bytes,
    String filename,
    String contentType,
  ) async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final created = await ref
          .read(scanRepositoryProvider)
          .uploadImage(bytes, filename, contentType);
      if (!mounted) return;
      final navigator = Navigator.of(context);
      navigator.pop(); // close the capture sheet
      await navigator.push<void>(
        MaterialPageRoute<void>(
          builder: (_) => ScanReviewScreen(draftId: created.draftId),
        ),
      );
    } on ApiException catch (e) {
      if (!mounted) return;
      setState(() {
        _busy = false;
        _error = e.message;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _busy = false;
        _error = 'Something went wrong. Please try again.';
      });
    }
  }

  Future<void> _pickImage(ImageSource source) async {
    final picked = await ImagePicker().pickImage(
      source: source,
      imageQuality: 80,
      maxWidth: 1600,
      maxHeight: 1600,
    );
    if (picked == null) return;
    final bytes = Uint8List.fromList(await picked.readAsBytes());
    await _uploadAndReview(bytes, picked.name, _mime(picked.name));
  }

  Future<void> _pickFile() async {
    final result = await FilePicker.platform.pickFiles(
      type: FileType.custom,
      allowedExtensions: const ['pdf', 'jpg', 'jpeg', 'png', 'webp', 'heic'],
      withData: true,
    );
    final file = result?.files.single;
    if (file == null) return;
    final bytes = file.bytes;
    if (bytes == null) {
      setState(() => _error = "Couldn't read the selected file.");
      return;
    }
    // NOTE (P0): the scan pipeline accepts PDF bytes on /scans; deeper
    // multi-page PDF assembly + on-device doc-scanner is P1 (see review §B2).
    await _uploadAndReview(bytes, file.name, _mime(file.name));
  }

  String _mime(String filename) {
    final lower = filename.toLowerCase();
    if (lower.endsWith('.png')) return 'image/png';
    if (lower.endsWith('.webp')) return 'image/webp';
    if (lower.endsWith('.heic')) return 'image/heic';
    if (lower.endsWith('.pdf')) return 'application/pdf';
    return 'image/jpeg';
  }

  void _openVoice() {
    final navigator = Navigator.of(context);
    navigator.pop();
    navigator.push<void>(
      MaterialPageRoute<void>(builder: (_) => const TellMeScreen()),
    );
  }

  void _typeIt() {
    final navigator = Navigator.of(context);
    navigator.pop();
    navigator.push<void>(
      MaterialPageRoute<void>(builder: (_) => const PaymentsScreen()),
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    if (_busy) {
      return const Padding(
        padding: EdgeInsets.symmetric(vertical: 48),
        child: Center(child: CircularProgressIndicator()),
      );
    }

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 12, 16, 24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              width: 36,
              height: 4,
              margin: const EdgeInsets.only(bottom: 16),
              decoration: BoxDecoration(
                color: cs.outlineVariant,
                borderRadius: BorderRadius.circular(2),
              ),
            ),
            Text(
              'Scan / Add',
              style: theme.textTheme.titleLarge?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 4),
            Text(
              'Snap it, say it, or type it — the app does the rest.',
              style: theme.textTheme.bodySmall?.copyWith(
                color: cs.onSurfaceVariant,
              ),
              textAlign: TextAlign.center,
            ),
            if (_error != null) ...[
              const SizedBox(height: 12),
              Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: cs.errorContainer,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Text(
                  _error!,
                  style: TextStyle(color: cs.onErrorContainer),
                ),
              ),
            ],
            const SizedBox(height: 20),
            Row(
              children: [
                Expanded(
                  child: _CaptureTile(
                    icon: Symbols.photo_camera_rounded,
                    label: 'Scan',
                    family: M3TonalFamily.sky,
                    onTap: () => _pickImage(ImageSource.camera),
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: _CaptureTile(
                    icon: Symbols.photo_library_rounded,
                    label: 'Gallery',
                    family: M3TonalFamily.mint,
                    onTap: () => _pickImage(ImageSource.gallery),
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: _CaptureTile(
                    icon: Symbols.picture_as_pdf_rounded,
                    label: 'PDF / file',
                    family: M3TonalFamily.amber,
                    onTap: _pickFile,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 10),
            Row(
              children: [
                Expanded(
                  child: _CaptureTile(
                    icon: Symbols.mic_rounded,
                    label: 'Tell me',
                    family: M3TonalFamily.violet,
                    onTap: _openVoice,
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: _CaptureTile(
                    icon: Symbols.keyboard_rounded,
                    label: 'Type it',
                    family: M3TonalFamily.coral,
                    onTap: _typeIt,
                  ),
                ),
                const SizedBox(width: 10),
                const Expanded(child: SizedBox.shrink()),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _CaptureTile extends StatelessWidget {
  const _CaptureTile({
    required this.icon,
    required this.label,
    required this.family,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final M3TonalFamily family;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return M3TonalCard(
      family: family,
      padding: const EdgeInsets.symmetric(vertical: 18, horizontal: 8),
      onTap: onTap,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, color: cs.onSurface, size: 26, fill: 1),
          const SizedBox(height: 8),
          Text(
            label,
            textAlign: TextAlign.center,
            style: Theme.of(context).textTheme.labelMedium?.copyWith(
                  color: cs.onSurface,
                ),
          ),
        ],
      ),
    );
  }
}

/// Opens the [CaptureFabSheet] as a modal bottom sheet.
Future<void> showCaptureFabSheet(BuildContext context) {
  return showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    showDragHandle: false,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(28)),
    ),
    builder: (_) => const CaptureFabSheet(),
  );
}
