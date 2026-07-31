import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/properties/properties_list_screen.dart';
import 'package:rental_command/features/properties/properties_repository.dart';

void main() {
  testWidgets(
    'properties next-page control remains tappable beside Scan / Add',
    (tester) async {
      tester.view.physicalSize = const Size(1080, 2400);
      tester.view.devicePixelRatio = 3;
      addTearDown(tester.view.reset);

      final repository = _PagedPropertiesRepository();
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            authControllerProvider.overrideWith(
              () => _StaticAuthController(_authenticatedState),
            ),
            propertiesRepositoryProvider.overrideWithValue(repository),
          ],
          child: const MaterialApp(home: PropertiesListScreen()),
        ),
      );
      await tester.pumpAndSettle();

      final nextPage = find.byTooltip('Next properties page');
      await tester.scrollUntilVisible(
        nextPage,
        160,
        scrollable: find.byType(Scrollable).last,
      );
      final scrollable = tester.state<ScrollableState>(
        find.byType(Scrollable).last,
      );
      scrollable.position.jumpTo(scrollable.position.pixels - 120);
      await tester.pumpAndSettle();

      final nextRect = tester.getRect(nextPage);
      final fabRect = tester.getRect(find.byTooltip('Scan / Add'));
      expect(nextRect.overlaps(fabRect), isFalse);

      await tester.tap(nextPage);
      await tester.pumpAndSettle();

      expect(repository.queries.last.skip, 20);
      expect(find.text('Scan / Add'), findsNothing);
    },
  );
}

class _PagedPropertiesRepository extends PropertiesRepository {
  _PagedPropertiesRepository() : super(Dio());

  final queries = <PropertyListQuery>[];

  @override
  Future<PropertyListPage> listPropertiesPage([
    PropertyListQuery query = const PropertyListQuery(),
  ]) async {
    queries.add(query);
    final remaining = 33 - query.skip;
    final count = remaining.clamp(0, query.take);
    return PropertyListPage(
      items: [for (var i = 0; i < count; i++) _property(query.skip + i + 1)],
      totalCount: 33,
      skip: query.skip,
      take: query.take,
      workspaceEntries: const {},
    );
  }
}

Property _property(int id) => Property(
  id: id,
  portfolioId: 1,
  name: 'Property $id',
  type: 'SingleFamily',
  rentalStructure: RentalStructure.singleRental,
  status: 'Active',
  addressLine1: '$id Main Street',
  city: 'Columbus',
  state: 'OH',
  postalCode: '43215',
  unitCount: 1,
  occupiedUnits: 1,
  createdAt: DateTime(2027),
  updatedAt: DateTime(2027),
);

final _authenticatedState = AuthStateAuthenticated(
  const AuthUser(
    id: 1,
    email: 'admin@example.test',
    displayName: 'Admin',
    emailVerified: true,
  ),
  const AccessEnvelope(
    identity: AccessIdentity(userId: 1, displayName: 'Admin'),
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
        capabilityKeys: ['rentals.manage'],
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
