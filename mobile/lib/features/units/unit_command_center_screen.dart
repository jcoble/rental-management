import 'package:flutter/foundation.dart' show ValueListenable;
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/scheduler.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:material_symbols_icons/symbols.dart';
import 'package:url_launcher/url_launcher.dart';
import 'package:uuid/uuid.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/auth/mobile_access_policy.dart';
import '../../core/auth/solo_landlord.dart';
import '../../core/models/work_order.dart';
import '../../core/models/property.dart';
import '../../core/router/mobile_restoration_state.dart';
import '../../core/presentation/plain_english_labels.dart';
import '../../core/widgets/mobile_pill_tab_bar.dart';
import '../activity/activity_history_screen.dart';
import '../applications/application_detail_screen.dart';
import '../applications/applications_models.dart';
import '../applications/applications_repository.dart';
import '../applications/applications_shared.dart';
import '../deposits/deposits_screen.dart';
import '../home/mobile_domain_navigation.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import '../leases/lease_detail_screen.dart';
import '../inspections/inspections_repository.dart';
import '../recurring_maintenance/recurring_maintenance_repository.dart';
import '../maintenance/create_work_order_sheet.dart';
import '../maintenance/work_order_detail_screen.dart';
import '../money/expense_detail_screen.dart';
import '../money/expense_models.dart';
import '../money/money_repository.dart';
import '../money/tenant_ledger_view.dart';
import '../payments/payment_detail_screen.dart';
import '../payments/payments_screen.dart';
import '../properties/properties_repository.dart';
import '../tenants/tenant_detail_screen.dart';
import 'unit_command_center_tabs.dart';
import 'unit_form_sheet.dart';
import 'units_repository.dart';
import '../../core/presentation/formatting.dart';

export 'unit_command_center_tabs.dart';

class UnitCommandCenterLoaderScreen extends ConsumerWidget {
  const UnitCommandCenterLoaderScreen({
    super.key,
    required this.unitId,
    this.initialTab = UnitCommandCenterTab.summary,
    this.initialView,
    this.initialLeaseManagementId,
    this.initialApplication,
    this.initialWorkOrder,
    this.selectedTenantId,
  });

  final int unitId;
  final UnitCommandCenterTab initialTab;
  final UnitCommandCenterView? initialView;
  final int? initialLeaseManagementId;
  final RentalApplication? initialApplication;
  final WorkOrder? initialWorkOrder;
  final int? selectedTenantId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final dashboardAsync = ref.watch(unitDashboardProvider(unitId));
    final usesDomainHeader = MobileDomainHeaderScope.maybeOf(context) != null;

    return dashboardAsync.when(
      loading: () => Scaffold(
        appBar: usesDomainHeader ? null : AppBar(title: const Text('Unit')),
        body: const Center(child: CircularProgressIndicator()),
      ),
      error: (e, _) => Scaffold(
        appBar: usesDomainHeader ? null : AppBar(title: const Text('Unit')),
        body: _UnitErrorBody(
          message: e is ApiException ? e.message : e.toString(),
          onRetry: () => ref.invalidate(unitDashboardProvider(unitId)),
        ),
      ),
      data: (dashboard) {
        final property = dashboard.propertyName.trim().isEmpty
            ? 'Property'
            : dashboard.propertyName.trim();
        final screen = UnitCommandCenterScreen(
          dashboard: dashboard,
          initialTab: initialTab,
          initialView: initialView,
          initialLeaseManagementId: initialLeaseManagementId,
          initialApplication: initialApplication,
          initialWorkOrder: initialWorkOrder,
          selectedTenantId: selectedTenantId,
        );
        if (!usesDomainHeader) return screen;
        return MobileDomainDetailHeader(
          title: _unitLabel(dashboard.unit.unitNumber),
          subtitle: property,
          child: screen,
        );
      },
    );
  }
}

class UnitCommandCenterScreen extends StatefulWidget {
  const UnitCommandCenterScreen({
    super.key,
    required this.dashboard,
    this.initialTab = UnitCommandCenterTab.summary,
    this.initialView,
    this.initialLeaseManagementId,
    this.initialApplication,
    this.initialWorkOrder,
    this.selectedTenantId,
  });

  final UnitDashboard dashboard;
  final UnitCommandCenterTab initialTab;
  final UnitCommandCenterView? initialView;
  final int? initialLeaseManagementId;
  final RentalApplication? initialApplication;
  final WorkOrder? initialWorkOrder;
  final int? selectedTenantId;

  @override
  State<UnitCommandCenterScreen> createState() =>
      _UnitCommandCenterScreenState();
}

class _UnitCommandCenterScreenState extends State<UnitCommandCenterScreen> {
  late final ValueNotifier<UnitCommandCenterView?> _activeView;
  late UnitCommandCenterTab _activeTab;
  final Map<UnitCommandCenterTab, UnitCommandCenterView?>
  _activeViewsByDestination = {};
  RestorableMobileRestorationState? _restoration;
  TabController? _topController;

  @override
  void initState() {
    super.initState();
    _activeTab = widget.initialTab;
    _activeView = ValueNotifier(_startingView);
    _activeViewsByDestination[_activeTab] = _activeView.value;
    _activeView.addListener(_saveRestoration);
  }

  UnitCommandCenterView? get _startingView {
    if (widget.initialView != null) return widget.initialView;
    if (widget.initialApplication != null) {
      return UnitCommandCenterView.applications;
    }
    if (widget.selectedTenantId != null) {
      return UnitCommandCenterView.residents;
    }
    if (widget.initialWorkOrder != null) {
      return UnitCommandCenterView.workOrders;
    }
    return switch (widget.initialTab) {
      UnitCommandCenterTab.leasing => UnitCommandCenterView.listing,
      UnitCommandCenterTab.tenantLease => UnitCommandCenterView.agreements,
      UnitCommandCenterTab.money => UnitCommandCenterView.tenantAccount,
      UnitCommandCenterTab.maintenance => UnitCommandCenterView.workOrders,
      UnitCommandCenterTab.documentsHistory => UnitCommandCenterView.documents,
      _ => null,
    };
  }

  @override
  void dispose() {
    _topController?.removeListener(_handleTopTabChanged);
    _activeView.removeListener(_saveRestoration);
    _activeView.dispose();
    super.dispose();
  }

  void _bindTopController(TabController controller) {
    if (identical(controller, _topController)) return;
    _topController?.removeListener(_handleTopTabChanged);
    _topController = controller..addListener(_handleTopTabChanged);
    _restoration ??= MobileRestorationScope.maybeOf(context);
    _saveRestoration();
  }

  void _handleTopTabChanged() {
    final controller = _topController;
    if (controller == null || controller.indexIsChanging) return;
    final tab = UnitCommandCenterTab.values[controller.index];
    _activeViewsByDestination[_activeTab] = _activeView.value;
    final allowed = switch (tab) {
      UnitCommandCenterTab.leasing => const {
        UnitCommandCenterView.listing,
        UnitCommandCenterView.applications,
      },
      UnitCommandCenterTab.tenantLease => const {
        UnitCommandCenterView.agreements,
        UnitCommandCenterView.residents,
      },
      UnitCommandCenterTab.money => const {
        UnitCommandCenterView.tenantAccount,
        UnitCommandCenterView.operatingCosts,
      },
      UnitCommandCenterTab.maintenance => const {
        UnitCommandCenterView.workOrders,
        UnitCommandCenterView.inspections,
        UnitCommandCenterView.recurring,
        UnitCommandCenterView.turnover,
      },
      UnitCommandCenterTab.documentsHistory => const {
        UnitCommandCenterView.documents,
        UnitCommandCenterView.history,
      },
      UnitCommandCenterTab.summary => const <UnitCommandCenterView>{},
    };
    final requested = _activeViewsByDestination[tab];
    final next = allowed.isEmpty
        ? null
        : allowed.contains(requested)
        ? requested
        : allowed.first;
    _activeTab = tab;
    if (_activeView.value != next) _activeView.value = next;
    _saveRestoration();
  }

  void _saveRestoration() {
    _activeViewsByDestination[_activeTab] = _activeView.value;
    final restoration = _restoration;
    if (restoration == null) return;
    final tab = UnitCommandCenterTab
        .values[_topController?.index ?? widget.initialTab.index];
    restoration.value = restoration.value.updateUnit(
      unitId: widget.dashboard.unit.id,
      destination: tab.name,
      anchor: _activeView.value?.name,
    );
  }

  @override
  Widget build(BuildContext context) {
    final unit = widget.dashboard.unit;
    final property = widget.dashboard.propertyName.trim().isEmpty
        ? 'Property'
        : widget.dashboard.propertyName.trim();
    final usesDomainHeader = MobileDomainHeaderScope.maybeOf(context) != null;
    final tabView = TabBarView(
      children: [
        _UnitOverviewTab(dashboard: widget.dashboard),
        _UnitAreaSurface(
          area: UnitCommandCenterTab.leasing,
          labels: const ['Listing', 'Applications'],
          views: const [
            UnitCommandCenterView.listing,
            UnitCommandCenterView.applications,
          ],
          initialView: _startingView,
          activeView: _activeView,
          children: [
            _UnitListingTab(dashboard: widget.dashboard),
            _UnitApplicationsTab(
              unitId: widget.dashboard.unit.id,
              application: widget.initialApplication,
            ),
          ],
        ),
        _UnitTenantLeaseSurface(
          dashboard: widget.dashboard,
          selectedLeaseManagementId: widget.initialLeaseManagementId,
          selectedTenantId: widget.selectedTenantId,
          activeView: _activeView,
        ),
        _UnitLedgerTab(dashboard: widget.dashboard, activeView: _activeView),
        _UnitAreaSurface(
          area: UnitCommandCenterTab.maintenance,
          labels: const [
            'Repairs',
            'Inspections',
            'Recurring maintenance',
            'Getting it ready',
          ],
          views: const [
            UnitCommandCenterView.workOrders,
            UnitCommandCenterView.inspections,
            UnitCommandCenterView.recurring,
            UnitCommandCenterView.turnover,
          ],
          initialView: _startingView,
          activeView: _activeView,
          children: [
            _UnitWorkTab(
              dashboard: widget.dashboard,
              selectedWorkOrder: widget.initialWorkOrder,
            ),
            _UnitInspectionsTab(unitId: widget.dashboard.unit.id),
            _UnitRecurringMaintenanceTab(unitId: widget.dashboard.unit.id),
            _UnitTurnoverTab(dashboard: widget.dashboard),
          ],
        ),
        _UnitAreaSurface(
          area: UnitCommandCenterTab.documentsHistory,
          labels: const ['Documents', 'History'],
          views: const [
            UnitCommandCenterView.documents,
            UnitCommandCenterView.history,
          ],
          fillViewport: const [false, true],
          initialView: _startingView,
          activeView: _activeView,
          children: [
            _UnitDocumentsTab(dashboard: widget.dashboard),
            ActivityHistoryScreen(
              entityType: 'Unit',
              entityId: widget.dashboard.unit.id,
              title: 'Unit history',
              subtitle: _unitLabel(widget.dashboard.unit.unitNumber),
              embedded: true,
            ),
          ],
        ),
      ],
    );

    return DefaultTabController(
      length: UnitCommandCenterTab.values.length,
      initialIndex: widget.initialTab.index,
      child: Builder(
        builder: (tabContext) {
          final topController = DefaultTabController.of(tabContext);
          _bindTopController(topController);
          final unitTabs = AnimatedBuilder(
            animation: topController.animation!,
            builder: (context, _) => MobilePillTabBar(
              key: const Key('unit-section-tabs'),
              scrollKey: const Key('unit-section-tabs-scroll'),
              semanticLabel: 'Unit sections',
              selectedIndex: topController.index,
              onSelected: topController.animateTo,
              tabs: const [
                MobilePillTab(
                  key: ValueKey('unit-section-summary'),
                  label: 'Summary',
                ),
                MobilePillTab(
                  key: ValueKey('unit-section-leasing'),
                  label: 'Leasing',
                ),
                MobilePillTab(
                  key: ValueKey('unit-section-tenant-lease'),
                  label: 'Tenant & lease',
                ),
                MobilePillTab(
                  key: ValueKey('unit-section-money'),
                  label: 'Money',
                ),
                MobilePillTab(
                  key: ValueKey('unit-section-maintenance'),
                  label: 'Maintenance',
                ),
                MobilePillTab(
                  key: ValueKey('unit-section-documents-history'),
                  label: 'Documents & history',
                ),
              ],
            ),
          );
          return _UnitViewScope(
            activeView: _activeView,
            child: Scaffold(
              appBar: usesDomainHeader
                  ? null
                  : AppBar(
                      title: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Text(_unitLabel(unit.unitNumber)),
                          Text(
                            property,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: Theme.of(context).textTheme.bodySmall
                                ?.copyWith(
                                  color: Theme.of(
                                    context,
                                  ).colorScheme.onSurfaceVariant,
                                ),
                          ),
                        ],
                      ),
                    ),
              body: usesDomainHeader
                  ? Column(
                      children: [
                        Material(
                          color: Theme.of(context).colorScheme.surface,
                          child: unitTabs,
                        ),
                        Expanded(child: tabView),
                      ],
                    )
                  : Column(
                      children: [
                        Material(
                          color: Theme.of(context).colorScheme.surface,
                          child: unitTabs,
                        ),
                        Expanded(child: tabView),
                      ],
                    ),
              floatingActionButton: _UnitWorkOrderQuickActionFab(
                dashboard: widget.dashboard,
                selectedWorkOrder: widget.initialWorkOrder,
                activeView: _activeView,
              ),
              floatingActionButtonLocation:
                  FloatingActionButtonLocation.endFloat,
            ),
          );
        },
      ),
    );
  }
}

class _UnitViewScope extends InheritedWidget {
  const _UnitViewScope({required this.activeView, required super.child});

  final ValueNotifier<UnitCommandCenterView?> activeView;

  static ValueNotifier<UnitCommandCenterView?>? maybeOf(BuildContext context) =>
      context.dependOnInheritedWidgetOfExactType<_UnitViewScope>()?.activeView;

  @override
  bool updateShouldNotify(_UnitViewScope oldWidget) =>
      !identical(activeView, oldWidget.activeView);
}

class _UnitAreaSurface extends StatefulWidget {
  const _UnitAreaSurface({
    required this.area,
    required this.labels,
    required this.views,
    required this.children,
    required this.activeView,
    this.initialView,
    this.fillViewport,
  }) : assert(labels.length == views.length),
       assert(views.length == children.length),
       assert(fillViewport == null || fillViewport.length == children.length);

  final UnitCommandCenterTab area;
  final List<String> labels;
  final List<UnitCommandCenterView> views;
  final List<Widget> children;
  final ValueNotifier<UnitCommandCenterView?> activeView;
  final UnitCommandCenterView? initialView;
  final List<bool>? fillViewport;

  @override
  State<_UnitAreaSurface> createState() => _UnitAreaSurfaceState();
}

class _UnitAreaSurfaceState extends State<_UnitAreaSurface> {
  ScrollController? _scrollController;
  late final List<GlobalKey> _sectionKeys;
  TabController? _outerController;
  bool _activeViewSyncScheduled = false;

  @override
  void initState() {
    super.initState();
    _sectionKeys = List.generate(widget.views.length, (_) => GlobalKey());
    widget.activeView.addListener(_applyRequestedView);
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final requestedView = widget.activeView.value;
    final requestedIndex = requestedView == null
        ? -1
        : widget.views.indexOf(requestedView);
    _scrollController ??= ScrollController(
      initialScrollOffset: requestedIndex > 0
          ? MediaQuery.sizeOf(context).height * requestedIndex
          : 0,
    );
    final outer = DefaultTabController.maybeOf(context);
    if (identical(outer, _outerController)) return;
    _outerController?.removeListener(_syncActiveView);
    _outerController = outer;
    _outerController?.addListener(_syncActiveView);
    _syncActiveView();
  }

  @override
  void dispose() {
    _outerController?.removeListener(_syncActiveView);
    widget.activeView.removeListener(_applyRequestedView);
    _scrollController?.dispose();
    super.dispose();
  }

  void _syncActiveView() {
    final phase = SchedulerBinding.instance.schedulerPhase;
    if (phase != SchedulerPhase.idle &&
        phase != SchedulerPhase.postFrameCallbacks) {
      if (_activeViewSyncScheduled) return;
      _activeViewSyncScheduled = true;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        _activeViewSyncScheduled = false;
        if (mounted) _syncActiveView();
      });
      return;
    }

    if (_outerController?.index != widget.area.index) return;
    final requested = widget.activeView.value;
    final next = requested != null && widget.views.contains(requested)
        ? requested
        : widget.views.first;
    if (widget.activeView.value != next) widget.activeView.value = next;
    _applyRequestedView();
  }

  void _applyRequestedView() {
    final activeView = widget.activeView.value;
    if (activeView == null) return;
    final requested = widget.views.indexOf(activeView);
    if (requested < 0 || _outerController?.index != widget.area.index) return;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      final sectionContext = _sectionKeys[requested].currentContext;
      if (sectionContext != null) {
        Scrollable.ensureVisible(
          sectionContext,
          duration: Duration.zero,
          alignment: 0,
        );
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) => ListView(
        controller: _scrollController,
        scrollCacheExtent: ScrollCacheExtent.pixels(
          constraints.maxHeight * widget.children.length,
        ),
        padding: const EdgeInsets.only(bottom: 32),
        children: [
          for (var index = 0; index < widget.children.length; index++)
            Column(
              key: _sectionKeys[index],
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Padding(
                  padding: const EdgeInsets.fromLTRB(16, 16, 16, 8),
                  child: Text(
                    widget.labels[index],
                    style: Theme.of(context).textTheme.titleLarge,
                  ),
                ),
                if (widget.fillViewport?[index] ?? true)
                  SizedBox(
                    height: constraints.maxHeight,
                    child: widget.children[index],
                  )
                else
                  widget.children[index],
                if (index < widget.children.length - 1)
                  const Divider(height: 32),
              ],
            ),
        ],
      ),
    );
  }
}

class _UnitWorkOrderQuickActionFab extends ConsumerStatefulWidget {
  const _UnitWorkOrderQuickActionFab({
    required this.dashboard,
    required this.activeView,
    this.selectedWorkOrder,
  });

  final UnitDashboard dashboard;
  final ValueListenable<UnitCommandCenterView?> activeView;
  final WorkOrder? selectedWorkOrder;

  @override
  ConsumerState<_UnitWorkOrderQuickActionFab> createState() =>
      _UnitWorkOrderQuickActionFabState();
}

class _UnitWorkOrderQuickActionFabState
    extends ConsumerState<_UnitWorkOrderQuickActionFab> {
  TabController? _tabController;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final nextController = DefaultTabController.maybeOf(context);

    if (!identical(_tabController, nextController)) {
      _tabController?.removeListener(_handleTabChanged);
      _tabController = nextController;
      _tabController?.addListener(_handleTabChanged);
    }

    widget.activeView.removeListener(_handleTabChanged);
    widget.activeView.addListener(_handleTabChanged);
  }

  @override
  void dispose() {
    _tabController?.removeListener(_handleTabChanged);
    widget.activeView.removeListener(_handleTabChanged);
    super.dispose();
  }

  void _handleTabChanged() {
    if (mounted) setState(() {});
  }

  String get _propertyLabel {
    final property = widget.dashboard.propertyName.trim();
    return property.isEmpty ? 'Property' : property;
  }

  MobileQuickAction? _currentAction() {
    final auth = ref.watch(authControllerProvider);
    if (auth is! AuthStateAuthenticated) return null;
    final canManageWork = canUseMobileCapabilityAction(
      experience: auth.activeExperience,
      capabilities: auth.capabilities,
      capability: 'work.manage',
      experiences: const {WorkspaceExperience.management},
    );
    final canRecordReceipts = canUseMobileCapabilityAction(
      experience: auth.activeExperience,
      capabilities: auth.capabilities,
      capability: 'money.payments.manage',
      experiences: const {WorkspaceExperience.management},
    );
    final index =
        _tabController?.index ?? DefaultTabController.of(context).index;
    if (index == UnitCommandCenterTab.maintenance.index &&
        widget.activeView.value == UnitCommandCenterView.workOrders &&
        widget.selectedWorkOrder == null &&
        canManageWork) {
      return MobileQuickAction(
        label: 'New repair',
        icon: Icons.add,
        onPressed: _showWorkOrderSheet,
      );
    }

    if (index == UnitCommandCenterTab.maintenance.index &&
        widget.activeView.value == UnitCommandCenterView.turnover &&
        canManageWork) {
      return MobileQuickAction(
        label: 'New preparation task',
        icon: Icons.add,
        onPressed: _showWorkOrderSheet,
      );
    }

    if (index == UnitCommandCenterTab.money.index &&
        widget.dashboard.tenantAccountId != null &&
        canRecordReceipts) {
      return MobileQuickAction(
        label: 'Record receipt',
        icon: Icons.add_card_outlined,
        onPressed: _showReceiptSheet,
      );
    }

    return null;
  }

  void _showWorkOrderSheet() {
    showCreateWorkOrderSheet(
      context: context,
      ref: ref,
      onSaved: () =>
          ref.invalidate(unitDashboardProvider(widget.dashboard.unit.id)),
      initialPropertyId: widget.dashboard.unit.propertyId,
      initialUnitId: widget.dashboard.unit.id,
      initialPropertyLabel: _propertyLabel,
      initialUnitLabel: _unitLabel(widget.dashboard.unit.unitNumber),
    );
  }

  Future<void> _showReceiptSheet() async {
    final tenantAccountId = widget.dashboard.tenantAccountId;
    final leaseManagementId = widget.dashboard.leaseManagementId;
    if (tenantAccountId == null || leaseManagementId == null) return;
    final result = await showRecordTenantReceiptSheet(
      context,
      ref,
      tenantAccountId: tenantAccountId,
      leaseManagementId: leaseManagementId,
      tenantName: widget.dashboard.header.currentTenantName,
      rentalLabel:
          '$_propertyLabel · ${_unitLabel(widget.dashboard.unit.unitNumber)}',
    );
    if (result != null) {
      ref.invalidate(unitDashboardProvider(widget.dashboard.unit.id));
    }
  }

  Future<void> _openUnitScan() {
    return openMobileScan(
      context,
      propertyId: widget.dashboard.unit.propertyId,
      unitId: widget.dashboard.unit.id,
      leaseManagementId: widget.dashboard.leaseManagementId,
      leaseAgreementId: widget.dashboard.currentLease?.id,
      tenantAccountId: widget.dashboard.tenantAccountId,
      sourceLabel: "Today's summary",
    );
  }

  @override
  Widget build(BuildContext context) {
    final action = _currentAction();
    return MobileQuickActionFab(
      heroTag: 'unit-work-order-quick-action-fab',
      primaryAction: action,
      onChat: () => openMobileAssistant(context),
      onRecord: () => openMobileRecord(context),
      onScan: _openUnitScan,
    );
  }
}

class _UnitOverviewTab extends ConsumerWidget {
  const _UnitOverviewTab({required this.dashboard});

  final UnitDashboard dashboard;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final header = dashboard.header;
    final unit = dashboard.unit;
    final auth = ref.watch(authControllerProvider);
    final canManageRentals =
        auth is AuthStateAuthenticated &&
        canUseMobileCapabilityAction(
          experience: auth.activeExperience,
          capabilities: auth.capabilities,
          capability: 'rentals.manage',
          experiences: const {WorkspaceExperience.management},
        );

    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      children: [
        if (dashboard.nextBestAction.label.trim().isNotEmpty) ...[
          _ActionPanel(
            label: dashboard.nextBestAction.label.trim(),
            href: dashboard.nextBestAction.href,
            currentUnitId: dashboard.unit.id,
          ),
          const SizedBox(height: 14),
        ],
        _SurfacePanel(
          children: [
            _MetricRow(
              icon: Symbols.home_work_rounded,
              label: 'Occupancy / possession',
              value: plainEnglishLabel(dashboard.occupancyPossession.status),
            ),
            _MetricRow(
              icon: Symbols.campaign_rounded,
              label: 'Marketing availability',
              value: plainEnglishLabel(dashboard.marketingAvailability.status),
            ),
            _MetricRow(
              icon: Symbols.account_balance_wallet_rounded,
              label: 'Tenant account',
              value: plainEnglishLabel(dashboard.tenantAccountCondition.status),
            ),
            _MetricRow(
              icon: Symbols.gavel_rounded,
              label: 'Legal / notice',
              value: plainEnglishLabel(dashboard.legalNoticeCondition.status),
            ),
            _MetricRow(
              icon: Symbols.build_rounded,
              label: 'Maintenance / getting it ready',
              value: plainEnglishLabel(dashboard.maintenanceTurnover.status),
            ),
          ],
        ),
        const SizedBox(height: 12),
        _SurfacePanel(
          children: [
            _MetricRow(
              icon: Symbols.home_work_rounded,
              label: 'Status',
              value: plainEnglishLabel(
                unit.status.isEmpty ? dashboard.lifecycleStage : unit.status,
              ),
            ),
            _MetricRow(
              icon: Symbols.route_rounded,
              label: "Where it's at",
              value: plainEnglishLabel(
                dashboard.lifecycleStage,
                fallback: 'Rental',
              ),
            ),
            _MetricRow(
              icon: Symbols.payments_rounded,
              label: 'Market rent',
              value: '${moneyFmt(unit.marketRent, whole: true)}/mo',
            ),
            if (unit.bedrooms > 0 || unit.bathrooms > 0)
              _MetricRow(
                icon: Symbols.bed_rounded,
                label: 'Plan',
                value: _bedBathLabel(unit.bedrooms, unit.bathrooms),
              ),
          ],
        ),
        const SizedBox(height: 12),
        if (canManageRentals) ...[
          Align(
            alignment: Alignment.centerLeft,
            child: FilledButton.icon(
              icon: const Icon(Icons.edit_outlined),
              label: const Text('Edit unit'),
              onPressed: () => _showEditUnitSheet(context, ref),
            ),
          ),
          const SizedBox(height: 14),
        ],
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            _InfoChip(
              icon: Symbols.account_balance_wallet_rounded,
              label: header.rentState,
            ),
            if (header.outstandingRentBalance > 0)
              _InfoChip(
                icon: Symbols.attach_money_rounded,
                label:
                    '${moneyFmt(header.outstandingRentBalance, whole: true)} due',
              ),
            _InfoChip(
              icon: Symbols.build_rounded,
              label: '${header.openWorkOrderCount} open repairs',
            ),
            if (header.leaseEndsInDays != null)
              _InfoChip(
                icon: Symbols.event_rounded,
                label: _leaseEndsLabel(header.leaseEndsInDays!),
              ),
            if (header.currentTenantName != null &&
                header.currentTenantName!.trim().isNotEmpty)
              _InfoChip(
                icon: Symbols.person_rounded,
                label: header.currentTenantName!.trim(),
              ),
          ],
        ),
        const SizedBox(height: 18),
        _PaymentsSection(items: dashboard.overview.recentPayments),
        const SizedBox(height: 14),
        _WorkOrdersSection(items: dashboard.overview.openWorkOrders),
        const SizedBox(height: 14),
        _DocumentsSection(items: dashboard.overview.pendingDocs),
        const SizedBox(height: 14),
        _AppointmentsSection(items: dashboard.overview.upcomingAppointments),
      ],
    );
  }

  void _showEditUnitSheet(BuildContext context, WidgetRef ref) {
    showUnitFormSheet(
      context,
      propertyId: dashboard.unit.propertyId,
      unit: dashboard.unit,
      onSaved: (_) {
        ref.invalidate(unitDashboardProvider(dashboard.unit.id));
        ref.invalidate(unitsProvider(dashboard.unit.propertyId));
      },
    );
  }
}

class _UnitListingTab extends ConsumerStatefulWidget {
  const _UnitListingTab({required this.dashboard});

  final UnitDashboard dashboard;

  @override
  ConsumerState<_UnitListingTab> createState() => _UnitListingTabState();
}

class _UnitListingTabState extends ConsumerState<_UnitListingTab> {
  static const _zillowRentalManagerUrl =
      'https://www.zillow.com/rental-manager/properties';
  static const _workspaceStatusOptions = [
    'Draft',
    'ReadyToPublish',
    'Published',
    'Paused',
    'Filled',
    'Archived',
  ];
  static const _publicationStatusOptions = [
    'Draft',
    'Ready',
    'Published',
    'Paused',
    'Removed',
  ];

  final _headlineController = TextEditingController();
  final _descriptionController = TextEditingController();
  final _rentController = TextEditingController();
  final _depositController = TextEditingController();
  final _leaseTermsController = TextEditingController();
  final _petPolicyController = TextEditingController();
  final _utilitiesController = TextEditingController();
  final _parkingController = TextEditingController();
  final _amenitiesController = TextEditingController();
  final _externalListingIdController = TextEditingController();
  final _zillowListingUrlController = TextEditingController();
  final _zillowApplicationUrlController = TextEditingController();
  final _externalStatusController = TextEditingController();

  int? _loadedContentVersion;
  String _status = 'Draft';
  String _publicationStatus = 'Draft';
  bool _copyConfirmed = false;
  bool _termsConfirmed = false;
  bool _photosConfirmed = false;
  bool _isGenerating = false;
  bool _isSaving = false;
  bool _isSyncingForm = false;

  @override
  void initState() {
    super.initState();
    for (final controller in _formControllers) {
      controller.addListener(_onFormChanged);
    }
  }

  @override
  void dispose() {
    for (final controller in _formControllers) {
      controller.removeListener(_onFormChanged);
      controller.dispose();
    }
    super.dispose();
  }

  List<TextEditingController> get _formControllers => [
    _headlineController,
    _descriptionController,
    _rentController,
    _depositController,
    _leaseTermsController,
    _petPolicyController,
    _utilitiesController,
    _parkingController,
    _amenitiesController,
    _externalListingIdController,
    _zillowListingUrlController,
    _zillowApplicationUrlController,
    _externalStatusController,
  ];

  void _onFormChanged() {
    if (!_isSyncingForm && mounted) setState(() {});
  }

  @override
  Widget build(BuildContext context) {
    final unitId = widget.dashboard.unit.id;
    final auth = ref.watch(authControllerProvider);
    final canManageListings =
        auth is AuthStateAuthenticated &&
        canUseMobileCapabilityAction(
          experience: auth.activeExperience,
          capabilities: auth.capabilities,
          capability: 'leasing.listings.manage',
          experiences: const {
            WorkspaceExperience.management,
            WorkspaceExperience.leasing,
          },
        );
    if (!canManageListings) {
      return const _EmptyTab(
        icon: Symbols.lock_outline_rounded,
        title: 'Listing access unavailable',
        body: 'Your current workspace experience cannot manage this listing.',
      );
    }
    final listingAsync = ref.watch(unitListingWorkspaceProvider(unitId));

    return listingAsync.when(
      loading: () => const Center(child: CircularProgressIndicator()),
      error: (e, _) => _UnitErrorBody(
        message: e is ApiException ? e.message : e.toString(),
        onRetry: () => ref.invalidate(unitListingWorkspaceProvider(unitId)),
      ),
      data: (listing) {
        _syncFromListing(listing);
        if (listing == null) {
          return _EmptyTab(
            icon: Symbols.real_estate_agent_rounded,
            title: 'No listing workspace',
            body:
                'Prepare reusable listing copy, terms, and an ordered photo package for this unit.',
            action: FilledButton.icon(
              icon: _isGenerating
                  ? const SizedBox.square(
                      dimension: 16,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Symbols.auto_awesome_rounded),
              label: Text(_isGenerating ? 'Preparing...' : 'Prepare listing'),
              onPressed: _isGenerating ? null : _generateListing,
            ),
          );
        }

        return ListView(
          padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
          children: [
            _ListingHeader(
              status: _status,
              updatedAt: listing.updatedAt,
              onGenerate: _isGenerating ? null : _generateListing,
              onSave: _canSave ? () => _saveListing() : null,
              isGenerating: _isGenerating,
              isSaving: _isSaving,
            ),
            if (_guidedPublication(listing)?.needsRepublish == true) ...[
              const SizedBox(height: 14),
              _ListingWarning(
                message:
                    'Changes need republishing. Rental Command has listing version ${listing.contentVersion}, but Zillow was last confirmed at version ${_guidedPublication(listing)?.publishedContentVersion ?? 'an earlier version'}.',
              ),
            ],
            const SizedBox(height: 14),
            _Section(
              title: 'Listing copy',
              empty: 'No listing fields',
              children: [
                _ListingStatusRow(
                  label: 'Listing status',
                  value: _status,
                  options: _workspaceStatusOptions,
                  onChanged: (value) => setState(() => _status = value),
                ),
                _ListingField(
                  label: 'Headline',
                  controller: _headlineController,
                  actionIcon: Symbols.content_copy_rounded,
                  onAction: () =>
                      _copyText('Headline', _headlineController.text),
                ),
                _ListingField(
                  label: 'Description',
                  controller: _descriptionController,
                  maxLines: 5,
                  actionIcon: Symbols.content_copy_rounded,
                  onAction: () =>
                      _copyText('Description', _descriptionController.text),
                ),
                _ListingField(
                  label: 'Rent',
                  controller: _rentController,
                  keyboardType: const TextInputType.numberWithOptions(
                    decimal: true,
                  ),
                ),
                _ListingField(
                  label: 'Deposit',
                  controller: _depositController,
                  keyboardType: const TextInputType.numberWithOptions(
                    decimal: true,
                  ),
                ),
                _ListingField(
                  label: 'Lease terms',
                  controller: _leaseTermsController,
                ),
                _ListingField(
                  label: 'Pet policy',
                  controller: _petPolicyController,
                ),
                _ListingField(
                  label: 'Utilities',
                  controller: _utilitiesController,
                ),
                _ListingField(label: 'Parking', controller: _parkingController),
                _ListingField(
                  label: 'Amenities',
                  controller: _amenitiesController,
                  maxLines: 3,
                ),
              ],
            ),
            const SizedBox(height: 14),
            _ListingPhotoPackage(
              photos: listing.photoManifest,
              busy: _isSaving,
              onAddOrReplace: _pickListingPhoto,
              onRemove: _removeListingPhoto,
              onMove: _moveListingPhoto,
            ),
            const SizedBox(height: 14),
            _Section(
              title: 'Post on Zillow yourself',
              empty: 'No posting details',
              children: [
                _CompactRow(
                  icon: Symbols.open_in_new_rounded,
                  title: 'Open Zillow Rental Manager',
                  subtitle: 'You stay signed in and publish there',
                  onTap: _openZillowWorkspace,
                ),
                _CompactRow(
                  icon: Symbols.search_rounded,
                  title: 'Search Zillow rentals',
                  subtitle: 'Compare nearby listings in a browser',
                  onTap: () => _openExternalUrl(
                    'https://www.zillow.com/homes/for_rent/',
                  ),
                ),
                _ListingStatusRow(
                  label: 'Listing status',
                  value: _publicationStatus,
                  options: _publicationStatusOptions,
                  onChanged: (value) =>
                      setState(() => _publicationStatus = value),
                ),
                _ListingChecklist(
                  copyConfirmed: _copyConfirmed,
                  termsConfirmed: _termsConfirmed,
                  photosConfirmed: _photosConfirmed,
                  onCopyChanged: (value) =>
                      setState(() => _copyConfirmed = value),
                  onTermsChanged: (value) =>
                      setState(() => _termsConfirmed = value),
                  onPhotosChanged: (value) =>
                      setState(() => _photosConfirmed = value),
                ),
                _ListingField(
                  label: 'Zillow listing ID',
                  controller: _externalListingIdController,
                ),
                _ListingField(
                  label: 'Zillow listing URL',
                  controller: _zillowListingUrlController,
                  keyboardType: TextInputType.url,
                  actionIcon: Symbols.open_in_new_rounded,
                  onAction: () =>
                      _openExternalUrl(_zillowListingUrlController.text),
                ),
                _ListingField(
                  label: 'Zillow application URL',
                  controller: _zillowApplicationUrlController,
                  keyboardType: TextInputType.url,
                  actionIcon: Symbols.open_in_new_rounded,
                  onAction: () =>
                      _openExternalUrl(_zillowApplicationUrlController.text),
                ),
                _ListingField(
                  label: 'Last status you confirmed',
                  controller: _externalStatusController,
                ),
                Padding(
                  padding: const EdgeInsets.fromLTRB(16, 6, 16, 12),
                  child: OutlinedButton.icon(
                    icon: const Icon(Symbols.publish_rounded),
                    label: const Text('Save as published in Zillow'),
                    onPressed: _isSaving
                        ? null
                        : () => _saveListing(markPublished: true),
                  ),
                ),
              ],
            ),
            if (!isSoloLandlord(ref) ||
                _connectedPublication(listing) != null) ...[
              const SizedBox(height: 14),
              _ListingConnectedCard(
                publication: _connectedPublication(listing),
                busy: _isSaving,
                onAction: _runConnectedAction,
              ),
            ],
            if (_guidedPublication(listing)?.unconfirmedSignals.isNotEmpty ==
                true) ...[
              const SizedBox(height: 14),
              _ListingSignalsSection(
                signals: _guidedPublication(listing)!.unconfirmedSignals,
                onDecision: _confirmSignal,
              ),
            ],
            const SizedBox(height: 14),
            _SurfacePanel(
              children: [
                _CompactRow(
                  icon: Symbols.document_scanner_rounded,
                  title: 'Import a signed Zillow lease',
                  subtitle:
                      'Scan or upload the lease; Rental Command fills the unit context for review.',
                  onTap: _importSignedLease,
                ),
              ],
            ),
          ],
        );
      },
    );
  }

  Future<void> _pickListingPhoto(ListingPhoto photo) async {
    final picked = await ImagePicker().pickImage(
      source: ImageSource.gallery,
      imageQuality: 90,
    );
    if (picked == null) return;
    setState(() => _isSaving = true);
    try {
      final bytes = await picked.readAsBytes();
      final extension = picked.name.toLowerCase().split('.').last;
      final contentType = switch (extension) {
        'png' => 'image/png',
        'heic' || 'heif' => 'image/heic',
        _ => 'image/jpeg',
      };
      final workspace = await ref
          .read(unitsRepositoryProvider)
          .attachListingPhoto(
            unitId: widget.dashboard.unit.id,
            photoId: photo.id,
            bytes: bytes,
            fileName: picked.name,
            contentType: contentType,
            clientOperationId: const Uuid().v4(),
          );
      ref.invalidate(unitListingWorkspaceProvider(widget.dashboard.unit.id));
      _loadedContentVersion = null;
      _syncFromListing(workspace);
    } on ApiException catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(error.message)));
      }
    } finally {
      if (mounted) setState(() => _isSaving = false);
    }
  }

  Future<void> _removeListingPhoto(ListingPhoto photo) async {
    setState(() => _isSaving = true);
    try {
      await ref
          .read(unitsRepositoryProvider)
          .removeListingPhoto(widget.dashboard.unit.id, photo.id);
      ref.invalidate(unitListingWorkspaceProvider(widget.dashboard.unit.id));
    } on ApiException catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(error.message)));
      }
    } finally {
      if (mounted) setState(() => _isSaving = false);
    }
  }

  Future<void> _moveListingPhoto(ListingPhoto photo, int direction) async {
    final listing = ref
        .read(unitListingWorkspaceProvider(widget.dashboard.unit.id))
        .value;
    if (listing == null) return;
    final ids = listing.photoManifest.map((item) => item.id).toList();
    final index = ids.indexOf(photo.id);
    final destination = index + direction;
    if (index < 0 || destination < 0 || destination >= ids.length) return;
    final moved = ids.removeAt(index);
    ids.insert(destination, moved);
    setState(() => _isSaving = true);
    try {
      await ref
          .read(unitsRepositoryProvider)
          .reorderListingPhotos(widget.dashboard.unit.id, ids);
      ref.invalidate(unitListingWorkspaceProvider(widget.dashboard.unit.id));
    } on ApiException catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(error.message)));
      }
    } finally {
      if (mounted) setState(() => _isSaving = false);
    }
  }

  bool get _canSave =>
      !_isSaving &&
      _headlineController.text.trim().isNotEmpty &&
      _descriptionController.text.trim().isNotEmpty &&
      _isOptionalNumber(_rentController.text) &&
      _isOptionalNumber(_depositController.text);

  ListingPublication? _guidedPublication(ListingWorkspace listing) => listing
      .publications
      .where((item) => item.providerKey == 'Zillow' && item.mode == 'Guided')
      .firstOrNull;

  ListingPublication? _connectedPublication(ListingWorkspace listing) => listing
      .publications
      .where((item) => item.providerKey == 'Zillow' && item.mode == 'Connected')
      .firstOrNull;

  void _syncFromListing(ListingWorkspace? listing) {
    if (listing == null) {
      if (_loadedContentVersion != null) {
        _loadedContentVersion = null;
        _setFormToBlank();
      }
      return;
    }
    if (_loadedContentVersion == listing.contentVersion) return;
    _loadedContentVersion = listing.contentVersion;
    final guided = _guidedPublication(listing);
    _isSyncingForm = true;
    _status = _workspaceStatusOptions.contains(listing.status)
        ? listing.status
        : 'Draft';
    _publicationStatus = _publicationStatusOptions.contains(guided?.status)
        ? guided!.status
        : 'Draft';
    _copyConfirmed = guided?.copyConfirmed ?? false;
    _termsConfirmed = guided?.termsConfirmed ?? false;
    _photosConfirmed = guided?.photosConfirmed ?? false;
    _headlineController.text = listing.headline;
    _descriptionController.text = listing.description;
    _rentController.text = _numberToText(listing.rent);
    _depositController.text = _numberToText(listing.securityDeposit);
    _leaseTermsController.text = listing.leaseTerms ?? '';
    _petPolicyController.text = listing.petPolicy ?? '';
    _utilitiesController.text = listing.utilities ?? '';
    _parkingController.text = listing.parking ?? '';
    _amenitiesController.text = listing.amenities ?? '';
    _externalListingIdController.text = guided?.externalListingId ?? '';
    _zillowListingUrlController.text = guided?.listingUrl ?? '';
    _zillowApplicationUrlController.text = guided?.applicationUrl ?? '';
    _externalStatusController.text = guided?.lastConfirmedExternalStatus ?? '';
    _isSyncingForm = false;
  }

  void _setFormToBlank() {
    _isSyncingForm = true;
    _status = 'Draft';
    _publicationStatus = 'Draft';
    _copyConfirmed = false;
    _termsConfirmed = false;
    _photosConfirmed = false;
    for (final controller in _formControllers) {
      controller.clear();
    }
    _isSyncingForm = false;
  }

  Future<void> _generateListing() async {
    setState(() => _isGenerating = true);
    try {
      final listing = await ref
          .read(unitsRepositoryProvider)
          .generateListingWorkspace(widget.dashboard.unit.id);
      _loadedContentVersion = null;
      _syncFromListing(listing);
      ref.invalidate(unitListingWorkspaceProvider(widget.dashboard.unit.id));
      _showSnack('Listing workspace prepared.');
    } catch (e) {
      _showSnack(e is ApiException ? e.message : e.toString());
    } finally {
      if (mounted) setState(() => _isGenerating = false);
    }
  }

  Future<void> _saveListing({bool markPublished = false}) async {
    if (!_canSave) {
      _showSnack('Headline, description, rent, and deposit must be valid.');
      return;
    }
    setState(() => _isSaving = true);
    try {
      final listing = await ref
          .read(unitsRepositoryProvider)
          .saveListingWorkspace(
            widget.dashboard.unit.id,
            _buildSaveRequest(markPublished: markPublished),
          );
      _loadedContentVersion = null;
      _syncFromListing(listing);
      ref.invalidate(unitListingWorkspaceProvider(widget.dashboard.unit.id));
      ref.invalidate(unitDashboardProvider(widget.dashboard.unit.id));
      _showSnack(
        markPublished
            ? 'Current version marked published in Zillow.'
            : 'Listing workspace saved.',
      );
    } catch (e) {
      _showSnack(e is ApiException ? e.message : e.toString());
    } finally {
      if (mounted) setState(() => _isSaving = false);
    }
  }

  SaveListingWorkspaceRequest _buildSaveRequest({bool markPublished = false}) {
    final workspace = ref
        .read(unitListingWorkspaceProvider(widget.dashboard.unit.id))
        .value;
    final guided = workspace == null ? null : _guidedPublication(workspace);
    final currentExternalStatus = guided?.lastConfirmedExternalStatus ?? '';
    final enteredExternalStatus = _externalStatusController.text.trim();
    return SaveListingWorkspaceRequest(
      status: _status,
      headline: _headlineController.text.trim(),
      description: _descriptionController.text.trim(),
      rent: _parseOptionalNumber(_rentController.text),
      securityDeposit: _parseOptionalNumber(_depositController.text),
      leaseTerms: _leaseTermsController.text.trim(),
      petPolicy: _petPolicyController.text.trim(),
      utilities: _utilitiesController.text.trim(),
      parking: _parkingController.text.trim(),
      amenities: _amenitiesController.text.trim(),
      zillowGuided: SaveGuidedPublicationRequest(
        status: _publicationStatus,
        externalListingId: _externalListingIdController.text.trim(),
        listingUrl: _zillowListingUrlController.text.trim(),
        applicationUrl: _zillowApplicationUrlController.text.trim(),
        lastConfirmedExternalStatus: enteredExternalStatus,
        lastConfirmedAtUtc:
            enteredExternalStatus.isNotEmpty &&
                enteredExternalStatus != currentExternalStatus
            ? DateTime.now().toUtc()
            : null,
        copyConfirmed: _copyConfirmed,
        termsConfirmed: _termsConfirmed,
        photosConfirmed: _photosConfirmed,
        providerWorkspaceOpened: guided?.providerWorkspaceOpened ?? false,
        markCurrentVersionPublished: markPublished,
      ),
    );
  }

  Future<void> _openZillowWorkspace() async {
    final listing = ref
        .read(unitListingWorkspaceProvider(widget.dashboard.unit.id))
        .value;
    final guided = listing == null ? null : _guidedPublication(listing);
    await _openExternalUrl(guided?.managementUrl ?? _zillowRentalManagerUrl);
    if (listing == null) return;
    try {
      final saved = await ref
          .read(unitsRepositoryProvider)
          .saveListingWorkspace(
            widget.dashboard.unit.id,
            const SaveListingWorkspaceRequest(
              zillowGuided: SaveGuidedPublicationRequest(
                providerWorkspaceOpened: true,
              ),
            ),
          );
      _loadedContentVersion = null;
      _syncFromListing(saved);
      ref.invalidate(unitListingWorkspaceProvider(widget.dashboard.unit.id));
    } catch (_) {
      // Opening Zillow remains useful if recording the checklist step fails.
    }
  }

  Future<void> _confirmSignal(int signalId, bool accept) async {
    try {
      final saved = await ref
          .read(unitsRepositoryProvider)
          .confirmListingSignal(
            widget.dashboard.unit.id,
            signalId,
            accept: accept,
          );
      _loadedContentVersion = null;
      _syncFromListing(saved);
      ref.invalidate(unitListingWorkspaceProvider(widget.dashboard.unit.id));
      _showSnack(
        accept ? 'External update confirmed.' : 'External update ignored.',
      );
    } catch (e) {
      _showSnack(e is ApiException ? e.message : e.toString());
    }
  }

  Future<void> _runConnectedAction(String action) async {
    final listing = ref
        .read(unitListingWorkspaceProvider(widget.dashboard.unit.id))
        .value;
    final publication = listing == null ? null : _connectedPublication(listing);
    if (publication == null || !publication.channelAvailable) return;
    setState(() => _isSaving = true);
    try {
      final repository = ref.read(unitsRepositoryProvider);
      final saved = action == 'prepare'
          ? await repository.prepareConnectedListing(
              widget.dashboard.unit.id,
              publication.id,
            )
          : await repository.runConnectedListingCommand(
              widget.dashboard.unit.id,
              publication.id,
              action,
              '${DateTime.now().toUtc().microsecondsSinceEpoch}-${publication.id}',
            );
      _loadedContentVersion = null;
      _syncFromListing(saved);
      ref.invalidate(unitListingWorkspaceProvider(widget.dashboard.unit.id));
      _showSnack('Connected listing state updated.');
    } catch (e) {
      _showSnack(e is ApiException ? e.message : e.toString());
    } finally {
      if (mounted) setState(() => _isSaving = false);
    }
  }

  Future<void> _importSignedLease() {
    final listing = ref
        .read(unitListingWorkspaceProvider(widget.dashboard.unit.id))
        .value;
    return openMobileScan(
      context,
      initialTargetEntityType: 'LeaseAgreement',
      lockTargetEntityType: true,
      propertyId: widget.dashboard.unit.propertyId,
      unitId: widget.dashboard.unit.id,
      rentalListingId: listing?.id,
      sourceLabel: 'Zillow signed lease import',
    );
  }

  Future<void> _copyText(String label, String text) async {
    final value = text.trim();
    if (value.isEmpty) {
      _showSnack('$label is empty.');
      return;
    }
    await Clipboard.setData(ClipboardData(text: value));
    _showSnack('$label copied.');
  }

  Future<void> _openExternalUrl(String rawUrl) async {
    final url = rawUrl.trim();
    if (url.isEmpty) {
      _showSnack('URL is empty.');
      return;
    }
    final uri = Uri.tryParse(url);
    if (uri == null || !uri.hasScheme) {
      _showSnack('Enter a full URL first.');
      return;
    }
    final ok = await launchUrl(uri, mode: LaunchMode.externalApplication);
    if (!ok) _showSnack('Could not open URL.');
  }

  void _showSnack(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(message)));
  }

  static bool _isOptionalNumber(String value) {
    final trimmed = value.trim();
    return trimmed.isEmpty || double.tryParse(trimmed) != null;
  }

  static double? _parseOptionalNumber(String value) {
    final trimmed = value.trim();
    return trimmed.isEmpty ? null : double.tryParse(trimmed);
  }

  static String _numberToText(num? value) {
    if (value == null) return '';
    if (value % 1 == 0) return value.toInt().toString();
    return value.toString();
  }
}

class _ListingHeader extends StatelessWidget {
  const _ListingHeader({
    required this.status,
    required this.updatedAt,
    required this.onGenerate,
    required this.onSave,
    required this.isGenerating,
    required this.isSaving,
  });

  final String status;
  final DateTime updatedAt;
  final VoidCallback? onGenerate;
  final VoidCallback? onSave;
  final bool isGenerating;
  final bool isSaving;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Material(
      color: colorScheme.primaryContainer,
      borderRadius: BorderRadius.circular(18),
      clipBehavior: Clip.antiAlias,
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(
                  Symbols.real_estate_agent_rounded,
                  color: colorScheme.onPrimaryContainer,
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    'Listing workspace',
                    style: theme.textTheme.titleMedium?.copyWith(
                      color: colorScheme.onPrimaryContainer,
                      fontWeight: FontWeight.w800,
                    ),
                  ),
                ),
                _InfoChip(
                  icon: Symbols.sell_rounded,
                  label: plainEnglishLabel(status),
                ),
              ],
            ),
            const SizedBox(height: 8),
            Text(
              'Keep one listing for this unit, then use Guided Zillow now or Connected publishing after provider approval.',
              style: theme.textTheme.bodyMedium?.copyWith(
                color: colorScheme.onPrimaryContainer,
              ),
            ),
            const SizedBox(height: 4),
            Text(
              'Sync updates bedrooms, bathrooms, and square footage without replacing your copy, rent, deposit, or lease terms.',
              style: theme.textTheme.bodySmall?.copyWith(
                color: colorScheme.onPrimaryContainer.withValues(alpha: 0.78),
              ),
            ),
            const SizedBox(height: 4),
            Text(
              'Updated ${_formatDate(updatedAt)}',
              style: theme.textTheme.bodySmall?.copyWith(
                color: colorScheme.onPrimaryContainer.withValues(alpha: 0.78),
              ),
            ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                OutlinedButton.icon(
                  icon: isGenerating
                      ? const SizedBox.square(
                          dimension: 16,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Symbols.auto_awesome_rounded),
                  label: Text(
                    isGenerating ? 'Syncing...' : 'Sync unit details',
                  ),
                  onPressed: onGenerate,
                ),
                FilledButton.icon(
                  icon: isSaving
                      ? const SizedBox.square(
                          dimension: 16,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Symbols.save_rounded),
                  label: Text(isSaving ? 'Saving...' : 'Save listing'),
                  onPressed: onSave,
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _ListingField extends StatelessWidget {
  const _ListingField({
    required this.label,
    required this.controller,
    this.keyboardType,
    this.maxLines = 1,
    this.actionIcon,
    this.onAction,
  });

  final String label;
  final TextEditingController controller;
  final TextInputType? keyboardType;
  final int maxLines;
  final IconData? actionIcon;
  final VoidCallback? onAction;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 10, 16, 10),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          Expanded(
            child: TextField(
              controller: controller,
              keyboardType: keyboardType,
              maxLines: maxLines,
              decoration: InputDecoration(labelText: label),
            ),
          ),
          if (actionIcon != null && onAction != null) ...[
            const SizedBox(width: 8),
            IconButton.filledTonal(
              tooltip: label,
              icon: Icon(actionIcon),
              onPressed: onAction,
            ),
          ],
        ],
      ),
    );
  }
}

class _ListingStatusRow extends StatelessWidget {
  const _ListingStatusRow({
    required this.label,
    required this.value,
    required this.options,
    required this.onChanged,
  });

  final String label;
  final String value;
  final List<String> options;
  final ValueChanged<String> onChanged;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 10, 16, 10),
      child: DropdownButtonFormField<String>(
        initialValue: options.contains(value) ? value : options.first,
        decoration: InputDecoration(labelText: label),
        items: [
          for (final option in options)
            DropdownMenuItem<String>(
              value: option,
              child: Text(plainEnglishLabel(option)),
            ),
        ],
        onChanged: (value) {
          if (value != null) onChanged(value);
        },
      ),
    );
  }
}

class _ListingWarning extends StatelessWidget {
  const _ListingWarning({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    return Material(
      color: colors.tertiaryContainer,
      borderRadius: BorderRadius.circular(16),
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(Symbols.warning_rounded, color: colors.onTertiaryContainer),
            const SizedBox(width: 10),
            Expanded(
              child: Text(
                message,
                style: TextStyle(color: colors.onTertiaryContainer),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _ListingPhotoPackage extends StatelessWidget {
  const _ListingPhotoPackage({
    required this.photos,
    required this.busy,
    required this.onAddOrReplace,
    required this.onRemove,
    required this.onMove,
  });

  final List<ListingPhoto> photos;
  final bool busy;
  final ValueChanged<ListingPhoto> onAddOrReplace;
  final ValueChanged<ListingPhoto> onRemove;
  final void Function(ListingPhoto, int) onMove;

  @override
  Widget build(BuildContext context) {
    return _Section(
      title: 'Ordered photo package',
      empty: 'No photo checklist was prepared',
      children: [
        // The API returns this manifest ordered DB-side by Position.
        for (var index = 0; index < photos.length; index++)
          ListTile(
            leading: Icon(
              photos[index].storedFileId == null
                  ? Symbols.add_a_photo_rounded
                  : Symbols.check_circle_rounded,
            ),
            title: Text('${photos[index].position}. ${photos[index].category}'),
            subtitle: Text(
              photos[index].fileName ??
                  photos[index].caption ??
                  'Photo needed in this position',
            ),
            trailing: Wrap(
              children: [
                IconButton(
                  onPressed: busy || index == 0
                      ? null
                      : () => onMove(photos[index], -1),
                  icon: const Icon(Symbols.arrow_upward_rounded),
                ),
                IconButton(
                  onPressed: busy || index == photos.length - 1
                      ? null
                      : () => onMove(photos[index], 1),
                  icon: const Icon(Symbols.arrow_downward_rounded),
                ),
                IconButton(
                  onPressed: busy ? null : () => onAddOrReplace(photos[index]),
                  icon: const Icon(Symbols.add_a_photo_rounded),
                ),
                if (photos[index].storedFileId != null)
                  IconButton(
                    onPressed: busy ? null : () => onRemove(photos[index]),
                    icon: const Icon(Symbols.delete_outline_rounded),
                  ),
              ],
            ),
          ),
      ],
    );
  }
}

class _ListingChecklist extends StatelessWidget {
  const _ListingChecklist({
    required this.copyConfirmed,
    required this.termsConfirmed,
    required this.photosConfirmed,
    required this.onCopyChanged,
    required this.onTermsChanged,
    required this.onPhotosChanged,
  });

  final bool copyConfirmed;
  final bool termsConfirmed;
  final bool photosConfirmed;
  final ValueChanged<bool> onCopyChanged;
  final ValueChanged<bool> onTermsChanged;
  final ValueChanged<bool> onPhotosChanged;

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        CheckboxListTile(
          value: copyConfirmed,
          onChanged: (value) => onCopyChanged(value ?? false),
          title: const Text('Listing copy entered in Zillow'),
          controlAffinity: ListTileControlAffinity.leading,
        ),
        CheckboxListTile(
          value: termsConfirmed,
          onChanged: (value) => onTermsChanged(value ?? false),
          title: const Text('Rent and terms reviewed in Zillow'),
          controlAffinity: ListTileControlAffinity.leading,
        ),
        CheckboxListTile(
          value: photosConfirmed,
          onChanged: (value) => onPhotosChanged(value ?? false),
          title: const Text('Photos uploaded in the prepared order'),
          controlAffinity: ListTileControlAffinity.leading,
        ),
      ],
    );
  }
}

class _ListingConnectedCard extends StatelessWidget {
  const _ListingConnectedCard({
    required this.publication,
    required this.busy,
    required this.onAction,
  });

  final ListingPublication? publication;
  final bool busy;
  final Future<void> Function(String action) onAction;

  @override
  Widget build(BuildContext context) {
    return _Section(
      title: 'Automatic posting',
      empty: 'No automatic posting status',
      children: [
        const Padding(
          padding: EdgeInsets.fromLTRB(16, 14, 16, 8),
          child: Text(
            'An approved provider adapter publishes the same canonical listing used by Guided mode. It never creates a second listing.',
          ),
        ),
        _CompactRow(
          icon: publication?.channelAvailable == true
              ? Symbols.check_circle_rounded
              : Symbols.cloud_off_rounded,
          title: plainEnglishLabel(publication?.channelState ?? 'Unavailable'),
          subtitle:
              publication?.lastDeliveryError ??
              publication?.channelUnavailableReason ??
              'Guided publishing remains available.',
        ),
        if (publication?.channelAvailable == true)
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 8, 16, 16),
            child: Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                OutlinedButton.icon(
                  onPressed: busy ? null : () => onAction('prepare'),
                  icon: const Icon(Symbols.description_rounded),
                  label: const Text('Prepare'),
                ),
                if (publication?.status == 'Ready')
                  FilledButton.icon(
                    onPressed: busy ? null : () => onAction('publish'),
                    icon: const Icon(Symbols.publish_rounded),
                    label: const Text('Publish'),
                  ),
                if (publication?.status == 'Published') ...[
                  OutlinedButton.icon(
                    onPressed: busy || publication?.needsRepublish != true
                        ? null
                        : () => onAction('update'),
                    icon: const Icon(Symbols.sync_rounded),
                    label: const Text('Send updates'),
                  ),
                  OutlinedButton.icon(
                    onPressed: busy ? null : () => onAction('unpublish'),
                    icon: const Icon(Symbols.delete_outline_rounded),
                    label: const Text('Remove'),
                  ),
                ],
              ],
            ),
          ),
      ],
    );
  }
}

class _ListingSignalsSection extends StatelessWidget {
  const _ListingSignalsSection({
    required this.signals,
    required this.onDecision,
  });

  final List<ExternalListingSignal> signals;
  final Future<void> Function(int signalId, bool accept) onDecision;

  @override
  Widget build(BuildContext context) {
    return _Section(
      title: 'External updates to confirm',
      empty: 'No external updates',
      children: [
        const Padding(
          padding: EdgeInsets.fromLTRB(16, 14, 16, 4),
          child: Text(
            'These are untrusted hints. Rental Command will not change the listing until you confirm one.',
          ),
        ),
        for (final signal in signals)
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
            child: Material(
              color: Theme.of(context).colorScheme.surfaceContainerLow,
              borderRadius: BorderRadius.circular(14),
              child: Padding(
                padding: const EdgeInsets.all(14),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      signal.signalType,
                      style: Theme.of(context).textTheme.titleSmall,
                    ),
                    const SizedBox(height: 4),
                    Text(
                      signal.suggestedExternalStatus ??
                          signal.suggestedListingUrl ??
                          signal.suggestedExternalListingId ??
                          'External listing metadata changed.',
                    ),
                    const SizedBox(height: 10),
                    Wrap(
                      spacing: 8,
                      children: [
                        FilledButton(
                          onPressed: () => onDecision(signal.id, true),
                          child: const Text('Confirm'),
                        ),
                        OutlinedButton(
                          onPressed: () => onDecision(signal.id, false),
                          child: const Text('Ignore'),
                        ),
                      ],
                    ),
                  ],
                ),
              ),
            ),
          ),
      ],
    );
  }
}

class _UnitTenantLeaseSurface extends StatelessWidget {
  const _UnitTenantLeaseSurface({
    required this.dashboard,
    required this.activeView,
    this.selectedLeaseManagementId,
    this.selectedTenantId,
  });

  final UnitDashboard dashboard;
  final ValueListenable<UnitCommandCenterView?> activeView;
  final int? selectedLeaseManagementId;
  final int? selectedTenantId;

  @override
  Widget build(BuildContext context) {
    return ValueListenableBuilder<UnitCommandCenterView?>(
      valueListenable: activeView,
      builder: (context, requestedView, _) =>
          _buildSurface(context, requestedView),
    );
  }

  Widget _buildSurface(
    BuildContext context,
    UnitCommandCenterView? requestedView,
  ) {
    final summary = dashboard.currentLease;
    final managementId =
        selectedLeaseManagementId ??
        dashboard.leaseManagementId ??
        (summary == null || summary.leaseManagementId <= 0
            ? null
            : summary.leaseManagementId);

    final residentsFirst = requestedView == UnitCommandCenterView.residents;
    final agreementSection = _UnitAgreementSection(dashboard: dashboard);
    final residentsSection = _UnitResidentsSection(
      dashboard: dashboard,
      selectedTenantId: selectedTenantId,
    );

    if (managementId == null) {
      return ListView(
        key: ValueKey('unit-tenant-lease-${dashboard.unit.id}-$requestedView'),
        padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
        children: residentsFirst
            ? [residentsSection, const SizedBox(height: 20), agreementSection]
            : [agreementSection, const SizedBox(height: 20), residentsSection],
      );
    }

    const agreementHeading = _UnitTenantLeaseSectionHeading(
      key: ValueKey('unit-agreement-section'),
      title: 'Agreement',
    );
    final residentsContent = KeyedSubtree(
      key: const ValueKey('unit-residents-section'),
      child: residentsSection,
    );
    return LeaseManagementDetailScreen(
      key: ValueKey('unit-tenant-lease-$managementId-$requestedView'),
      leaseManagementId: managementId,
      leadingContent: residentsFirst
          ? Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                residentsContent,
                const SizedBox(height: 20),
                agreementHeading,
              ],
            )
          : agreementHeading,
      trailingContent: residentsFirst ? null : residentsContent,
    );
  }
}

class _UnitAgreementSection extends ConsumerWidget {
  const _UnitAgreementSection({required this.dashboard});

  final UnitDashboard dashboard;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final auth = ref.watch(authControllerProvider);
    final canPrepareAgreement =
        auth is AuthStateAuthenticated &&
        canUseMobileCapabilityAction(
          experience: auth.activeExperience,
          capabilities: auth.capabilities,
          capability: 'leasing.agreements.prepare',
          experiences: const {
            WorkspaceExperience.management,
            WorkspaceExperience.leasing,
          },
        );

    return Column(
      key: const ValueKey('unit-agreement-section'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const _UnitTenantLeaseSectionHeading(title: 'Agreement'),
        const SizedBox(height: 10),
        _SurfacePanel(
          children: [
            const Padding(
              padding: EdgeInsets.fromLTRB(16, 16, 16, 8),
              child: Text(
                'No tenancy',
                style: TextStyle(fontWeight: FontWeight.w700),
              ),
            ),
            const Padding(
              padding: EdgeInsets.fromLTRB(16, 0, 16, 16),
              child: Text(
                'Approve an application and prepare move-in, or scan an existing signed lease.',
              ),
            ),
            if (canPrepareAgreement)
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
                child: Align(
                  alignment: Alignment.centerRight,
                  child: _UnitLeaseAddAction(
                    onPressed: () => openMobileScan(
                      context,
                      initialTargetEntityType: 'LeaseAgreement',
                      lockTargetEntityType: true,
                      propertyId: dashboard.unit.propertyId,
                      unitId: dashboard.unit.id,
                      leaseManagementId: dashboard.leaseManagementId,
                      leaseAgreementId: dashboard.currentLease?.id,
                      tenantAccountId: dashboard.tenantAccountId,
                      sourceLabel: 'Unit · Tenant & lease',
                    ),
                  ),
                ),
              ),
          ],
        ),
      ],
    );
  }
}

class _UnitTenantLeaseSectionHeading extends StatelessWidget {
  const _UnitTenantLeaseSectionHeading({super.key, required this.title});

  final String title;

  @override
  Widget build(BuildContext context) {
    return Text(title, style: Theme.of(context).textTheme.titleLarge);
  }
}

class _UnitLeaseAddAction extends StatelessWidget {
  const _UnitLeaseAddAction({required this.onPressed});

  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.end,
      children: [
        if (onPressed != null)
          FilledButton.icon(
            icon: const Icon(Icons.add),
            label: const Text('Scan signed lease'),
            onPressed: onPressed,
          ),
      ],
    );
  }
}

class _UnitApplicationsTab extends ConsumerStatefulWidget {
  const _UnitApplicationsTab({required this.unitId, this.application});

  final int unitId;
  final RentalApplication? application;

  @override
  ConsumerState<_UnitApplicationsTab> createState() =>
      _UnitApplicationsTabState();
}

class _UnitApplicationsTabState extends ConsumerState<_UnitApplicationsTab> {
  static const _pageSize = 20;
  final _searchController = TextEditingController();
  String? _search;
  String _sort = '-submittedAt';
  int _skip = 0;
  int? _selectedApplicationId;

  @override
  void initState() {
    super.initState();
    _selectedApplicationId = widget.application?.id;
  }

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  ApplicationListQuery get _query => ApplicationListQuery(
    skip: _skip,
    take: _pageSize,
    unitId: widget.unitId,
    search: _search,
    sort: _sort,
  );

  void _submitSearch([String? value]) {
    final next = (value ?? _searchController.text).trim();
    setState(() {
      _search = next.isEmpty ? null : next;
      _skip = 0;
    });
  }

  void _openDetail(int applicationId) {
    setState(() => _selectedApplicationId = applicationId);
  }

  Future<void> _retry() async {
    ref.invalidate(applicationsPageProvider(_query));
  }

  @override
  Widget build(BuildContext context) {
    final selectedApplicationId = _selectedApplicationId;
    if (selectedApplicationId != null) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Align(
            alignment: Alignment.centerLeft,
            child: TextButton.icon(
              key: const ValueKey('unit-applications-back'),
              onPressed: () => setState(() => _selectedApplicationId = null),
              icon: const Icon(Icons.arrow_back),
              label: const Text('Back to applications'),
            ),
          ),
          Expanded(
            child: ApplicationDetailScreen(
              applicationId: selectedApplicationId,
            ),
          ),
        ],
      );
    }

    final pageAsync = ref.watch(applicationsPageProvider(_query));
    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 12, 16, 8),
          child: LayoutBuilder(
            builder: (context, constraints) {
              final searchField = TextField(
                key: const ValueKey('unit-applications-search'),
                controller: _searchController,
                decoration: InputDecoration(
                  labelText: 'Search applications',
                  suffixIcon: IconButton(
                    tooltip: 'Search',
                    onPressed: _submitSearch,
                    icon: const Icon(Icons.search),
                  ),
                ),
                textInputAction: TextInputAction.search,
                onSubmitted: _submitSearch,
              );
              final sortControl = DropdownButton<String>(
                key: const ValueKey('unit-applications-sort'),
                value: _sort,
                items: const [
                  DropdownMenuItem(
                    value: '-submittedAt',
                    child: Text('Newest'),
                  ),
                  DropdownMenuItem(value: 'submittedAt', child: Text('Oldest')),
                  DropdownMenuItem(value: 'name', child: Text('Name')),
                  DropdownMenuItem(value: 'status', child: Text('Status')),
                ],
                onChanged: (value) {
                  if (value == null) return;
                  setState(() {
                    _sort = value;
                    _skip = 0;
                  });
                },
              );

              if (constraints.maxWidth < 360) {
                return Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    searchField,
                    const SizedBox(height: 8),
                    Align(alignment: Alignment.centerRight, child: sortControl),
                  ],
                );
              }

              return Row(
                children: [
                  Expanded(child: searchField),
                  const SizedBox(width: 12),
                  sortControl,
                ],
              );
            },
          ),
        ),
        Expanded(
          child: pageAsync.when(
            loading: () => const Center(child: CircularProgressIndicator()),
            error: (error, _) => _UnitErrorBody(
              message: error is ApiException ? error.message : error.toString(),
              onRetry: _retry,
            ),
            data: (page) {
              if (page.items.isEmpty) {
                return _EmptyTab(
                  icon: Symbols.assignment_ind_rounded,
                  title: 'No applications',
                  body: _search == null
                      ? 'No applications are tied to this Unit.'
                      : 'No applications match "$_search".',
                );
              }
              return ListView(
                key: const PageStorageKey('unit-applications-page'),
                padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
                children: [
                  for (final app in page.items)
                    Card(
                      child: ListTile(
                        key: ValueKey('unit-application-${app.id}'),
                        leading: const Icon(Symbols.assignment_ind_rounded),
                        title: Text(app.fullName),
                        subtitle: Text(
                          app.submittedAtUtc == null
                              ? 'Not yet submitted'
                              : 'Submitted ${dateFmt(app.submittedAtUtc!.toLocal())}',
                        ),
                        trailing: ApplicationStatusChip(status: app.status),
                        onTap: () => _openDetail(app.id),
                      ),
                    ),
                  Wrap(
                    alignment: WrapAlignment.spaceBetween,
                    crossAxisAlignment: WrapCrossAlignment.center,
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                      OutlinedButton(
                        onPressed: page.hasPrevious
                            ? () => setState(() {
                                _skip = _skip <= _pageSize
                                    ? 0
                                    : _skip - _pageSize;
                              })
                            : null,
                        child: const Text('Previous'),
                      ),
                      Text(
                        'Page ${page.skip ~/ _pageSize + 1} · ${page.totalCount} total',
                      ),
                      OutlinedButton(
                        onPressed: page.hasNext
                            ? () => setState(() => _skip += _pageSize)
                            : null,
                        child: const Text('Next'),
                      ),
                    ],
                  ),
                ],
              );
            },
          ),
        ),
      ],
    );
  }
}

class _UnitLedgerTab extends ConsumerStatefulWidget {
  const _UnitLedgerTab({required this.dashboard, required this.activeView});

  final UnitDashboard dashboard;
  final ValueListenable<UnitCommandCenterView?> activeView;

  @override
  ConsumerState<_UnitLedgerTab> createState() => _UnitLedgerTabState();
}

class _UnitLedgerTabState extends ConsumerState<_UnitLedgerTab> {
  static const _pageSize = 10;
  final _tenantAccountKey = GlobalKey();
  final _operatingCostsKey = GlobalKey();
  int _unitExpensePage = 0;
  int _propertyExpensePage = 0;
  int _financingPage = 0;

  @override
  void initState() {
    super.initState();
    widget.activeView.addListener(_landOnAnchor);
  }

  @override
  void dispose() {
    widget.activeView.removeListener(_landOnAnchor);
    super.dispose();
  }

  void _landOnAnchor() {
    final key = widget.activeView.value == UnitCommandCenterView.operatingCosts
        ? _operatingCostsKey
        : _tenantAccountKey;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      final target = key.currentContext;
      if (mounted && target != null) {
        Scrollable.ensureVisible(target, duration: Duration.zero, alignment: 0);
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final dashboard = widget.dashboard;
    final propertyAsync = ref.watch(
      propertyDetailProvider(dashboard.unit.propertyId),
    );
    final isSingleRental =
        propertyAsync.value?.rentalStructure == RentalStructure.singleRental;
    final unitExpensesAsync = ref.watch(
      expensesPageProvider(
        ExpenseListQuery(
          unitId: dashboard.unit.id,
          skip: _unitExpensePage * _pageSize,
          take: _pageSize,
        ),
      ),
    );
    final propertyExpensesAsync = isSingleRental
        ? ref.watch(
            expensesPageProvider(
              ExpenseListQuery(
                operationalScope: 'Property',
                propertyId: dashboard.unit.propertyId,
                skip: _propertyExpensePage * _pageSize,
                take: _pageSize,
              ),
            ),
          )
        : null;
    final financingAsync = isSingleRental
        ? ref.watch(
            unitMoneyFinancingPageProvider((
              propertyId: dashboard.unit.propertyId,
              skip: _financingPage * _pageSize,
              take: _pageSize,
            )),
          )
        : null;

    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      children: [
        KeyedSubtree(
          key: _tenantAccountKey,
          child: TenantLedgerView(
            dashboard: dashboard,
            embedded: true,
            onScan: () => openMobileScan(
              context,
              initialTargetEntityType: 'Payment',
              lockTargetEntityType: true,
              propertyId: dashboard.unit.propertyId,
              unitId: dashboard.unit.id,
              leaseManagementId: dashboard.leaseManagementId,
              leaseAgreementId: dashboard.currentLease?.id,
              tenantAccountId: dashboard.tenantAccountId,
              sourceLabel: 'Unit · Tenant ledger',
            ),
            onSaved: () =>
                ref.invalidate(unitDashboardProvider(dashboard.unit.id)),
            onDepositTap: () => Navigator.of(context).push<void>(
              MaterialPageRoute<void>(builder: (_) => const DepositsScreen()),
            ),
          ),
        ),
        const SizedBox(height: 14),
        KeyedSubtree(
          key: _operatingCostsKey,
          child: unitExpensesAsync.when(
            loading: () => const _LoadingSection(title: 'Operating costs'),
            error: (e, _) => _RetrySection(
              title: 'Operating costs',
              message: e is ApiException ? e.message : e.toString(),
              onRetry: () => setState(() {}),
            ),
            data: (page) => _UnitExpensesSection(
              title: 'Operating costs',
              items: page.items,
              pager: _MoneyPager(
                page: _unitExpensePage,
                hasPrevious: page.hasPrevious,
                hasNext: page.hasNext,
                onPrevious: () => setState(() => _unitExpensePage--),
                onNext: () => setState(() => _unitExpensePage++),
              ),
            ),
          ),
        ),
        if (isSingleRental && propertyExpensesAsync != null) ...[
          const SizedBox(height: 14),
          propertyExpensesAsync.when(
            loading: () => const _LoadingSection(title: 'Property expenses'),
            error: (e, _) => _RetrySection(
              title: 'Property expenses',
              message: e is ApiException ? e.message : e.toString(),
              onRetry: () => setState(() {}),
            ),
            data: (page) => _UnitExpensesSection(
              title: 'Property expenses',
              items: page.items,
              pager: _MoneyPager(
                page: _propertyExpensePage,
                hasPrevious: page.hasPrevious,
                hasNext: page.hasNext,
                onPrevious: () => setState(() => _propertyExpensePage--),
                onNext: () => setState(() => _propertyExpensePage++),
              ),
            ),
          ),
        ],
        if (isSingleRental && financingAsync != null) ...[
          const SizedBox(height: 14),
          financingAsync.when(
            loading: () => const _LoadingSection(title: 'Financing'),
            error: (e, _) => _RetrySection(
              title: 'Financing',
              message: e is ApiException ? e.message : e.toString(),
              onRetry: () => setState(() {}),
            ),
            data: (page) => _Section(
              title: 'Financing',
              empty: 'No financing recorded',
              children: [
                for (final loan in page.items)
                  _CompactRow(
                    icon: Symbols.account_balance_rounded,
                    title: loan.lender,
                    subtitle:
                        '${moneyFmt(loan.currentBalance, whole: true)} balance · '
                        '${moneyFmt(loan.monthlyPrincipalInterest + loan.monthlyEscrow, whole: true)}/month',
                  ),
                _MoneyPager(
                  page: _financingPage,
                  hasPrevious: page.hasPrevious,
                  hasNext: page.hasNext,
                  onPrevious: () => setState(() => _financingPage--),
                  onNext: () => setState(() => _financingPage++),
                ),
              ],
            ),
          ),
        ],
      ],
    );
  }
}

class _UnitExpensesSection extends StatelessWidget {
  const _UnitExpensesSection({
    required this.title,
    required this.items,
    required this.pager,
  });

  final String title;
  final List<Expense> items;
  final Widget pager;

  @override
  Widget build(BuildContext context) {
    return _Section(
      title: title,
      empty: 'No unit expenses',
      children: [
        for (final item in items)
          _CompactRow(
            icon: Symbols.receipt_long_rounded,
            title: item.description.isEmpty
                ? 'Expense #${item.id}'
                : item.description,
            subtitle:
                '${item.status.label} · ${item.category.label} · ${_formatDate(item.incurredAt)} · ${moneyFmt(item.amount, whole: true)}',
            onTap: () => Navigator.of(context).push<void>(
              MaterialPageRoute<void>(
                builder: (_) => ExpenseDetailScreen(expenseId: item.id),
              ),
            ),
          ),
        pager,
      ],
    );
  }
}

class _MoneyPager extends StatelessWidget {
  const _MoneyPager({
    required this.page,
    required this.hasPrevious,
    required this.hasNext,
    required this.onPrevious,
    required this.onNext,
  });

  final int page;
  final bool hasPrevious;
  final bool hasNext;
  final VoidCallback onPrevious;
  final VoidCallback onNext;

  @override
  Widget build(BuildContext context) {
    if (!hasPrevious && !hasNext) return const SizedBox.shrink();
    return Padding(
      padding: const EdgeInsets.all(12),
      child: Row(
        children: [
          OutlinedButton(
            onPressed: hasPrevious ? onPrevious : null,
            child: const Text('Previous'),
          ),
          Expanded(
            child: Text('Page ${page + 1}', textAlign: TextAlign.center),
          ),
          OutlinedButton(
            onPressed: hasNext ? onNext : null,
            child: const Text('Next'),
          ),
        ],
      ),
    );
  }
}

class _UnitResidentsSection extends StatelessWidget {
  const _UnitResidentsSection({required this.dashboard, this.selectedTenantId});

  final UnitDashboard dashboard;
  final int? selectedTenantId;

  @override
  Widget build(BuildContext context) {
    final currentTenants = dashboard.currentTenants.isNotEmpty
        ? dashboard.currentTenants
        : <UnitTenantSummary>[
            if (dashboard.currentTenant != null) dashboard.currentTenant!,
          ];
    final selected = selectedTenantId;
    final orderedTenants = [...currentTenants]
      ..sort((left, right) {
        if (left.id == selected) return -1;
        if (right.id == selected) return 1;
        return left.name.compareTo(right.name);
      });
    final selectedIsMissing =
        selected != null &&
        selected > 0 &&
        !orderedTenants.any((tenant) => tenant.id == selected);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const _UnitTenantLeaseSectionHeading(title: 'Residents'),
        const SizedBox(height: 10),
        _Section(
          title: 'Tenants',
          empty: 'This unit is currently vacant.',
          children: [
            for (final tenant in orderedTenants)
              _CompactRow(
                icon: Symbols.person_rounded,
                title: tenant.name.isEmpty
                    ? 'Tenant #${tenant.id}'
                    : tenant.name,
                subtitle: tenant.email?.trim().isNotEmpty == true
                    ? tenant.email!.trim()
                    : (tenant.phone?.trim().isNotEmpty == true
                          ? tenant.phone!.trim()
                          : 'Tenant'),
                onTap: () => Navigator.of(context).push<void>(
                  MaterialPageRoute<void>(
                    builder: (_) =>
                        TenantDetailLoaderScreen(tenantId: tenant.id),
                  ),
                ),
              ),
            if (selectedIsMissing)
              _CompactRow(
                icon: Symbols.person_rounded,
                title: 'Tenant #$selected',
                subtitle: 'Tenant',
                onTap: () => Navigator.of(context).push<void>(
                  MaterialPageRoute<void>(
                    builder: (_) =>
                        TenantDetailLoaderScreen(tenantId: selected),
                  ),
                ),
              ),
          ],
        ),
      ],
    );
  }
}

class _UnitInspectionsTab extends ConsumerStatefulWidget {
  const _UnitInspectionsTab({required this.unitId});
  final int unitId;

  @override
  ConsumerState<_UnitInspectionsTab> createState() =>
      _UnitInspectionsTabState();
}

class _UnitInspectionsTabState extends ConsumerState<_UnitInspectionsTab> {
  static const _take = 20;
  var _skip = 0;
  var _search = '';
  var _sort = '-scheduledFor';

  @override
  Widget build(BuildContext context) {
    final query = InspectionListQuery(
      unitId: widget.unitId,
      skip: _skip,
      take: _take,
      search: _search,
      sort: _sort,
    );
    final page = ref.watch(inspectionsPageProvider(query));
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        TextField(
          decoration: const InputDecoration(
            labelText: 'Search Unit inspections',
            prefixIcon: Icon(Icons.search),
          ),
          onChanged: (value) => setState(() {
            _search = value.trim();
            _skip = 0;
          }),
        ),
        const SizedBox(height: 8),
        DropdownButtonFormField<String>(
          initialValue: _sort,
          decoration: const InputDecoration(labelText: 'Sort'),
          items: const [
            DropdownMenuItem(
              value: '-scheduledFor',
              child: Text('Newest scheduled'),
            ),
            DropdownMenuItem(
              value: 'scheduledFor',
              child: Text('Oldest scheduled'),
            ),
            DropdownMenuItem(value: 'status', child: Text('Status')),
          ],
          onChanged: (value) => setState(() {
            _sort = value ?? '-scheduledFor';
            _skip = 0;
          }),
        ),
        const SizedBox(height: 12),
        page.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (error, _) => Text('Inspections could not be loaded: $error'),
          data: (result) => Column(
            children: [
              if (result.items.isEmpty)
                const ListTile(title: Text('No inspections for this Unit.'))
              else
                for (final inspection in result.items)
                  ListTile(
                    key: ValueKey('unit-inspection-${inspection.id}'),
                    title: Text(plainEnglishLabel(inspection.type)),
                    subtitle: Text(
                      '${plainEnglishLabel(inspection.status)} · ${_formatDate(inspection.scheduledFor)}',
                    ),
                  ),
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  OutlinedButton(
                    onPressed: _skip == 0
                        ? null
                        : () => setState(
                            () => _skip = (_skip - _take)
                                .clamp(0, 100000)
                                .toInt(),
                          ),
                    child: const Text('Previous'),
                  ),
                  Text('${result.totalCount} total'),
                  OutlinedButton(
                    onPressed: _skip + result.items.length >= result.totalCount
                        ? null
                        : () => setState(() => _skip += _take),
                    child: const Text('Next'),
                  ),
                ],
              ),
            ],
          ),
        ),
      ],
    );
  }
}

class _UnitRecurringMaintenanceTab extends ConsumerStatefulWidget {
  const _UnitRecurringMaintenanceTab({required this.unitId});
  final int unitId;

  @override
  ConsumerState<_UnitRecurringMaintenanceTab> createState() =>
      _UnitRecurringMaintenanceTabState();
}

class _UnitRecurringMaintenanceTabState
    extends ConsumerState<_UnitRecurringMaintenanceTab> {
  static const _take = 20;
  var _skip = 0;
  var _search = '';
  var _sort = 'nextDueDate';

  @override
  Widget build(BuildContext context) {
    final query = RecurringMaintenanceListQuery(
      unitId: widget.unitId,
      skip: _skip,
      take: _take,
      search: _search,
      sort: _sort,
    );
    final page = ref.watch(unitRecurringMaintenancePageProvider(query));
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        TextField(
          decoration: const InputDecoration(
            labelText: 'Search Unit recurring maintenance',
            prefixIcon: Icon(Icons.search),
          ),
          onChanged: (value) => setState(() {
            _search = value.trim();
            _skip = 0;
          }),
        ),
        const SizedBox(height: 8),
        DropdownButtonFormField<String>(
          initialValue: _sort,
          decoration: const InputDecoration(labelText: 'Sort'),
          items: const [
            DropdownMenuItem(value: 'nextDueDate', child: Text('Next due')),
            DropdownMenuItem(value: '-nextDueDate', child: Text('Latest due')),
            DropdownMenuItem(value: 'title', child: Text('Title')),
          ],
          onChanged: (value) => setState(() {
            _sort = value ?? 'nextDueDate';
            _skip = 0;
          }),
        ),
        const SizedBox(height: 12),
        page.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (error, _) =>
              Text('Recurring maintenance could not be loaded: $error'),
          data: (result) => Column(
            children: [
              if (result.items.isEmpty)
                const ListTile(
                  title: Text('No recurring maintenance for this Unit.'),
                )
              else
                for (final task in result.items)
                  ListTile(
                    key: ValueKey('unit-recurring-${task.id}'),
                    title: Text(task.title),
                    subtitle: Text(
                      '${task.recurrenceInterval} · Due ${_formatDate(task.nextDueDate)}',
                    ),
                  ),
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  OutlinedButton(
                    onPressed: _skip == 0
                        ? null
                        : () => setState(
                            () => _skip = (_skip - _take)
                                .clamp(0, 100000)
                                .toInt(),
                          ),
                    child: const Text('Previous'),
                  ),
                  Text('${result.totalCount} total'),
                  OutlinedButton(
                    onPressed: _skip + result.items.length >= result.totalCount
                        ? null
                        : () => setState(() => _skip += _take),
                    child: const Text('Next'),
                  ),
                ],
              ),
            ],
          ),
        ),
      ],
    );
  }
}

class _UnitTurnoverTab extends StatelessWidget {
  const _UnitTurnoverTab({required this.dashboard});

  final UnitDashboard dashboard;

  @override
  Widget build(BuildContext context) {
    final turnover = dashboard.turnover;

    return SingleChildScrollView(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text('Getting it ready', style: Theme.of(context).textTheme.titleLarge),
          const SizedBox(height: 12),
          _SurfacePanel(
            children: [
              _MetricRow(
                icon: Symbols.construction_rounded,
                label: 'Status',
                value: plainEnglishLabel(turnover.status),
              ),
              _MetricRow(
                icon: Symbols.task_alt_rounded,
                label: 'Punch list',
                value:
                    '${turnover.openTaskCount} open / ${turnover.completedTaskCount} done',
              ),
              _MetricRow(
                icon: Symbols.schedule_rounded,
                label: 'Turn time',
                value: turnover.daysInTurnover == null
                    ? 'Not started'
                    : '${turnover.daysInTurnover}d',
              ),
              _MetricRow(
                icon: Symbols.event_available_rounded,
                label: 'Target ready',
                value: turnover.targetReadyDate == null
                    ? 'Not set'
                    : _formatDate(turnover.targetReadyDate!),
              ),
              _MetricRow(
                icon: Symbols.receipt_long_rounded,
                label: 'Budget / actual',
                value:
                    '${moneyFmt(turnover.estimatedCost, whole: true)} / ${moneyFmt(turnover.actualCost, whole: true)}',
              ),
              _MetricRow(
                icon: Symbols.folder_open_rounded,
                label: 'Receipts',
                value: '${turnover.receiptCount}',
              ),
            ],
          ),
          const SizedBox(height: 14),
          _Section(
            title: 'Getting it ready plan',
            empty: '',
            children: const [
              _CompactRow(
                icon: Symbols.logout_rounded,
                title: 'Move-out',
                subtitle: 'Walkthrough, photos, keys, and vacancy handoff',
              ),
              _CompactRow(
                icon: Symbols.format_list_bulleted_rounded,
                title: 'Punch list',
                subtitle: 'Cleanout, paint, repairs, re-key, appliances',
              ),
              _CompactRow(
                icon: Symbols.verified_rounded,
                title: 'Rent-ready',
                subtitle: 'Close jobs, attach receipts, then list the unit',
              ),
            ],
          ),
          const SizedBox(height: 14),
          _WorkOrdersSection(
            items: dashboard.overview.openWorkOrders,
            openFullDetail: true,
          ),
        ],
      ),
    );
  }
}

class _UnitWorkTab extends StatelessWidget {
  const _UnitWorkTab({required this.dashboard, this.selectedWorkOrder});

  final UnitDashboard dashboard;
  final WorkOrder? selectedWorkOrder;

  @override
  Widget build(BuildContext context) {
    final selected = selectedWorkOrder;

    if (selected != null) {
      return WorkOrderDetailScreen(workOrderId: selected.id);
    }

    return SingleChildScrollView(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      child: Column(
        children: [
          _WorkOrdersSection(
            items: dashboard.overview.openWorkOrders,
            openFullDetail: true,
          ),
        ],
      ),
    );
  }
}

class _UnitDocumentsTab extends StatelessWidget {
  const _UnitDocumentsTab({required this.dashboard});

  final UnitDashboard dashboard;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Align(
            alignment: Alignment.centerLeft,
            child: FilledButton.icon(
              icon: const Icon(Symbols.document_scanner_rounded),
              label: const Text('Scan document'),
              onPressed: () => openMobileScan(
                context,
                propertyId: dashboard.unit.propertyId,
                unitId: dashboard.unit.id,
                leaseManagementId: dashboard.leaseManagementId,
                leaseAgreementId: dashboard.currentLease?.id,
                tenantAccountId: dashboard.tenantAccountId,
                sourceLabel: 'Unit · Documents',
              ),
            ),
          ),
          const SizedBox(height: 14),
          _DocumentsSection(items: dashboard.overview.pendingDocs),
        ],
      ),
    );
  }
}

class _PaymentsSection extends StatelessWidget {
  const _PaymentsSection({required this.items});

  final List<UnitPaymentSummary> items;

  @override
  Widget build(BuildContext context) {
    return _Section(
      title: 'Payments',
      empty: 'No recent payments',
      children: [
        for (final item in items)
          _CompactRow(
            icon: Symbols.receipt_long_rounded,
            title:
                '${plainEnglishLabel(item.type)} · ${moneyFmt(item.amount, whole: true)}',
            subtitle:
                '${plainEnglishLabel(item.status)} · Due ${_formatDate(item.dueDate)}',
            onTap: () => Navigator.of(context).push<void>(
              MaterialPageRoute<void>(
                builder: (_) => PaymentDetailScreen(
                  tenantAccountId: item.tenantAccountId,
                  tenantLedgerEntryId: item.id,
                ),
              ),
            ),
          ),
      ],
    );
  }
}

class _WorkOrdersSection extends StatelessWidget {
  const _WorkOrdersSection({required this.items, this.openFullDetail = false});

  final List<UnitWorkOrderSummary> items;
  final bool openFullDetail;

  @override
  Widget build(BuildContext context) {
    return _Section(
      title: 'Repairs',
      empty: 'No open repairs',
      children: [
        for (final item in items)
          _CompactRow(
            icon: Symbols.build_rounded,
            title: item.title.isEmpty ? 'Repair #${item.id}' : item.title,
            subtitle:
                '${plainEnglishLabel(item.priority, fallback: 'Priority')} · '
                '${plainEnglishLabel(item.status, fallback: 'Open')}',
            onTap: openFullDetail
                ? () => Navigator.of(context).push<void>(
                    MaterialPageRoute<void>(
                      builder: (_) =>
                          WorkOrderDetailScreen(workOrderId: item.id),
                    ),
                  )
                : null,
          ),
      ],
    );
  }
}

class _DocumentsSection extends StatelessWidget {
  const _DocumentsSection({required this.items});

  final List<UnitDocumentSummary> items;

  @override
  Widget build(BuildContext context) {
    return _Section(
      title: 'Documents',
      empty: 'No documents needing review',
      children: [
        for (final item in items)
          _CompactRow(
            icon: Symbols.folder_open_rounded,
            title: item.fileName.isEmpty
                ? 'Document #${item.id}'
                : item.fileName,
            subtitle: item.entityType?.isNotEmpty == true
                ? '${plainEnglishLabel(item.entityType ?? '')} · ${_formatDate(item.uploadedAt)}'
                : _formatDate(item.uploadedAt),
          ),
      ],
    );
  }
}

class _AppointmentsSection extends StatelessWidget {
  const _AppointmentsSection({required this.items});

  final List<UnitAppointmentSummary> items;

  @override
  Widget build(BuildContext context) {
    return _Section(
      title: 'Appointments',
      empty: 'No upcoming appointments',
      children: [
        for (final item in items)
          _CompactRow(
            icon: Symbols.event_rounded,
            title: item.title.isEmpty ? 'Appointment #${item.id}' : item.title,
            subtitle:
                '${plainEnglishLabel(item.status.isEmpty ? item.type : item.status)} · '
                '${_formatDate(item.scheduledStart)}',
          ),
      ],
    );
  }
}

class _ActionPanel extends StatelessWidget {
  const _ActionPanel({
    required this.label,
    required this.href,
    required this.currentUnitId,
  });

  final String label;
  final String href;
  final int currentUnitId;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Material(
      color: colorScheme.primaryContainer,
      borderRadius: BorderRadius.circular(16),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: () => _openHref(context),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
          child: Row(
            children: [
              Icon(Symbols.bolt_rounded, color: colorScheme.onPrimaryContainer),
              const SizedBox(width: 12),
              Expanded(
                child: Text(
                  label,
                  style: Theme.of(context).textTheme.titleSmall?.copyWith(
                    color: colorScheme.onPrimaryContainer,
                    fontWeight: FontWeight.w700,
                  ),
                ),
              ),
              Icon(
                Symbols.chevron_right_rounded,
                size: 20,
                color: colorScheme.onPrimaryContainer,
              ),
            ],
          ),
        ),
      ),
    );
  }

  void _openHref(BuildContext context) {
    final target = parseUnitCommandCenterRoute(href);
    if (target == null) return;

    if (target.unitId == currentUnitId) {
      _UnitViewScope.maybeOf(context)?.value = target.initialView;
      DefaultTabController.of(context).animateTo(target.initialTab.index);
      return;
    }

    Widget detailBuilder(BuildContext _) => UnitCommandCenterLoaderScreen(
      unitId: target.unitId,
      initialTab: target.initialTab,
      initialView: target.initialView,
    );

    final shellNavigator = mobileShellNavigatorOf(context);
    if (shellNavigator != null) {
      shellNavigator.openTab(
        MobileShellTabId.rentals,
        destination: MobileDestinationId.units,
        detailBuilder: detailBuilder,
      );
      revealMobileShellIfDetached(context);
      return;
    }

    Navigator.of(
      context,
    ).push<void>(MaterialPageRoute<void>(builder: detailBuilder));
  }
}

class _SurfacePanel extends StatelessWidget {
  const _SurfacePanel({required this.children});

  final List<Widget> children;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Material(
      color: colorScheme.surfaceContainerHigh,
      borderRadius: BorderRadius.circular(18),
      clipBehavior: Clip.antiAlias,
      child: Column(children: children),
    );
  }
}

class _LoadingSection extends StatelessWidget {
  const _LoadingSection({required this.title});

  final String title;

  @override
  Widget build(BuildContext context) {
    return _Section(
      title: title,
      empty: '',
      children: const [
        Padding(
          padding: EdgeInsets.all(16),
          child: Center(child: CircularProgressIndicator()),
        ),
      ],
    );
  }
}

class _RetrySection extends StatelessWidget {
  const _RetrySection({
    required this.title,
    required this.message,
    required this.onRetry,
  });

  final String title;
  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    return _Section(
      title: title,
      empty: '',
      children: [
        Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(message, style: Theme.of(context).textTheme.bodyMedium),
              const SizedBox(height: 10),
              OutlinedButton.icon(
                icon: const Icon(Icons.refresh),
                label: const Text('Retry'),
                onPressed: onRetry,
              ),
            ],
          ),
        ),
      ],
    );
  }
}

class _Section extends StatelessWidget {
  const _Section({
    required this.title,
    required this.children,
    required this.empty,
  });

  final String title;
  final List<Widget> children;
  final String empty;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.only(left: 2, bottom: 8),
          child: Row(
            children: [
              Expanded(
                child: Text(
                  title,
                  style: theme.textTheme.titleSmall?.copyWith(
                    fontWeight: FontWeight.w800,
                  ),
                ),
              ),
            ],
          ),
        ),
        Material(
          color: colorScheme.surfaceContainerHigh,
          borderRadius: BorderRadius.circular(18),
          clipBehavior: Clip.antiAlias,
          child: children.isEmpty
              ? Padding(
                  padding: const EdgeInsets.all(16),
                  child: Text(
                    empty,
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: colorScheme.onSurfaceVariant,
                    ),
                  ),
                )
              : Column(children: children),
        ),
      ],
    );
  }
}

class _MetricRow extends StatelessWidget {
  const _MetricRow({
    required this.icon,
    required this.label,
    required this.value,
  });

  final IconData icon;
  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 13),
      decoration: BoxDecoration(
        border: Border(
          top: BorderSide(
            color: colorScheme.outlineVariant.withValues(alpha: 0.45),
          ),
        ),
      ),
      child: Row(
        children: [
          Icon(icon, size: 20, color: colorScheme.onSurfaceVariant),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              label,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
          ),
          Flexible(
            child: Text(
              value.isEmpty ? '-' : value,
              textAlign: TextAlign.end,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: theme.textTheme.bodyMedium?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _CompactRow extends StatelessWidget {
  const _CompactRow({
    required this.icon,
    required this.title,
    required this.subtitle,
    this.onTap,
  });

  final IconData icon;
  final String title;
  final String subtitle;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return InkWell(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
        decoration: BoxDecoration(
          border: Border(
            top: BorderSide(
              color: colorScheme.outlineVariant.withValues(alpha: 0.45),
            ),
          ),
        ),
        child: Row(
          children: [
            Icon(icon, size: 20, color: colorScheme.onSurfaceVariant),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    title,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: theme.textTheme.bodyMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  if (subtitle.isNotEmpty) ...[
                    const SizedBox(height: 2),
                    Text(
                      subtitle,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ],
                ],
              ),
            ),
            if (onTap != null)
              Icon(
                Symbols.chevron_right_rounded,
                size: 20,
                color: colorScheme.onSurfaceVariant,
              ),
          ],
        ),
      ),
    );
  }
}

class _InfoChip extends StatelessWidget {
  const _InfoChip({required this.icon, required this.label});

  final IconData icon;
  final String label;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
      decoration: BoxDecoration(
        color: colorScheme.secondaryContainer,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 16, color: colorScheme.onSecondaryContainer),
          const SizedBox(width: 5),
          Text(
            label,
            style: TextStyle(
              color: colorScheme.onSecondaryContainer,
              fontSize: 12,
              fontWeight: FontWeight.w700,
            ),
          ),
        ],
      ),
    );
  }
}

class _EmptyTab extends StatelessWidget {
  const _EmptyTab({
    required this.icon,
    required this.title,
    required this.body,
    this.action,
  });

  final IconData icon;
  final String title;
  final String body;
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 40, color: colorScheme.onSurfaceVariant),
            const SizedBox(height: 12),
            Text(
              title,
              textAlign: TextAlign.center,
              style: theme.textTheme.titleMedium?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 6),
            Text(
              body,
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            if (action != null) ...[const SizedBox(height: 16), action!],
          ],
        ),
      ),
    );
  }
}

class _UnitErrorBody extends StatelessWidget {
  const _UnitErrorBody({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return ListView(
      padding: const EdgeInsets.all(32),
      children: [
        const SizedBox(height: 96),
        Icon(Symbols.error_rounded, size: 40, color: colorScheme.error),
        const SizedBox(height: 12),
        Text(
          message,
          textAlign: TextAlign.center,
          style: TextStyle(color: colorScheme.error),
        ),
        const SizedBox(height: 16),
        Center(
          child: FilledButton.tonal(
            onPressed: onRetry,
            child: const Text('Retry'),
          ),
        ),
      ],
    );
  }
}

String _unitLabel(String unitNumber) {
  final value = unitNumber.trim();
  if (value.isEmpty) return 'Unit';
  return value.toLowerCase().startsWith('unit ') ? value : 'Unit $value';
}

String _bedBathLabel(int bedrooms, double bathrooms) {
  final bed = bedrooms == 1 ? '1 bed' : '$bedrooms beds';
  final bathNumber = bathrooms == bathrooms.roundToDouble()
      ? bathrooms.round().toString()
      : bathrooms.toStringAsFixed(1);
  final bath = bathNumber == '1' ? '1 bath' : '$bathNumber baths';
  return '$bed · $bath';
}

String _leaseEndsLabel(int days) {
  if (days == 0) return 'Lease ends today';
  if (days == 1) return 'Lease ends tomorrow';
  return 'Lease ends in ${days}d';
}

String _formatDate(DateTime date) {
  if (date.year <= 1) return 'Not set';
  return '${date.month}/${date.day}/${date.year}';
}
