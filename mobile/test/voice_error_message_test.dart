import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/api/api_exception.dart';
import 'package:rental_command/features/voice/voice_error_message.dart';

void main() {
  group('voiceDraftErrorMessage', () {
    test('maps no usable transcript to retry guidance', () {
      expect(
        voiceDraftErrorMessage(
          const ApiException(
            statusCode: 400,
            message: 'A transcript or non-empty transcribable audio is required.',
          ),
        ),
        contains("Couldn't hear that"),
      );
    });

    test('maps unavailable transcription setup to provider guidance', () {
      expect(
        voiceDraftErrorMessage(
          const ApiException(
            statusCode: 503,
            message: 'Voice transcription is not configured.',
          ),
        ),
        contains("Voice transcription isn't configured"),
      );
    });

    test('keeps other API messages', () {
      expect(
        voiceDraftErrorMessage(
          const ApiException(statusCode: 500, message: 'Server failed.'),
        ),
        'Server failed.',
      );
    });
  });
}
