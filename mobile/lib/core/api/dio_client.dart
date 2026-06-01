import 'dart:io';

import 'package:dio/dio.dart';
import 'package:dio/io.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../config/app_config.dart';

/// Configures and exposes a [Dio] instance as a Riverpod [Provider].
///
/// In debug builds with [kAllowSelfSignedCertInDebug] == true, a custom
/// [IOHttpClientAdapter] is installed that accepts the mkcert self-signed
/// certificate used in local development. This path is never compiled into
/// release builds.
final dioProvider = Provider<Dio>((ref) {
  return _buildDio();
});

Dio _buildDio() {
  final dio = Dio(
    BaseOptions(
      baseUrl: kApiBaseUrl,
      connectTimeout: const Duration(seconds: 15),
      receiveTimeout: const Duration(seconds: 20),
      sendTimeout: const Duration(seconds: 20),
      headers: {
        HttpHeaders.contentTypeHeader: 'application/json',
        HttpHeaders.acceptHeader: 'application/json',
      },
      responseType: ResponseType.json,
    ),
  );

  // Allow the mkcert self-signed cert for local dev — active in !kReleaseMode
  // (debug + profile builds). Kept out of release builds (tree-shaken), so a
  // shipped app still does full certificate validation.
  if (!kReleaseMode && kAllowSelfSignedCertInDebug) {
    dio.httpClientAdapter = IOHttpClientAdapter(
      createHttpClient: () {
        final client = HttpClient();
        // Accept self-signed / mkcert certificates for local dev.
        // This is intentionally scoped to !kReleaseMode (debug + profile builds).
        client.badCertificateCallback =
            (X509Certificate cert, String host, int port) => true;
        return client;
      },
    );
  }

  return dio;
}
