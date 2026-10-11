import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/scan/scan_upload_operation.dart';

void main() {
  test('ambiguous retry reuses the logical upload operation id', () {
    var sequence = 0;
    final state = ScanUploadOperationState(createId: () => 'op-${++sequence}');

    expect(state.idFor(payloadKey: 'file-a', contextKey: 'unit-1'), 'op-1');
    expect(state.idFor(payloadKey: 'file-a', contextKey: 'unit-1'), 'op-1');
  });

  test('payload/context changes and completion start a new operation', () {
    var sequence = 0;
    final state = ScanUploadOperationState(createId: () => 'op-${++sequence}');

    expect(state.idFor(payloadKey: 'file-a', contextKey: 'unit-1'), 'op-1');
    expect(state.idFor(payloadKey: 'file-b', contextKey: 'unit-1'), 'op-2');
    expect(state.idFor(payloadKey: 'file-b', contextKey: 'unit-2'), 'op-3');
    state.complete();
    expect(state.idFor(payloadKey: 'file-b', contextKey: 'unit-2'), 'op-4');
  });

  test(
    'fresh picker sessions clear old upload identity and use full file hash',
    () {
      final source = File(
        'lib/features/scan/scan_capture.dart',
      ).readAsStringSync();

      expect(source, contains('sha256.convert(bytes)'));
      expect(source, contains('FilePicker.platform.clearTemporaryFiles()'));
      expect(
        RegExp(
          r'Future<void> _pick\([^}]+_uploadOperation\.cancel\(\);',
          dotAll: true,
        ).hasMatch(source),
        isTrue,
      );
      expect(
        RegExp(
          r'Future<void> _pickFile\([^}]+_uploadOperation\.cancel\(\);',
          dotAll: true,
        ).hasMatch(source),
        isTrue,
      );
      expect(
        RegExp(
          r'Future<void> _pickDocumentPages\([^}]+_uploadOperation\.cancel\(\);',
          dotAll: true,
        ).hasMatch(source),
        isTrue,
      );
    },
  );
}
