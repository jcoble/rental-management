import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/lease.dart';
import '../../core/models/work_order.dart';
import '../applications/application_detail_screen.dart';
import '../applications/applications_models.dart';
import '../home/mobile_domain_navigation.dart';
import '../leases/lease_detail_screen.dart';
import '../leases/leases_repository.dart';
import '../maintenance/create_work_order_sheet.dart';
import '../maintenance/work_order_detail_screen.dart';
import '../tenants/tenant_detail_screen.dart';
import 'unit_command_center_tabs.dart';
import 'units_repository.dart';

export 'unit_command_center_tabs.dart';

class UnitCommandCenterLoaderScreen extends ConsumerWidget {
  const UnitCommandCenterLoaderScreen({
    super.key,
    required this.unitId,
    this.initialTab = UnitCommandCenterTab.overview,
    this.initialLease,
    this.initialApplication,
    this.initialWorkOrder,
    this.selectedTenantId,
  });

  final int unitId;
  final UnitCommandCenterTab initialTab;
  final Lease? initialLease;
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
          initialLease: initialLease,
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

class UnitCommandCenterScreen extends StatelessWidget {
  const UnitCommandCenterScreen({
    super.key,
    required this.dashboard,
    this.initialTab = UnitCommandCenterTab.overview,
    this.initialLease,
    this.initialApplication,
    this.initialWorkOrder,
    this.selectedTenantId,
  });

  final UnitDashboard dashboard;
  final UnitCommandCenterTab initialTab;
  final Lease? initialLease;
  final RentalApplication? initialApplication;
  final WorkOrder? initialWorkOrder;
  final int? selectedTenantId;

  @override
  Widget build(BuildContext context) {
    final unit = dashboard.unit;
    final property = dashboard.propertyName.trim().isEmpty
        ? 'Property'
        : dashboard.propertyName.trim();
    final usesDomainHeader = MobileDomainHeaderScope.maybeOf(context) != null;
    const unitTabs = TabBar(
      isScrollable: true,
      tabAlignment: TabAlignment.start,
      tabs: [
        Tab(text: 'Overview'),
        Tab(text: 'Lease'),
        Tab(text: 'Apps'),
        Tab(text: 'Tenants'),
        Tab(text: 'Work'),
      ],
    );
    final tabView = TabBarView(
      children: [
        _UnitOverviewTab(dashboard: dashboard),
        _UnitLeaseTab(dashboard: dashboard, selectedLease: initialLease),
        _UnitApplicationsTab(application: initialApplication),
        _UnitTenantsTab(
          dashboard: dashboard,
          selectedTenantId: selectedTenantId,
        ),
        _UnitWorkTab(dashboard: dashboard, selectedWorkOrder: initialWorkOrder),
      ],
    );

    return DefaultTabController(
      length: UnitCommandCenterTab.values.length,
      initialIndex: initialTab.index,
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
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: Theme.of(context).colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
                bottom: unitTabs,
              ),
        body: usesDomainHeader
            ? Column(
                children: [
                  Material(
                    color: Theme.of(context).colorScheme.surface,
                    child: const Align(
                      alignment: Alignment.centerLeft,
                      child: unitTabs,
                    ),
                  ),
                  Expanded(child: tabView),
                ],
              )
            : tabView,
      ),
    );
  }
}

class _UnitOverviewTab extends StatelessWidget {
  const _UnitOverviewTab({required this.dashboard});

  final UnitDashboard dashboard;

  @override
  Widget build(BuildContext context) {
    final header = dashboard.header;
    final unit = dashboard.unit;

    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      children: [
        _SurfacePanel(
          children: [
            _MetricRow(
              icon: Symbols.home_work_rounded,
              label: 'Status',
              value: unit.status.isEmpty
                  ? dashboard.lifecycleStage
                  : unit.status,
            ),
            _MetricRow(
              icon: Symbols.route_rounded,
              label: 'Stage',
              value: dashboard.lifecycleStage.isEmpty
                  ? 'Unit'
                  : dashboard.lifecycleStage,
            ),
            _MetricRow(
              icon: Symbols.payments_rounded,
              label: 'Market rent',
              value: '${_formatCurrency(unit.marketRent)}/mo',
            ),
            if (unit.bedrooms > 0 || unit.bathrooms > 0)
              _MetricRow(
                icon: Symbols.bed_rounded,
                label: 'Plan',
                value: _bedBathLabel(unit.bedrooms, unit.bathrooms),
              ),
          ],
        ),
        const SizedBox(height: 14),
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
                label: '${_formatCurrency(header.outstandingRentBalance)} due',
              ),
            _InfoChip(
              icon: Symbols.build_rounded,
              label: '${header.openWorkOrderCount} open work',
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
        if (dashboard.nextBestAction.label.trim().isNotEmpty) ...[
          const SizedBox(height: 14),
          _ActionPanel(
            label: dashboard.nextBestAction.label.trim(),
            href: dashboard.nextBestAction.href,
            currentUnitId: dashboard.unit.id,
          ),
        ],
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
}

class _UnitLeaseTab extends ConsumerWidget {
  const _UnitLeaseTab({required this.dashboard, this.selectedLease});

  final UnitDashboard dashboard;
  final Lease? selectedLease;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final lease = selectedLease;
    final summary = dashboard.currentLease;

    if (lease != null) {
      return LeaseDetailScreen(lease: lease);
    }

    if (summary == null) {
      return const _EmptyTab(
        icon: Symbols.description_rounded,
        title: 'No lease',
        body: 'This unit has no current lease.',
      );
    }

    final leaseAsync = ref.watch(leaseDetailProvider(summary.id));
    return leaseAsync.when(
      loading: () => const Center(child: CircularProgressIndicator()),
      error: (e, _) => _UnitErrorBody(
        message: e is ApiException ? e.message : e.toString(),
        onRetry: () =>
            ref.read(leaseDetailProvider(summary.id).notifier).refresh(),
      ),
      data: (loadedLease) => LeaseDetailScreen(lease: loadedLease),
    );
  }
}

class _UnitApplicationsTab extends StatelessWidget {
  const _UnitApplicationsTab({this.application});

  final RentalApplication? application;

  @override
  Widget build(BuildContext context) {
    final app = application;
    if (app == null) {
      return const _EmptyTab(
        icon: Symbols.assignment_ind_rounded,
        title: 'No application selected',
        body: 'Open-ended applications stay in the Applications list.',
      );
    }

    return ApplicationDetailScreen(applicationId: app.id);
  }
}

class _UnitTenantsTab extends StatelessWidget {
  const _UnitTenantsTab({required this.dashboard, this.selectedTenantId});

  final UnitDashboard dashboard;
  final int? selectedTenantId;

  @override
  Widget build(BuildContext context) {
    final tenantId = selectedTenantId ?? dashboard.currentTenant?.id;
    if (tenantId == null || tenantId <= 0) {
      return const _EmptyTab(
        icon: Symbols.group_rounded,
        title: 'No tenant',
        body: 'This unit is currently vacant.',
      );
    }

    return TenantDetailLoaderScreen(tenantId: tenantId);
  }
}

class _UnitWorkTab extends ConsumerWidget {
  const _UnitWorkTab({required this.dashboard, this.selectedWorkOrder});

  final UnitDashboard dashboard;
  final WorkOrder? selectedWorkOrder;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final selected = selectedWorkOrder;

    if (selected != null) {
      return WorkOrderDetailScreen(workOrderId: selected.id);
    }

    final property = dashboard.propertyName.trim().isEmpty
        ? 'Property'
        : dashboard.propertyName.trim();

    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      children: [
        _WorkOrdersSection(
          items: dashboard.overview.openWorkOrders,
          openFullDetail: true,
          trailing: IconButton.filledTonal(
            tooltip: 'New work order',
            icon: const Icon(Icons.add),
            onPressed: () {
              showCreateWorkOrderSheet(
                context: context,
                ref: ref,
                onSaved: () =>
                    ref.invalidate(unitDashboardProvider(dashboard.unit.id)),
                initialPropertyId: dashboard.unit.propertyId,
                initialUnitId: dashboard.unit.id,
                initialPropertyLabel: property,
                initialUnitLabel: _unitLabel(dashboard.unit.unitNumber),
              );
            },
          ),
        ),
      ],
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
            title: '${item.type} · ${_formatCurrency(item.amount)}',
            subtitle: '${item.status} · Due ${_formatDate(item.dueDate)}',
          ),
      ],
    );
  }
}

class _WorkOrdersSection extends StatelessWidget {
  const _WorkOrdersSection({
    required this.items,
    this.openFullDetail = false,
    this.trailing,
  });

  final List<UnitWorkOrderSummary> items;
  final bool openFullDetail;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    return _Section(
      title: 'Work orders',
      empty: 'No open work orders',
      trailing: trailing,
      children: [
        for (final item in items)
          _CompactRow(
            icon: Symbols.build_rounded,
            title: item.title.isEmpty ? 'Work order #${item.id}' : item.title,
            subtitle:
                '${item.priority.isEmpty ? 'Priority' : item.priority} · ${item.status.isEmpty ? 'Open' : item.status}',
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
                ? '${item.entityType} · ${_formatDate(item.uploadedAt)}'
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
                '${item.status.isEmpty ? item.type : item.status} · ${_formatDate(item.scheduledStart)}',
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
      DefaultTabController.of(context).animateTo(target.initialTab.index);
      return;
    }

    Widget detailBuilder(BuildContext _) => UnitCommandCenterLoaderScreen(
      unitId: target.unitId,
      initialTab: target.initialTab,
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

class _Section extends StatelessWidget {
  const _Section({
    required this.title,
    required this.children,
    required this.empty,
    this.trailing,
  });

  final String title;
  final List<Widget> children;
  final String empty;
  final Widget? trailing;

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
              ?trailing,
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
  });

  final IconData icon;
  final String title;
  final String body;

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

String _formatCurrency(double amount) {
  final rounded = amount.round();
  final s = rounded.toString();
  final buf = StringBuffer(r'$');
  final start = s.length % 3;
  if (start > 0) buf.write(s.substring(0, start));
  for (var i = start; i < s.length; i += 3) {
    if (i > 0) buf.write(',');
    buf.write(s.substring(i, i + 3));
  }
  return buf.toString();
}
