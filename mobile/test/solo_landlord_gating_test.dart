import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/auth/biometric_auth_service.dart';
import 'package:rental_command/core/auth/solo_landlord.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/owners/owners_models.dart';
import 'package:rental_command/features/owners/owners_repository.dart';
import 'package:rental_command/features/properties/property_detail_screen.dart';
import 'package:rental_command/features/settings/settings_screen.dart';
import 'package:rental_command/features/team/team_repository.dart';

void main() {
  test('solo landlord is true with one owner and no team', () async {
    final container = _container(ownerCount: 1, otherTeamMembers: 0);
    addTearDown(container.dispose);

    expect(await container.read(soloLandlordProvider.future), isTrue);
  });

  test('solo landlord is false with two owners', () async {
    final container = _container(ownerCount: 2, otherTeamMembers: 0);
    addTearDown(container.dispose);

    expect(await container.read(soloLandlordProvider.future), isFalse);
  });

  test('solo landlord is false with another team member', () async {
    final container = _container(ownerCount: 1, otherTeamMembers: 1);
    addTearDown(container.dispose);

    expect(await container.read(soloLandlordProvider.future), isFalse);
  });

  test('solo landlord is false when lists fail', () async {
    final container = _container(ownerCount: 1, otherTeamMembers: 0, fail: true);
    addTearDown(container.dispose);

    expect(await container.read(soloLandlordProvider.future), isFalse);
  });

  testWidgets(
    'property detail hides management card and shows owned by you when solo',
    (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [soloLandlordProvider.overrideWith((ref) => true)],
          child: MaterialApp(home: PropertyDetailScreen(property: _property)),
        ),
      );
      await tester.pump();

      await tester.ensureVisible(find.text('Ownership & management'));
      await tester.tap(find.text('Ownership & management'));
      await tester.pumpAndSettle();

      expect(find.text('Owned by you'), findsOneWidget);
      expect(find.text('Add another owner'), findsOneWidget);
      expect(find.text('Management fee'), findsNothing);
    },
  );

  testWidgets('property detail keeps the management card for a company', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [soloLandlordProvider.overrideWith((ref) => false)],
        child: MaterialApp(home: PropertyDetailScreen(property: _property)),
      ),
    );
    await tester.pump();

    await tester.ensureVisible(find.text('Ownership & management'));
    await tester.tap(find.text('Ownership & management'));
    await tester.pumpAndSettle();

    expect(find.text('Management fee'), findsOneWidget);
    expect(find.text('Owned by you'), findsNothing);
  });

  testWidgets('settings hides team routing when solo', (tester) async {
    await tester.pumpWidget(_settingsApp(solo: true));
    await tester.pump();

    expect(find.text('Who gets told what'), findsNothing);
    expect(find.text('Tenant notices'), findsOneWidget);
  });

  testWidgets('settings keeps team routing for a company', (tester) async {
    await tester.pumpWidget(_settingsApp(solo: false));
    await tester.pump();

    expect(find.text('Who gets told what'), findsOneWidget);
  });
}

ProviderContainer _container({
  required int ownerCount,
  required int otherTeamMembers,
  bool fail = false,
}) {
  return ProviderContainer(
    overrides: [
      authControllerProvider.overrideWith(
        () => _StaticAuthController(_authenticatedState),
      ),
      ownersRepositoryProvider.overrideWithValue(
        _FakeOwnersRepository(totalCount: ownerCount, fail: fail),
      ),
      teamRepositoryProvider.overrideWithValue(
        _FakeTeamRepository(otherMembers: otherTeamMembers),
      ),
    ],
  );
}

Widget _settingsApp({required bool solo}) {
  return ProviderScope(
    overrides: [
      soloLandlordProvider.overrideWith((ref) => solo),
      authControllerProvider.overrideWith(
        () => _StaticAuthController(_authenticatedState),
      ),
      biometricAuthServiceProvider.overrideWithValue(_FakeBiometric()),
    ],
    child: const MaterialApp(home: SettingsScreen()),
  );
}

final _property = Property(
  id: 1,
  portfolioId: 1,
  name: 'Maple Street',
  type: 'SingleFamily',
  rentalStructure: RentalStructure.singleRental,
  status: 'Active',
  addressLine1: '12 Maple Street',
  city: 'Springfield',
  state: 'IL',
  postalCode: '62701',
  managementFeePercent: 8,
  createdAt: DateTime.utc(2026, 1, 1),
  updatedAt: DateTime.utc(2026, 1, 1),
);

final _authenticatedState = AuthStateAuthenticated(
  const AuthUser(
    id: 7,
    email: 'landlord@example.test',
    displayName: 'Landlord',
    emailVerified: true,
  ),
  const AccessEnvelope(
    identity: AccessIdentity(userId: 7, displayName: 'Landlord'),
    selectedContext: SelectedAccessContext(
      accessContextId: 1,
      portfolioId: 1,
      workspaceName: 'Test workspace',
      accessRevision: 1,
      activeExperience: WorkspaceExperience.management,
    ),
    defaultExperience: WorkspaceExperience.management,
    availableExperiences: [WorkspaceExperience.management],
    assignments: [],
    navigation: [
      NavigationCapabilities(
        experience: WorkspaceExperience.management,
        capabilityKeys: ['rentals.manage', 'notifications.manage'],
      ),
    ],
  ),
  activeExperience: WorkspaceExperience.management,
);

class _StaticAuthController extends AuthController {
  _StaticAuthController(this.initialState);

  final AuthState initialState;

  @override
  AuthState build() => initialState;
}

class _FakeOwnersRepository extends OwnersRepository {
  _FakeOwnersRepository({required this.totalCount, required this.fail})
    : super(Dio());

  final int totalCount;
  final bool fail;

  @override
  Future<OwnerEntityPage> listPage([
    OwnerListQuery query = const OwnerListQuery(),
  ]) async {
    if (fail) throw Exception('owners unavailable');
    return OwnerEntityPage(
      items: const [],
      totalCount: totalCount,
      skip: 0,
      take: query.take,
    );
  }
}

class _FakeTeamRepository extends TeamRepository {
  _FakeTeamRepository({required this.otherMembers}) : super(Dio());

  final int otherMembers;

  @override
  Future<TeamMemberPage> listMembers({
    int skip = 0,
    int take = 20,
    String search = '',
    String sort = '-createdAt',
  }) async {
    final items = <TeamMember>[
      _member(7),
      for (var index = 0; index < otherMembers; index++) _member(100 + index),
    ];
    return TeamMemberPage(
      items: items,
      totalCount: items.length,
      skip: 0,
      take: take,
      search: search,
    );
  }

  static TeamMember _member(int userId) => TeamMember(
    userId: userId,
    accessContextId: userId,
    workspaceMembershipId: userId,
    email: 'user$userId@example.test',
    displayName: 'User $userId',
    accessStatus: 'Active',
    membershipStatus: 'Active',
    accessRevision: 1,
    assignmentCount: 1,
    roleSummary: 'Owner',
    requiresAccountActivation: false,
    createdAtUtc: DateTime.utc(2026, 1, 1),
  );
}

class _FakeBiometric extends BiometricAuthService {
  _FakeBiometric() : super(isAndroid: false);

  @override
  Future<bool> isEnabled() async => false;
}
