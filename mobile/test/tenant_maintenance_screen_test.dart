import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/api/api_exception.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/notifications/notification_models.dart';
import 'package:rental_command/features/notifications/notifications_repository.dart';
import 'package:rental_command/features/portal/create_tenant_work_order_sheet.dart';
import 'package:rental_command/features/portal/tenant_maintenance_screen.dart';
import 'package:rental_command/features/portal/tenant_portal_repository.dart';

void main() {
  testWidgets('shows provider-controlled loading error empty and list states', (
    tester,
  ) async {
    final loadingRepo = _FakeTenantPortalRepository(
      pageHandler: (_) => Completer<PortalTenantWorkOrderPage>().future,
    );
    await tester.pumpWidget(_app(loadingRepo));
    await tester.pump();
    expect(find.byType(CircularProgressIndicator), findsOneWidget);

    final errorRepo = _FakeTenantPortalRepository(
      pageHandler: (_) => throw const ApiException(
        statusCode: 503,
        message: 'Repairs unavailable.',
      ),
    );
    await tester.pumpWidget(_app(errorRepo));
    await tester.pumpAndSettle();
    expect(find.text('Repairs unavailable.'), findsOneWidget);
    expect(find.text('Try again'), findsOneWidget);

    final emptyRepo = _FakeTenantPortalRepository(
      pageHandler: (_) async => _page(items: const []),
    );
    await tester.pumpWidget(_app(emptyRepo));
    await tester.pumpAndSettle();
    expect(find.text('No repairs found.'), findsOneWidget);

    final listRepo = _FakeTenantPortalRepository(
      pageHandler: (_) async =>
          _page(items: [_workOrder(id: 1, title: 'Sink leak')]),
    );
    await tester.pumpWidget(_app(listRepo));
    await tester.pumpAndSettle();
    expect(find.text('Sink leak'), findsOneWidget);
    expect(find.text('Normal'), findsOneWidget);
  });

  testWidgets('sends exact query state and resets paging on control changes', (
    tester,
  ) async {
    final repo = _FakeTenantPortalRepository(
      pageHandler: (request) async => _page(
        skip: request.skip,
        totalCount: 45,
        items: [_workOrder(id: request.skip + 1, title: 'Repair')],
      ),
    );
    await tester.pumpWidget(_app(repo));
    await tester.pumpAndSettle();

    expect(repo.requests.single.openOnly, isTrue);
    expect(repo.requests.single.take, 20);
    expect(repo.requests.single.sort, '-requestedAt');

    await tester.tap(find.byTooltip('Next repairs page'));
    await tester.pumpAndSettle();
    expect(repo.requests.last.skip, 20);

    await tester.tap(find.byKey(const Key('tenant-repairs-controls-button')));
    await tester.pumpAndSettle();
    final controlsSheetScrollable = find.descendant(
      of: find.byType(BottomSheet),
      matching: find.byType(Scrollable),
    );
    expect(controlsSheetScrollable, findsOneWidget);

    Future<void> tapSheetControl(
      Key key, {
      required double scrollDelta,
      double alignment = 0.3,
    }) async {
      final target = find.byKey(key);
      await tester.scrollUntilVisible(
        target,
        scrollDelta,
        scrollable: controlsSheetScrollable,
        maxScrolls: 8,
      );
      await Scrollable.ensureVisible(
        tester.element(target),
        alignment: alignment,
        duration: Duration.zero,
      );
      await tester.pump();
      final hitTestableTarget = target.hitTestable();
      expect(hitTestableTarget, findsOneWidget);
      await tester.tap(hitTestableTarget);
      await tester.pump();
    }

    await tapSheetControl(
      const Key('tenant-repairs-sort-status'),
      scrollDelta: 200,
    );
    await tapSheetControl(
      const Key('tenant-repairs-filter-view-all'),
      scrollDelta: 200,
    );
    await tapSheetControl(
      const Key('tenant-repairs-filter-status-InProgress'),
      scrollDelta: 200,
      alignment: 0.5,
    );
    await tester.tap(find.byKey(const Key('tenant-repairs-apply-controls')));
    await tester.pumpAndSettle();

    final request = repo.requests.last;
    expect(request.skip, 0);
    expect(request.openOnly, isFalse);
    expect(request.status, 'InProgress');
    expect(request.sort, 'status');
  });

  testWidgets('FAB is on repairs root and create refreshes the list', (
    tester,
  ) async {
    late final _FakeTenantPortalRepository repo;
    repo = _FakeTenantPortalRepository(
      pageHandler: (_) async => _page(
        items: repo.createdTitles.isEmpty
            ? const []
            : [_workOrder(id: 99, title: repo.createdTitles.single)],
      ),
    );
    await tester.pumpWidget(_app(repo));
    await tester.pumpAndSettle();
    final requestCountBeforeCreate = repo.requests.length;

    expect(find.text('Add repair'), findsOneWidget);
    await tester.tap(find.text('Add repair'));
    await tester.pumpAndSettle();

    expect(find.text('Add repair'), findsAtLeastNWidgets(1));
    await tester.enterText(
      find.byKey(const Key('tenant-repair-title-field')),
      'Dripping faucet',
    );
    await tester.enterText(
      find.byKey(const Key('tenant-repair-description-field')),
      'The bathroom faucet drips overnight.',
    );
    await tester.tap(find.byKey(const Key('tenant-repair-submit-button')));
    await tester.pumpAndSettle();

    expect(repo.createdTitles, ['Dripping faucet']);
    expect(repo.requests.length, greaterThan(requestCountBeforeCreate));
    expect(find.text('Repair request submitted.'), findsOneWidget);
    expect(find.text('Dripping faucet'), findsOneWidget);
  });

  testWidgets('detail push and Back preserve filters page and scroll state', (
    tester,
  ) async {
    final repo = _FakeTenantPortalRepository(
      pageHandler: (_) async => _page(
        totalCount: 30,
        items: [
          for (var i = 0; i < 20; i++)
            _workOrder(id: i + 1, title: 'Repair ${i + 1}'),
        ],
      ),
    );
    await tester.pumpWidget(_app(repo));
    await tester.pumpAndSettle();

    await tester.drag(find.byType(ListView).last, const Offset(0, -500));
    await tester.pumpAndSettle();
    final before = tester
        .state<ScrollableState>(find.byType(Scrollable).last)
        .position
        .pixels;

    await tester.tap(find.text('Repair 10'));
    await tester.pumpAndSettle();
    expect(find.text('Request status'), findsOneWidget);

    await tester.binding.handlePopRoute();
    await tester.pumpAndSettle();
    expect(find.text('Repair 10'), findsOneWidget);
    final after = tester
        .state<ScrollableState>(find.byType(Scrollable).last)
        .position
        .pixels;
    expect(after, before);
  });

  testWidgets('sheet closes after successful create when photo upload fails', (
    tester,
  ) async {
    final repo = _FakeTenantPortalRepository(
      pageHandler: (_) async => _page(items: const []),
    )..failPhotoUpload = true;
    CreateTenantWorkOrderResult? result;

    await tester.pumpWidget(
      ProviderScope(
        overrides: [tenantPortalRepositoryProvider.overrideWithValue(repo)],
        child: MaterialApp(
          home: Builder(
            builder: (context) => Scaffold(
              body: Center(
                child: FilledButton(
                  onPressed: () async {
                    result =
                        await showModalBottomSheet<CreateTenantWorkOrderResult>(
                          context: context,
                          builder: (_) => UncontrolledProviderScope(
                            container: ProviderScope.containerOf(
                              context,
                              listen: false,
                            ),
                            child: CreateTenantWorkOrderSheet(
                              initialPhoto: TenantWorkOrderPhotoAttachment(
                                bytes: base64Decode(
                                  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=',
                                ),
                                fileName: 'repair.png',
                                contentType: 'image/png',
                                clientOperationId: 'photo-op-1',
                              ),
                            ),
                          ),
                        );
                  },
                  child: const Text('Open'),
                ),
              ),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('Open'));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const Key('tenant-repair-title-field')),
      'Loose rail',
    );
    await tester.enterText(
      find.byKey(const Key('tenant-repair-description-field')),
      'The porch rail is loose.',
    );
    final submitButton = find.byKey(const Key('tenant-repair-submit-button'));
    final createFormScrollable = find.descendant(
      of: find.byType(CreateTenantWorkOrderSheet),
      matching: find.byWidgetPredicate(
        (widget) =>
            widget is Scrollable &&
            widget.axisDirection == AxisDirection.down &&
            widget.physics is AlwaysScrollableScrollPhysics,
      ),
    );
    expect(createFormScrollable, findsOneWidget);
    await tester.scrollUntilVisible(
      submitButton,
      200,
      scrollable: createFormScrollable,
    );
    await Scrollable.ensureVisible(
      tester.element(submitButton),
      alignment: 0.3,
      duration: const Duration(milliseconds: 1),
    );
    await tester.pumpAndSettle();
    await tester.tap(submitButton);
    await tester.pumpAndSettle();

    expect(result, CreateTenantWorkOrderResult.submittedPhotoFailed);
    expect(repo.createdTitles, ['Loose rail']);
    expect(repo.uploadedPhotoOperationIds, ['photo-op-1']);
    expect(find.byKey(const Key('tenant-repair-title-field')), findsNothing);
  });
}

Widget _app(_FakeTenantPortalRepository repo) {
  return ProviderScope(
    overrides: [
      authControllerProvider.overrideWith(
        () => _StaticAuthController(_tenantAuthority()),
      ),
      notificationsRepositoryProvider.overrideWithValue(
        _FakeNotificationsRepository(),
      ),
      tenantPortalRepositoryProvider.overrideWithValue(repo),
    ],
    child: const MaterialApp(home: TenantMaintenanceScreen()),
  );
}

AuthStateAuthenticated _tenantAuthority() {
  const experience = WorkspaceExperience.tenant;
  return AuthStateAuthenticated(
    const AuthUser(
      id: 1,
      email: 'tenant@example.test',
      displayName: 'Test tenant',
      emailVerified: true,
    ),
    const AccessEnvelope(
      identity: AccessIdentity(userId: 1, displayName: 'Test tenant'),
      selectedContext: SelectedAccessContext(
        accessContextId: 1,
        portfolioId: 1,
        workspaceName: 'Test workspace',
        accessRevision: 1,
        activeExperience: experience,
      ),
      defaultExperience: experience,
      availableExperiences: [experience],
      assignments: [],
      navigation: [
        NavigationCapabilities(
          experience: experience,
          capabilityKeys: ['tenant.portal'],
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

class _FakeNotificationsRepository extends NotificationsRepository {
  _FakeNotificationsRepository() : super(Dio());

  @override
  Future<int> unreadCount() async => 0;

  @override
  Future<List<AppNotification>> list({
    bool unreadOnly = false,
    int skip = 0,
    int take = 20,
  }) async => const [];
}

class _FakeTenantPortalRepository extends TenantPortalRepository {
  _FakeTenantPortalRepository({required this.pageHandler}) : super(Dio());

  final Future<PortalTenantWorkOrderPage> Function(
    TenantWorkOrderPageRequest request,
  )
  pageHandler;
  final requests = <TenantWorkOrderPageRequest>[];
  final createdTitles = <String>[];
  final uploadedPhotoOperationIds = <String>[];
  bool failPhotoUpload = false;

  @override
  Future<PortalTenantWorkOrderPage> workOrdersPage({
    int skip = 0,
    int take = 20,
    bool openOnly = false,
    String search = '',
    String? status,
    String sort = '-requestedAt',
    String? requestedFrom,
    String? requestedTo,
  }) {
    final request = (
      skip: skip,
      take: take,
      openOnly: openOnly,
      search: search,
      status: status,
      sort: sort,
      from: requestedFrom,
      to: requestedTo,
    );
    requests.add(request);
    return pageHandler(request);
  }

  @override
  Future<WorkOrder> createWorkOrder({
    required String title,
    required String description,
    String category = 'Resident Request',
    String priority = 'Normal',
  }) async {
    createdTitles.add(title);
    return _workOrder(id: 99, title: title, description: description);
  }

  @override
  Future<void> uploadWorkOrderPhoto({
    required int workOrderId,
    required Uint8List bytes,
    required String fileName,
    required String contentType,
    required String clientOperationId,
  }) async {
    uploadedPhotoOperationIds.add(clientOperationId);
    if (failPhotoUpload) {
      throw const ApiException(
        statusCode: 503,
        message: 'Photo upload failed.',
      );
    }
  }

  @override
  Future<WorkOrderDetail> getWorkOrderDetail(int id) async {
    return WorkOrderDetail(
      workOrder: _workOrder(id: id),
      timeline: const [],
    );
  }
}

PortalTenantWorkOrderPage _page({
  required List<WorkOrder> items,
  int skip = 0,
  int totalCount = 0,
}) {
  return PortalTenantWorkOrderPage(
    items: items,
    totalCount: totalCount == 0 ? items.length : totalCount,
    skip: skip,
    take: 20,
  );
}

WorkOrder _workOrder({
  required int id,
  String title = 'Kitchen sink leak',
  String description = 'Water is dripping under the sink.',
}) {
  return WorkOrder(
    id: id,
    portfolioId: 1,
    propertyId: 2,
    unitId: 3,
    title: title,
    description: description,
    category: 'Resident Request',
    priority: 'Normal',
    status: 'New',
    requestedAt: DateTime(2026, 7, 20),
    updatedAt: DateTime(2026, 7, 20),
    propertyName: 'Maple Ridge',
    unitNumber: '4B',
  );
}
