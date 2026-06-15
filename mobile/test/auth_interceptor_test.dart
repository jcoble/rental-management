import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_interceptor.dart';

/// Regression coverage for audit finding H-5: on a 401 the interceptor retries
/// the original request, but a multipart `FormData` body is single-use. Once it
/// has been dispatched (finalized) it cannot be re-sent — so the retry path must
/// rebuild a fresh, replayable body. These tests pin the exact behaviour the fix
/// relies on without standing up a full HTTP + secure-storage harness.
void main() {
  group('AuthInterceptor.replayableBody', () {
    test(
        'a FormData consumed by the first send is rebuilt into a fresh, '
        're-finalizable instance for the retry (no StateError)', () {
      final original = FormData.fromMap({
        'file': MultipartFile.fromBytes(
          Uint8List.fromList([1, 2, 3, 4]),
          filename: 'receipt.jpg',
          contentType: DioMediaType.parse('image/jpeg'),
        ),
        'targetEntityType': 'Expense',
      });

      // Simulate the first dispatch finalizing the body in place (this is what
      // `dio.fetch` does internally before the 401 comes back).
      original.finalize().drain<void>();
      expect(original.isFinalized, isTrue);
      // Sanity: re-sending the SAME instance is exactly the bug — it throws.
      expect(original.finalize, throwsStateError);

      final replay = AuthInterceptor.replayableBody(original);

      // The retry body must be a *different*, not-yet-finalized FormData...
      expect(replay, isA<FormData>());
      expect(identical(replay, original), isFalse);
      final replayForm = replay as FormData;
      expect(replayForm.isFinalized, isFalse);

      // ...whose fields and files survived the clone...
      expect(replayForm.fields.map((e) => '${e.key}=${e.value}'),
          contains('targetEntityType=Expense'));
      expect(replayForm.files.single.key, 'file');
      expect(replayForm.files.single.value.filename, 'receipt.jpg');

      // ...and which can be finalized (i.e. re-sent) without throwing. This is
      // the assertion that fails on the unpatched interceptor.
      expect(() => replayForm.finalize().drain<void>(), returnsNormally);
    });

    test('non-multipart bodies are returned unchanged so JSON retries still work',
        () {
      const jsonBody = {'amount': 40, 'note': 'plumbing'};
      expect(
          identical(AuthInterceptor.replayableBody(jsonBody), jsonBody), isTrue);

      const stringBody = 'raw=payload';
      expect(identical(AuthInterceptor.replayableBody(stringBody), stringBody),
          isTrue);

      expect(AuthInterceptor.replayableBody(null), isNull);
    });
  });
}
