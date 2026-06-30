import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_repository.dart';

/// Verify-email screen reached from emailed `/verify-email?userId=&token=` links.
///
/// The link is a one-tap confirmation, so this screen posts the token as soon as
/// it opens and then shows a success/error state with a path back to sign in.
class VerifyEmailScreen extends ConsumerStatefulWidget {
  const VerifyEmailScreen({
    super.key,
    required this.userId,
    required this.token,
  });

  final String userId;
  final String token;

  @override
  ConsumerState<VerifyEmailScreen> createState() => _VerifyEmailScreenState();
}

class _VerifyEmailScreenState extends ConsumerState<VerifyEmailScreen> {
  late final Future<void> _future;

  bool get _invalidLink => widget.userId.isEmpty || widget.token.isEmpty;

  @override
  void initState() {
    super.initState();
    _future = _invalidLink
        ? Future<void>.error(
            const ApiException(
              statusCode: 400,
              message: 'Invalid verification link.',
            ),
          )
        : ref
              .read(authRepositoryProvider)
              .confirmEmail(userId: widget.userId, token: widget.token);
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(
        backgroundColor: Colors.transparent,
        elevation: 0,
        leading: IconButton(
          icon: const Icon(Icons.arrow_back),
          tooltip: 'Back to sign in',
          onPressed: () => context.go('/login'),
        ),
      ),
      extendBodyBehindAppBar: true,
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
                  child: FutureBuilder<void>(
                    future: _future,
                    builder: (context, snapshot) {
                      if (snapshot.connectionState != ConnectionState.done) {
                        return _StatusContent(
                          icon: Icons.mark_email_read_outlined,
                          title: 'Verifying your email',
                          body: 'Hang tight while we confirm this link.',
                          color: colorScheme.primaryContainer,
                          foreground: colorScheme.onPrimaryContainer,
                          trailing: const Padding(
                            padding: EdgeInsets.only(top: 20),
                            child: Center(child: CircularProgressIndicator()),
                          ),
                        );
                      }

                      if (snapshot.hasError) {
                        final message = snapshot.error is ApiException
                            ? (snapshot.error! as ApiException).message
                            : 'Email verification failed.';
                        return _StatusContent(
                          icon: Icons.error_outline_rounded,
                          title: 'Could not verify email',
                          body: message,
                          color: colorScheme.errorContainer,
                          foreground: colorScheme.onErrorContainer,
                          trailing: TextButton(
                            onPressed: () => context.go('/login'),
                            child: const Text('Back to sign in'),
                          ),
                        );
                      }

                      return _StatusContent(
                        icon: Icons.verified_rounded,
                        title: 'Email verified',
                        body: 'You can now sign in to Rental Command.',
                        color: colorScheme.primaryContainer,
                        foreground: colorScheme.onPrimaryContainer,
                        trailing: FilledButton(
                          onPressed: () => context.go('/login'),
                          child: const Text('Sign in'),
                        ),
                      );
                    },
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

class _StatusContent extends StatelessWidget {
  const _StatusContent({
    required this.icon,
    required this.title,
    required this.body,
    required this.color,
    required this.foreground,
    required this.trailing,
  });

  final IconData icon;
  final String title;
  final String body;
  final Color color;
  final Color foreground;
  final Widget trailing;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      mainAxisSize: MainAxisSize.min,
      children: [
        Center(
          child: Container(
            width: 64,
            height: 64,
            decoration: BoxDecoration(
              color: color,
              borderRadius: BorderRadius.circular(16),
            ),
            child: Icon(icon, size: 34, color: foreground),
          ),
        ),
        const SizedBox(height: 18),
        Text(
          title,
          style: theme.textTheme.headlineSmall?.copyWith(
            fontWeight: FontWeight.w700,
          ),
          textAlign: TextAlign.center,
        ),
        const SizedBox(height: 8),
        Text(
          body,
          style: theme.textTheme.bodyMedium?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
          textAlign: TextAlign.center,
        ),
        const SizedBox(height: 24),
        trailing,
      ],
    );
  }
}
