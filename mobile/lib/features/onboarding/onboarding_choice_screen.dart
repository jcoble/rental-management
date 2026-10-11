import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import 'onboarding_models.dart';
import 'onboarding_repository.dart';

/// First-login welcome screen.
///
/// Shown once, right after a brand-new account's first login. The router keeps an undecided account
/// on this screen until a choice is made (see `app_router.dart`). The screen leads with the real
/// job — adding the first rental — and offers the sample data as a quieter second option:
///   - Add your first rental → record the choice (empty real portfolio) and go to the add-rental step.
///   - Explore with sample data → hand off to the seeding screen, which records the choice + seeds it.
class OnboardingChoiceScreen extends ConsumerStatefulWidget {
  const OnboardingChoiceScreen({super.key});

  @override
  ConsumerState<OnboardingChoiceScreen> createState() =>
      _OnboardingChoiceScreenState();
}

class _OnboardingChoiceScreenState
    extends ConsumerState<OnboardingChoiceScreen> {
  bool _submitting = false;
  OnboardingMode? _selected;

  void _chooseSandbox() {
    if (_submitting) return;
    setState(() => _selected = OnboardingMode.sandbox);
    // The seeding screen owns the API call + the ~20s "setting up" theater.
    context.go('/setting-up');
  }

  Future<void> _chooseLive() async {
    if (_submitting) return;
    setState(() {
      _selected = OnboardingMode.live;
      _submitting = true;
    });
    try {
      await ref
          .read(onboardingRepositoryProvider)
          .submitChoice(OnboardingMode.live);
      // Clear the gate so the router lets us past the choice screen, then route
      // to the guided "add your first property" step (I8) — parity with web,
      // which walks Live users into a setup wizard rather than a bare dashboard.
      ref.read(authControllerProvider.notifier).markOnboardingComplete();
      if (mounted) context.go('/live-setup');
    } on ApiException catch (e) {
      if (!mounted) return;
      setState(() {
        _submitting = false;
        _selected = null;
      });
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  Future<void> _signInWithDifferentAccount() async {
    if (_submitting) return;
    setState(() => _submitting = true);
    await ref.read(authControllerProvider.notifier).logout();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;

    final authState = ref.watch(authControllerProvider);
    final displayName = authState is AuthStateAuthenticated
        ? authState.user.displayName
        : '';
    final firstName = displayName.trim().isEmpty
        ? ''
        : displayName.trim().split(RegExp(r'\s+')).first;

    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 32),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 480),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  // Brand mark
                  Center(
                    child: Container(
                      width: 60,
                      height: 60,
                      decoration: BoxDecoration(
                        color: scheme.primaryContainer,
                        borderRadius: BorderRadius.circular(18),
                      ),
                      child: Icon(
                        Icons.home_work_rounded,
                        size: 30,
                        color: scheme.onPrimaryContainer,
                      ),
                    ),
                  ),
                  const SizedBox(height: 20),
                  Text(
                    firstName.isEmpty
                        ? 'Welcome to Rental Command'
                        : 'Welcome, $firstName',
                    style: theme.textTheme.headlineSmall?.copyWith(
                      fontWeight: FontWeight.w700,
                      color: scheme.onSurface,
                    ),
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 8),
                  Text(
                    "Let's get your first rental in. It takes a couple of "
                    'minutes, and you can add the rest whenever you like.',
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: scheme.onSurfaceVariant,
                    ),
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 28),

                  // The real job, front and centre.
                  _ChoiceCard(
                    testKey: const Key('choose-live'),
                    icon: Icons.add_home_work_rounded,
                    accent: scheme.primary,
                    accentContainer: scheme.primaryContainer,
                    onAccentContainer: scheme.onPrimaryContainer,
                    title: 'Add your first rental',
                    badge: 'My real rentals',
                    body:
                        'Start with a clean, empty account and add a rental you '
                        'own. Tenants, leases and everything else follow from '
                        'there.',
                    cta: 'Add your first rental',
                    selected: _selected == OnboardingMode.live,
                    busy: _selected == OnboardingMode.live && _submitting,
                    enabled: !_submitting,
                    onTap: _chooseLive,
                  ),

                  const SizedBox(height: 18),
                  // Quieter second option: look around with made-up data first.
                  TextButton(
                    key: const Key('choose-sandbox'),
                    onPressed: _submitting ? null : _chooseSandbox,
                    child: const Text('Explore with sample data'),
                  ),
                  const SizedBox(height: 4),
                  Text(
                    'Sample data lets you try everything risk-free — nothing '
                    'sends real emails or texts, or charges any cards. Switching '
                    'to your real rentals later clears it and starts you clean.',
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: scheme.onSurfaceVariant,
                    ),
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 12),
                  TextButton.icon(
                    key: const Key('onboarding-sign-out'),
                    onPressed: _submitting ? null : _signInWithDifferentAccount,
                    icon: const Icon(Icons.logout_rounded),
                    label: const Text('Sign in with a different account'),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// A single tappable decision card (icon, title, sample/live badge, blurb, CTA row).
class _ChoiceCard extends StatelessWidget {
  const _ChoiceCard({
    required this.testKey,
    required this.icon,
    required this.accent,
    required this.accentContainer,
    required this.onAccentContainer,
    required this.title,
    required this.badge,
    required this.body,
    required this.cta,
    required this.selected,
    required this.busy,
    required this.enabled,
    required this.onTap,
  });

  final Key testKey;
  final IconData icon;
  final Color accent;
  final Color accentContainer;
  final Color onAccentContainer;
  final String title;
  final String badge;
  final String body;
  final String cta;
  final bool selected;
  final bool busy;
  final bool enabled;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;

    return Opacity(
      opacity: enabled || selected ? 1 : 0.6,
      child: Material(
        color: scheme.surfaceContainerHighest.withValues(alpha: 0.35),
        borderRadius: BorderRadius.circular(20),
        child: InkWell(
          key: testKey,
          onTap: enabled ? onTap : null,
          borderRadius: BorderRadius.circular(20),
          child: Container(
            padding: const EdgeInsets.all(18),
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(20),
              border: Border.all(
                color: selected ? accent : scheme.outlineVariant,
                width: selected ? 2 : 1,
              ),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Container(
                  width: 44,
                  height: 44,
                  decoration: BoxDecoration(
                    color: accentContainer,
                    borderRadius: BorderRadius.circular(14),
                  ),
                  child: Icon(icon, size: 22, color: onAccentContainer),
                ),
                const SizedBox(height: 14),
                Text(
                  title,
                  style: theme.textTheme.titleMedium?.copyWith(
                    fontWeight: FontWeight.w700,
                    color: scheme.onSurface,
                  ),
                ),
                const SizedBox(height: 6),
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 8,
                    vertical: 3,
                  ),
                  decoration: BoxDecoration(
                    color: accentContainer,
                    borderRadius: BorderRadius.circular(999),
                  ),
                  child: Text(
                    badge.toUpperCase(),
                    style: theme.textTheme.labelSmall?.copyWith(
                      color: onAccentContainer,
                      fontWeight: FontWeight.w700,
                      letterSpacing: 0.4,
                    ),
                  ),
                ),
                const SizedBox(height: 10),
                Text(
                  body,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: scheme.onSurfaceVariant,
                    height: 1.35,
                  ),
                ),
                const SizedBox(height: 14),
                Row(
                  children: [
                    if (busy) ...[
                      SizedBox(
                        height: 16,
                        width: 16,
                        child: CircularProgressIndicator(
                          strokeWidth: 2,
                          valueColor: AlwaysStoppedAnimation(accent),
                        ),
                      ),
                      const SizedBox(width: 8),
                      Text(
                        'Setting up…',
                        style: theme.textTheme.labelLarge?.copyWith(
                          color: accent,
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                    ] else ...[
                      Text(
                        cta,
                        style: theme.textTheme.labelLarge?.copyWith(
                          color: accent,
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                      const SizedBox(width: 4),
                      Icon(
                        Icons.arrow_forward_rounded,
                        size: 18,
                        color: accent,
                      ),
                    ],
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
