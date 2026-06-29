import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test('auth secondary screens are pushed so mobile back navigation works', () {
    final loginSource = File(
      'lib/features/auth/login_screen.dart',
    ).readAsStringSync();

    expect(loginSource, contains("context.push('/register')"));
    expect(loginSource, contains("context.push('/forgot-password')"));
    expect(loginSource, isNot(contains("context.go('/register')")));
    expect(loginSource, isNot(contains("context.go('/forgot-password')")));
  });

  test('auth back arrows pop stacked screens before falling back to login', () {
    for (final path in [
      'lib/features/auth/register_screen.dart',
      'lib/features/auth/forgot_password_screen.dart',
    ]) {
      final source = File(path).readAsStringSync();

      expect(source, contains('context.canPop()'));
      expect(source, contains('context.pop()'));
      expect(source, contains("context.go('/login')"));
    }
  });

  test('addressable mobile app sub-screens avoid route replacement', () {
    final appScreenPaths = [
      'lib/features/analytics/insights_screen.dart',
      'lib/features/appointments/appointment_detail_screen.dart',
      'lib/features/appointments/appointments_screen.dart',
      'lib/features/banking/banking_screen.dart',
      'lib/features/leases/lease_detail_screen.dart',
      'lib/features/maintenance/work_order_detail_screen.dart',
      'lib/features/money/expense_detail_screen.dart',
      'lib/features/owner_reports/owner_reports_screen.dart',
      'lib/features/payments/payment_detail_screen.dart',
      'lib/features/properties/property_detail_screen.dart',
      'lib/features/scan/scan_review_screen.dart',
      'lib/features/settings/settings_screen.dart',
      'lib/features/tenants/tenant_detail_screen.dart',
    ];

    for (final path in appScreenPaths) {
      final source = File(path).readAsStringSync();

      expect(
        source,
        isNot(contains('context.go(')),
        reason: '$path should not replace the mobile navigation stack.',
      );
      expect(
        source,
        anyOf(
          contains('appBar: AppBar'),
          contains('appBar: mobileDomainRootAppBar'),
        ),
        reason: '$path should expose the Material back affordance when pushed.',
      );
    }
  });
}
