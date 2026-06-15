import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/api/api_exception.dart';
import 'onboarding_repository.dart';

/// A11: the actionable "switch to my real rentals" (go-live) flow, reachable
/// from the Sandbox banner and the dashboard "Getting started" nudge.
///
/// Going live permanently deletes the seeded example data and is irreversible,
/// so — exactly like the web client — it is guarded by a type-to-confirm step
/// (type "GO LIVE"). The Sandbox feature itself is untouched; this is only the
/// deliberate one-way exit out of it.
const _confirmPhrase = 'GO LIVE';

/// Opens the go-live confirmation sheet. Returns `true` if the account was
/// switched to Live, otherwise `null`/`false`.
Future<bool?> showGoLiveSheet(BuildContext context) {
  return showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    showDragHandle: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(28)),
    ),
    builder: (_) => const _GoLiveSheet(),
  );
}

class _GoLiveSheet extends ConsumerStatefulWidget {
  const _GoLiveSheet();

  @override
  ConsumerState<_GoLiveSheet> createState() => _GoLiveSheetState();
}

class _GoLiveSheetState extends ConsumerState<_GoLiveSheet> {
  final _confirmCtrl = TextEditingController();
  bool _busy = false;
  String? _error;

  bool get _confirmed =>
      _confirmCtrl.text.trim().toUpperCase() == _confirmPhrase;

  @override
  void dispose() {
    _confirmCtrl.dispose();
    super.dispose();
  }

  Future<void> _goLive() async {
    if (!_confirmed || _busy) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await ref.read(onboardingRepositoryProvider).goLive();
      // The account is now Live: refresh the sandbox indicator + the dashboard
      // data so the example records disappear and the real (empty) state shows.
      ref.invalidate(sandboxStateProvider);
      if (!mounted) return;
      Navigator.of(context).pop(true);
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          const SnackBar(
            content: Text(
              "You're set up with your real rentals! The example data was "
              'cleared.',
            ),
          ),
        );
    } on ApiException catch (e) {
      if (!mounted) return;
      setState(() {
        _busy = false;
        _error = e.message;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _busy = false;
        _error = 'Could not switch over. Please try again.';
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final bottomInset = MediaQuery.viewInsetsOf(context).bottom;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 4, 20, 24 + bottomInset),
      child: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Center(
              child: Container(
                width: 56,
                height: 56,
                decoration: BoxDecoration(
                  color: cs.primaryContainer,
                  borderRadius: BorderRadius.circular(18),
                ),
                child: Icon(Symbols.rocket_launch_rounded,
                    size: 28, color: cs.onPrimaryContainer, fill: 1),
              ),
            ),
            const SizedBox(height: 18),
            Text(
              'Set up my real rentals',
              textAlign: TextAlign.center,
              style: theme.textTheme.titleLarge?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              'This permanently deletes all the example data in this account and '
              "switches it over to your real rentals for good. You'll start with "
              "a clean account. There's no way back to the example data "
              'afterward.',
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: cs.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 20),
            Text(
              'Type $_confirmPhrase to confirm',
              style: theme.textTheme.labelMedium?.copyWith(
                color: cs.onSurfaceVariant,
                fontWeight: FontWeight.w600,
              ),
            ),
            const SizedBox(height: 8),
            TextField(
              controller: _confirmCtrl,
              autocorrect: false,
              enableSuggestions: false,
              textCapitalization: TextCapitalization.characters,
              textInputAction: TextInputAction.done,
              enabled: !_busy,
              onChanged: (_) => setState(() {}),
              onSubmitted: (_) => _goLive(),
              inputFormatters: [
                LengthLimitingTextInputFormatter(_confirmPhrase.length),
              ],
              decoration: InputDecoration(
                hintText: _confirmPhrase,
                border: const OutlineInputBorder(),
              ),
            ),
            if (_error != null) ...[
              const SizedBox(height: 12),
              Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: cs.errorContainer,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Text(
                  _error!,
                  style: TextStyle(color: cs.onErrorContainer),
                ),
              ),
            ],
            const SizedBox(height: 20),
            FilledButton.icon(
              onPressed: _confirmed && !_busy ? _goLive : null,
              icon: _busy
                  ? const SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Symbols.rocket_launch_rounded, fill: 1),
              label: Text(_busy ? 'Switching over…' : 'Use my real rentals'),
            ),
            const SizedBox(height: 8),
            TextButton(
              onPressed: _busy ? null : () => Navigator.of(context).pop(false),
              child: const Text('Cancel'),
            ),
          ],
        ),
      ),
    );
  }
}
