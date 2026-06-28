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
}

class _FakeTenantsRepository extends TenantsRepository {
  _FakeTenantsRepository() : super(Dio());

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
}
