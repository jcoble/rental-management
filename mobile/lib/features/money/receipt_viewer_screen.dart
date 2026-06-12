import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/files/document_opener.dart';
import 'money_repository.dart';

/// Full-screen receipt viewer. Images render inline (pinch-zoom);
/// PDFs/non-images are handed to the OS viewer via [DocumentOpener].
class ReceiptViewerScreen extends ConsumerWidget {
  const ReceiptViewerScreen({
    super.key,
    required this.expenseId,
    required this.isImage,
  });

  final int expenseId;

  /// True when the linked receipt's content type is an image; otherwise it is
  /// treated as a PDF/document and opened externally.
  final bool isImage;

  Future<void> _openExternal(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.of(context);
    try {
      final bytes =
          await ref.read(moneyRepositoryProvider).receiptBytes(expenseId);
      await DocumentOpener.openBytes(
        bytes: bytes,
        fileName: 'receipt-$expenseId.pdf',
      );
    } on ApiException catch (e) {
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (!isImage) {
      // Open the PDF/document immediately, then pop back.
      WidgetsBinding.instance.addPostFrameCallback((_) async {
        await _openExternal(context, ref);
        if (context.mounted) Navigator.of(context).maybePop();
      });
      return const Scaffold(
        body: Center(child: CircularProgressIndicator()),
      );
    }

    final async = ref.watch(expenseReceiptProvider(expenseId));
    return Scaffold(
      appBar: AppBar(
        title: const Text('Receipt'),
        actions: [
          IconButton(
            icon: const Icon(Icons.ios_share),
            tooltip: 'Share',
            onPressed: () async {
              final messenger = ScaffoldMessenger.of(context);
              final value = ref.read(expenseReceiptProvider(expenseId));
              final bytes = value.value;
              if (bytes == null) return;
              try {
                await DocumentOpener.shareBytes(
                  bytes: bytes,
                  fileName: 'receipt-$expenseId.jpg',
                  mimeType: 'image/jpeg',
                );
              } on ApiException catch (e) {
                messenger
                  ..hideCurrentSnackBar()
                  ..showSnackBar(SnackBar(content: Text(e.message)));
              }
            },
          ),
        ],
      ),
      backgroundColor: Colors.black,
      body: async.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => Center(
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Text(
              e is ApiException ? e.message : "Couldn't load the receipt.",
              style: const TextStyle(color: Colors.white),
              textAlign: TextAlign.center,
            ),
          ),
        ),
        data: (bytes) => InteractiveViewer(
          minScale: 0.5,
          maxScale: 5,
          child: Center(child: Image.memory(bytes)),
        ),
      ),
    );
  }
}
