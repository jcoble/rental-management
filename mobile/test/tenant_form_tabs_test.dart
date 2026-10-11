import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/tenants/tenants_list_screen.dart';
import 'package:rental_command/features/tenants/tenants_repository.dart';

void main() {
  testWidgets('tenant bottom sheet splits contact and emergency fields', (
    tester,
  ) async {
    final repo = _FakeTenantsRepository();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [tenantsRepositoryProvider.overrideWithValue(repo)],
        child: MaterialApp(
          home: Scaffold(body: TenantFormSheet(onSaved: () {})),
        ),
      ),
    );

    expect(find.text('Contact'), findsOneWidget);
    expect(find.text('Emergency'), findsOneWidget);
    expect(find.text('First name'), findsOneWidget);
    expect(find.text('Emergency contact (optional)'), findsNothing);

    await tester.enterText(
      find.byKey(const Key('tenant-first-name-field')),
      'Jesse',
    );
    await tester.enterText(
      find.byKey(const Key('tenant-last-name-field')),
      'Coble',
    );
    await tester.tap(find.text('Emergency'));
    await tester.pumpAndSettle();

    expect(find.text('Emergency contact (optional)'), findsOneWidget);
    expect(find.byKey(const Key('tabbed-form-complete-0')), findsOneWidget);
  });

  testWidgets('tenant edit sends flags for cleared contact fields', (
    tester,
  ) async {
    final repo = _FakeTenantsRepository();
    final tenant = Tenant(
      id: 7,
      portfolioId: 1,
      firstName: 'Morgan',
      lastName: 'Resident',
      fullName: 'Morgan Resident',
      email: 'morgan@example.test',
      phone: '614-555-0199',
      emergencyContact: 'Jordan, 614-555-0123',
      createdAt: DateTime(2026),
      updatedAt: DateTime(2026),
    );

    await tester.pumpWidget(
      ProviderScope(
        overrides: [tenantsRepositoryProvider.overrideWithValue(repo)],
        child: MaterialApp(
          home: Scaffold(
            body: TenantFormSheet(existing: tenant, onSaved: () {}),
          ),
        ),
      ),
    );

    await tester.enterText(
      find.widgetWithText(TextFormField, 'Email (optional)'),
      '',
    );
    await tester.enterText(
      find.widgetWithText(TextFormField, 'Phone (optional)'),
      '',
    );
    await tester.tap(find.text('Emergency'));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.widgetWithText(TextFormField, 'Emergency contact (optional)'),
      '',
    );
    await tester.tap(find.text('Save Changes'));
    await tester.pumpAndSettle();

    expect(repo.updatedData?['email'], isNull);
    expect(repo.updatedData?['phone'], isNull);
    expect(repo.updatedData?['emergencyContact'], isNull);
    expect(repo.updatedData?['clearEmail'], isTrue);
    expect(repo.updatedData?['clearPhone'], isTrue);
    expect(repo.updatedData?['clearEmergencyContact'], isTrue);
  });
}

class _FakeTenantsRepository extends TenantsRepository {
  _FakeTenantsRepository() : super(Dio());

  Map<String, dynamic>? updatedData;

  @override
  Future<Tenant> createTenant(Map<String, dynamic> data) async {
    return Tenant(
      id: 1,
      portfolioId: 1,
      firstName: data['firstName'] as String? ?? '',
      lastName: data['lastName'] as String? ?? '',
      email: data['email'] as String?,
      phone: data['phone'] as String?,
      emergencyContact: data['emergencyContact'] as String?,
      createdAt: DateTime(2026),
      updatedAt: DateTime(2026),
    );
  }

  @override
  Future<Tenant> updateTenant(int id, Map<String, dynamic> data) async {
    updatedData = Map<String, dynamic>.from(data);
    return Tenant(
      id: id,
      portfolioId: 1,
      firstName: data['firstName'] as String? ?? '',
      lastName: data['lastName'] as String? ?? '',
      createdAt: DateTime(2026),
      updatedAt: DateTime(2026),
    );
  }
}
