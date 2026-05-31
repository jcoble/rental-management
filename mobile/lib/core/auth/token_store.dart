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

  Future<String?> getAccessToken() => _storage.read(key: _keyAccessToken);
  Future<String?> getRefreshToken() => _storage.read(key: _keyRefreshToken);

  Future<void> saveTokens({
    required String accessToken,
    required String refreshToken,
  }) async {
    await Future.wait([
      _storage.write(key: _keyAccessToken, value: accessToken),
      _storage.write(key: _keyRefreshToken, value: refreshToken),
    ]);
  }

  Future<void> clearTokens() async {
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
