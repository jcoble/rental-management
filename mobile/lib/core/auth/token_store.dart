import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

const _keyAccessToken = 'rc_access_token';
const _keyRefreshToken = 'rc_refresh_token';

/// Secure storage wrapper for the JWT access token and refresh token.
///
/// The refresh token is stored locally to allow the mobile client to call the
/// refresh endpoint directly. On the web the refresh token lives in an httpOnly
/// cookie; on mobile we store it in the OS secure enclave (Keychain / Keystore).
///
/// NOTE: The refresh endpoint (`POST /api/v1/auth/refresh`) reads the refresh
/// token exclusively from an httpOnly cookie — there is no body-based option.
/// Mobile must send the refresh token as a cookie header manually.
/// TODO(api): Add a body-based refresh parameter so mobile doesn't need to
/// replicate cookie behaviour. Track with API team.
class TokenStore {
  TokenStore(this._storage);

  final FlutterSecureStorage _storage;

  // In-memory cache. Secure storage (Android Keystore / iOS Keychain) is for
  // PERSISTENCE across app launches — NOT for per-request reads. The auth
  // interceptor calls getAccessToken() on every HTTP request, and Keystore reads
  // can intermittently stall for seconds. Read once, then serve from memory.
  String? _accessToken;
  bool _accessLoaded = false;

  Future<String?> getAccessToken() async {
    if (_accessLoaded) return _accessToken;
    final sw = Stopwatch()..start();
    _accessToken = await _storage.read(key: _keyAccessToken);
    sw.stop();
    _accessLoaded = true;
    if (sw.elapsedMilliseconds > 40) {
      debugPrint('[TokenStore] first secure-storage read took ${sw.elapsedMilliseconds}ms');
    }
    return _accessToken;
  }

  Future<String?> getRefreshToken() => _storage.read(key: _keyRefreshToken);

  Future<void> saveTokens({
    required String accessToken,
    required String refreshToken,
  }) async {
    _accessToken = accessToken;
    _accessLoaded = true;
    await Future.wait([
      _storage.write(key: _keyAccessToken, value: accessToken),
      _storage.write(key: _keyRefreshToken, value: refreshToken),
    ]);
  }

  Future<void> clearTokens() async {
    _accessToken = null;
    _accessLoaded = true;
    await Future.wait([
      _storage.delete(key: _keyAccessToken),
      _storage.delete(key: _keyRefreshToken),
    ]);
  }
}

final tokenStoreProvider = Provider<TokenStore>((ref) {
  // AndroidOptions defaults are sufficient; encryptedSharedPreferences was
  // deprecated in flutter_secure_storage v10 (Jetpack Security deprecated by Google).
  const storage = FlutterSecureStorage();
  return TokenStore(storage);
});
