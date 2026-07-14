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

  test('account menu does not expose duplicate Browse all navigation', () {
    final source = File(
      'lib/features/home/mobile_shell_actions.dart',
    ).readAsStringSync();

    expect(source, isNot(contains("import 'more_tab.dart';")));
    expect(source, isNot(contains("label: 'Browse all'")));
    expect(source, isNot(contains('const MoreTab()')));
  });

  test('mobile notification settings use only canonical separated routes', () {
    final repositorySource = File(
      'lib/features/settings/notification_foundation_repository.dart',
    ).readAsStringSync();
    final settingsSource = File(
      'lib/features/settings/settings_screen.dart',
    ).readAsStringSync();
    final routerSource = File(
      'lib/core/router/app_router.dart',
    ).readAsStringSync();

    expect(repositorySource, isNot(contains("'/notifications/settings'")));
    expect(repositorySource, isNot(contains('notifyTenants')));
    expect(repositorySource, isNot(contains('leaseEndAutoAction')));
    expect(repositorySource, contains("'/notification-settings/my-alerts'"));
    expect(repositorySource, contains("'/notification-settings/team-routing'"));
    expect(
      repositorySource,
      contains("'/notification-settings/tenant-notices/deliveries'"),
    );
    expect(settingsSource, contains("title: 'My alerts'"));
    expect(settingsSource, contains("title: 'Team routing'"));
    expect(settingsSource, contains("title: 'Tenant notices'"));
    expect(routerSource, contains("path: '/settings/notifications/my-alerts'"));
    expect(
      routerSource,
      contains("path: '/settings/notifications/team-routing'"),
    );
    expect(
      routerSource,
      contains("path: '/settings/notifications/tenant-notices'"),
    );
  });

  test('denied deep links return the access-changed screen', () {
    final routerSource = File(
      'lib/core/router/app_router.dart',
    ).readAsStringSync();
    final deniedScreenSource = File(
      'lib/core/router/mobile_access_denied_screen.dart',
    ).readAsStringSync();

    expect(routerSource, contains('return _accessDeniedPath;'));
    expect(routerSource, contains('path: _accessDeniedPath'));
    expect(routerSource, contains('MobileAccessDeniedScreen'));
    expect(
      deniedScreenSource,
      contains("AppBar(title: const Text('Access changed'))"),
    );
    expect(
      deniedScreenSource,
      contains("This destination isn't available in your current work area."),
    );
  });

  test('restricted shells never fall back to management Today', () {
    final shellSource = File(
      'lib/features/home/home_shell.dart',
    ).readAsStringSync();

    expect(
      shellSource,
      isNot(
        contains(
          'return tabs.isEmpty ? const [MobileShellTabId.today] : tabs;',
        ),
      ),
    );
    expect(
      shellSource,
      contains('auth.activeExperience == WorkspaceExperience.management'),
    );
    expect(shellSource, contains('if (!tenantMode && landlordTabs.isEmpty)'));
    expect(shellSource, contains("returnLabel: 'Refresh access'"));
    expect(shellSource, contains('MobileAccessDeniedScreen('));
  });

  test('owner experience uses a dedicated relationship-scoped shell', () {
    final shellSource = File(
      'lib/features/home/home_shell.dart',
    ).readAsStringSync();
    final ownerLandingSource = File(
      'lib/features/home/owner_landing_screen.dart',
    ).readAsStringSync();
    final shellActionsSource = File(
      'lib/features/home/mobile_shell_actions.dart',
    ).readAsStringSync();

    expect(
      shellSource,
      contains('authState.activeExperience == WorkspaceExperience.owner'),
    );
    expect(shellSource, contains('child: const OwnerLandingScreen()'));
    expect(
      shellSource.indexOf(
        'authState.activeExperience == WorkspaceExperience.owner',
      ),
      lessThan(shellSource.indexOf('final landlordTabs = tenantMode')),
    );
    expect(ownerLandingSource, contains("'Overview',"));
    expect(ownerLandingSource, contains("'Properties',"));
    expect(ownerLandingSource, contains("'Statements',"));
    expect(ownerLandingSource, contains("'Approvals',"));
    expect(ownerLandingSource, contains("'Messages',"));
    expect(ownerLandingSource, contains('MobileAccountMenu()'));
    expect(ownerLandingSource, isNot(contains('_HomeTab')));
    expect(ownerLandingSource, isNot(contains("Text('Today')")));
    expect(ownerLandingSource, contains('ownerPortalRepositoryProvider'));
    expect(ownerLandingSource, isNot(contains('RentalsHubScreen')));
    expect(ownerLandingSource, isNot(contains('MoneyHubScreen')));
    expect(ownerLandingSource, isNot(contains('WorkHubScreen')));
    expect(ownerLandingSource, isNot(contains('InboxHubScreen')));
    expect(
      File('lib/core/auth/mobile_access_policy.dart').readAsStringSync(),
      contains('experience != WorkspaceExperience.owner'),
    );
    expect(
      shellActionsSource,
      contains('auth.activeExperience == WorkspaceExperience.management'),
    );
    expect(
      shellActionsSource,
      contains("managementMode && capabilities.contains('rentals.manage')"),
    );
  });
}
