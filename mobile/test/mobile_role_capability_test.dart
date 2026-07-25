import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/auth/mobile_access_policy.dart';
import 'package:rental_command/features/home/mobile_destination.dart';
import 'package:rental_command/features/home/mobile_domain_navigation.dart';

void main() {
  const propertyManagerCapabilities = <String>{
    'rentals.read',
    'rentals.manage',
    'work.read',
    'work.manage',
    'reports.read',
    'money.balances.read',
    'money.charges.manage',
    'money.payments.manage',
    'money.expenses.manage',
    'money.deposits.manage',
    'money.owner-reports.read',
    'money.reconciliation.operate',
    'responsibility.assign-existing-member',
    'notifications.tenant-notices.manage',
  };

  const leasingCapabilities = <String>{
    'rentals.read',
    'leasing.listings.manage',
    'leasing.applications.manage',
    'leasing.showings.manage',
    'leasing.agreements.prepare',
    'leasing.onboarding.manage',
    'leasing.terms.read',
    'leasing.deposits.read',
    'notifications.tenant-notices.manage',
  };

  test(
    'canonical property manager capabilities expose matching destinations',
    () {
      expect(
        _visibleIds(rentalDestinations, propertyManagerCapabilities),
        const {
          MobileDestinationId.properties,
          MobileDestinationId.owners,
          MobileDestinationId.units,
          MobileDestinationId.tenants,
          MobileDestinationId.leases,
        },
      );
      expect(
        _visibleIds(moneyHubDestinations, propertyManagerCapabilities),
        const {
          MobileDestinationId.insights,
          MobileDestinationId.moneyOverview,
          MobileDestinationId.moneyLedger,
          MobileDestinationId.deposits,
          MobileDestinationId.banking,
          MobileDestinationId.reports,
        },
      );
      expect(
        _visibleIds(inboxHubDestinations, propertyManagerCapabilities),
        const {
          MobileDestinationId.messages,
          MobileDestinationId.notifications,
          MobileDestinationId.activityHistory,
        },
      );
    },
  );

  test('leasing uses its dedicated shell and canonical detail routes', () {
    expect(
      rentalHubDestinationsFor(
        experience: WorkspaceExperience.leasing,
        capabilities: leasingCapabilities,
      ).map((destination) => destination.id).toSet(),
      const {
        MobileDestinationId.properties,
        MobileDestinationId.units,
        MobileDestinationId.tenants,
        MobileDestinationId.leases,
        MobileDestinationId.applications,
        MobileDestinationId.deposits,
      },
    );
    expect(
      canOpenMobilePath(
        experience: WorkspaceExperience.leasing,
        capabilities: leasingCapabilities,
        path: '/rentals',
      ),
      isFalse,
    );
    expect(
      _visibleIds(workHubDestinations, leasingCapabilities),
      contains(MobileDestinationId.notices),
    );
    expect(
      canOpenMobilePath(
        experience: WorkspaceExperience.leasing,
        capabilities: leasingCapabilities,
        path: '/units/42',
      ),
      isFalse,
    );
    expect(
      canOpenMobilePath(
        experience: WorkspaceExperience.leasing,
        capabilities: leasingCapabilities,
        path: '/work',
      ),
      isFalse,
    );
    expect(
      canOpenMobilePath(
        experience: WorkspaceExperience.leasing,
        capabilities: leasingCapabilities,
        path: '/money',
      ),
      isFalse,
    );
    expect(
      canOpenMobilePath(
        experience: WorkspaceExperience.leasing,
        capabilities: leasingCapabilities,
        path: '/notifications',
      ),
      isTrue,
    );
    expect(
      canOpenMobilePath(
        experience: WorkspaceExperience.leasing,
        capabilities: leasingCapabilities,
        path: '/owners',
      ),
      isFalse,
    );
  });

  test('leasing deposit-only access is reachable without the Money hub', () {
    const capabilities = {'leasing.deposits.read'};

    expect(
      canOpenRentalsHubForExperience(
        experience: WorkspaceExperience.leasing,
        capabilities: capabilities,
      ),
      isTrue,
    );
    expect(
      rentalHubDestinationsFor(
        experience: WorkspaceExperience.leasing,
        capabilities: capabilities,
      ).map((destination) => destination.id),
      [MobileDestinationId.deposits],
    );
    expect(
      canOpenMobilePath(
        experience: WorkspaceExperience.leasing,
        capabilities: capabilities,
        path: '/money',
      ),
      isFalse,
    );
  });

  test('canonical action capabilities gate scan record and assistant', () {
    expect(canUseGlobalScan({'leasing.agreements.prepare'}), isTrue);
    expect(canUseVoiceRecord({'leasing.agreements.prepare'}), isFalse);
    expect(canUseAssistant({'leasing.agreements.prepare'}), isFalse);

    expect(canUseGlobalScan({'money.expenses.manage'}), isTrue);
    expect(canUseVoiceRecord({'money.expenses.manage'}), isTrue);
    expect(canUseAssistant({'money.expenses.manage'}), isFalse);

    expect(canUseGlobalScan({'reports.read'}), isFalse);
    expect(canUseVoiceRecord({'reports.read'}), isFalse);
    expect(canUseAssistant({'reports.read'}), isTrue);
  });

  test('mutation actions require both capability and matching experience', () {
    expect(
      canUseMobileCapabilityAction(
        experience: WorkspaceExperience.management,
        capabilities: const {'rentals.manage'},
        capability: 'rentals.manage',
        experiences: const {WorkspaceExperience.management},
      ),
      isTrue,
    );
    expect(
      canUseMobileCapabilityAction(
        experience: WorkspaceExperience.leasing,
        capabilities: const {'rentals.manage'},
        capability: 'rentals.manage',
        experiences: const {WorkspaceExperience.management},
      ),
      isFalse,
    );
    expect(
      canUseMobileCapabilityAction(
        experience: WorkspaceExperience.management,
        capabilities: const {'rentals.read'},
        capability: 'rentals.manage',
        experiences: const {WorkspaceExperience.management},
      ),
      isFalse,
    );
  });

  test('owner experience cannot inherit management routes', () {
    for (final path in [
      '/rentals',
      '/owners',
      '/money',
      '/work',
      '/inbox',
      '/choose-setup',
      '/setting-up',
      '/live-setup',
    ]) {
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.owner,
          capabilities: propertyManagerCapabilities,
          path: path,
        ),
        isFalse,
        reason: 'Owner must not open $path through overlapping capabilities.',
      );
    }

    expect(
      canOpenMobilePath(
        experience: WorkspaceExperience.owner,
        capabilities: const {},
        path: '/',
      ),
      isTrue,
    );
    expect(
      canOpenMobilePath(
        experience: WorkspaceExperience.owner,
        capabilities: const {},
        path: '/settings',
      ),
      isTrue,
    );
    expect(
      canOpenMobilePath(
        experience: WorkspaceExperience.owner,
        capabilities: const {},
        path: '/settings/notifications/my-alerts',
      ),
      isTrue,
    );
  });

  test('maintenance uses only the assigned-work experience', () {
    const capabilities = {
      'maintenance.assigned-work.read',
      'maintenance.assigned-work.update',
      'maintenance.assigned-work.converse',
    };

    expect(
      canOpenMobilePath(
        experience: WorkspaceExperience.maintenance,
        capabilities: capabilities,
        path: '/maintenance/work/42',
      ),
      isTrue,
    );
    for (final path in [
      '/work',
      '/maintenance/42',
      '/inbox',
      '/messages/2',
      '/rentals',
      '/money',
    ]) {
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.maintenance,
          capabilities: capabilities,
          path: path,
        ),
        isFalse,
        reason: 'Technicians must not open the management route $path.',
      );
    }
    expect(
      canOpenMobilePath(
        experience: WorkspaceExperience.maintenance,
        capabilities: capabilities,
        path: '/scan/capture',
      ),
      isTrue,
    );
  });

  test('notification settings separate personal and administrator access', () {
    const staffExperiences = [
      WorkspaceExperience.management,
      WorkspaceExperience.leasing,
      WorkspaceExperience.maintenance,
    ];
    const relationshipExperiences = [
      WorkspaceExperience.owner,
      WorkspaceExperience.tenant,
    ];

    for (final experience in WorkspaceExperience.values) {
      expect(
        canOpenMobilePath(
          experience: experience,
          capabilities: const {},
          path: '/settings/notifications/my-alerts',
        ),
        isTrue,
        reason: 'Every signed-in persona can manage its own alerts.',
      );
      for (final path in [
        '/settings/notifications/team-routing',
        '/settings/notifications/tenant-notices',
      ]) {
        expect(
          canOpenMobilePath(
            experience: experience,
            capabilities: const {},
            path: path,
          ),
          isFalse,
          reason: '$experience needs notifications.manage to open $path.',
        );
      }
    }

    for (final experience in staffExperiences) {
      for (final path in [
        '/settings/notifications/team-routing',
        '/settings/notifications/tenant-notices',
      ]) {
        expect(
          canOpenMobilePath(
            experience: experience,
            capabilities: const {'notifications.manage'},
            path: path,
          ),
          isTrue,
          reason: '$experience should administer notifications via $path.',
        );
      }
    }

    for (final experience in relationshipExperiences) {
      for (final path in [
        '/settings/notifications/team-routing',
        '/settings/notifications/tenant-notices',
      ]) {
        expect(
          canOpenMobilePath(
            experience: experience,
            capabilities: const {'notifications.manage'},
            path: path,
          ),
          isFalse,
          reason: 'Relationship experiences must never administer $path.',
        );
      }
    }
  });

  test(
    'property-scoped notice drafts do not grant workspace notification administration',
    () {
      for (final experience in [
        WorkspaceExperience.management,
        WorkspaceExperience.leasing,
      ]) {
        expect(
          canManagePropertyTenantNoticeDrafts({tenantNoticeDraftCapability}),
          isTrue,
        );
        for (final path in [
          '/settings/notifications/team-routing',
          '/settings/notifications/tenant-notices',
        ]) {
          expect(
            canOpenMobilePath(
              experience: experience,
              capabilities: const {tenantNoticeDraftCapability},
              path: path,
            ),
            isFalse,
            reason:
                'Draft review must not unlock workspace notification policy at $path.',
          );
        }
      }
    },
  );

  test('workspace setup is management-only and capability-gated', () {
    for (final path in ['/choose-setup', '/setting-up', '/live-setup']) {
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.management,
          capabilities: const {'security.manage'},
          path: path,
        ),
        isTrue,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.management,
          capabilities: const {'rentals.manage'},
          path: path,
        ),
        isFalse,
        reason: 'Property Managers do not administer workspace setup.',
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.leasing,
          capabilities: const {'rentals.manage'},
          path: path,
        ),
        isFalse,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.maintenance,
          capabilities: const {},
          path: path,
        ),
        isFalse,
      );
    }
  });
}

Set<MobileDestinationId> _visibleIds(
  Iterable<MobileDestination> destinations,
  Set<String> capabilities,
) => visibleMobileDestinations(
  destinations,
  capabilities,
).map((destination) => destination.id).toSet();
