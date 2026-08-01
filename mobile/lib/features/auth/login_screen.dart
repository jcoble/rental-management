import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/auth/auth_repository.dart';
import '../../core/auth/biometric_auth_service.dart';
import '../../core/auth/google_sign_in_service.dart';
import '../../core/api/api_exception.dart';

class LoginScreen extends ConsumerStatefulWidget {
  const LoginScreen({super.key});

  @override
  ConsumerState<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends ConsumerState<LoginScreen> {
  final _formKey = GlobalKey<FormState>();
  final _emailController = TextEditingController();
  final _passwordController = TextEditingController();

  bool _obscurePassword = true;
  bool _isLoading = false;
  bool _isGoogleLoading = false;
  bool _isBiometricUnlocking = false;
  bool _automaticBiometricPromptShown = false;
  bool _isResendingVerification = false;
  bool _resendSucceeded = false;
  String? _errorMessage;
  List<EffectiveAccessContextOption> _accessContexts = const [];
  int? _selectedAccessContextId;

  /// True when the last login failed specifically because the email is unverified
  /// (the API marks it with an `EMAIL_NOT_VERIFIED:` prefix). Drives the resend UI.
  bool _emailNotVerified = false;

  /// True while either sign-in path is in flight — disables all actions.
  bool get _busy => _isLoading || _isGoogleLoading;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || _automaticBiometricPromptShown) return;
      if (ref.read(authControllerProvider) is AuthStateBiometricLocked) {
        _automaticBiometricPromptShown = true;
        _unlockWithBiometrics();
      }
    });
  }

  @override
  void dispose() {
    _emailController.dispose();
    _passwordController.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;

    setState(() {
      _isLoading = true;
      _errorMessage = null;
      _emailNotVerified = false;
      _resendSucceeded = false;
    });

    try {
      await ref
          .read(authControllerProvider.notifier)
          .login(
            _emailController.text.trim(),
            _passwordController.text,
            accessContextId: _selectedAccessContextId,
          );
      // Router redirect handles navigation on success.
    } on AccessContextSelectionRequiredException catch (e) {
      if (mounted) {
        setState(() {
          _accessContexts = e.contexts;
          _selectedAccessContextId = e.contexts.length == 1
              ? e.contexts.single.accessContextId
              : null;
          _errorMessage = 'Choose the workspace you want to open.';
        });
      }
    } on ApiException catch (e) {
      if (mounted) {
        final unverified = e.message.contains('EMAIL_NOT_VERIFIED');
        setState(() {
          _emailNotVerified = unverified;
          // Show friendly copy for the unverified case; the inline resend button
          // explains the next step, so we don't echo the raw marker string.
          _errorMessage = unverified
              ? 'Please verify your email address before signing in.'
              : e.message;
        });
      }
    } finally {
      if (mounted) {
        setState(() => _isLoading = false);
      }
    }
  }

  /// Re-sends the verification email for the address in the box. The endpoint is
  /// anonymous and neutral (no account enumeration), so any non-throw = "sent".
  Future<void> _resendVerification() async {
    final email = _emailController.text.trim();
    if (email.isEmpty) return;

    setState(() {
      _isResendingVerification = true;
      _resendSucceeded = false;
    });

    try {
      await ref.read(authControllerProvider.notifier).resendVerification(email);
      if (mounted) setState(() => _resendSucceeded = true);
    } on ApiException {
      // Stay silent — the API intentionally hides whether the account exists.
    } finally {
      if (mounted) setState(() => _isResendingVerification = false);
    }
  }

  Future<void> _signInWithGoogle() async {
    setState(() {
      _isGoogleLoading = true;
      _errorMessage = null;
    });

    try {
      final idToken = await ref.read(googleSignInServiceProvider).signIn();
      // Null means the user cancelled the Google sheet — silent no-op.
      if (idToken == null) return;
      await ref.read(authControllerProvider.notifier).signInWithGoogle(idToken);
      // Router redirect handles navigation on success.
    } on GoogleSignInUnavailable catch (e) {
      if (mounted) setState(() => _errorMessage = e.message);
    } on ApiException catch (e) {
      if (mounted) setState(() => _errorMessage = e.message);
    } catch (e) {
      if (mounted) {
        setState(
          () => _errorMessage = 'Google sign-in failed. Please try again.',
        );
      }
    } finally {
      if (mounted) setState(() => _isGoogleLoading = false);
    }
  }

  Future<void> _unlockWithBiometrics() async {
    if (_isBiometricUnlocking) return;
    setState(() => _isBiometricUnlocking = true);
    await ref.read(authControllerProvider.notifier).unlockBiometricSession();
    if (mounted) setState(() => _isBiometricUnlocking = false);
  }

  void _fillDevLogin() {
    _emailController.text = 'admin@rentalcommand.local';
    _passwordController.text = 'Admin123!';
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    // Backstop: if a sign-in attempt left the controller in an unauthenticated
    // state with an error but our local `_errorMessage` wasn't set for some
    // reason, still surface it. The local message (set in _submit/_signInWithGoogle)
    // takes priority so we never show two error chips for the same failure.
    final authState = ref.watch(authControllerProvider);
    final stateError = authState is AuthStateUnauthenticated
        ? authState.error
        : null;
    final effectiveError = _errorMessage ?? stateError;

    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 32),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 400),
              child: Card(
                margin: EdgeInsets.zero,
                child: Padding(
                  padding: const EdgeInsets.all(24),
                  child: Form(
                    key: _formKey,
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Center(
                          child: Container(
                            width: 64,
                            height: 64,
                            decoration: BoxDecoration(
                              color: colorScheme.primaryContainer,
                              borderRadius: BorderRadius.circular(16),
                            ),
                            child: Icon(
                              Icons.home_work_rounded,
                              size: 34,
                              color: colorScheme.onPrimaryContainer,
                            ),
                          ),
                        ),
                        const SizedBox(height: 18),
                        Text(
                          'Rental Command',
                          style: theme.textTheme.headlineSmall?.copyWith(
                            fontWeight: FontWeight.w700,
                            color: colorScheme.onSurface,
                          ),
                          textAlign: TextAlign.center,
                        ),
                        const SizedBox(height: 8),
                        Text(
                          'Sign in to manage your properties',
                          style: theme.textTheme.bodyMedium?.copyWith(
                            color: colorScheme.onSurfaceVariant,
                          ),
                          textAlign: TextAlign.center,
                        ),
                        if (authState is AuthStateBiometricLocked) ...[
                          const SizedBox(height: 24),
                          _BiometricUnlockCard(
                            result: authState.lastResult,
                            isUnlocking: _isBiometricUnlocking,
                            onRetry: _unlockWithBiometrics,
                          ),
                        ],
                        const SizedBox(height: 32),

                        if (_accessContexts.length > 1) ...[
                          DropdownButtonFormField<int>(
                            initialValue: _selectedAccessContextId,
                            decoration: const InputDecoration(
                              labelText: 'Workspace',
                              prefixIcon: Icon(Icons.business_outlined),
                            ),
                            items: _accessContexts
                                .map(
                                  (context) => DropdownMenuItem<int>(
                                    value: context.accessContextId,
                                    child: Text(context.workspaceName),
                                  ),
                                )
                                .toList(growable: false),
                            onChanged: _busy
                                ? null
                                : (value) => setState(
                                    () => _selectedAccessContextId = value,
                                  ),
                            validator: (_) =>
                                _accessContexts.length > 1 &&
                                    _selectedAccessContextId == null
                                ? 'Choose a workspace'
                                : null,
                          ),
                          const SizedBox(height: 16),
                        ],

                        TextFormField(
                          controller: _emailController,
                          keyboardType: TextInputType.emailAddress,
                          textInputAction: TextInputAction.next,
                          autocorrect: false,
                          decoration: const InputDecoration(
                            labelText: 'Email',
                            hintText: 'you@example.com',
                            prefixIcon: Icon(Icons.email_outlined),
                          ),
                          validator: (value) {
                            if (value == null || value.trim().isEmpty) {
                              return 'Email is required';
                            }
                            if (!value.contains('@')) {
                              return 'Enter a valid email address';
                            }
                            return null;
                          },
                        ),
                        const SizedBox(height: 16),

                        TextFormField(
                          controller: _passwordController,
                          obscureText: _obscurePassword,
                          textInputAction: TextInputAction.done,
                          onFieldSubmitted: (_) => _busy ? null : _submit(),
                          decoration: InputDecoration(
                            labelText: 'Password',
                            prefixIcon: const Icon(Icons.lock_outlined),
                            suffixIcon: IconButton(
                              icon: Icon(
                                _obscurePassword
                                    ? Icons.visibility_outlined
                                    : Icons.visibility_off_outlined,
                              ),
                              onPressed: () => setState(
                                () => _obscurePassword = !_obscurePassword,
                              ),
                              tooltip: _obscurePassword
                                  ? 'Show password'
                                  : 'Hide password',
                            ),
                          ),
                          validator: (value) {
                            if (value == null || value.isEmpty) {
                              return 'Password is required';
                            }
                            return null;
                          },
                        ),
                        const SizedBox(height: 24),

                        if (effectiveError != null) ...[
                          Container(
                            padding: const EdgeInsets.symmetric(
                              horizontal: 12,
                              vertical: 10,
                            ),
                            decoration: BoxDecoration(
                              color: colorScheme.errorContainer,
                              borderRadius: BorderRadius.circular(10),
                            ),
                            child: Row(
                              children: [
                                Icon(
                                  Icons.error_outline,
                                  size: 18,
                                  color: colorScheme.onErrorContainer,
                                ),
                                const SizedBox(width: 8),
                                Expanded(
                                  child: Text(
                                    effectiveError,
                                    style: theme.textTheme.bodySmall?.copyWith(
                                      color: colorScheme.onErrorContainer,
                                    ),
                                  ),
                                ),
                              ],
                            ),
                          ),
                          const SizedBox(height: 16),
                        ],

                        // Unverified-email recovery: offer to resend the
                        // verification email for the address in the box.
                        if (_emailNotVerified) ...[
                          if (_resendSucceeded)
                            Padding(
                              padding: const EdgeInsets.only(bottom: 16),
                              child: Text(
                                'Verification email sent! Check your inbox '
                                '(and spam folder).',
                                style: theme.textTheme.bodySmall?.copyWith(
                                  color: colorScheme.primary,
                                ),
                                textAlign: TextAlign.center,
                              ),
                            )
                          else ...[
                            OutlinedButton.icon(
                              onPressed: _isResendingVerification
                                  ? null
                                  : _resendVerification,
                              icon: _isResendingVerification
                                  ? const SizedBox(
                                      height: 18,
                                      width: 18,
                                      child: CircularProgressIndicator(
                                        strokeWidth: 2,
                                      ),
                                    )
                                  : const Icon(
                                      Icons.mark_email_read_outlined,
                                      size: 18,
                                    ),
                              label: Text(
                                _isResendingVerification
                                    ? 'Sending…'
                                    : 'Resend verification email',
                              ),
                            ),
                            const SizedBox(height: 16),
                          ],
                        ],

                        FilledButton(
                          onPressed: _busy ? null : _submit,
                          child: _isLoading
                              ? const SizedBox(
                                  height: 20,
                                  width: 20,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 2,
                                  ),
                                )
                              : const Text('Sign In'),
                        ),

                        const SizedBox(height: 20),
                        _OrDivider(theme: theme),
                        const SizedBox(height: 20),

                        OutlinedButton.icon(
                          onPressed: _busy ? null : _signInWithGoogle,
                          icon: _isGoogleLoading
                              ? const SizedBox(
                                  height: 18,
                                  width: 18,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 2,
                                  ),
                                )
                              : const Icon(
                                  Icons.g_mobiledata_rounded,
                                  size: 26,
                                ),
                          label: const Text('Sign in with Google'),
                        ),

                        const SizedBox(height: 20),
                        TextButton(
                          onPressed: _busy
                              ? null
                              : () => context.push('/register'),
                          child: const Text('Create an account'),
                        ),
                        TextButton(
                          onPressed: _busy
                              ? null
                              : () => context.push('/forgot-password'),
                          child: const Text('Forgot password?'),
                        ),

                        // Dev convenience: fill demo credentials. Shown in debug + profile
                        // builds (!kReleaseMode); hidden in a real release build.
                        if (!kReleaseMode) ...[
                          const SizedBox(height: 16),
                          OutlinedButton.icon(
                            onPressed: _fillDevLogin,
                            icon: const Icon(Icons.developer_mode, size: 18),
                            label: const Text('Fill dev login'),
                            style: OutlinedButton.styleFrom(
                              foregroundColor: colorScheme.tertiary,
                              side: BorderSide(color: colorScheme.tertiary),
                            ),
                          ),
                        ],
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// Compact biometric affordance shown only for an opted-in stored session.
///
/// The Android system prompt remains the authentication surface. This control
/// is deliberately styled like the other login actions instead of a status
/// card so returning users can scan it as another sign-in method.
class _BiometricUnlockCard extends StatelessWidget {
  const _BiometricUnlockCard({
    required this.result,
    required this.isUnlocking,
    required this.onRetry,
  });

  final BiometricAuthenticationResult? result;
  final bool isUnlocking;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Column(
      children: [
        TextButton.icon(
          key: const Key('biometric-login-action'),
          onPressed: isUnlocking ? null : onRetry,
          icon: isUnlocking
              ? const SizedBox(
                  width: 24,
                  height: 24,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : const Icon(Icons.fingerprint, size: 28),
          label: const Text('Fingerprint'),
          style: TextButton.styleFrom(
            foregroundColor: colorScheme.primary,
            textStyle: theme.textTheme.titleSmall,
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
          ),
        ),
        if (result != null) ...[
          const SizedBox(height: 4),
          Text(
            _biometricFallbackCopy(result),
            style: theme.textTheme.bodySmall?.copyWith(
              color: colorScheme.onSurfaceVariant,
            ),
            textAlign: TextAlign.center,
          ),
        ],
      ],
    );
  }
}

String _biometricFallbackCopy(BiometricAuthenticationResult? result) {
  return switch (result) {
    null => 'Confirm it’s you, or use your password or Google below.',
    BiometricAuthenticationResult.authenticated =>
      'Confirm it’s you, or use your password or Google below.',
    BiometricAuthenticationResult.canceled =>
      'Biometric sign-in was canceled. Use your password or Google, or try again.',
    BiometricAuthenticationResult.lockedOut =>
      'Biometric sign-in is temporarily locked. Unlock your phone or use your password or Google.',
    BiometricAuthenticationResult.notEnrolled =>
      'No fingerprint or other biometric is set up on this device. Use your password or Google.',
    BiometricAuthenticationResult.unavailable =>
      'Biometric sign-in isn’t available on this device. Use your password or Google.',
  };
}

/// A horizontal rule with a centered "or" label, themed via [ColorScheme].
class _OrDivider extends StatelessWidget {
  const _OrDivider({required this.theme});

  final ThemeData theme;

  @override
  Widget build(BuildContext context) {
    final color = theme.colorScheme.outlineVariant;
    return Row(
      children: [
        Expanded(child: Divider(color: color)),
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 12),
          child: Text(
            'or',
            style: theme.textTheme.bodySmall?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
        ),
        Expanded(child: Divider(color: color)),
      ],
    );
  }
}

/// Compact biometric affordance shown only for an opted-in stored session.
///
/// The Android system prompt remains the authentication surface. This control
/// is deliberately styled like the other login actions instead of a status
/// card so returning users can scan it as another sign-in method.
class _BiometricUnlockCard extends StatelessWidget {
  const _BiometricUnlockCard({
    required this.result,
    required this.isUnlocking,
    required this.onRetry,
  });

  final BiometricAuthenticationResult? result;
  final bool isUnlocking;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Column(
      children: [
        TextButton.icon(
          key: const Key('biometric-login-action'),
          onPressed: isUnlocking ? null : onRetry,
          icon: isUnlocking
              ? const SizedBox(
                  width: 24,
                  height: 24,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : const Icon(Icons.fingerprint, size: 28),
          label: const Text('Fingerprint'),
          style: TextButton.styleFrom(
            foregroundColor: colorScheme.primary,
            textStyle: theme.textTheme.titleSmall,
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
          ),
        ),
        if (result != null) ...[
          const SizedBox(height: 4),
          Text(
            _biometricFallbackCopy(result),
            style: theme.textTheme.bodySmall?.copyWith(
              color: colorScheme.onSurfaceVariant,
            ),
            textAlign: TextAlign.center,
          ),
        ],
      ],
    );
  }
}

String _biometricFallbackCopy(BiometricAuthenticationResult? result) {
  return switch (result) {
    null => 'Confirm it’s you, or use your password or Google below.',
    BiometricAuthenticationResult.authenticated =>
      'Confirm it’s you, or use your password or Google below.',
    BiometricAuthenticationResult.canceled =>
      'Biometric sign-in was canceled. Use your password or Google, or try again.',
    BiometricAuthenticationResult.lockedOut =>
      'Biometric sign-in is temporarily locked. Unlock your phone or use your password or Google.',
    BiometricAuthenticationResult.notEnrolled =>
      'No fingerprint or other biometric is set up on this device. Use your password or Google.',
    BiometricAuthenticationResult.unavailable =>
      'Biometric sign-in isn’t available on this device. Use your password or Google.',
  };
}
