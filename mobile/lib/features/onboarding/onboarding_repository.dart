import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'onboarding_models.dart';

/// Repository for the first-login Sandbox-vs-Live onboarding flow.
///
/// Endpoints (under /api/v1/portfolio, all portfolio-scoped via the JWT claim):
///   GET  /portfolio/sandbox-state      — current Sandbox/Live + onboardingChoicePending
///   POST /portfolio/onboarding-choice  — body: { mode: "sandbox" | "live" }
class OnboardingRepository {
  const OnboardingRepository(this._dio);

  final Dio _dio;

  /// Reads the caller's current sandbox state, including whether the first-login choice is pending.
  Future<SandboxState> sandboxState() async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/portfolio/sandbox-state');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return SandboxState.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Records the first-login choice. `sandbox` seeds the demo portfolio server-side; `live` keeps an
  /// empty real portfolio. Idempotent on the server. Returns the resulting sandbox state.
  Future<SandboxState> submitChoice(OnboardingMode mode) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/portfolio/onboarding-choice',
        data: {'mode': mode.wire},
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return SandboxState.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final onboardingRepositoryProvider = Provider<OnboardingRepository>((ref) {
  return OnboardingRepository(ref.watch(dioProvider));
});

/// Current Sandbox/Live state for the signed-in account. Drives the app-wide "Sandbox mode" indicator
/// in the home shell. autoDispose so it refreshes when the shell remounts (e.g. after going live).
final sandboxStateProvider = FutureProvider.autoDispose<SandboxState>((ref) {
  return ref.watch(onboardingRepositoryProvider).sandboxState();
});
