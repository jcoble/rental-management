import '../../core/api/api_exception.dart';

String voiceDraftErrorMessage(ApiException error) {
  if (error.statusCode == 400) {
    return "Couldn't hear that — try again. Speak a little longer and closer to the mic.";
  }
  if (error.statusCode == 503) {
    return "Voice transcription isn't configured yet. Set up the voice provider, then try again.";
  }
  return error.message;
}
