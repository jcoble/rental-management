/// The first-login Sandbox-vs-Live choice the user makes.
enum OnboardingMode {
  sandbox,
  live;

  /// Wire value expected by `POST /api/v1/portfolio/onboarding-choice`.
  String get wire => switch (this) {
        OnboardingMode.sandbox => 'sandbox',
        OnboardingMode.live => 'live',
      };
}

/// Mirrors the API `SandboxStateResponse`: the account's Sandbox/Live lifecycle state plus whether
/// the first-login choice is still pending.
class SandboxState {
  const SandboxState({
    required this.portfolioId,
    required this.isSandbox,
    required this.onboardingChoicePending,
    this.sandboxSeededAtUtc,
  });

  final int portfolioId;
  final bool isSandbox;
  final bool onboardingChoicePending;
  final DateTime? sandboxSeededAtUtc;

  factory SandboxState.fromJson(Map<String, dynamic> json) {
    final seeded = json['sandboxSeededAtUtc'] as String?;
    return SandboxState(
      portfolioId: (json['portfolioId'] as num?)?.toInt() ?? 0,
      isSandbox: json['isSandbox'] as bool? ?? false,
      onboardingChoicePending: json['onboardingChoicePending'] as bool? ?? false,
      sandboxSeededAtUtc:
          seeded == null || seeded.isEmpty ? null : DateTime.tryParse(seeded),
    );
  }
}
