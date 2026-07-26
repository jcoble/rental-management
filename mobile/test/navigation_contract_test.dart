import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test('Today work orders use readable two-line status copy', () {
    final source = File('lib/features/home/home_shell.dart').readAsStringSync();
    final cardStart = source.indexOf('class _FieldQueueCard');
    final cardEnd = source.indexOf('class _LoadingCard', cardStart);
    final card = source.substring(cardStart, cardEnd);

    expect(card, contains('plainEnglishLabel(workOrder.status)'));
    expect(card, contains('plainEnglishLabel(workOrder.priority)'));
    expect(card, contains(r"join('\n')"));
    expect(card, contains('maxLines: 2'));
    expect(card, contains('_pushTodayDetail(context, detailBuilder)'));
    expect(card, isNot(contains("join(' / ')")));
  });

  test('Unit Maintenance keeps four vertical subjects without nested tabs', () {
    final source = File(
      'lib/features/units/unit_command_center_screen.dart',
    ).readAsStringSync();
    for (final label in [
      "'Work orders'",
      "'Inspections'",
      "'Recurring maintenance'",
      "'Turnover/make-ready'",
    ]) {
      expect(source, contains(label));
    }
    expect(
      RegExp(
        r'area: UnitCommandCenterTab\.maintenance[\s\S]*?children: \[',
      ).hasMatch(source),
      isTrue,
    );
  });
  test('startup keeps the Android restoration scope while auth restores', () {
    final source = File('lib/main.dart').readAsStringSync();
    final startupBranch = source.substring(
      source.indexOf('if (authState is AuthStateUnknown) {'),
      source.indexOf('return const RentalCommandApp();'),
    );

    expect(startupBranch, contains("restorationScopeId: 'rental-command'"));
  });

  test('home shell loads durable Unit restoration before opening stack', () {
    final source = File('lib/features/home/home_shell.dart').readAsStringSync();
    final loadStart = source.indexOf(
      'Future<void> _loadDurableRestorationForAuthorityKey',
    );
    final loadBlock = source.substring(
      loadStart,
      source.indexOf('void _restoreUnitStackIfAuthorized()', loadStart),
    );

    expect(loadBlock, contains('final loadResult = await store.load();'));
    expect(
      loadBlock,
      contains('MobileRestorationStateLoadSuccess(:final state)'),
    );
    expect(loadBlock, contains('_matchesCurrentAuthority(state, auth)'));
    expect(
      loadBlock.indexOf('final loadResult = await store.load();'),
      lessThan(loadBlock.indexOf('_restoreUnitStackIfAuthorized();')),
    );
    expect(source, contains('mobileRestorationStateStoreProvider'));
    expect(source, contains('_clearRestorationForSignedOutSession()'));
    expect(
      source,
      contains('_clearRestorationForAuthority(AuthStateAuthenticated auth)'),
    );
    expect(source, contains('unitId: restored.unitId!'));
  });

  test(
    'cold session rehydration loads durable Unit state without clearing it',
    () {
      final source = File(
        'lib/features/home/home_shell.dart',
      ).readAsStringSync();
      final listenerStart = source.indexOf(
        'ref.listen<AuthState>(authControllerProvider',
      );
      final listenerBlock = source.substring(
        listenerStart,
        source.indexOf('final authState = ref.watch', listenerStart),
      );
      final coldRestoreBranch = listenerBlock.substring(
        listenerBlock.indexOf('if (previous is! AuthStateAuthenticated)'),
        listenerBlock.indexOf('if (!accessAuthorityChanged(previous, next))'),
      );
      final authorityChangeBranch = listenerBlock.substring(
        listenerBlock.indexOf('if (!accessAuthorityChanged(previous, next))'),
      );

      expect(coldRestoreBranch, contains('_prepareRestorationForAuthority();'));
      expect(
        coldRestoreBranch,
        isNot(contains('_clearRestorationForAuthority(next)')),
      );
      expect(
        authorityChangeBranch,
        contains('_clearRestorationForAuthority(next)'),
      );
    },
  );

  test('home shell ignores obsolete delayed durable restoration loads', () {
    final source = File('lib/features/home/home_shell.dart').readAsStringSync();
    final signedOutClear = source.substring(
      source.indexOf('void _clearRestorationForSignedOutSession()'),
      source.indexOf('void _clearRestorationForAuthority'),
    );
    final authorityClear = source.substring(
      source.indexOf('void _clearRestorationForAuthority'),
      source.indexOf('void _prepareRestorationForAuthority'),
    );
    final loadStart = source.substring(
      source.indexOf('void _loadDurableRestorationForAuthority'),
      source.indexOf('Future<void> _loadDurableRestorationForAuthorityKey'),
    );
    final currentAuthority = source.substring(
      source.indexOf('AuthStateAuthenticated? _currentRestorationAuthority'),
      source.indexOf('Future<void> _loadDurableRestorationForAuthorityKey'),
    );
    final loadBlock = source.substring(
      source.indexOf('Future<void> _loadDurableRestorationForAuthorityKey'),
      source.indexOf('void _restoreUnitStackIfAuthorized()'),
    );

    expect(source, contains('int _durableRestorationGeneration = 0'));
    expect(signedOutClear, contains('_durableRestorationGeneration++'));
    expect(authorityClear, contains('_durableRestorationGeneration++'));
    expect(
      loadStart,
      contains('final generation = _durableRestorationGeneration'),
    );
    expect(loadStart, contains('key,'));
    expect(loadStart, contains('generation,'));
    expect(
      currentAuthority,
      contains('_durableRestorationGeneration != generation'),
    );
    expect(currentAuthority, contains("_authorityKey(auth) != key"));
    expect(loadBlock, contains('final loadResult = await store.load();'));
    expect(
      loadBlock.indexOf(
        'final auth = _currentRestorationAuthority(key, generation);',
      ),
      greaterThan(loadBlock.indexOf('final loadResult = await store.load();')),
    );
    expect(
      loadBlock.indexOf(
        'if (loadResult is! MobileRestorationStateLoadMissing)',
      ),
      lessThan(loadBlock.indexOf('await store.clear();')),
    );
    expect(loadBlock, contains('await store.clear();'));
    expect(
      loadBlock.indexOf(
        'final currentAuth = _currentRestorationAuthority(key, generation);',
      ),
      greaterThan(loadBlock.indexOf('await store.clear();')),
    );
    expect(
      loadBlock.indexOf('_setRestorationStateWithoutPersist(state);'),
      greaterThan(
        loadBlock.indexOf(
          'final auth = _currentRestorationAuthority(key, generation);',
        ),
      ),
    );
    expect(
      loadBlock.indexOf('_restoreUnitStackIfAuthorized();'),
      greaterThan(
        loadBlock.indexOf(
          'final auth = _currentRestorationAuthority(key, generation);',
        ),
      ),
    );
  });

  test(
    'obsolete A restoration cannot run delayed domain navigation after B generation',
    () {
      final homeShellSource = File(
        'lib/features/home/home_shell.dart',
      ).readAsStringSync();
      final domainNavigationSource = File(
        'lib/features/home/mobile_domain_navigation.dart',
      ).readAsStringSync();
      final domainHubSource = File(
        'lib/features/home/mobile_domain_hub.dart',
      ).readAsStringSync();
      final restoreBlock = homeShellSource.substring(
        homeShellSource.indexOf('void _restoreUnitStackIfAuthorized()'),
        homeShellSource.indexOf('void _unregisterDomain'),
      );
      final shellOpenBlock = homeShellSource.substring(
        homeShellSource.indexOf('void _openShellTab'),
        homeShellSource.indexOf('bool _openShellRoute'),
      );
      final hubOpenBlock = domainHubSource.substring(
        domainHubSource.indexOf('void _openDestination'),
        domainHubSource.indexOf('void _replaceContentRoot'),
      );
      final deferredHubOpenBlock = hubOpenBlock.substring(
        hubOpenBlock.indexOf('WidgetsBinding.instance.addPostFrameCallback'),
      );

      expect(
        homeShellSource,
        contains(
          'bool _isCurrentRestorationAuthority(String key, int generation)',
        ),
      );
      expect(
        restoreBlock.indexOf(
          'final restorationAuthorityKey = _authorityKey(auth);',
        ),
        lessThan(
          restoreBlock.indexOf(
            'final restorationGeneration = _durableRestorationGeneration;',
          ),
        ),
      );
      expect(
        restoreBlock.indexOf('bool isCurrentRestorationNavigation() =>'),
        lessThan(
          restoreBlock.indexOf('canNavigate: isCurrentRestorationNavigation'),
        ),
      );
      expect(restoreBlock, contains('_isCurrentRestorationAuthority('));
      expect(
        restoreBlock,
        contains('canNavigate: isCurrentRestorationNavigation'),
      );
      expect(
        domainNavigationSource,
        contains('typedef MobileNavigationGuard = bool Function();'),
      );
      expect(shellOpenBlock, contains('MobileNavigationGuard? canNavigate'));
      expect(
        shellOpenBlock.indexOf('if (canNavigate?.call() == false) return;'),
        lessThan(shellOpenBlock.indexOf('domainNavigator.openDestination(')),
      );
      expect(shellOpenBlock, contains('canNavigate: canNavigate'));
      expect(hubOpenBlock, contains('MobileNavigationGuard? canNavigate'));
      expect(
        deferredHubOpenBlock.indexOf(
          'if (canNavigate?.call() == false) return;',
        ),
        lessThan(deferredHubOpenBlock.indexOf('setState(() {')),
      );
      expect(
        deferredHubOpenBlock.indexOf('setState(() {'),
        lessThan(
          deferredHubOpenBlock.indexOf(
            '_replaceContentRoot(widget.destinations[index]);',
          ),
        ),
      );
      expect(
        deferredHubOpenBlock.indexOf('_selectedIndex = index;'),
        lessThan(
          deferredHubOpenBlock.indexOf(
            '_replaceContentRoot(widget.destinations[index]);',
          ),
        ),
      );
      expect(
        deferredHubOpenBlock.indexOf('_scheduleQuickActionFallbackCheck();'),
        lessThan(
          deferredHubOpenBlock.indexOf(
            '_replaceContentRoot(widget.destinations[index]);',
          ),
        ),
      );
      expect(
        deferredHubOpenBlock.lastIndexOf(
          'if (canNavigate?.call() == false) return;',
        ),
        greaterThan(
          deferredHubOpenBlock.indexOf(
            '_replaceContentRoot(widget.destinations[index]);',
          ),
        ),
      );
      expect(
        deferredHubOpenBlock.lastIndexOf(
          'if (canNavigate?.call() == false) return;',
        ),
        lessThan(
          deferredHubOpenBlock.indexOf(
            '_contentNavigatorKey.currentState?.push',
          ),
        ),
      );
    },
  );

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
    expect(repositorySource, contains("'/my-alerts'"));
    expect(repositorySource, contains("'/team-routing'"));
    expect(repositorySource, contains("'/tenant-notices/deliveries'"));
    expect(repositorySource, isNot(contains("'/notification-settings/")));
    expect(settingsSource, contains("title: 'My alerts'"));
    expect(settingsSource, contains('icon: Icons.notifications_outlined'));
    expect(settingsSource, contains("title: 'Team routing'"));
    expect(settingsSource, contains("title: 'Tenant notices'"));
    expect(settingsSource, contains('canManageMobileNotificationFoundation('));
    expect(settingsSource, contains('experience: auth.activeExperience'));
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

  test('Today work-order details preserve their Today navigation origin', () {
    final shellSource = File(
      'lib/features/home/home_shell.dart',
    ).readAsStringSync();
    final queueStart = shellSource.indexOf('class _FieldQueueCard');
    final queueEnd = shellSource.indexOf('class _LoadingCard', queueStart);
    final queueBlock = shellSource.substring(queueStart, queueEnd);
    final workOrderTargetStart = shellSource.indexOf("case 'WorkOrder':");
    final workOrderTargetEnd = shellSource.indexOf(
      "case 'Payment':",
      workOrderTargetStart,
    );
    final workOrderTarget = shellSource.substring(
      workOrderTargetStart,
      workOrderTargetEnd,
    );
    final targetOpeningStart = shellSource.indexOf(
      'void _openTarget(BuildContext context, _BriefingTarget target)',
    );
    final targetOpeningEnd = shellSource.indexOf(
      '/// Maps a briefing',
      targetOpeningStart,
    );
    final targetOpening = shellSource.substring(
      targetOpeningStart,
      targetOpeningEnd,
    );

    expect(queueBlock, contains('_pushTodayDetail(context, detailBuilder)'));
    expect(queueBlock, isNot(contains('MobileShellTabId.work')));
    expect(workOrderTarget, contains('preserveTodayOrigin: true'));
    expect(
      targetOpening,
      contains('target.preserveTodayOrigin && target.detailBuilder != null'),
    );
    expect(
      targetOpening,
      contains('_pushTodayDetail(context, target.detailBuilder!)'),
    );
    expect(
      shellSource,
      contains('onAction: () => onSwitchToTab(_workTabIndex)'),
    );
    expect(
      RegExp(r'preserveTodayOrigin:\s*true').allMatches(shellSource),
      hasLength(1),
    );
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
      contains("managementMode && capabilities.contains('security.manage')"),
    );
  });

  test('leasing experience uses a dedicated projection-backed shell', () {
    final shellSource = File(
      'lib/features/home/home_shell.dart',
    ).readAsStringSync();
    final leasingSource = File(
      'lib/features/leasing/leasing_landing_screen.dart',
    ).readAsStringSync();
    final repositorySource = File(
      'lib/features/leasing/leasing_workspace_repository.dart',
    ).readAsStringSync();

    expect(
      shellSource,
      contains('authState.activeExperience == WorkspaceExperience.leasing'),
    );
    expect(shellSource, contains('child: const LeasingLandingScreen()'));
    for (final label in ['Today', 'Pipeline', 'Rentals', 'Calendar', 'Inbox']) {
      expect(leasingSource, contains("label: '$label'"));
    }
    expect(leasingSource, contains('openAuthorizedMobileScan(context, ref)'));
    expect(
      leasingSource,
      contains('canManagePropertyTenantNoticeDrafts(auth.capabilities)'),
    );
    expect(leasingSource, contains("title: const Text('Tenant notices')"));
    expect(leasingSource, isNot(contains('MoneyHubScreen')));
    expect(leasingSource, isNot(contains('OwnerLandingScreen')));
    expect(leasingSource, isNot(contains('UnitCommandCenterLoaderScreen')));
    expect(leasingSource, contains('LeasingRentalDetailScreen'));
    expect(leasingSource, contains('LeasingAppointmentDetailScreen'));
    expect(leasingSource, contains('LeasingMoveInDetailScreen'));
    for (final route in [
      '/leasing/today',
      '/leasing/pipeline/page',
      '/leasing/rentals/page',
      '/leasing/calendar/page',
      '/leasing/inbox/page',
      '/leasing/rentals/\$unitId',
      '/leasing/appointments/\$appointmentId',
      '/leasing/move-ins/\$leaseManagementId',
    ]) {
      expect(repositorySource, contains("'$route'"));
    }
  });

  test('technician shell exposes only assignment-scoped scan capture', () {
    final technicianSource = File(
      'lib/features/technician/technician_landing_screen.dart',
    ).readAsStringSync();

    expect(technicianSource, contains("label: const Text('Scan / Add')"));
    expect(
      technicianSource,
      contains('openAuthorizedMobileScan(context, ref)'),
    );
    expect(technicianSource, isNot(contains('openMobileScan(')));
  });

  test(
    'live property setup keeps the atomic rental context for next steps',
    () {
      final setupSource = File(
        'lib/features/onboarding/onboarding_live_setup_screen.dart',
      ).readAsStringSync();
      final propertyFormSource = File(
        'lib/features/properties/property_form_sheet.dart',
      ).readAsStringSync();

      expect(propertyFormSource, contains('onSetupSaved?.call(setup)'));
      expect(setupSource, contains("title: 'Scan an existing lease'"));
      expect(
        setupSource,
        contains("initialTargetEntityType: 'LeaseAgreement'"),
      );
      expect(
        setupSource,
        contains("title: 'Set up tenants and lease manually'"),
      );
      expect(setupSource, contains('propertyId: setup.property.id'));
      expect(setupSource, contains('unitId: setup.units.length == 1'));
      expect(setupSource, contains('UnitCommandCenterTab.tenantLease'));
    },
  );

  test('global scan capture exposes authorized scan history', () {
    final captureSource = File(
      'lib/features/scan/scan_capture.dart',
    ).readAsStringSync();
    final routerSource = File(
      'lib/core/router/app_router.dart',
    ).readAsStringSync();

    expect(captureSource, contains("label: const Text('View scan history')"));
    expect(captureSource, contains("router.push<void>('/scans')"));
    expect(routerSource, contains("path: '/scans'"));
    expect(
      routerSource,
      contains('builder: (context, state) => const ScanListScreen()'),
    );
  });

  test('scan history reopens leases in the complete canonical review', () {
    final listSource = File(
      'lib/features/scan/scan_list_screen.dart',
    ).readAsStringSync();

    expect(listSource, contains('ScanReviewScreen(draftId: draft.id)'));
    expect(listSource, isNot(contains('GuidedRentalFlow.open')));
    expect(listSource, isNot(contains("import 'guided_rental_flow.dart';")));
  });

  test('scan gallery capture preserves document resolution', () {
    final captureSource = File(
      'lib/features/scan/scan_capture.dart',
    ).readAsStringSync();

    expect(
      captureSource,
      contains(
        'source == ImageSource.gallery\n'
        '        ? await picker.pickImage(source: source)',
      ),
    );
    expect(captureSource, contains('ImagePicker().pickMultiImage()'));
    expect(
      captureSource,
      isNot(contains('maxHeight: 1600')),
      reason: 'A height cap makes tall, multi-page document scans unreadable.',
    );
  });
}
