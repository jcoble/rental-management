import 'package:uuid/uuid.dart';

/// Retains a caller-owned operation key across ambiguous failures and clears it only on success.
class IdempotentMutation {
  static const _uuid = Uuid();
  static final Map<String, String> _pendingKeys = {};

  static Future<T> run<T>(
    String scope,
    Future<T> Function(String operationKey) execute,
  ) async {
    final key = _pendingKeys.putIfAbsent(scope, _uuid.v4);
    final result = await execute(key);
    _pendingKeys.remove(scope);
    return result;
  }

  static void cancel(String scope) => _pendingKeys.remove(scope);
}
