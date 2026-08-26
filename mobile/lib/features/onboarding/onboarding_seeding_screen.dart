import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import 'onboarding_models.dart';
import 'onboarding_repository.dart';

/// A single staged step in the "setting up your sandbox" theater.
class _Stage {
  const _Stage(this.label, this.icon, this.ms);
  final String label;
  final IconData icon;
  final int ms;
}

/// Theatrical "Setting up your sandbox…" screen.
///
/// Reached only when the user picks Sandbox on the choice gate. Fires the real seed (which the server
/// does quickly) and plays a deliberate ~20s staged progress animation; lands on the dashboard only
/// once BOTH the animation has finished AND the seed succeeded — so the app always has data when we
/// arrive. The 20s is intentional client-side theater; the backend stays fast.
class OnboardingSeedingScreen extends ConsumerStatefulWidget {
  const OnboardingSeedingScreen({super.key});

  @override
  ConsumerState<OnboardingSeedingScreen> createState() =>
      _OnboardingSeedingScreenState();
}

class _OnboardingSeedingScreenState
    extends ConsumerState<OnboardingSeedingScreen>
    with SingleTickerProviderStateMixin {
  static const _stages = <_Stage>[
    _Stage('Creating sample properties…', Icons.apartment_rounded, 4200),
    _Stage('Adding tenants & leases…', Icons.group_rounded, 4200),
    _Stage('Generating 6 months of payment history…',
        Icons.account_balance_wallet_rounded, 4600),
    _Stage('Setting up repairs…', Icons.build_rounded, 4200),
    _Stage('Finishing up…', Icons.auto_awesome_rounded, 2800),
  ];

  late final int _totalMs =
      _stages.fold(0, (sum, s) => sum + s.ms);
  late final AnimationController _controller;

  bool _animationDone = false;
  bool _seedDone = false;
  String? _seedError;

  int get _stageIndex {
    final elapsed = _controller.value * _totalMs;
    var acc = 0;
    for (var i = 0; i < _stages.length; i++) {
      acc += _stages[i].ms;
      if (elapsed < acc) return i;
    }
    return _stages.length - 1;
  }

  @override
  void initState() {
    super.initState();
    _controller = AnimationController(
      vsync: this,
      duration: Duration(milliseconds: _totalMs),
    )..addStatusListener((status) {
        if (status == AnimationStatus.completed) {
          _animationDone = true;
          _maybeFinish();
        }
      });

    _controller.forward();
    _runSeed();
  }

  Future<void> _runSeed() async {
    try {
      await ref
          .read(onboardingRepositoryProvider)
          .submitChoice(OnboardingMode.sandbox);
      _seedDone = true;
      _maybeFinish();
    } on ApiException catch (e) {
      if (!mounted) return;
      setState(() => _seedError = e.message);
    }
  }

  void _maybeFinish() {
    if (!_animationDone || !_seedDone || _seedError != null) return;
    // Demo data now exists server-side; clear the gate and land on the dashboard.
    ref.read(authControllerProvider.notifier).markOnboardingComplete();
    if (mounted) context.go('/');
  }

  void _retry() {
    setState(() => _seedError = null);
    _runSeed();
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final accent = scheme.tertiary;

    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.symmetric(horizontal: 28, vertical: 32),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 440),
              child: _seedError != null
                  ? _buildError(theme, scheme)
                  : _buildProgress(theme, scheme, accent),
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildError(ThemeData theme, ColorScheme scheme) {
    return Column(
      key: const Key('setting-up-error'),
      children: [
        Container(
          width: 56,
          height: 56,
          decoration: BoxDecoration(
            color: scheme.errorContainer,
            borderRadius: BorderRadius.circular(18),
          ),
          child: Icon(Icons.warning_amber_rounded,
              size: 28, color: scheme.onErrorContainer),
        ),
        const SizedBox(height: 18),
        Text(
          'Setup hit a snag',
          style: theme.textTheme.titleLarge?.copyWith(
            fontWeight: FontWeight.w700,
            color: scheme.onSurface,
          ),
        ),
        const SizedBox(height: 8),
        Text(
          _seedError ?? '',
          style: theme.textTheme.bodyMedium
              ?.copyWith(color: scheme.onSurfaceVariant),
          textAlign: TextAlign.center,
        ),
        const SizedBox(height: 22),
        Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            FilledButton(
              key: const Key('setting-up-retry'),
              onPressed: _retry,
              child: const Text('Try again'),
            ),
            const SizedBox(width: 10),
            OutlinedButton(
              onPressed: () => context.go('/choose-setup'),
              child: const Text('Back'),
            ),
          ],
        ),
      ],
    );
  }

  Widget _buildProgress(ThemeData theme, ColorScheme scheme, Color accent) {
    return AnimatedBuilder(
      animation: _controller,
      builder: (context, _) {
        final stageIndex = _stageIndex;
        final allComplete = _animationDone && _seedDone;
        return Column(
          key: const Key('setting-up-progress'),
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Center(
              child: Container(
                width: 64,
                height: 64,
                decoration: BoxDecoration(
                  color: scheme.tertiaryContainer,
                  borderRadius: BorderRadius.circular(20),
                ),
                child: Icon(Icons.science_outlined,
                    size: 32, color: scheme.onTertiaryContainer),
              ),
            ),
            const SizedBox(height: 22),
            Text(
              'Setting up your sample portfolio…',
              style: theme.textTheme.titleLarge?.copyWith(
                fontWeight: FontWeight.w700,
                color: scheme.onSurface,
              ),
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 8),
            Text(
              "We're filling your sample portfolio with realistic sample data so "
              'you can explore everything.',
              style: theme.textTheme.bodyMedium
                  ?.copyWith(color: scheme.onSurfaceVariant),
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 26),
            ClipRRect(
              borderRadius: BorderRadius.circular(999),
              child: LinearProgressIndicator(
                value: _controller.value,
                minHeight: 8,
                backgroundColor: scheme.surfaceContainerHighest,
                valueColor: AlwaysStoppedAnimation(accent),
              ),
            ),
            const SizedBox(height: 12),
            Row(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                SizedBox(
                  height: 16,
                  width: 16,
                  child: CircularProgressIndicator(
                    strokeWidth: 2,
                    valueColor: AlwaysStoppedAnimation(accent),
                  ),
                ),
                const SizedBox(width: 8),
                Flexible(
                  child: Text(
                    _animationDone && !_seedDone
                        ? 'Almost there…'
                        : _stages[stageIndex].label,
                    key: const Key('setting-up-stage'),
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: scheme.onSurface,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 24),
            // Stage checklist
            ...List.generate(_stages.length, (i) {
              final complete = i < stageIndex || allComplete;
              final active = i == stageIndex && !complete;
              return Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Container(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                  decoration: BoxDecoration(
                    color: active
                        ? scheme.tertiaryContainer.withValues(alpha: 0.4)
                        : Colors.transparent,
                    borderRadius: BorderRadius.circular(14),
                    border: Border.all(
                      color: active
                          ? accent.withValues(alpha: 0.4)
                          : Colors.transparent,
                    ),
                  ),
                  child: Row(
                    children: [
                      Container(
                        width: 24,
                        height: 24,
                        decoration: BoxDecoration(
                          color: complete || active
                              ? scheme.tertiaryContainer
                              : scheme.surfaceContainerHighest,
                          shape: BoxShape.circle,
                        ),
                        child: Icon(
                          complete ? Icons.check_rounded : _stages[i].icon,
                          size: 14,
                          color: complete || active
                              ? scheme.onTertiaryContainer
                              : scheme.onSurfaceVariant,
                        ),
                      ),
                      const SizedBox(width: 12),
                      Expanded(
                        child: Text(
                          _stages[i].label,
                          style: theme.textTheme.bodyMedium?.copyWith(
                            color: complete
                                ? scheme.onSurfaceVariant
                                : active
                                    ? scheme.onSurface
                                    : scheme.onSurfaceVariant
                                        .withValues(alpha: 0.6),
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
              );
            }),
          ],
        );
      },
    );
  }
}
