import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test('document preview cannot trap the review list scroll gesture', () {
    final source = File(
      'lib/features/scan/scan_review_screen.dart',
    ).readAsStringSync();
    final previewStart = source.indexOf('class _DocumentPreview');
    final previewEnd = source.indexOf(
      '// ---------------------------------------------------------------------------',
      previewStart + 1,
    );
    final preview = source.substring(previewStart, previewEnd);

    expect(preview, isNot(contains('InteractiveViewer(')));
    expect(preview, contains('data: (bytes) => Image.memory('));
  });
}
