import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'getting_started_repository.dart';
import 'getting_started_tasks.dart';

/// Reads one server-shaped [GettingStartedSignals] summary. Counts and booleans
/// are computed database-side so mobile does not download full property, tenant,
/// lease, alert, routing, and tenant-notice payloads just to render the checklist.
///
/// `autoDispose` mirrors the home-tab `FutureProvider.autoDispose` style: the
/// dashboard card and the checklist screen are the only watchers, so the request
/// drops the moment both are gone.
final gettingStartedSignalsProvider =
    FutureProvider.autoDispose<GettingStartedSignals>((ref) async {
      return ref.watch(gettingStartedRepositoryProvider).signals();
    });

/// Convenience: the progress rollup over the live signals. Returns null while
/// the signals are still loading or errored, so callers can hide their surface
/// (no flash of an "all to-do" state) until data settles.
final gettingStartedProgressProvider =
    Provider.autoDispose<GettingStartedProgress?>((ref) {
      final signals = ref.watch(gettingStartedSignalsProvider);
      return signals.maybeWhen(
        data: computeGettingStartedProgress,
        orElse: () => null,
      );
    });
