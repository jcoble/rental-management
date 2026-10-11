import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/features/home/mobile_quick_action_fab.dart';

void main() {
  testWidgets(
    'quick action FAB expands into scan record and assistant actions',
    (tester) async {
      var chatCount = 0;
      var recordCount = 0;
      var scanCount = 0;

      await tester.pumpWidget(
        _authenticatedApp(
          home: Scaffold(
            floatingActionButton: MobileQuickActionFab(
              onChat: () => chatCount++,
              onRecord: () => recordCount++,
              onScan: () => scanCount++,
            ),
          ),
        ),
      );
      expect(find.text('Assistant'), findsNothing);
      expect(find.text('Record'), findsNothing);
      expect(find.text('Scan / Add'), findsNothing);

      await tester.tap(find.byTooltip('Scan / Add'));
      await tester.pumpAndSettle();

      expect(find.text('Assistant'), findsOneWidget);
      expect(find.text('Record'), findsOneWidget);
      expect(find.text('Scan / Add'), findsOneWidget);

      await tester.tap(find.text('Record'));
      await tester.pumpAndSettle();

      expect(recordCount, 1);
      expect(chatCount, 0);
      expect(scanCount, 0);
      expect(find.text('Record'), findsNothing);
    },
  );

  testWidgets('closed launcher is compact and keeps scan discoverable', (
    tester,
  ) async {
    await tester.pumpWidget(
      _authenticatedApp(
        home: Scaffold(
          floatingActionButton: MobileQuickActionFab(
            onChat: () {},
            onRecord: () {},
            onScan: () {},
          ),
        ),
      ),
    );

    final closedFab = tester.widget<FloatingActionButton>(
      find.byType(FloatingActionButton),
    );
    final closedSize = tester.getSize(find.byType(FloatingActionButton));

    expect(closedFab.isExtended, isFalse);
    expect(closedSize.width, lessThanOrEqualTo(closedSize.height + 8));
    expect(find.byTooltip('Scan / Add'), findsOneWidget);
    expect(find.text('Scan / Add'), findsNothing);

    await tester.tap(find.byTooltip('Scan / Add'));
    await tester.pumpAndSettle();

    final openFab = tester.widget<FloatingActionButton>(
      find.byType(FloatingActionButton),
    );
    expect(openFab.isExtended, isTrue);
    expect(find.byTooltip('Close quick actions'), findsOneWidget);
    expect(find.text('Scan / Add'), findsOneWidget);

    await tester.tap(find.byTooltip('Close quick actions'));
    await tester.pumpAndSettle();

    expect(
      tester
          .widget<FloatingActionButton>(find.byType(FloatingActionButton))
          .isExtended,
      isFalse,
    );
    expect(find.text('Scan / Add'), findsNothing);
  });

  testWidgets('disabled animations preserve the exact Scan / Add action', (
    tester,
  ) async {
    var scanCount = 0;

    await tester.pumpWidget(
      _authenticatedApp(
        disableAnimations: true,
        home: Scaffold(
          floatingActionButton: MobileQuickActionFab(
            onChat: () {},
            onRecord: () {},
            onScan: () => scanCount++,
          ),
        ),
      ),
    );

    await tester.tap(find.byTooltip('Scan / Add'));
    await tester.pump();

    expect(find.text('Scan / Add'), findsOneWidget);
    expect(find.byType(AnimatedSize), findsNothing);
    expect(find.byType(AnimatedSwitcher), findsNothing);
    expect(find.byType(TweenAnimationBuilder<double>), findsNothing);

    await tester.tap(find.text('Scan / Add'));
    await tester.pump();
    expect(scanCount, 1);
  });

  testWidgets('quick action FAB keeps an existing page action in the menu', (
    tester,
  ) async {
    var primaryCount = 0;

    await tester.pumpWidget(
      _authenticatedApp(
        home: Scaffold(
          floatingActionButton: MobileQuickActionFab(
            primaryAction: MobileQuickAction(
              label: 'New repair',
              icon: Icons.add,
              onPressed: () => primaryCount++,
            ),
            onChat: () {},
            onRecord: () {},
            onScan: () {},
          ),
        ),
      ),
    );

    await tester.tap(find.byTooltip('Scan / Add'));
    await tester.pumpAndSettle();

    expect(find.text('New repair'), findsOneWidget);
    expect(find.text('Assistant'), findsOneWidget);
    expect(find.text('Record'), findsOneWidget);
    expect(find.text('Scan / Add'), findsOneWidget);

    await tester.tap(find.text('New repair'));
    await tester.pumpAndSettle();

    expect(primaryCount, 1);
  });

  testWidgets('leasing scan capability hides record and assistant actions', (
    tester,
  ) async {
    await tester.pumpWidget(
      _authenticatedApp(
        capabilities: const {'leasing.agreements.prepare'},
        home: Scaffold(
          floatingActionButton: MobileQuickActionFab(
            onChat: () {},
            onRecord: () {},
            onScan: () {},
          ),
        ),
      ),
    );

    await tester.tap(find.byTooltip('Scan / Add'));
    await tester.pumpAndSettle();

    expect(find.text('Scan / Add'), findsOneWidget);
    expect(find.text('Record'), findsNothing);
    expect(find.text('Assistant'), findsNothing);
  });

  testWidgets('expense capability shows scan and record but not assistant', (
    tester,
  ) async {
    await tester.pumpWidget(
      _authenticatedApp(
        capabilities: const {'money.expenses.manage'},
        home: Scaffold(
          floatingActionButton: MobileQuickActionFab(
            onChat: () {},
            onRecord: () {},
            onScan: () {},
          ),
        ),
      ),
    );

    await tester.tap(find.byTooltip('Scan / Add'));
    await tester.pumpAndSettle();

    expect(find.text('Scan / Add'), findsOneWidget);
    expect(find.text('Record'), findsOneWidget);
    expect(find.text('Assistant'), findsNothing);
  });

  testWidgets('reports capability shows assistant without scan or record', (
    tester,
  ) async {
    await tester.pumpWidget(
      _authenticatedApp(
        capabilities: const {'reports.read'},
        home: Scaffold(
          floatingActionButton: MobileQuickActionFab(
            onChat: () {},
            onRecord: () {},
            onScan: () {},
          ),
        ),
      ),
    );

    await tester.tap(find.byTooltip('Open quick actions'));
    await tester.pumpAndSettle();

    expect(find.text('Assistant'), findsOneWidget);
    expect(find.text('Scan / Add'), findsNothing);
    expect(find.text('Record'), findsNothing);
  });

  testWidgets(
    'scoped page FAB registers its action without rendering duplicate',
    (tester) async {
      final controller = MobileQuickActionController();
      addTearDown(controller.dispose);
      var primaryCount = 0;
      var embeddedScanCount = 0;

      await tester.pumpWidget(
        _authenticatedApp(
          home: Scaffold(
            body: MobileQuickActionScope(
              controller: controller,
              child: MobileQuickActionFab(
                heroTag: 'embedded-fab',
                primaryAction: MobileQuickAction(
                  label: 'New repair',
                  icon: Icons.add,
                  onPressed: () => primaryCount++,
                ),
                onChat: () {},
                onRecord: () {},
                onScan: () => embeddedScanCount++,
              ),
            ),
            floatingActionButton: AnimatedBuilder(
              animation: controller,
              builder: (context, _) => MobileQuickActionFab(
                heroTag: 'shell-fab',
                primaryAction: controller.primaryAction,
                useNearestScope: false,
                onChat: () {},
                onRecord: () {},
                onScan: controller.scanAction ?? () {},
              ),
            ),
          ),
        ),
      );
      await tester.pump();

      expect(find.byType(FloatingActionButton), findsOneWidget);
      expect(controller.scanAction, isNotNull);

      await tester.tap(find.byTooltip('Scan / Add'));
      await tester.pumpAndSettle();

      expect(find.byType(FloatingActionButton), findsOneWidget);
      expect(find.text('New repair'), findsOneWidget);
      expect(find.text('Assistant'), findsOneWidget);
      expect(find.text('Record'), findsOneWidget);
      expect(find.text('Scan / Add'), findsOneWidget);

      await tester.tap(find.text('Scan / Add'));
      await tester.pumpAndSettle();

      expect(embeddedScanCount, 1);

      await tester.tap(find.byTooltip('Scan / Add'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('New repair'));
      await tester.pumpAndSettle();

      expect(primaryCount, 1);
    },
  );

  testWidgets('controller hidden state suppresses the shell quick action FAB', (
    tester,
  ) async {
    final controller = MobileQuickActionController();
    final owner = Object();
    addTearDown(controller.dispose);

    await tester.pumpWidget(
      _authenticatedApp(
        home: Scaffold(
          body: MobileQuickActionScope(
            controller: controller,
            child: MobileQuickActionFab(
              heroTag: 'embedded-fab',
              primaryAction: MobileQuickAction(
                label: 'New conversation',
                icon: Icons.edit_outlined,
                onPressed: () {},
              ),
              onChat: () {},
              onRecord: () {},
              onScan: () {},
            ),
          ),
          floatingActionButton: AnimatedBuilder(
            animation: controller,
            builder: (context, _) {
              if (controller.hidden) return const SizedBox.shrink();
              return MobileQuickActionFab(
                heroTag: 'shell-fab',
                primaryActions: controller.primaryActions,
                useNearestScope: false,
                onChat: () {},
                onRecord: () {},
                onScan: () {},
              );
            },
          ),
        ),
      ),
    );
    await tester.pump();

    expect(controller.primaryAction?.label, 'New conversation');
    expect(controller.hidden, isFalse);
    expect(find.byType(FloatingActionButton), findsOneWidget);

    controller.setHidden(owner, true);
    await tester.pump();

    expect(controller.primaryAction?.label, 'New conversation');
    expect(controller.hidden, isTrue);
    expect(find.byType(FloatingActionButton), findsNothing);

    controller.setHidden(owner, false);
    await tester.pump();

    expect(controller.hidden, isFalse);
    expect(find.byType(FloatingActionButton), findsOneWidget);
  });

  testWidgets('controller remains hidden until every owner clears', (
    tester,
  ) async {
    final controller = MobileQuickActionController();
    final firstOwner = Object();
    final secondOwner = Object();
    addTearDown(controller.dispose);

    controller.setHidden(firstOwner, true);
    controller.setHidden(secondOwner, true);
    expect(controller.hidden, isTrue);

    controller.setHidden(firstOwner, false);
    expect(controller.hidden, isTrue);

    controller.clearHidden(secondOwner);
    expect(controller.hidden, isFalse);
  });

  testWidgets(
    'scope hider restores visibility without clearing another owner',
    (tester) async {
      final controller = MobileQuickActionController();
      final otherOwner = Object();
      var showHider = true;
      late StateSetter setHostState;
      addTearDown(controller.dispose);

      await tester.pumpWidget(
        _authenticatedApp(
          home: MobileQuickActionScope(
            controller: controller,
            child: StatefulBuilder(
              builder: (context, setState) {
                setHostState = setState;
                return showHider
                    ? const MobileQuickActionHider(child: Text('Action form'))
                    : const Text('Ordinary page');
              },
            ),
          ),
        ),
      );
      await tester.pump();

      expect(controller.hidden, isTrue);

      controller.setHidden(otherOwner, true);
      setHostState(() => showHider = false);
      await tester.pump();

      expect(find.text('Ordinary page'), findsOneWidget);
      expect(controller.hidden, isTrue);

      controller.clearHidden(otherOwner);
      await tester.pump();

      expect(controller.hidden, isFalse);
    },
  );

  testWidgets('modal helper hides shell quick action while sheet is mounted', (
    tester,
  ) async {
    final controller = MobileQuickActionController();
    addTearDown(controller.dispose);

    await tester.pumpWidget(
      _authenticatedApp(
        home: MobileQuickActionScope(
          controller: controller,
          child: Scaffold(
            body: Builder(
              builder: (context) => Center(
                child: FilledButton(
                  onPressed: () {
                    showMobileQuickActionHiddenModalBottomSheet<void>(
                      context: context,
                      builder: (_) => const SizedBox(
                        height: 120,
                        child: Center(child: Text('Action sheet')),
                      ),
                    );
                  },
                  child: const Text('Open sheet'),
                ),
              ),
            ),
            floatingActionButton: AnimatedBuilder(
              animation: controller,
              builder: (context, _) {
                if (controller.hidden) return const SizedBox.shrink();
                return MobileQuickActionFab(
                  heroTag: 'shell-fab',
                  primaryActions: controller.primaryActions,
                  useNearestScope: false,
                  onChat: () {},
                  onRecord: () {},
                  onScan: () {},
                );
              },
            ),
          ),
        ),
      ),
    );
    await tester.pump();

    expect(find.byType(FloatingActionButton), findsOneWidget);

    await tester.tap(find.text('Open sheet'));
    await tester.pumpAndSettle();

    expect(find.text('Action sheet'), findsOneWidget);
    expect(controller.hidden, isTrue);
    expect(find.byType(FloatingActionButton), findsNothing);

    await tester.tapAt(const Offset(20, 20));
    await tester.pumpAndSettle();

    expect(find.text('Action sheet'), findsNothing);
    expect(controller.hidden, isFalse);
    expect(find.byType(FloatingActionButton), findsOneWidget);
  });

  testWidgets('shell primary action can open a hidden modal sheet', (
    tester,
  ) async {
    final controller = MobileQuickActionController();
    addTearDown(controller.dispose);

    await tester.pumpWidget(
      _authenticatedApp(
        home: MobileQuickActionScope(
          controller: controller,
          child: Scaffold(
            body: Builder(
              builder: (context) => Stack(
                children: [
                  const Text('Inbox root'),
                  MobileQuickActionFab(
                    heroTag: 'embedded-fab',
                    primaryAction: MobileQuickAction(
                      label: 'New conversation',
                      icon: Icons.edit_outlined,
                      onPressed: () {
                        showMobileQuickActionHiddenModalBottomSheet<void>(
                          context: context,
                          builder: (_) => const SizedBox(
                            height: 120,
                            child: Center(child: Text('Compose conversation')),
                          ),
                        );
                      },
                    ),
                    onChat: () {},
                    onRecord: () {},
                    onScan: () {},
                  ),
                ],
              ),
            ),
            floatingActionButton: AnimatedBuilder(
              animation: controller,
              builder: (context, _) {
                if (controller.hidden) return const SizedBox.shrink();
                return MobileQuickActionFab(
                  heroTag: 'shell-fab',
                  primaryActions: controller.primaryActions,
                  useNearestScope: false,
                  onChat: () {},
                  onRecord: () {},
                  onScan: () {},
                );
              },
            ),
          ),
        ),
      ),
    );
    await tester.pump();

    await tester.tap(find.byTooltip('Scan / Add'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('New conversation'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));

    expect(find.text('Compose conversation'), findsOneWidget);
    expect(find.byType(FloatingActionButton), findsNothing);
  });

  testWidgets('scoped primary action falls back when top action unmounts', (
    tester,
  ) async {
    final controller = MobileQuickActionController();
    addTearDown(controller.dispose);
    var showDetailAction = false;

    await tester.pumpWidget(
      _authenticatedApp(
        home: StatefulBuilder(
          builder: (context, setHostState) => Scaffold(
            body: MobileQuickActionScope(
              controller: controller,
              child: Column(
                children: [
                  MobileQuickActionFab(
                    heroTag: 'list-fab',
                    primaryAction: MobileQuickAction(
                      label: 'Add vendor',
                      icon: Icons.add,
                      onPressed: () {},
                    ),
                    onChat: () {},
                    onRecord: () {},
                    onScan: () {},
                  ),
                  if (showDetailAction)
                    MobileQuickActionFab(
                      heroTag: 'detail-fab',
                      primaryAction: MobileQuickAction(
                        label: 'Rate vendor',
                        icon: Icons.star,
                        onPressed: () {},
                      ),
                      onChat: () {},
                      onRecord: () {},
                      onScan: () {},
                    ),
                  TextButton(
                    onPressed: () => setHostState(
                      () => showDetailAction = !showDetailAction,
                    ),
                    child: const Text('Toggle detail'),
                  ),
                ],
              ),
            ),
            floatingActionButton: AnimatedBuilder(
              animation: controller,
              builder: (context, _) {
                if (controller.hidden) return const SizedBox.shrink();
                return MobileQuickActionFab(
                  heroTag: 'shell-fab',
                  primaryActions: controller.primaryActions,
                  useNearestScope: false,
                  onChat: () {},
                  onRecord: () {},
                  onScan: () {},
                );
              },
            ),
          ),
        ),
      ),
    );
    await tester.pump();

    expect(controller.primaryAction?.label, 'Add vendor');

    await tester.tap(find.text('Toggle detail'));
    await tester.pump();
    await tester.pump();

    expect(controller.primaryAction?.label, 'Rate vendor');

    await tester.tap(find.text('Toggle detail'));
    await tester.pump();
    await tester.pump();

    expect(controller.primaryAction?.label, 'Add vendor');

    await tester.tap(find.byTooltip('Scan / Add'));
    await tester.pumpAndSettle();

    expect(find.byType(FloatingActionButton), findsOneWidget);
    expect(find.text('Add vendor'), findsOneWidget);
    expect(find.text('Rate vendor'), findsNothing);
    expect(find.text('Assistant'), findsOneWidget);
    expect(find.text('Record'), findsOneWidget);
    expect(find.text('Scan / Add'), findsOneWidget);
  });
}

const _fullActionCapabilities = <String>{
  'money.expenses.manage',
  'reports.read',
  'rentals.manage',
};

Widget _authenticatedApp({
  required Widget home,
  Set<String> capabilities = _fullActionCapabilities,
  bool disableAnimations = false,
}) {
  return ProviderScope(
    overrides: [
      authControllerProvider.overrideWith(
        () => _StaticAuthController(_authenticatedState(capabilities)),
      ),
    ],
    child: MaterialApp(
      builder: (context, child) => MediaQuery(
        data: MediaQuery.of(
          context,
        ).copyWith(disableAnimations: disableAnimations),
        child: child!,
      ),
      home: home,
    ),
  );
}

AuthStateAuthenticated _authenticatedState(Set<String> capabilities) {
  const experience = WorkspaceExperience.management;
  return AuthStateAuthenticated(
    const AuthUser(
      id: 1,
      email: 'test@example.test',
      displayName: 'Test user',
      emailVerified: true,
    ),
    AccessEnvelope(
      identity: const AccessIdentity(userId: 1, displayName: 'Test user'),
      selectedContext: const SelectedAccessContext(
        accessContextId: 1,
        portfolioId: 1,
        workspaceName: 'Test workspace',
        accessRevision: 1,
        activeExperience: experience,
      ),
      defaultExperience: experience,
      availableExperiences: const [experience],
      assignments: const [],
      navigation: [
        NavigationCapabilities(
          experience: experience,
          capabilityKeys: capabilities.toList(growable: false),
        ),
      ],
    ),
    activeExperience: experience,
  );
}

class _StaticAuthController extends AuthController {
  _StaticAuthController(this.initialState);

  final AuthState initialState;

  @override
  AuthState build() => initialState;
}
