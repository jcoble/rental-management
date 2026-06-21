import 'dart:typed_data';
import 'package:image/image.dart' as img;
import 'package:pdf/pdf.dart';
import 'package:pdf/widgets.dart' as pw;

/// Stitch lease photos into ONE PDF (one image per page, orientation per image), uploaded via the
/// existing single-file /scans path. The server already handles PDF (PdfPig + vision fallback), so
/// this is zero-backend-change multi-photo intake. We re-encode each photo to JPEG (q85) so a HEIC
/// or oversized capture lands as a predictable, embeddable raster.
Future<Uint8List> stitchImagesToPdf(List<Uint8List> images) async {
  if (images.isEmpty) {
    throw ArgumentError('No photos to combine');
  }
  final doc = pw.Document();
  for (final bytes in images) {
    final decoded = img.decodeImage(bytes);
    if (decoded == null) {
      // Skip an undecodable frame rather than failing the whole stitch.
      continue;
    }
    final jpeg = Uint8List.fromList(img.encodeJpg(decoded, quality: 85));
    final memImage = pw.MemoryImage(jpeg);
    final landscape = decoded.width > decoded.height;
    doc.addPage(
      pw.Page(
        pageFormat: landscape ? PdfPageFormat.a4.landscape : PdfPageFormat.a4,
        margin: const pw.EdgeInsets.all(18),
        build: (context) => pw.Center(
          child: pw.Image(memImage, fit: pw.BoxFit.contain),
        ),
      ),
    );
  }
  return doc.save();
}
