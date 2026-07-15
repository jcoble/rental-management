import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/auth/mobile_access_policy.dart';
import 'package:rental_command/features/units/unit_command_center_tabs.dart';

void main() {
  const leasing = <String>{
    'leasing.listings.manage',
    'leasing.applications.manage',
    'leasing.showings.manage',
    'leasing.onboarding.manage',
    'leasing.agreements.prepare',
  };
  const maintenance = <String>{
    'maintenance.assigned-work.read',
    'maintenance.assigned-work.update',
    'maintenance.assigned-work.converse',
  };

  test(
    'role detail routes are capability gated and never management aliases',
    () {
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.leasing,
          capabilities: leasing,
          path: '/leasing/applications/8',
        ),
        isTrue,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.leasing,
          capabilities: leasing,
          path: '/leasing/appointments/9',
        ),
        isTrue,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.leasing,
          capabilities: leasing,
          path: '/leasing/conversations/12',
        ),
        isTrue,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.leasing,
          capabilities: leasing,
          path: '/leasing/move-ins/10',
        ),
        isTrue,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.leasing,
          capabilities: leasing,
          path: '/applications/8',
        ),
        isFalse,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.maintenance,
          capabilities: maintenance,
          path: '/technician/assignments/11',
        ),
        isTrue,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.maintenance,
          capabilities: maintenance,
          path: '/work-orders/11',
        ),
        isFalse,
      );
    },
  );

  test(
    'unit command center accepts canonical sections and drops old aliases',
    () {
      final agreement = unitCommandCenterDestinationFromName(
        'tenant-lease',
        'agreements',
      );
      expect(agreement.tab, UnitCommandCenterTab.tenantLease);
      expect(agreement.view, UnitCommandCenterView.agreements);

      final work = unitCommandCenterDestinationFromName(
        'maintenance',
        'work-orders',
      );
      expect(work.tab, UnitCommandCenterTab.maintenance);
      expect(work.view, UnitCommandCenterView.workOrders);

      for (final removedAlias in ['lease', 'apps', 'ledger', 'make-ready']) {
        final destination = unitCommandCenterDestinationFromName(
          removedAlias,
          null,
        );
        expect(destination.tab, UnitCommandCenterTab.summary);
        expect(destination.view, isNull);
      }
    },
  );

  test('leasing application uses only the assignment-scoped projection', () {
    final landing = File(
      'lib/features/leasing/leasing_landing_screen.dart',
    ).readAsStringSync();
    final repository = File(
      'lib/features/leasing/leasing_workspace_repository.dart',
    ).readAsStringSync();
    final detail = File(
      'lib/features/leasing/leasing_detail_screens.dart',
    ).readAsStringSync();

    expect(landing, contains('LeasingApplicationDetailScreen'));
    expect(
      landing,
      isNot(contains('../applications/application_detail_screen.dart')),
    );
    expect(repository, contains("'/leasing/applications/\$applicationId'"));
    expect(detail, contains('LeasingApplicationDetail'));
    expect(detail, isNot(contains('applicationsRepositoryProvider')));
    expect(landing, contains('LeasingConversationDetailScreen'));
    expect(landing, isNot(contains('../messages/message_detail_screen.dart')));
    expect(repository, contains("'/leasing/conversations/\$conversationId'"));
    expect(
      repository,
      contains("'/leasing/conversations/\$conversationId/messages'"),
    );
  });

  test('technician scan selection is server paged and assignment scoped', () {
    final helper = File(
      'lib/features/home/mobile_quick_action_helpers.dart',
    ).readAsStringSync();
    final picker = File(
      'lib/features/technician/technician_assignment_picker_sheet.dart',
    ).readAsStringSync();

    expect(helper, contains('showTechnicianAssignmentPicker(context)'));
    expect(helper, isNot(contains('listWorkOrders(')));
    expect(helper, isNot(contains('take: 100')));
    expect(picker, contains("area: 'assignments'"));
    expect(picker, contains('search: _searchController.text.trim()'));
    expect(picker, contains('status: _status'));
    expect(picker, contains('skip: _skip'));
    expect(picker, contains('take: _take'));
  });

  test('every role shell preserves independent tab navigation', () {
    final roleShell = File(
      'lib/features/home/mobile_role_shell.dart',
    ).readAsStringSync();
    final home = File('lib/features/home/home_shell.dart').readAsStringSync();
    final leasing = File(
      'lib/features/leasing/leasing_landing_screen.dart',
    ).readAsStringSync();
    final technician = File(
      'lib/features/technician/technician_landing_screen.dart',
    ).readAsStringSync();
    final owner = File(
      'lib/features/home/owner_landing_screen.dart',
    ).readAsStringSync();

    expect(roleShell, contains('List<GlobalKey<NavigatorState>>'));
    expect(roleShell, contains('popUntil((route) => route.isFirst)'));
    expect(roleShell, contains('scrollToTop()'));
    expect(roleShell, contains('NavigatorPopHandler<void>'));
    expect(roleShell, contains('ownsScaffold'));
    expect(roleShell, contains('_tabAtRoot[_selectedIndex]'));
    expect(roleShell, contains('_RoleTabNavigatorObserver'));
    expect(leasing, contains('MobileRoleShell('));
    expect(technician, contains('MobileRoleShell('));
    expect(owner, contains('MobileRoleShell('));
    expect(
      home,
      contains('authState.activeExperience == WorkspaceExperience.tenant'),
    );
    expect(home, contains('child: MobileRoleShell('));
  });

  test('push navigation is typed, expiring and access revision bound', () {
    final source = File(
      'lib/core/push/mobile_navigation_intent.dart',
    ).readAsStringSync();
    expect(source, contains('expiresAtUtc'));
    expect(source, contains('accessContextId'));
    expect(source, contains('accessRevision'));
    expect(source, contains('WorkspaceExperience? experience'));
    expect(source, contains('return fallbackRoute'));

    final push = File('lib/core/push/push_service.dart').readAsStringSync();
    expect(push, contains('pendingPushLinkProvider.notifier).set(intent)'));
    expect(push, isNot(contains('appRouterProvider')));
  });

  test('scan capture keeps canonical rental and financial context', () {
    final helper = File(
      'lib/features/home/mobile_quick_action_helpers.dart',
    ).readAsStringSync();
    for (final field in [
      'propertyId',
      'unitId',
      'leaseManagementId',
      'leaseAgreementId',
      'tenantAccountId',
      'tenantLedgerEntryId',
      'workOrderId',
      'applicationId',
      'rentalListingId',
    ]) {
      expect(helper, contains(field));
    }
  });
}
