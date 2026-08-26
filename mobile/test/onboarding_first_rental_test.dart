import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:rental_command/features/onboarding/getting_started_tasks.dart';
import 'package:rental_command/features/onboarding/onboarding_choice_screen.dart';
import 'package:rental_command/features/onboarding/onboarding_models.dart';
import 'package:rental_command/features/onboarding/onboarding_repository.dart';

void main() {
  testWidgets(
    'welcome leads with add your first rental and offers sample data second',
    (tester) async {
      final repository = _FakeOnboardingRepository();
      await _pumpChoiceScreen(tester, repository);

      // The primary call to action is the real-rental path.
      final primary = find.byKey(const Key('choose-live'));
      expect(primary, findsOneWidget);
      expect(
        find.descendant(
          of: primary,
          matching: find.text('Add your first rental'),
        ),
        findsWidgets,
      );

      // Sample data is a secondary text button underneath.
      final sampleData = find.byKey(const Key('choose-sandbox'));
      expect(sampleData, findsOneWidget);
      expect(
        find.descendant(
          of: sampleData,
          matching: find.text('Explore with sample data'),
        ),
        findsOneWidget,
      );
      expect(tester.widget(sampleData), isA<TextButton>());

      await tester.tap(find.byKey(const Key('choose-live')));
      await tester.pumpAndSettle();

      expect(repository.submittedModes, [OnboardingMode.live]);
      expect(find.text('live setup'), findsOneWidget);
    },
  );

  test('getting started has no owner confirmation task', () {
    expect(kGettingStartedTasks.where((task) => task.key == 'owner'), isEmpty);
    expect(
      kGettingStartedTasks.where(
        (task) => task.label.toLowerCase().contains('owns'),
      ),
      isEmpty,
    );
  });
}

Future<void> _pumpChoiceScreen(
  WidgetTester tester,
  _FakeOnboardingRepository repository,
) async {
  final router = GoRouter(
    initialLocation: '/',
    routes: [
      GoRoute(path: '/', builder: (_, _) => const OnboardingChoiceScreen()),
      GoRoute(
        path: '/live-setup',
        builder: (_, _) => const Scaffold(body: Text('live setup')),
      ),
      GoRoute(
        path: '/setting-up',
        builder: (_, _) => const Scaffold(body: Text('setting up')),
      ),
    ],
  );
  addTearDown(router.dispose);

  await tester.pumpWidget(
    ProviderScope(
      overrides: [onboardingRepositoryProvider.overrideWithValue(repository)],
      child: MaterialApp.router(routerConfig: router),
    ),
  );
  await tester.pumpAndSettle();
}

class _FakeOnboardingRepository extends OnboardingRepository {
  _FakeOnboardingRepository() : super(Dio());

  final List<OnboardingMode> submittedModes = [];

  @override
  Future<SandboxState> submitChoice(OnboardingMode mode) async {
    submittedModes.add(mode);
    return const SandboxState(
      portfolioId: 1,
      isSandbox: false,
      onboardingChoicePending: false,
    );
  }
}
