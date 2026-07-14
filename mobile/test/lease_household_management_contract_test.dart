import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test(
    'mobile household workflow is canonical, scoped, and capability gated',
    () {
      final repository = File(
        'lib/features/leases/leases_repository.dart',
      ).readAsStringSync();
      final sheet = File(
        'lib/features/leases/household_management_sheet.dart',
      ).readAsStringSync();
      final detail = File(
        'lib/features/leases/lease_detail_screen.dart',
      ).readAsStringSync();
      final tenants = File(
        'lib/features/tenants/tenants_repository.dart',
      ).readAsStringSync();

      for (final route in ['change-role', '/end', '/access']) {
        expect(repository, contains(route));
      }
      expect(repository, contains("'Idempotency-Key': operationKey"));
      expect(tenants, isNot(contains('/portal-access')));
      expect(tenants, isNot(contains('/portal-invite')));
      expect(sheet, contains('Stepper('));
      expect(sheet, contains('signed correction or restatement'));
      expect(sheet, contains('sameRelationshipConfirmed'));
      expect(detail, contains("auth.hasCapability('rentals.manage')"));
      expect(
        detail,
        contains("auth.hasCapability('leasing.onboarding.manage')"),
      );
      expect(
        detail,
        contains("auth.hasCapability('leasing.agreements.prepare')"),
      );
    },
  );
}
