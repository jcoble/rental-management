import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:image_picker/image_picker.dart';
import 'package:record/record.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../properties/properties_repository.dart';
import '../voice/voice_error_message.dart';
import 'pdf_stitch.dart';
import 'scan_models.dart';
import 'scan_repository.dart';
import 'scan_target_options.dart';
import 'scan_upload_operation.dart';

/// Handles picking an image (camera or gallery), uploading it, then navigating
/// to the review screen for the newly created draft.
///
/// This is a helper widget — host it as a modal bottom sheet or call the static
/// helpers directly from [ScanListScreen].
class ScanCaptureSheet extends ConsumerStatefulWidget {
  const ScanCaptureSheet({
    super.key,
    this.initialTargetEntityType = '',
    this.lockTargetEntityType = false,
    this.propertyId,
    this.unitId,
    this.leaseManagementId,
    this.leaseAgreementId,
    this.tenantAccountId,
    this.tenantLedgerEntryId,
    this.workOrderId,
    this.applicationId,
    this.rentalListingId,
    this.sourceLabel,
  });

  final String initialTargetEntityType;
  final bool lockTargetEntityType;
  final int? propertyId;
  final int? unitId;
  final int? leaseManagementId;
  final int? leaseAgreementId;
  final int? tenantAccountId;
  final int? tenantLedgerEntryId;
  final int? workOrderId;
  final int? applicationId;
  final int? rentalListingId;
  final String? sourceLabel;

  @override
  ConsumerState<ScanCaptureSheet> createState() => _ScanCaptureSheetState();
}

class _ScanCaptureSheetState extends ConsumerState<ScanCaptureSheet> {
  final AudioRecorder _recorder = AudioRecorder();
  final ScanUploadOperationState _uploadOperation = ScanUploadOperationState();
  bool _uploading = false;
  double _uploadProgress = 0;
  String? _error;
  bool _recording = false;
  bool _voiceUploading = false;
  String? _recordingPath;

  /// What kind of record this scan will become. The global sheet offers
  /// Expense/Payment/WorkOrder; contextual callers can lock this to Lease or Loan.
  late String _targetEntityType;
  late ScanCaptureContext _captureContext;

  @override
  void initState() {
    super.initState();
    _targetEntityType = widget.initialTargetEntityType;
    _captureContext = ScanCaptureContext(
      propertyId: widget.propertyId,
      unitId: widget.unitId,
      leaseManagementId: widget.leaseManagementId,
      leaseAgreementId: widget.leaseAgreementId,
      tenantAccountId: widget.tenantAccountId,
      tenantLedgerEntryId: widget.tenantLedgerEntryId,
      workOrderId: widget.workOrderId,
      applicationId: widget.applicationId,
      rentalListingId: widget.rentalListingId,
      sourceLabel: widget.sourceLabel,
    );
  }

  @override
  void dispose() {
    _uploadOperation.cancel();
    _recorder.dispose();
    super.dispose();
  }

  String get _captureContextKey => [
    _targetEntityType,
    _captureContext.propertyId,
    _captureContext.unitId,
    _captureContext.leaseManagementId,
    _captureContext.leaseAgreementId,
    _captureContext.tenantAccountId,
    _captureContext.tenantLedgerEntryId,
    _captureContext.workOrderId,
    _captureContext.applicationId,
    _captureContext.rentalListingId,
    _captureContext.sourceLabel?.trim(),
  ].join('|');

  static String _payloadKey(
    Uint8List bytes,
    String filename,
    String contentType,
  ) {
    return '$filename|$contentType|${bytes.length}|${sha256.convert(bytes)}';
  }

  Future<void> _pick(ImageSource source) async {
    _uploadOperation.cancel();
    final picker = ImagePicker();
    // Gallery documents may be multi-page scans composited into one tall image.
    // A maxHeight silently reduced a 6,605px scan to 1,600px (only 165px wide),
    // making its text unreadable before it ever reached extraction. Preserve
    // selected document bytes; camera captures may still use JPEG compression,
    // but must retain their original dimensions.
    final XFile? picked = source == ImageSource.gallery
        ? await picker.pickImage(source: source)
        : await picker.pickImage(source: source, imageQuality: 90);
    if (picked == null) return; // user cancelled

    await _uploadBytes(
      Uint8List.fromList(await picked.readAsBytes()),
      picked.name,
      _mimeFromExtension(picked.name),
    );
  }

  Future<void> _pickFile() async {
    _uploadOperation.cancel();
    await FilePicker.platform.clearTemporaryFiles().catchError((_) => false);
    final result = await FilePicker.platform.pickFiles(
      type: FileType.custom,
      allowedExtensions: const ['pdf', 'jpg', 'jpeg', 'png', 'webp', 'heic'],
      withData: true,
    );
    final file = result?.files.single;
    if (file == null) return;
    if (file.bytes == null) {
      setState(() => _error = "Couldn't read the selected file.");
      return;
    }
    await _uploadBytes(file.bytes!, file.name, _mimeFromExtension(file.name));
  }

  Future<void> _pickDocumentPages() async {
    _uploadOperation.cancel();
    // Preserve page resolution for OCR/extraction. The PDF stitcher controls
    // the rendered page size without discarding source pixels.
    final picked = await ImagePicker().pickMultiImage();
    if (picked.isEmpty) return;
    try {
      final pages = <Uint8List>[];
      for (final page in picked) {
        pages.add(Uint8List.fromList(await page.readAsBytes()));
      }
      final pdf = await stitchImagesToPdf(pages);
      await _uploadBytes(
        pdf,
        _targetEntityType == 'Application'
            ? 'application-scan.pdf'
            : 'agreement-scan.pdf',
        'application/pdf',
      );
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _error = 'Could not combine those pages. Try again or choose a PDF.';
      });
    }
  }

  Future<void> _uploadBytes(
    Uint8List bytes,
    String filename,
    String contentType,
  ) async {
    final clientOperationId = _uploadOperation.idFor(
      payloadKey: _payloadKey(bytes, filename, contentType),
      contextKey: _captureContextKey,
    );
    setState(() {
      _uploading = true;
      _uploadProgress = 0;
      _error = null;
    });

    try {
      final created = await ref
          .read(scanRepositoryProvider)
          .uploadImage(
            bytes,
            filename,
            contentType,
            targetEntityType: _targetEntityType,
            clientOperationId: clientOperationId,
            propertyId: _captureContext.propertyId,
            unitId: _captureContext.unitId,
            leaseManagementId: _captureContext.leaseManagementId,
            leaseAgreementId: _captureContext.leaseAgreementId,
            tenantAccountId: _captureContext.tenantAccountId,
            tenantLedgerEntryId: _captureContext.tenantLedgerEntryId,
            workOrderId: _captureContext.workOrderId,
            applicationId: _captureContext.applicationId,
            rentalListingId: _captureContext.rentalListingId,
            sourceLabel: _captureContext.sourceLabel,
            onSendProgress: (progress) {
              if (mounted) setState(() => _uploadProgress = progress);
            },
          );

      if (!mounted) return;
      _uploadOperation.complete();
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

  Future<void> _toggleVoice() async {
    if (_recording) {
      await _stopVoice();
    } else {
      await _startVoice();
    }
  }

  Future<void> _startVoice() async {
    setState(() => _error = null);

    try {
      final hasPermission = await _recorder.hasPermission();
      if (!hasPermission) {
        if (!mounted) return;
        setState(() {
          _error = 'Microphone permission is required to record a voice note.';
        });
        return;
      }

      final path =
          '${Directory.systemTemp.path}/rental-command-voice-${DateTime.now().microsecondsSinceEpoch}.m4a';
      await _recorder.start(
        const RecordConfig(
          encoder: AudioEncoder.aacLc,
          bitRate: 64000,
          sampleRate: 44100,
          numChannels: 1,
          noiseSuppress: true,
        ),
        path: path,
      );

      if (!mounted) return;
      setState(() {
        _recording = true;
        _recordingPath = path;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _error = 'Could not start recording. Please try again.';
      });
    }
  }

  Future<void> _stopVoice() async {
    setState(() {
      _voiceUploading = true;
      _error = null;
    });

    try {
      final path = await _recorder.stop() ?? _recordingPath;
      if (!mounted) return;
      setState(() {
        _recording = false;
        _recordingPath = null;
      });

      if (path == null || path.isEmpty) {
        setState(() {
          _voiceUploading = false;
          _error = 'No voice note was recorded.';
        });
        return;
      }

      final file = File(path);
      final bytes = Uint8List.fromList(await file.readAsBytes());
      unawaited(file.delete().catchError((_) => file));

      final draft = await ref
          .read(scanRepositoryProvider)
          .createVoiceDraft(bytes, 'voice.m4a', 'audio/mp4');

      if (!mounted) return;
      Navigator.of(context).pop(draft.id);
    } on ApiException catch (e) {
      if (!mounted) return;
      setState(() {
        _recording = false;
        _voiceUploading = false;
        _error = voiceDraftErrorMessage(e);
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _recording = false;
        _voiceUploading = false;
        _error = 'Voice capture failed. Please try again.';
      });
    }
  }

  Future<void> _changeContext() async {
    final choice = await showModalBottomSheet<_ScanContextChoice>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => _ScanContextPickerSheet(
        targetEntityType: _targetEntityType,
        current: _captureContext,
        allowClear: !widget.lockTargetEntityType,
      ),
    );
    if (choice == null || !mounted) return;
    _uploadOperation.cancel();
    setState(() => _captureContext = choice.context);
  }

  void _openScanHistory() {
    final router = GoRouter.of(context);
    Navigator.of(context).pop();
    unawaited(router.push<void>('/scans'));
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final auth = ref.watch(authControllerProvider);
    final capabilities = auth is AuthStateAuthenticated
        ? auth.capabilities
        : const <String>{};
    final allowedTargets = allowedScanTargets(capabilities);
    final lockedLoan =
        widget.lockTargetEntityType && _targetEntityType == 'Loan';
    final lockedLease =
        widget.lockTargetEntityType && _targetEntityType == 'LeaseAgreement';

    if (_uploading || _voiceUploading) {
      return Padding(
        padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const SizedBox(height: 8),
            Text(
              _voiceUploading ? 'Creating voice draft…' : 'Uploading…',
              style: theme.textTheme.titleMedium,
            ),
            const SizedBox(height: 20),
            LinearProgressIndicator(
              value: _voiceUploading
                  ? null
                  : _uploadProgress > 0
                  ? _uploadProgress
                  : null,
            ),
            const SizedBox(height: 8),
            Text(
              _voiceUploading
                  ? 'Reading your note…'
                  : _uploadProgress > 0
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
      child: SingleChildScrollView(
        padding: EdgeInsets.only(
          bottom: MediaQuery.viewInsetsOf(context).bottom,
        ),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                lockedLoan
                    ? 'Scan a mortgage statement'
                    : lockedLease
                    ? 'Import a signed lease'
                    : 'Scan a document',
                style: theme.textTheme.titleLarge?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 8),
              Text(
                lockedLoan
                    ? 'Take a photo of a mortgage statement or closing disclosure.\nThe app will read the loan details for review.'
                    : lockedLease
                    ? 'Take a photo or choose the signed lease PDF.\nRental Command will read it and keep this unit selected for review.'
                    : 'Take a photo or choose a file. Rental Command will classify it, extract the details, and show the exact business record before it is created.',
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: colorScheme.onSurfaceVariant,
                ),
                textAlign: TextAlign.center,
              ),
              if (!widget.lockTargetEntityType) ...[
                const SizedBox(height: 8),
                TextButton.icon(
                  icon: const Icon(Icons.history_outlined),
                  label: const Text('View scan history'),
                  onPressed: _openScanHistory,
                ),
              ],
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
              if (!widget.lockTargetEntityType) ...[
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
                  options: allowedTargets,
                  onChanged: (value) => setState(() {
                    if (_targetEntityType != value) {
                      _uploadOperation.cancel();
                      _targetEntityType = value;
                    }
                  }),
                ),
                const SizedBox(height: 6),
                Text(
                  scanTargetFor(_targetEntityType).description,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: colorScheme.onSurfaceVariant,
                  ),
                ),
              ],
              const SizedBox(height: 16),
              _CaptureContextCard(
                context: _captureContext,
                onChange: widget.lockTargetEntityType ? null : _changeContext,
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
              const SizedBox(height: 12),
              OutlinedButton.icon(
                icon: const Icon(Icons.upload_file_outlined),
                label: const Text('Choose a PDF or image'),
                onPressed: _pickFile,
              ),
              if (_targetEntityType == 'LeaseAgreement' ||
                  _targetEntityType == 'Application') ...[
                const SizedBox(height: 12),
                OutlinedButton.icon(
                  icon: const Icon(Icons.collections_outlined),
                  label: const Text('Choose multiple page photos'),
                  onPressed: _pickDocumentPages,
                ),
              ],
              if (!lockedLoan && !lockedLease) ...[
                const SizedBox(height: 12),
                OutlinedButton.icon(
                  icon: Icon(
                    _recording
                        ? Icons.stop_circle_outlined
                        : Icons.mic_outlined,
                  ),
                  label: Text(
                    _recording ? 'Stop voice note' : 'Record voice note',
                  ),
                  onPressed: _toggleVoice,
                ),
              ],
              const SizedBox(height: 8),
            ],
          ),
        ),
      ),
    );
  }
}

/// Large, thumb-friendly toggle buttons that pick what kind of record this scan
/// should become. The choice flows through to the upload's `targetEntityType`.
class _DocTypeSelector extends StatelessWidget {
  const _DocTypeSelector({
    required this.selected,
    required this.options,
    required this.onChanged,
  });

  final String selected;
  final List<ScanTargetOption> options;
  final ValueChanged<String> onChanged;

  @override
  Widget build(BuildContext context) {
    return DropdownButtonFormField<String>(
      initialValue: options.any((option) => option.value == selected)
          ? selected
          : options.firstOrNull?.value,
      isExpanded: true,
      decoration: const InputDecoration(
        labelText: 'Document type',
        border: OutlineInputBorder(),
      ),
      items: options
          .map(
            (option) => DropdownMenuItem<String>(
              value: option.value,
              child: Row(
                children: [
                  Icon(option.icon, size: 20),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Text(option.label, overflow: TextOverflow.ellipsis),
                  ),
                ],
              ),
            ),
          )
          .toList(growable: false),
      onChanged: (value) {
        if (value != null) onChanged(value);
      },
    );
  }
}

class _CaptureContextCard extends StatelessWidget {
  const _CaptureContextCard({required this.context, required this.onChange});

  final ScanCaptureContext context;
  final VoidCallback? onChange;

  @override
  Widget build(BuildContext buildContext) {
    final theme = Theme.of(buildContext);
    final label = context.sourceLabel?.trim();
    final parts = context.userFacingParts;
    return Card.filled(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                const Icon(Icons.link_outlined, size: 20),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    'Where this will go',
                    style: theme.textTheme.titleSmall?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                if (onChange != null)
                  TextButton(onPressed: onChange, child: const Text('Change')),
              ],
            ),
            Text(
              label?.isNotEmpty == true ? label! : 'No rental selected yet',
              style: theme.textTheme.bodyMedium,
            ),
            if (parts.isNotEmpty) ...[
              const SizedBox(height: 8),
              Wrap(
                spacing: 6,
                runSpacing: 6,
                children: parts
                    .map((part) => Chip(label: Text(part)))
                    .toList(growable: false),
              ),
            ],
            const SizedBox(height: 6),
            Text(
              context.hasBusinessContext
                  ? 'This context came with you and will be checked again.'
                  : 'Choose an authorized property or tenant balance now, or keep this workspace-wide.',
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _ScanContextChoice {
  const _ScanContextChoice(this.context);

  final ScanCaptureContext context;
}

enum _ScanContextKind { property, rentalAccount }

class _ScanContextPickerSheet extends ConsumerStatefulWidget {
  const _ScanContextPickerSheet({
    required this.targetEntityType,
    required this.current,
    required this.allowClear,
  });

  final String targetEntityType;
  final ScanCaptureContext current;
  final bool allowClear;

  @override
  ConsumerState<_ScanContextPickerSheet> createState() =>
      _ScanContextPickerSheetState();
}

class _ScanContextPickerSheetState
    extends ConsumerState<_ScanContextPickerSheet> {
  final _searchController = TextEditingController();
  Timer? _debounce;
  late _ScanContextKind _kind;
  String _search = '';
  int _skip = 0;

  @override
  void initState() {
    super.initState();
    _kind = widget.targetEntityType == 'Payment'
        ? _ScanContextKind.rentalAccount
        : _ScanContextKind.property;
  }

  @override
  void dispose() {
    _debounce?.cancel();
    _searchController.dispose();
    super.dispose();
  }

  void _onSearch(String value) {
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 350), () {
      if (!mounted) return;
      setState(() {
        _search = value.trim();
        _skip = 0;
      });
    });
  }

  void _selectProperty(int id, String label) {
    Navigator.of(context).pop(
      _ScanContextChoice(
        ScanCaptureContext(propertyId: id, sourceLabel: label),
      ),
    );
  }

  void _selectAccount(TenantAccountOption account) {
    final home = [
      account.propertyName,
      if (account.unitNumber.trim().isNotEmpty) 'Unit ${account.unitNumber}',
    ].where((part) => part.trim().isNotEmpty).join(' · ');
    final label = [
      if (account.primaryTenantName?.trim().isNotEmpty ?? false)
        account.primaryTenantName!.trim(),
      home,
    ].where((part) => part.isNotEmpty).join(' · ');
    Navigator.of(context).pop(
      _ScanContextChoice(
        ScanCaptureContext(
          tenantAccountId: account.tenantAccountId,
          sourceLabel: label,
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final bottom = MediaQuery.viewInsetsOf(context).bottom;
    return Padding(
      padding: EdgeInsets.fromLTRB(16, 16, 16, 16 + bottom),
      child: SizedBox(
        height: MediaQuery.sizeOf(context).height * 0.72,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              'Choose scan context',
              style: Theme.of(
                context,
              ).textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 6),
            Text(
              'Only rentals and tenant balances available to you are shown.',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                color: Theme.of(context).colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 12),
            if (widget.targetEntityType == 'Payment' ||
                widget.targetEntityType.isEmpty)
              SegmentedButton<_ScanContextKind>(
                segments: const [
                  ButtonSegment(
                    value: _ScanContextKind.property,
                    label: Text('Property'),
                    icon: Icon(Icons.apartment_outlined),
                  ),
                  ButtonSegment(
                    value: _ScanContextKind.rentalAccount,
                    label: Text('Tenant balance'),
                    icon: Icon(Icons.account_balance_wallet_outlined),
                  ),
                ],
                selected: {_kind},
                onSelectionChanged: (values) => setState(() {
                  _kind = values.single;
                  _skip = 0;
                }),
              ),
            const SizedBox(height: 12),
            SearchBar(
              controller: _searchController,
              leading: const Icon(Icons.search),
              hintText: _kind == _ScanContextKind.property
                  ? 'Search properties'
                  : 'Search tenant, property, rental, or balance',
              onChanged: _onSearch,
              trailing: [
                if (_searchController.text.isNotEmpty)
                  IconButton(
                    tooltip: 'Clear search',
                    icon: const Icon(Icons.close),
                    onPressed: () {
                      _searchController.clear();
                      _onSearch('');
                    },
                  ),
              ],
            ),
            const SizedBox(height: 8),
            if (widget.current.hasBusinessContext)
              ListTile(
                contentPadding: EdgeInsets.zero,
                leading: const Icon(Icons.history),
                title: const Text('Keep current context'),
                subtitle: Text(
                  widget.current.sourceLabel?.trim().isNotEmpty ?? false
                      ? widget.current.sourceLabel!.trim()
                      : widget.current.userFacingParts.join(' · '),
                ),
                onTap: () => Navigator.of(
                  context,
                ).pop(_ScanContextChoice(widget.current)),
              ),
            Expanded(
              child: _kind == _ScanContextKind.property
                  ? _PropertyContextResults(
                      search: _search,
                      skip: _skip,
                      onSkipChanged: (value) => setState(() => _skip = value),
                      onSelected: _selectProperty,
                    )
                  : _AccountContextResults(
                      search: _search,
                      skip: _skip,
                      onSkipChanged: (value) => setState(() => _skip = value),
                      onSelected: _selectAccount,
                    ),
            ),
            if (widget.allowClear)
              TextButton.icon(
                icon: const Icon(Icons.link_off_outlined),
                label: const Text('Keep workspace-wide'),
                onPressed: () => Navigator.of(
                  context,
                ).pop(const _ScanContextChoice(ScanCaptureContext())),
              ),
          ],
        ),
      ),
    );
  }
}

class _PropertyContextResults extends ConsumerWidget {
  const _PropertyContextResults({
    required this.search,
    required this.skip,
    required this.onSkipChanged,
    required this.onSelected,
  });

  final String search;
  final int skip;
  final ValueChanged<int> onSkipChanged;
  final void Function(int id, String label) onSelected;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final query = PropertyListQuery(search: search, skip: skip, take: 25);
    final page = ref.watch(propertiesPageProvider(query));
    return page.when(
      loading: () => const Center(child: CircularProgressIndicator()),
      error: (error, _) => Center(child: Text(error.toString())),
      data: (result) => ListView.builder(
        itemCount: result.items.length + 1,
        itemBuilder: (context, index) {
          if (index == result.items.length) {
            return _PageControls(
              hasPrevious: result.hasPrevious,
              hasNext: result.hasNext,
              onPrevious: () =>
                  onSkipChanged((skip - result.take).clamp(0, skip).toInt()),
              onNext: () => onSkipChanged(skip + result.take),
            );
          }
          final property = result.items[index];
          final address = [
            property.addressLine1,
            property.city,
          ].where((value) => value.trim().isNotEmpty).join(' · ');
          return ListTile(
            leading: const Icon(Icons.apartment_outlined),
            title: Text(property.name),
            subtitle: address.isEmpty ? null : Text(address),
            onTap: () => onSelected(property.id, property.name),
          );
        },
      ),
    );
  }
}

class _AccountContextResults extends ConsumerWidget {
  const _AccountContextResults({
    required this.search,
    required this.skip,
    required this.onSkipChanged,
    required this.onSelected,
  });

  final String search;
  final int skip;
  final ValueChanged<int> onSkipChanged;
  final ValueChanged<TenantAccountOption> onSelected;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final page = ref.watch(
      _captureTenantAccountPageProvider((search: search, skip: skip)),
    );
    return page.when(
      loading: () => const Center(child: CircularProgressIndicator()),
      error: (error, _) => Center(child: Text(error.toString())),
      data: (result) => ListView.builder(
        itemCount: result.items.length + 1,
        itemBuilder: (context, index) {
          if (index == result.items.length) {
            return _PageControls(
              hasPrevious: result.skip > 0,
              hasNext: result.skip + result.items.length < result.totalCount,
              onPrevious: () =>
                  onSkipChanged((skip - result.take).clamp(0, skip).toInt()),
              onNext: () => onSkipChanged(skip + result.take),
            );
          }
          final account = result.items[index];
          final home = [
            account.propertyName,
            if (account.unitNumber.trim().isNotEmpty)
              'Unit ${account.unitNumber}',
          ].where((value) => value.trim().isNotEmpty).join(' · ');
          return ListTile(
            leading: const Icon(Icons.account_balance_wallet_outlined),
            title: Text(
              account.primaryTenantName?.trim().isNotEmpty ?? false
                  ? account.primaryTenantName!.trim()
                  : 'Tenant balance #${account.relationshipNumber}',
            ),
            subtitle: Text(home),
            onTap: () => onSelected(account),
          );
        },
      ),
    );
  }
}

class _PageControls extends StatelessWidget {
  const _PageControls({
    required this.hasPrevious,
    required this.hasNext,
    required this.onPrevious,
    required this.onNext,
  });

  final bool hasPrevious;
  final bool hasNext;
  final VoidCallback onPrevious;
  final VoidCallback onNext;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          TextButton(
            onPressed: hasPrevious ? onPrevious : null,
            child: const Text('Previous'),
          ),
          TextButton(
            onPressed: hasNext ? onNext : null,
            child: const Text('Next'),
          ),
        ],
      ),
    );
  }
}

typedef _CaptureTenantAccountQuery = ({String search, int skip});

final _captureTenantAccountPageProvider = FutureProvider.autoDispose
    .family<TenantAccountOptionPage, _CaptureTenantAccountQuery>((ref, query) {
      return ref
          .read(scanRepositoryProvider)
          .listTenantAccountOptions(search: query.search, skip: query.skip);
    });

/// Shows [ScanCaptureSheet] as a modal bottom sheet and returns the new
/// draft id, or null if the user cancelled.
Future<int?> showScanCaptureSheet(
  BuildContext context, {
  String initialTargetEntityType = '',
  bool lockTargetEntityType = false,
  int? propertyId,
  int? unitId,
  int? leaseManagementId,
  int? leaseAgreementId,
  int? tenantAccountId,
  int? tenantLedgerEntryId,
  int? workOrderId,
  int? applicationId,
  int? rentalListingId,
  String? sourceLabel,
}) {
  return showModalBottomSheet<int>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    builder: (_) => ScanCaptureSheet(
      initialTargetEntityType: initialTargetEntityType,
      lockTargetEntityType: lockTargetEntityType,
      propertyId: propertyId,
      unitId: unitId,
      leaseManagementId: leaseManagementId,
      leaseAgreementId: leaseAgreementId,
      tenantAccountId: tenantAccountId,
      tenantLedgerEntryId: tenantLedgerEntryId,
      workOrderId: workOrderId,
      applicationId: applicationId,
      rentalListingId: rentalListingId,
      sourceLabel: sourceLabel,
    ),
  );
}
