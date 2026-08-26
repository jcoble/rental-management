import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../features/owners/owners_models.dart';
import '../../features/owners/owners_repository.dart';
import '../../features/team/team_repository.dart';
import 'auth_controller.dart';

/// True when this account is one person looking after their own rentals: at
/// most one owner on file, and nobody else on the team. Management-company
/// extras stay hidden while it is true.
///
/// Anything we cannot confirm counts as "not solo" so nothing is hidden by
/// mistake.
final soloLandlordProvider = FutureProvider<bool>((ref) async {
  final auth = ref.watch(authControllerProvider);
  if (auth is! AuthStateAuthenticated) return false;

  try {
    final owners = await ref
        .watch(ownersRepositoryProvider)
        .listPage(const OwnerListQuery(take: 2));
    if (owners.totalCount > 1) return false;

    final team = await ref.watch(teamRepositoryProvider).listMembers(take: 2);
    if (team.totalCount > 1) return false;

    final signedInUserId = auth.access.identity.userId;
    return team.items.every((member) => member.userId == signedInUserId);
  } catch (_) {
    return false;
  }
});

/// Reads [soloLandlordProvider] for a widget. Still loading, or failed, counts
/// as not solo.
bool isSoloLandlord(WidgetRef ref) =>
    ref.watch(soloLandlordProvider).value ?? false;
