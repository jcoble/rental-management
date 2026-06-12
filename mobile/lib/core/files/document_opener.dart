import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:open_filex/open_filex.dart';
import 'package:path_provider/path_provider.dart';
import 'package:share_plus/share_plus.dart';

/// Opens / shares generated documents (lease agreement, signed lease, inspection
/// report, move-out statement, year-end packet, adverse-action letter, …).
///
/// Replaces the broken `launchUrl(Uri.file(path), externalApplication)` pattern:
/// Android 7+ rejects a raw `file://` URI handed to another app
/// (`FileUriExposedException`), and an iOS viewer cannot read another sandbox's
/// temp path. Instead we write the bytes to a stable app-documents path and hand
/// the file to the OS via `open_filex` (which uses an Android FileProvider
/// `content://` URI under the hood) with a share-sheet fallback.
class DocumentOpener {
  DocumentOpener._();

  /// Persists [bytes] under the app documents dir as [fileName] and asks the OS
  /// to open it in an appropriate viewer. Falls back to the system share sheet
  /// when no viewer is available (e.g. no PDF app installed). Returns the saved
  /// file path.
  static Future<String> openBytes({
    required Uint8List bytes,
    required String fileName,
    String mimeType = 'application/pdf',
  }) async {
    final file = await _writeToDocuments(bytes, fileName);
    final result = await OpenFilex.open(file.path, type: mimeType);
    if (result.type != ResultType.done) {
      // No viewer / permission issue — fall back to the share sheet so the user
      // can still get the document out (save to Files, email, etc.).
      await shareFile(file.path, mimeType: mimeType);
    }
    return file.path;
  }

  /// Shares a generated document out via the system share sheet.
  static Future<void> shareBytes({
    required Uint8List bytes,
    required String fileName,
    String mimeType = 'application/pdf',
  }) async {
    final file = await _writeToDocuments(bytes, fileName);
    await shareFile(file.path, mimeType: mimeType);
  }

  /// Shares an already-saved file path via the system share sheet.
  static Future<void> shareFile(String path, {String mimeType = 'application/pdf'}) async {
    await Share.shareXFiles([XFile(path, mimeType: mimeType)]);
  }

  static Future<File> _writeToDocuments(Uint8List bytes, String fileName) async {
    final dir = await getApplicationDocumentsDirectory();
    final safeName = _sanitize(fileName);
    final file = File('${dir.path}/$safeName');
    await file.writeAsBytes(bytes, flush: true);
    return file;
  }

  static String _sanitize(String name) {
    final cleaned = name.replaceAll(RegExp(r'[^A-Za-z0-9._-]'), '_');
    return cleaned.isEmpty ? 'document.pdf' : cleaned;
  }
}
