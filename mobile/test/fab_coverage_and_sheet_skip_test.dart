import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/widgets/tabbed_form_sheet.dart';
import 'package:rental_command/features/applications/applications_list_screen.dart';
import 'package:rental_command/features/applications/applications_repository.dart';
import 'package:rental_command/features/home/mobile_quick_action_fab.dart';
import 'package:rental_command/features/money/expenses_list_screen.dart';
import 'package:rental_command/features/money/money_repository.dart';
import 'package:rental_command/features/notifications/notification_models.dart';
import 'package:rental_command/features/notifications/notifications_inbox_screen.dart';
import 'package:rental_command/features/notifications/notifications_repository.dart';
import 'package:rental_command/features/payments/payments_repository.dart';
import 'package:rental_command/features/payments/payments_screen.dart';
import 'package:rental_command/features/units/units_list_screen.dart';
import 'package:rental_command/features/units/units_repository.dart';

void main() {
  testWidgets('expenses units applications payments screens register add and '
      'scan', (tester) async {
    tester.view.physicalSize = const Size(1080, 2400);
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);

    final screens = <_ScreenCase>[
      _ScreenCase(
        name: 'Expenses',
        screen: const ExpensesListScreen(),
        overrides: [
          moneyRepositoryProvider.overrideWithValue(_EmptyMoneyRepository()),
        ],
        expectedLabelPrefix: 'Add',
      ),
      _ScreenCase(
        name: 'Units',
        screen: const UnitsListScreen(),
        overrides: [
          unitsRepositoryProvider.overrideWithValue(_EmptyUnitsRepository()),
        ],
        expectedLabelPrefix: 'Add',
      ),
      _ScreenCase(
        name: 'Applications',
        screen: const ApplicationsListScreen(),
        overrides: [
          applicationsRepositoryProvider.overrideWithValue(
            _EmptyApplicationsRepository(),
          ),
        ],
        expectedLabelPrefix: 'Share',
      ),
      _ScreenCase(
        name: 'Receipts',
        screen: const PaymentsScreen(),
        overrides: [
          paymentsRepositoryProvider.overrideWithValue(
            _EmptyPaymentsRepository(),
          ),
        ],
        expectedLabelPrefix: 'Add',
      ),
      _ScreenCase(
        name: 'Notifications',
        screen: const NotificationsInboxScreen(),
        overrides: [
          notificationsRepositoryProvider.overrideWithValue(
            _EmptyNotificationsRepository(),
          ),
        ],
      ),
    ];

    for (final screen in screens) {
      final controller = await _pumpScreen(tester, screen);

      expect(
        controller.scanAction,
        isNotNull,
        reason: '${screen.name} must offer Scan',
      );

      final labels = controller.primaryActions
          .map((action) => action.label)
          .toList();
      final prefix = screen.expectedLabelPrefix;
      if (prefix == null) {
        expect(labels, isEmpty, reason: '${screen.name} has nothing to add');
        continue;
      }

      expect(labels, isNotEmpty, reason: '${screen.name} must offer $prefix');
      expect(
        labels.first.startsWith(prefix),
        isTrue,
        reason: '${screen.name} first action was ${labels.first}',
      );
    }
  });

  testWidgets('tabbed form sheet offers save when remaining steps are '
      'optional', (tester) async {
    await _pumpSkipForm(
      tester,
      tabs: [
        const TabbedFormStepSpec(label: 'Basics', child: Text('Basics body')),
        TabbedFormStepSpec(
          label: 'Money',
          isComplete: () => true,
          child: const Text('Money body'),
        ),
        TabbedFormStepSpec(
          label: 'Notes',
          isComplete: () => true,
          child: const Text('Notes body'),
        ),
      ],
    );

    expect(find.text('Basics body'), findsOneWidget);
    expect(find.text('Save'), findsOneWidget);
    expect(find.text('Next'), findsNothing);

    await _pumpSkipForm(
      tester,
      tabs: [
        const TabbedFormStepSpec(label: 'Basics', child: Text('Basics body')),
        TabbedFormStepSpec(
          label: 'Money',
          isComplete: () => false,
          child: const Text('Money body'),
        ),
        TabbedFormStepSpec(
          label: 'Notes',
          isComplete: () => true,
          child: const Text('Notes body'),
        ),
      ],
    );

    expect(find.text('Next'), findsOneWidget);
    expect(find.text('Save'), findsNothing);
  });

  testWidgets('forward tap can jump two steps', (tester) async {
    await _pumpSkipForm(
      tester,
      tabs: [
        const TabbedFormStepSpec(label: 'Basics', child: Text('Basics body')),
        const TabbedFormStepSpec(label: 'Money', child: Text('Money body')),
        const TabbedFormStepSpec(label: 'Notes', child: Text('Notes body')),
      ],
    );

    expect(find.byKey(const Key('tabbed-form-current-0')), findsOneWidget);

    await tester.tap(find.byKey(const Key('tabbed-form-step-2')));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('tabbed-form-current-2')), findsOneWidget);
    expect(find.text('Notes body'), findsOneWidget);
  });
}

class _ScreenCase {
  const _ScreenCase({
    required this.name,
    required this.screen,
    required this.overrides,
    this.expectedLabelPrefix,
  });

  final String name;
  final Widget screen;

  /// Riverpod's `Override` type is not exported publicly, so the repository
  /// overrides travel as dynamic and are spread into the scope's typed list.
  final List<dynamic> overrides;
  final String? expectedLabelPrefix;
}

Future<MobileQuickActionController> _pumpScreen(
  WidgetTester tester,
  _ScreenCase screen,
) async {
  final controller = MobileQuickActionController();
  addTearDown(controller.dispose);

  await tester.pumpWidget(
    ProviderScope(
      // A fresh scope per screen: Riverpod refuses to swap one override set
      // for a different one on the same scope element.
      key: ValueKey(screen.name),
      overrides: [
        authControllerProvider.overrideWith(
          () => _StaticAuthController(_authenticatedState),
        ),
        ...screen.overrides,
      ],
      child: MaterialApp(
        home: MobileQuickActionScope(
          controller: controller,
          child: screen.screen,
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return controller;
}

Future<void> _pumpSkipForm(
  WidgetTester tester, {
  required List<TabbedFormStepSpec> tabs,
}) async {
  await tester.pumpWidget(
    MaterialApp(
      home: Scaffold(
        body: TabbedFormSheet(
          title: 'Skip form',
          saveLabel: 'Save',
          saving: false,
          onSave: () async {},
          tabs: tabs,
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

class _EmptyMoneyRepository extends MoneyRepository {
  _EmptyMoneyRepository() : super(Dio());

  @override
  Future<ExpenseListPage> listExpensesPage([
    ExpenseListQuery query = const ExpenseListQuery(),
  ]) async => ExpenseListPage(
    items: const [],
    totalCount: 0,
    skip: query.skip,
    take: query.take,
  );
}

class _EmptyUnitsRepository extends UnitsRepository {
  _EmptyUnitsRepository() : super(Dio());

  @override
  Future<UnitHealthPage> listWithHealthPage({
    String? search,
    String sort = 'propertyName',
    String? status,
    String? stage,
    int skip = 0,
    int take = 20,
  }) async =>
      UnitHealthPage(items: const [], totalCount: 0, skip: skip, take: take);
}

class _EmptyApplicationsRepository extends ApplicationsRepository {
  _EmptyApplicationsRepository() : super(Dio());

  @override
  Future<ApplicationListPage> listPage([
    ApplicationListQuery query = const ApplicationListQuery(),
  ]) async => ApplicationListPage(
    items: const [],
    totalCount: 0,
    skip: query.skip,
    take: query.take,
  );
}

class _EmptyPaymentsRepository extends PaymentsRepository {
  _EmptyPaymentsRepository() : super(Dio());

  @override
  Future<TenantLedgerEntryListPage> listTenantLedgerEntriesPage([
    TenantLedgerEntryListQuery query = const TenantLedgerEntryListQuery(),
  ]) async => TenantLedgerEntryListPage(
    items: const [],
    totalCount: 0,
    skip: query.skip,
    take: query.take,
  );
}

class _EmptyNotificationsRepository extends NotificationsRepository {
  _EmptyNotificationsRepository() : super(Dio());

  @override
  Future<List<AppNotification>> list({
    bool unreadOnly = false,
    int skip = 0,
    int take = 20,
  }) async => const [];

  @override
  Future<int> unreadCount() async => 0;
}

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
    assignments: [
      AccessAssignment(
        assignmentId: 1,
        roleProfileKey: 'owner',
        roleProfileName: 'Owner',
        status: 'Active',
        scope: AccessAssignmentScope(
          kind: 'AllProperties',
          selectedPropertyCount: 0,
          selectedProperties: [],
        ),
      ),
    ],
    navigation: [
      NavigationCapabilities(
        experience: WorkspaceExperience.management,
        capabilityKeys: [
          'rentals.manage',
          'money.payments.manage',
          'money.expenses.manage',
          'leasing.applications.manage',
        ],
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
