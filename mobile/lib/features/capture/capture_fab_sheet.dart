import 'dart:typed_data';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/api/api_exception.dart';
import '../../core/theme/app_recipes.dart';
import '../payments/payments_screen.dart';
import '../scan/guided_rental_flow.dart';
import '../scan/pdf_stitch.dart';
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
///   Scan a lease   → photos (stitched PDF) OR an imported PDF → guided flow
///   Tell me        → voice capture screen
///   Type it        → record a payment manually (no-paper intake)
/// How the user wants to bring in a multi-page document (lease / application):
/// snap each page as photos (stitched into one PDF) or import an existing PDF.
enum _DocSource { photos, pdf }

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
    // The scan pipeline accepts PDF bytes on /scans; multi-photo lease
    // assembly shipped via `_scanLease` (pdf_stitch.dart). On-device
    // doc-scanner remains a later enhancement (see review §B2).
    await _uploadAndReview(bytes, file.name, _mime(file.name));
  }

  /// A lease can be captured as multi-page phone photos OR imported as a PDF.
  /// Tapping "Scan a lease" first asks which; both paths upload as a `LeaseAgreement`
  /// draft so the server runs the lease schema (not the receipt/expense one)
  /// and the draft routes to the guided "New rental from your lease" flow.
  Future<void> _scanLease() async {
    final choice = await _pickDocSource(
      photosSubtitle: 'Snap each page of the lease',
      pdfSubtitle: 'Import an existing lease PDF',
    );
    if (choice == null || !mounted) return;
    switch (choice) {
      case _DocSource.photos:
        await _scanLeaseFromPhotos();
      case _DocSource.pdf:
        await _scanLeaseFromFile();
    }
  }

  /// "Scan an application": import a COMPLETED paper rental application (multi-
  /// page photos stitched to a PDF, OR an existing PDF) as an `Application` draft
  /// so the worker runs [ApplicationExtractionSchema] and the review screen shows
  /// applicant fields. Confirming creates a RentalApplication — mirrors web.
  Future<void> _scanApplication() async {
    final choice = await _pickDocSource(
      photosSubtitle: 'Snap each page of the application',
      pdfSubtitle: 'Import an existing application PDF',
    );
    if (choice == null || !mounted) return;
    switch (choice) {
      case _DocSource.photos:
        await _scanApplicationFromPhotos();
      case _DocSource.pdf:
        await _scanApplicationFromFile();
    }
  }

  /// Shared "photos OR PDF" source chooser used by lease/application scan-IN.
  Future<_DocSource?> _pickDocSource({
    required String photosSubtitle,
    required String pdfSubtitle,
  }) {
    return showModalBottomSheet<_DocSource>(
      context: context,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (sheetCtx) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            ListTile(
              leading: const Icon(Symbols.photo_camera_rounded),
              title: const Text('Take photos'),
              subtitle: Text(photosSubtitle),
              onTap: () => Navigator.of(sheetCtx).pop(_DocSource.photos),
            ),
            ListTile(
              leading: const Icon(Symbols.picture_as_pdf_rounded),
              title: const Text('Pick a PDF'),
              subtitle: Text(pdfSubtitle),
              onTap: () => Navigator.of(sheetCtx).pop(_DocSource.pdf),
            ),
          ],
        ),
      ),
    );
  }

  /// Captures MANY photos of a lease, stitches them into one PDF client-side,
  /// then uploads as a LeaseAgreement draft and opens the guided flow.
  Future<void> _scanLeaseFromPhotos() async {
    final picked = await ImagePicker().pickMultiImage(
      imageQuality: 80,
      maxWidth: 1600,
      maxHeight: 1600,
    );
    if (picked.isEmpty) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final images = <Uint8List>[];
      for (final x in picked) {
        images.add(Uint8List.fromList(await x.readAsBytes()));
      }
      final pdf = await stitchImagesToPdf(images);
      await _uploadLeaseAndOpen(pdf, 'lease-scan.pdf', 'application/pdf');
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
        _error = "Couldn't read that lease. Try clearer photos or a PDF.";
      });
    }
  }

  /// Imports an existing lease PDF (or image) and uploads it as a LeaseAgreement draft so
  /// it routes to the guided flow rather than being misclassified as an expense.
  Future<void> _scanLeaseFromFile() async {
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
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await _uploadLeaseAndOpen(bytes, file.name, _mime(file.name));
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
        _error = "Couldn't read that lease. Try a clearer PDF.";
      });
    }
  }

  /// Uploads lease bytes as a `LeaseAgreement` draft, closes the sheet, and opens the
  /// guided rental flow for the new draft.
  Future<void> _uploadLeaseAndOpen(
    Uint8List bytes,
    String filename,
    String contentType,
  ) async {
    final created = await ref
        .read(scanRepositoryProvider)
        .uploadImage(
          bytes,
          filename,
          contentType,
          targetEntityType: 'LeaseAgreement',
        );
    if (!mounted) return;
    final navigator = Navigator.of(context);
    navigator.pop(); // close the capture sheet
    await GuidedRentalFlow.open(navigator.context, created.draftId);
  }

  /// Captures MANY photos of an application, stitches them into one PDF client-
  /// side, uploads as an Application draft, and opens the scan-review screen.
  Future<void> _scanApplicationFromPhotos() async {
    final picked = await ImagePicker().pickMultiImage(
      imageQuality: 80,
      maxWidth: 1600,
      maxHeight: 1600,
    );
    if (picked.isEmpty) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final images = <Uint8List>[];
      for (final x in picked) {
        images.add(Uint8List.fromList(await x.readAsBytes()));
      }
      final pdf = await stitchImagesToPdf(images);
      await _uploadApplicationAndReview(
        pdf,
        'application-scan.pdf',
        'application/pdf',
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
        _error = "Couldn't read that application. Try clearer photos or a PDF.";
      });
    }
  }

  /// Imports an existing application PDF (or image) and uploads it as an
  /// Application draft so it routes to the applicant review screen.
  Future<void> _scanApplicationFromFile() async {
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
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await _uploadApplicationAndReview(bytes, file.name, _mime(file.name));
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
        _error = "Couldn't read that application. Try a clearer PDF.";
      });
    }
  }

  /// Uploads bytes as an `Application` draft, closes the sheet, and opens the
  /// scan-review screen (applications use the review screen, not the guided
  /// rental flow). The server routes confirm by the draft's TargetEntityType.
  Future<void> _uploadApplicationAndReview(
    Uint8List bytes,
    String filename,
    String contentType,
  ) async {
    final created = await ref
        .read(scanRepositoryProvider)
        .uploadImage(
          bytes,
          filename,
          contentType,
          targetEntityType: 'Application',
        );
    if (!mounted) return;
    final navigator = Navigator.of(context);
    navigator.pop(); // close the capture sheet
    await navigator.push<void>(
      MaterialPageRoute<void>(
        builder: (_) => ScanReviewScreen(draftId: created.draftId),
      ),
    );
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
                Expanded(
                  child: _CaptureTile(
                    icon: Symbols.home_work_rounded,
                    label: 'Scan a lease',
                    family: M3TonalFamily.rose,
                    onTap: _scanLease,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 10),
            Row(
              children: [
                Expanded(
                  child: _CaptureTile(
                    icon: Symbols.assignment_ind_rounded,
                    label: 'Scan an application',
                    family: M3TonalFamily.sky,
                    onTap: _scanApplication,
                  ),
                ),
                const SizedBox(width: 10),
                // Spacers keep the application tile the same width as the tiles
                // in the rows above (3-up grid) rather than stretching full-width.
                const Spacer(),
                const SizedBox(width: 10),
                const Spacer(),
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
            style: Theme.of(
              context,
            ).textTheme.labelMedium?.copyWith(color: cs.onSurface),
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
