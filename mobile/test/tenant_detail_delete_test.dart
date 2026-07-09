import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/leases/leases_repository.dart';
import 'package:rental_command/features/tenants/tenant_detail_screen.dart';
import 'package:rental_command/features/tenants/tenants_repository.dart';

void main() {
  testWidgets(
    'tenant detail blocks delete when active lease count is present without canDelete flag',
    (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            leasesRepositoryProvider.overrideWithValue(_FakeLeasesRepository()),
            tenantsRepositoryProvider.overrideWithValue(
              _FakeTenantsRepository(),
            ),
          ],
          child: MaterialApp(home: TenantDetailScreen(tenant: _tenant())),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.byTooltip('Delete tenant'));
      await tester.pumpAndSettle();

      expect(find.text('Tenant has an active lease'), findsOneWidget);
      expect(
        find.text(
          'End or reassign this tenant’s active leases before deleting them.',
        ),
        findsOneWidget,
      );

      final deleteButton = tester.widget<FilledButton>(
        find.widgetWithText(FilledButton, 'Delete'),
      );
      expect(deleteButton.onPressed, isNull);

      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      expect(find.text('Tenant has an active lease'), findsNothing);
    },
  );
}

Tenant _tenant() {
  return Tenant(
    id: 3,
    portfolioId: 1,
    firstName: 'Verify',
    lastName: 'Tenant',
    email: 'verify@example.test',
    activeLeaseCount: 1,
    portalAccess: 'active',
    createdAt: DateTime(2026, 6, 29),
    updatedAt: DateTime(2026, 6, 29),
  );
}

class _FakeLeasesRepository extends LeasesRepository {
  _FakeLeasesRepository() : super(Dio());

  @override
  Future<List<Lease>> listLeases({
    int? tenantId,
    int? propertyId,
    int? unitId,
    String? status,
    String? startFrom,
    String? startTo,
    String? endFrom,
    String? endTo,
    String? activeOn,
    String? activeFrom,
    String? activeTo,
  }) async {
    return const [];
  }
}

class _FakeTenantsRepository extends TenantsRepository {
  _FakeTenantsRepository() : super(Dio());

  @override
  Future<Tenant> getTenant(int id) async => _tenant();

  @override
  Future<void> deleteTenant(int id) async {
    throw StateError('deleteTenant should not be called');
  }
}
