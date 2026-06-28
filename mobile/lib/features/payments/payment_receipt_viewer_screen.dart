import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/files/document_opener.dart';
import 'payments_repository.dart';

/// Full-screen viewer for the latest receipt/scan attached to a payment.
class PaymentReceiptViewerScreen extends ConsumerWidget {
  const PaymentReceiptViewerScreen({
    super.key,
    required this.paymentId,
    required this.isImage,
  });

  final int paymentId;
  final bool isImage;

  Future<void> _openExternal(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.of(context);
    try {
      final bytes = await ref
          .read(paymentsRepositoryProvider)
          .scanBytes(paymentId);
      await DocumentOpener.openBytes(
        bytes: bytes,
        fileName: 'payment-receipt-$paymentId.pdf',
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
      WidgetsBinding.instance.addPostFrameCallback((_) async {
        await _openExternal(context, ref);
        if (context.mounted) Navigator.of(context).maybePop();
      });
      return const Scaffold(body: Center(child: CircularProgressIndicator()));
    }

    final async = ref.watch(paymentReceiptProvider(paymentId));
    return Scaffold(
      appBar: AppBar(
        title: const Text('Receipt'),
        actions: [
          IconButton(
            icon: const Icon(Icons.ios_share),
            tooltip: 'Share',
            onPressed: () async {
              final messenger = ScaffoldMessenger.of(context);
              final bytes = ref.read(paymentReceiptProvider(paymentId)).value;
              if (bytes == null) return;
              try {
                await DocumentOpener.shareBytes(
                  bytes: bytes,
                  fileName: 'payment-receipt-$paymentId.jpg',
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
