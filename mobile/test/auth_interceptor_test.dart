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
    test('a FormData consumed by the first send is rebuilt into a fresh, '
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
      expect(
        replayForm.fields.map((e) => '${e.key}=${e.value}'),
        contains('targetEntityType=Expense'),
      );
      expect(replayForm.files.single.key, 'file');
      expect(replayForm.files.single.value.filename, 'receipt.jpg');

      // ...and which can be finalized (i.e. re-sent) without throwing. This is
      // the assertion that fails on the unpatched interceptor.
      expect(() => replayForm.finalize().drain<void>(), returnsNormally);
    });

    test(
      'non-multipart bodies are returned unchanged so JSON retries still work',
      () {
        const jsonBody = {'amount': 40, 'note': 'plumbing'};
        expect(
          identical(AuthInterceptor.replayableBody(jsonBody), jsonBody),
          isTrue,
        );

        const stringBody = 'raw=payload';
        expect(
          identical(AuthInterceptor.replayableBody(stringBody), stringBody),
          isTrue,
        );

        expect(AuthInterceptor.replayableBody(null), isNull);
      },
    );
  });

  group('stale access revision replay policy', () {
    test('safe reads may be replayed after the envelope refreshes', () {
      expect(AuthInterceptor.canReplayAfterAccessRefresh('GET'), isTrue);
      expect(AuthInterceptor.canReplayAfterAccessRefresh('HEAD'), isTrue);
      expect(AuthInterceptor.canReplayAfterAccessRefresh('OPTIONS'), isTrue);
    });

    test('mutations must return to the caller without automatic replay', () {
      for (final method in ['POST', 'PUT', 'PATCH', 'DELETE']) {
        expect(
          AuthInterceptor.canReplayAfterAccessRefresh(method),
          isFalse,
          reason: '$method could duplicate or apply under changed authority',
        );
      }
    });

    test(
      'ordinary token expiry may replay a mutation in the same authority',
      () {
        expect(
          AuthInterceptor.canReplayRequestAfterRefresh(
            method: 'POST',
            requestStartAccess: _accessEnvelope(),
            refreshedAccess: _accessEnvelope(),
            staleRevisionRecovery: false,
          ),
          isTrue,
        );
      },
    );

    test(
      'mutation cannot cross workspace revision or experience boundaries',
      () {
        for (final refreshed in [
          _accessEnvelope(accessContextId: 22),
          _accessEnvelope(accessRevision: 8),
          _accessEnvelope(activeExperience: 'Leasing'),
        ]) {
          expect(
            AuthInterceptor.canReplayRequestAfterRefresh(
              method: 'PATCH',
              requestStartAccess: _accessEnvelope(),
              refreshedAccess: refreshed,
              staleRevisionRecovery: false,
            ),
            isFalse,
          );
        }
      },
    );

    test('stale-revision recovery never replays mutations automatically', () {
      expect(
        AuthInterceptor.canReplayRequestAfterRefresh(
          method: 'DELETE',
          requestStartAccess: _accessEnvelope(),
          refreshedAccess: _accessEnvelope(accessRevision: 8),
          staleRevisionRecovery: true,
        ),
        isFalse,
      );
    });

    test('reads may recover after an authority change', () {
      expect(
        AuthInterceptor.canReplayRequestAfterRefresh(
          method: 'GET',
          requestStartAccess: _accessEnvelope(),
          refreshedAccess: _accessEnvelope(accessContextId: 22),
          staleRevisionRecovery: false,
        ),
        isTrue,
      );
    });

    test('missing request-start authority fails closed for mutations', () {
      expect(
        AuthInterceptor.canReplayRequestAfterRefresh(
          method: 'POST',
          requestStartAccess: null,
          refreshedAccess: _accessEnvelope(),
          staleRevisionRecovery: false,
        ),
        isFalse,
      );
    });
  });
}

Map<String, dynamic> _accessEnvelope({
  int accessContextId = 11,
  int accessRevision = 7,
  String activeExperience = 'Management',
}) => {
  'identity': {'userId': 5, 'displayName': 'Test user'},
  'selectedContext': {
    'accessContextId': accessContextId,
    'portfolioId': 3,
    'workspaceName': 'Test workspace',
    'accessRevision': accessRevision,
    'activeExperience': activeExperience,
  },
};
