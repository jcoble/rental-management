import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/token_store.dart';
import 'package:rental_command/core/realtime/signalr_service.dart';

void main() {
  test('event arriving after dispose is ignored', () async {
    final service = SignalrService(_FakeTokenStore());

    service.dispose();
    await Future<void>.delayed(Duration.zero);

    expect(
      () =>
          service.handleEventForTesting(RealtimeEventType.entityUpdated, const [
            <String, Object>{'entityType': 'Payment', 'entityId': 7},
          ]),
      returnsNormally,
    );
  });
}

class _FakeTokenStore implements TokenStore {
  @override
  Future<void> clearTokens() async {}

  @override
  Future<String?> getAccessToken() async => null;

  @override
  Future<Map<String, dynamic>?> getAccessEnvelope() async => null;

  @override
  Future<String?> getRefreshToken() async => null;

  @override
  Future<void> saveAccessEnvelope(Map<String, dynamic> access) async {}

  @override
  Future<void> saveTokens({
    required String accessToken,
    required String refreshToken,
  }) async {}
}
