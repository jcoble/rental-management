import 'package:uuid/uuid.dart';

typedef ScanOperationIdFactory = String Function();

/// Retains one idempotency key for one logical scan upload. Transport failures do not clear it;
/// changing the payload/context creates a new operation, while success or cancellation ends it.
class ScanUploadOperationState {
  ScanUploadOperationState({ScanOperationIdFactory? createId})
    : _createId = createId ?? (() => const Uuid().v4());

  final ScanOperationIdFactory _createId;
  String? _operationId;
  String? _payloadKey;
  String? _contextKey;

  String idFor({required String payloadKey, required String contextKey}) {
    if (_payloadKey != payloadKey || _contextKey != contextKey) {
      clear();
      _payloadKey = payloadKey;
      _contextKey = contextKey;
    }
    return _operationId ??= _createId();
  }

  void complete() => clear();

  void cancel() => clear();

  void clear() {
    _operationId = null;
    _payloadKey = null;
    _contextKey = null;
  }
}
