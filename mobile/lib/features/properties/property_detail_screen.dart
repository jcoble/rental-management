import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/models.dart';
import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/auth/mobile_access_policy.dart';
import '../accounting/accounting_book_models.dart';
import '../accounting/accounting_impact_card.dart';
import '../activity/activity_history_screen.dart';
import '../money/money_format.dart' as money;
import '../scan/scan_capture.dart';
import '../scan/scan_review_screen.dart';
import '../units/unit_command_center_screen.dart';
import '../units/unit_form_sheet.dart';
import '../units/unit_navigation.dart';
import '../maintenance/work_order_detail_screen.dart';
import '../maintenance/work_orders_repository.dart';
import 'capital_assets_repository.dart';
import 'properties_repository.dart';
import 'property_capital_asset_form_sheet.dart';
import 'property_disposition_form_sheet.dart';
import 'property_dispositions_repository.dart';
import 'property_documents_section.dart';
import 'property_form_sheet.dart';
import 'property_labels.dart';
import 'property_loan_form_sheet.dart';
import 'property_loans_repository.dart';
import 'property_workspace_sections.dart';

String _formatCurrency(double amount) {
  final rounded = amount.round();
  // Insert commas: e.g. 1200 -> $1,200
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

const _monthNames = [
  '',
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec',
];

String _formatDate(DateTime d) => '${_monthNames[d.month]} ${d.day}, ${d.year}';

String _formatOwnerships(Property property) {
  if (property.ownerships.isEmpty) return 'No owner assigned';
  return property.ownerships
      .map(
        (ownership) =>
            property.ownerships.length == 1 &&
                ownership.ownershipSharePercent == 100
            ? ownership.ownerName
            : '${ownership.ownerName} (${ownership.ownershipSharePercent.toStringAsFixed(ownership.ownershipSharePercent.truncateToDouble() == ownership.ownershipSharePercent ? 0 : 2)}%)',
      )
      .join(', ');
}

void _openLeaseDetail(BuildContext context, LeaseManagementSummary management) {
  openUnitCommandCenter(
    context,
    unitId: management.unitId,
    initialTab: UnitCommandCenterTab.tenantLease,
    initialView: UnitCommandCenterView.agreements,
    leaseManagementId: management.id,
  );
}

/// Loads a property by id, then shows [PropertyDetailScreen]. Use this when the
/// caller only has a property id (e.g. a lease, which carries `propertyId` but
/// not the full model).
class PropertyDetailLoaderScreen extends ConsumerWidget {
  const PropertyDetailLoaderScreen({super.key, required this.propertyId});

  final int propertyId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(propertyWorkspaceDetailProvider(propertyId));
    return async.when(
      loading: () =>
          const Scaffold(body: Center(child: CircularProgressIndicator())),
      error: (e, _) => Scaffold(
        appBar: AppBar(title: const Text('Property')),
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Text(
              e is ApiException ? e.message : e.toString(),
              textAlign: TextAlign.center,
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          ),
        ),
      ),
      data: (detail) {
        return PropertyDetailScreen(property: detail.property);
      },
    );
  }
}

/// Detail screen for a single property.
///
/// Shows a header with name / address / status, a Units section (with add +
/// inline edit), and a read-only Leases section for active leases.
class PropertyDetailScreen extends ConsumerStatefulWidget {
  const PropertyDetailScreen({super.key, required this.property});

  final Property property;

  @override
  ConsumerState<PropertyDetailScreen> createState() =>
      _PropertyDetailScreenState();
}

class _PropertyDetailScreenState extends ConsumerState<PropertyDetailScreen> {
  static const _pageSize = 20;

  late Property _property;
  PropertyWorkspaceSection _section = PropertyWorkspaceSection.summary;
  int _rentalsSkip = 0;
  int _leaseSkip = 0;
  int _workSkip = 0;

  @override
  void initState() {
    super.initState();
    _property = widget.property;
  }

  @override
  void didUpdateWidget(covariant PropertyDetailScreen oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.property.id != widget.property.id ||
        oldWidget.property.updatedAt != widget.property.updatedAt) {
      _property = widget.property;
    }
  }

  // Both property providers self-load on first watch
  // (their notifier build() calls Future.microtask(load)), so no explicit
  // initState load is needed — adding one just double-fetches.

  Future<void> _refresh() async {
    final propertyId = _property.id;
    final refreshes = <Future<void>>[
      ref.read(propertiesRepositoryProvider).getProperty(propertyId).then((
        property,
      ) {
        if (mounted) setState(() => _property = property);
      }),
    ];
    switch (_section) {
      case PropertyWorkspaceSection.rentals:
        ref.invalidate(propertyWorkspaceUnitsPageProvider);
        ref.invalidate(propertyWorkspaceLeasesPageProvider);
        break;
      case PropertyWorkspaceSection.propertyWork:
        ref.invalidate(workOrdersPageProvider);
        break;
      case PropertyWorkspaceSection.propertyFinances:
        refreshes.addAll([
          ref.read(propertyLoansProvider(propertyId).notifier).refresh(),
          ref
              .read(propertyCapitalAssetsProvider(propertyId).notifier)
              .refresh(),
          ref.read(propertyDispositionsProvider(propertyId).notifier).refresh(),
        ]);
        break;
      case PropertyWorkspaceSection.summary:
      case PropertyWorkspaceSection.ownershipManagement:
        break;
      case PropertyWorkspaceSection.documentsHistory:
        ref.invalidate(propertyDocumentsProvider(propertyId));
        break;
    }
    await Future.wait(refreshes);
  }

  Future<void> _showAddUnitSheet(BuildContext context) async {
    final saved = await showUnitFormSheet(context, propertyId: _property.id);
    if (saved != null && mounted) await _refreshAfterUnitCreate();
  }

  Future<void> _refreshAfterUnitCreate() async {
    final propertyId = _property.id;
    ref.invalidate(propertyDetailProvider(propertyId));
    ref.invalidate(propertiesPageProvider);

    final unitsFuture = ref.refresh(
      propertyWorkspaceUnitsPageProvider(
        PropertyWorkspacePageQuery(
          propertyId: propertyId,
          skip: _rentalsSkip,
          take: _pageSize,
        ),
      ).future,
    );
    final detail = await ref.refresh(
      propertyWorkspaceDetailProvider(propertyId).future,
    );
    await unitsFuture;
    if (mounted) setState(() => _property = detail.property);
  }

  Future<void> _showEditPropertySheet(BuildContext context) async {
    final saved = await showPropertyFormSheet(
      context,
      property: _property,
      onSaved: (property) {
        ref.invalidate(propertyDetailProvider(property.id));
        ref.invalidate(propertiesPageProvider);
        ref.read(propertiesProvider.notifier).refresh();
      },
    );
    if (saved != null && mounted) {
      setState(() => _property = saved);
    }
  }

  Future<void> _confirmDeleteProperty(BuildContext context) async {
    final property = _property;
    final unitCount = property.unitCount ?? 0;
    final isSingleRental =
        property.rentalStructure == RentalStructure.singleRental;
    final message = isSingleRental && unitCount == 1
        ? 'This also removes the rental’s underlying Unit if it is still empty. If it has leases, work orders, expenses, inspections, applications, appointments, or documents, the server will stop the delete.'
        : unitCount > 0
        ? 'This property still has $unitCount ${unitCount == 1 ? 'unit' : 'units'}. Remove the units before deleting the property.'
        : 'This cannot be undone.';

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text('Delete ${property.name}?'),
        content: Text(message),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    if (!context.mounted) return;

    final messenger = ScaffoldMessenger.of(context);
    final navigator = Navigator.of(context);
    try {
      await ref.read(propertiesRepositoryProvider).deleteProperty(property.id);
      ref.invalidate(propertyDetailProvider(property.id));
      ref.invalidate(propertiesPageProvider);
      unawaited(ref.read(propertiesProvider.notifier).refresh());
      if (!mounted) return;
      navigator.pop();
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Property deleted.')));
    } on ApiException catch (e) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    } catch (_) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          const SnackBar(content: Text('Could not delete property.')),
        );
    }
  }

  Future<void> _showAddLoanSheet(BuildContext context) async {
    final saved = await showPropertyLoanFormSheet(
      context,
      propertyId: _property.id,
    );
    if (saved && mounted) {
      await ref.read(propertyLoansProvider(_property.id).notifier).refresh();
    }
  }

  Future<void> _showEditLoanSheet(
    BuildContext context,
    PropertyLoan loan,
  ) async {
    final saved = await showPropertyLoanFormSheet(
      context,
      propertyId: _property.id,
      loan: loan,
    );
    if (saved && mounted) {
      await ref.read(propertyLoansProvider(_property.id).notifier).refresh();
    }
  }

  Future<void> _startLoanScan(BuildContext context) async {
    final draftId = await showScanCaptureSheet(
      context,
      initialTargetEntityType: 'Loan',
      lockTargetEntityType: true,
      propertyId: _property.id,
      sourceLabel: _property.name,
    );
    if (draftId == null || !context.mounted) return;

    await Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) =>
            ScanReviewScreen(draftId: draftId, loanPropertyId: _property.id),
      ),
    );
    if (mounted) {
      unawaited(
        ref.read(propertyLoansProvider(_property.id).notifier).refresh(),
      );
    }
  }

  Future<void> _confirmDeleteLoan(
    BuildContext context,
    PropertyLoan loan,
  ) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text('Remove ${loan.lender}?'),
        content: const Text(
          'This removes the loan and its amortization history from this property.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
    if (confirmed != true || !context.mounted) return;

    final messenger = ScaffoldMessenger.of(context);
    try {
      await ref.read(propertyLoansRepositoryProvider).deleteLoan(loan.id);
      await ref.read(propertyLoansProvider(_property.id).notifier).refresh();
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Loan removed.')));
    } on ApiException catch (e) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    } catch (_) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Could not remove loan.')));
    }
  }

  Future<void> _showAddCapitalAssetSheet(BuildContext context) async {
    final saved = await showPropertyCapitalAssetFormSheet(
      context,
      propertyId: _property.id,
    );
    if (saved && mounted) {
      await ref
          .read(propertyCapitalAssetsProvider(_property.id).notifier)
          .refresh();
    }
  }

  Future<void> _showEditCapitalAssetSheet(
    BuildContext context,
    CapitalAsset asset,
  ) async {
    final saved = await showPropertyCapitalAssetFormSheet(
      context,
      propertyId: _property.id,
      asset: asset,
    );
    if (saved && mounted) {
      await ref
          .read(propertyCapitalAssetsProvider(_property.id).notifier)
          .refresh();
    }
  }

  Future<void> _confirmDeleteCapitalAsset(
    BuildContext context,
    CapitalAsset asset,
  ) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text('Remove ${asset.description}?'),
        content: const Text(
          'This removes the capital asset record from this property.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
    if (confirmed != true || !context.mounted) return;

    final messenger = ScaffoldMessenger.of(context);
    try {
      await ref.read(capitalAssetsRepositoryProvider).deleteAsset(asset.id);
      await ref
          .read(propertyCapitalAssetsProvider(_property.id).notifier)
          .refresh();
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Capital asset removed.')));
    } on ApiException catch (e) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    } catch (_) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          const SnackBar(content: Text('Could not remove capital asset.')),
        );
    }
  }

  Future<void> _showAddDispositionSheet(BuildContext context) async {
    final saved = await showPropertyDispositionFormSheet(
      context,
      propertyId: _property.id,
    );
    if (saved && mounted) await _refreshAfterDispositionChange();
  }

  Future<void> _showEditDispositionSheet(
    BuildContext context,
    PropertyDisposition disposition,
  ) async {
    final saved = await showPropertyDispositionFormSheet(
      context,
      propertyId: _property.id,
      disposition: disposition,
    );
    if (saved && mounted) await _refreshAfterDispositionChange();
  }

  Future<void> _refreshAfterDispositionChange() async {
    final propertyId = _property.id;
    ref.invalidate(propertiesPageProvider);
    await Future.wait<void>([
      ref.read(propertyDispositionsProvider(propertyId).notifier).refresh(),
      ref.read(propertyCapitalAssetsProvider(propertyId).notifier).refresh(),
      ref.read(unitsProvider(propertyId).notifier).refresh(),
      ref.read(propertyLeaseManagementsProvider(propertyId).notifier).refresh(),
      ref.read(propertiesProvider.notifier).refresh(),
      ref.read(propertiesRepositoryProvider).getProperty(propertyId).then((
        property,
      ) {
        if (mounted) setState(() => _property = property);
      }),
    ]);
  }

  Future<void> _confirmDeleteDisposition(
    BuildContext context,
    PropertyDisposition disposition,
  ) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Remove property sale?'),
        content: const Text(
          'This removes the disposition record from this property.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
    if (confirmed != true || !context.mounted) return;

    final messenger = ScaffoldMessenger.of(context);
    try {
      await ref
          .read(propertyDispositionsRepositoryProvider)
          .deleteDisposition(disposition.id);
      await ref
          .read(propertyDispositionsProvider(_property.id).notifier)
          .refresh();
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Property sale removed.')));
    } on ApiException catch (e) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    } catch (_) {
      if (!mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          const SnackBar(content: Text('Could not remove property sale.')),
        );
    }
  }

  void _showActivityHistory() {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => ActivityHistoryScreen(
          entityType: 'Property',
          entityId: _property.id,
          title: 'Property activity',
          subtitle: _property.name,
        ),
      ),
    );
  }

  List<Widget> _propertyWorkWidgets(
    BuildContext context,
    AsyncValue<WorkOrderListPage> workOrdersAsync,
    ColorScheme colorScheme,
  ) {
    return [
      Text(
        'Property work',
        style: Theme.of(
          context,
        ).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700),
      ),
      const SizedBox(height: 8),
      workOrdersAsync.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (error, _) => _InlineError(
          message: error is ApiException ? error.message : error.toString(),
        ),
        data: (page) {
          if (page.items.isEmpty) {
            return Padding(
              padding: const EdgeInsets.symmetric(vertical: 16),
              child: Text(
                'No property-wide or Unit work orders.',
                style: TextStyle(color: colorScheme.onSurfaceVariant),
              ),
            );
          }
          return Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              ...page.items.map(
                (workOrder) => Card(
                  margin: const EdgeInsets.only(bottom: 8),
                  child: ListTile(
                    title: Text(workOrder.title),
                    subtitle: Text(
                      '${workOrder.unitNumber ?? 'Property-wide'} · ${workOrder.priority}',
                    ),
                    trailing: Text(workOrder.status),
                    onTap: () => Navigator.of(context).push<void>(
                      MaterialPageRoute<void>(
                        builder: (_) =>
                            WorkOrderDetailScreen(workOrderId: workOrder.id),
                      ),
                    ),
                  ),
                ),
              ),
              _PropertyPagingBar(
                label: 'work orders',
                pageStart: page.skip,
                itemCount: page.items.length,
                totalCount: page.totalCount,
                onPrevious: page.hasPrevious
                    ? () => setState(
                        () => _workSkip = (_workSkip - _pageSize).clamp(
                          0,
                          _workSkip,
                        ),
                      )
                    : null,
                onNext: page.hasNext
                    ? () => setState(() => _workSkip += _pageSize)
                    : null,
              ),
            ],
          );
        },
      ),
    ];
  }

  @override
  Widget build(BuildContext context) {
    final property = _property;
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final unitsAsync = _section == PropertyWorkspaceSection.rentals
        ? ref.watch(
            propertyWorkspaceUnitsPageProvider(
              PropertyWorkspacePageQuery(
                propertyId: property.id,
                skip: _rentalsSkip,
                take: _pageSize,
              ),
            ),
          )
        : null;
    final leasesAsync = _section == PropertyWorkspaceSection.rentals
        ? ref.watch(
            propertyWorkspaceLeasesPageProvider(
              PropertyWorkspacePageQuery(
                propertyId: property.id,
                skip: _leaseSkip,
                take: _pageSize,
              ),
            ),
          )
        : null;
    final workOrdersAsync = _section == PropertyWorkspaceSection.propertyWork
        ? ref.watch(
            workOrdersPageProvider(
              WorkOrderListQuery(
                propertyId: property.id,
                skip: _workSkip,
                take: _pageSize,
                sort: '-updatedAt',
              ),
            ),
          )
        : null;
    final loansAsync = _section == PropertyWorkspaceSection.propertyFinances
        ? ref.watch(propertyLoansProvider(property.id))
        : null;
    final capitalAssetsAsync =
        _section == PropertyWorkspaceSection.propertyFinances
        ? ref.watch(propertyCapitalAssetsProvider(property.id))
        : null;
    final dispositionsAsync =
        _section == PropertyWorkspaceSection.propertyFinances
        ? ref.watch(propertyDispositionsProvider(property.id))
        : null;
    final auth = ref.watch(authControllerProvider);
    final canManageRentals =
        auth is AuthStateAuthenticated &&
        hasAllPropertiesRentalsManageAuthority(
          access: auth.access,
          activeExperience: auth.activeExperience,
        );
    final canManageMoneyExpenses =
        auth is AuthStateAuthenticated &&
        canUseMobileCapabilityAction(
          experience: auth.activeExperience,
          capabilities: auth.capabilities,
          capability: 'money.expenses.manage',
          experiences: const {WorkspaceExperience.management},
        );

    return Scaffold(
      appBar: AppBar(
        title: Text(property.name, overflow: TextOverflow.ellipsis),
        actions: [
          if (canManageRentals)
            IconButton(
              icon: const Icon(Icons.edit_outlined),
              tooltip: 'Edit property',
              onPressed: () => _showEditPropertySheet(context),
            ),
          IconButton(
            icon: const Icon(Icons.history_outlined),
            tooltip: 'View property activity',
            onPressed: _showActivityHistory,
          ),
          if (canManageRentals)
            IconButton(
              icon: const Icon(Icons.delete_outline),
              tooltip: 'Delete property',
              onPressed: () => _confirmDeleteProperty(context),
            ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            // ── Property header ────────────────────────────────────────────
            _PropertyHeader(
              property: property,
              theme: theme,
              colorScheme: colorScheme,
            ),
            const SizedBox(height: 12),
            _PropertyWorkspaceSectionBar(
              selected: _section,
              onSelected: (section) {
                setState(() {
                  _section = section;
                  if (section == PropertyWorkspaceSection.rentals) {
                    _rentalsSkip = 0;
                    _leaseSkip = 0;
                  }
                  if (section == PropertyWorkspaceSection.propertyWork) {
                    _workSkip = 0;
                  }
                });
              },
            ),
            const SizedBox(height: 24),

            if (_section == PropertyWorkspaceSection.summary)
              _PropertySummaryCard(property: property),
            if (_section == PropertyWorkspaceSection.ownershipManagement)
              _PropertyOwnershipCard(property: property),
            if (_section == PropertyWorkspaceSection.propertyWork)
              ..._propertyWorkWidgets(context, workOrdersAsync!, colorScheme),
            if (_section == PropertyWorkspaceSection.documentsHistory)
              Column(
                key: const ValueKey('property-documents-history-surface'),
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  PropertyDocumentsSection(
                    propertyId: property.id,
                    canUpload: canManageRentals,
                  ),
                  const SizedBox(height: 24),
                  Text(
                    'History',
                    style: theme.textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  const SizedBox(height: 8),
                  SizedBox(
                    height: MediaQuery.sizeOf(context).height * 0.55,
                    child: ActivityHistoryScreen(
                      entityType: 'Property',
                      entityId: property.id,
                      title: 'History',
                      subtitle: property.name,
                      embedded: true,
                    ),
                  ),
                ],
              ),

            // ── Units section ──────────────────────────────────────────────
            if (_section == PropertyWorkspaceSection.rentals) ...[
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'Units',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                  if (canManageRentals)
                    TextButton.icon(
                      onPressed: () => _showAddUnitSheet(context),
                      icon: const Icon(Icons.add, size: 18),
                      label: const Text('Add unit'),
                    ),
                ],
              ),
              const SizedBox(height: 8),

              unitsAsync!.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (e, _) => _InlineError(
                  message: e is ApiException ? e.message : e.toString(),
                ),
                data: (page) {
                  if (page.items.isEmpty) {
                    return Padding(
                      padding: const EdgeInsets.symmetric(vertical: 16),
                      child: Text(
                        'No units yet. Tap "Add unit" to create one.',
                        style: TextStyle(color: colorScheme.onSurfaceVariant),
                      ),
                    );
                  }
                  return Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      ...page.items.map((u) => _WorkspaceUnitTile(unit: u)),
                      _PropertyPagingBar(
                        label: 'units',
                        pageStart: page.skip,
                        itemCount: page.items.length,
                        totalCount: page.totalCount,
                        onPrevious: page.hasPrevious
                            ? () => setState(
                                () => _rentalsSkip = (_rentalsSkip - _pageSize)
                                    .clamp(0, _rentalsSkip),
                              )
                            : null,
                        onNext: page.hasNext
                            ? () => setState(() => _rentalsSkip += _pageSize)
                            : null,
                      ),
                    ],
                  );
                },
              ),

              const SizedBox(height: 24),
            ],

            // ── Mortgage / Loans section ──────────────────────────────────
            if (_section == PropertyWorkspaceSection.propertyFinances) ...[
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'Mortgage / Loans',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                  if (canManageMoneyExpenses) ...[
                    IconButton(
                      tooltip: 'Scan mortgage statement',
                      icon: const Icon(Icons.document_scanner_outlined),
                      onPressed: () => _startLoanScan(context),
                    ),
                    IconButton(
                      tooltip: 'Add loan',
                      icon: const Icon(Icons.add),
                      onPressed: () => _showAddLoanSheet(context),
                    ),
                  ],
                ],
              ),
              const SizedBox(height: 8),

              loansAsync!.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (e, _) => _InlineError(
                  message: e is ApiException ? e.message : e.toString(),
                ),
                data: (page) {
                  final loans = page.loans;
                  if (loans.isEmpty) {
                    return Padding(
                      padding: const EdgeInsets.symmetric(vertical: 16),
                      child: Text(
                        'No mortgage or loan records on this property yet.',
                        style: TextStyle(color: colorScheme.onSurfaceVariant),
                      ),
                    );
                  }
                  return Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      ...loans.map(
                        (loan) => _LoanTile(
                          loan: loan,
                          onEdit: canManageMoneyExpenses
                              ? () => _showEditLoanSheet(context, loan)
                              : null,
                          onDelete: canManageMoneyExpenses
                              ? () => _confirmDeleteLoan(context, loan)
                              : null,
                        ),
                      ),
                      if (page.loadMoreError != null)
                        Padding(
                          padding: const EdgeInsets.only(top: 4, bottom: 8),
                          child: Text(
                            page.loadMoreError is ApiException
                                ? (page.loadMoreError! as ApiException).message
                                : 'Could not load more loans.',
                            style: TextStyle(
                              color: colorScheme.error,
                              fontSize: 12,
                            ),
                            textAlign: TextAlign.center,
                          ),
                        ),
                      if (page.hasMore || page.isLoadingMore)
                        Padding(
                          padding: const EdgeInsets.only(top: 4),
                          child: OutlinedButton.icon(
                            icon: page.isLoadingMore
                                ? const SizedBox(
                                    width: 16,
                                    height: 16,
                                    child: CircularProgressIndicator(
                                      strokeWidth: 2,
                                    ),
                                  )
                                : const Icon(Icons.expand_more),
                            label: Text(
                              page.isLoadingMore
                                  ? 'Loading loans...'
                                  : 'Load more loans',
                            ),
                            onPressed: page.isLoadingMore
                                ? null
                                : () => ref
                                      .read(
                                        propertyLoansProvider(
                                          property.id,
                                        ).notifier,
                                      )
                                      .loadMore(),
                          ),
                        ),
                    ],
                  );
                },
              ),

              const SizedBox(height: 24),

              // ── Capital assets section ───────────────────────────────────
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'Capital Assets',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                  if (canManageMoneyExpenses)
                    IconButton(
                      tooltip: 'Add capital asset',
                      icon: const Icon(Icons.add),
                      onPressed: () => _showAddCapitalAssetSheet(context),
                    ),
                ],
              ),
              const SizedBox(height: 8),

              capitalAssetsAsync!.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (e, _) => _InlineError(
                  message: e is ApiException ? e.message : e.toString(),
                ),
                data: (page) {
                  final assets = page.assets;
                  if (assets.isEmpty) {
                    return Padding(
                      padding: const EdgeInsets.symmetric(vertical: 16),
                      child: Text(
                        'No capital assets recorded for this property yet.',
                        style: TextStyle(color: colorScheme.onSurfaceVariant),
                      ),
                    );
                  }
                  return Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      ...assets.map(
                        (asset) => _CapitalAssetTile(
                          asset: asset,
                          onEdit: canManageMoneyExpenses
                              ? () => _showEditCapitalAssetSheet(context, asset)
                              : null,
                          onDelete: canManageMoneyExpenses
                              ? () => _confirmDeleteCapitalAsset(context, asset)
                              : null,
                        ),
                      ),
                      if (page.loadMoreError != null)
                        Padding(
                          padding: const EdgeInsets.only(top: 4, bottom: 8),
                          child: Text(
                            page.loadMoreError is ApiException
                                ? (page.loadMoreError! as ApiException).message
                                : 'Could not load more capital assets.',
                            style: TextStyle(
                              color: colorScheme.error,
                              fontSize: 12,
                            ),
                            textAlign: TextAlign.center,
                          ),
                        ),
                      if (page.hasMore || page.isLoadingMore)
                        Padding(
                          padding: const EdgeInsets.only(top: 4),
                          child: OutlinedButton.icon(
                            icon: page.isLoadingMore
                                ? const SizedBox(
                                    width: 16,
                                    height: 16,
                                    child: CircularProgressIndicator(
                                      strokeWidth: 2,
                                    ),
                                  )
                                : const Icon(Icons.expand_more),
                            label: Text(
                              page.isLoadingMore
                                  ? 'Loading assets...'
                                  : 'Load more assets',
                            ),
                            onPressed: page.isLoadingMore
                                ? null
                                : () => ref
                                      .read(
                                        propertyCapitalAssetsProvider(
                                          property.id,
                                        ).notifier,
                                      )
                                      .loadMore(),
                          ),
                        ),
                    ],
                  );
                },
              ),

              const SizedBox(height: 24),

              // ── Property disposition section ─────────────────────────────
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'Property Sale / Disposition',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                  if (canManageRentals)
                    IconButton(
                      tooltip: 'Record property sale',
                      icon: const Icon(Icons.add),
                      onPressed: () => _showAddDispositionSheet(context),
                    ),
                ],
              ),
              const SizedBox(height: 8),

              dispositionsAsync!.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (e, _) => _InlineError(
                  message: e is ApiException ? e.message : e.toString(),
                ),
                data: (page) {
                  final dispositions = page.dispositions;
                  if (dispositions.isEmpty) {
                    return Padding(
                      padding: const EdgeInsets.symmetric(vertical: 16),
                      child: Text(
                        'No sale or disposition recorded for this property.',
                        style: TextStyle(color: colorScheme.onSurfaceVariant),
                      ),
                    );
                  }
                  return Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      ...dispositions.map(
                        (disposition) => _DispositionTile(
                          disposition: disposition,
                          onEdit: canManageRentals
                              ? () => _showEditDispositionSheet(
                                  context,
                                  disposition,
                                )
                              : null,
                          onDelete: canManageRentals
                              ? () => _confirmDeleteDisposition(
                                  context,
                                  disposition,
                                )
                              : null,
                        ),
                      ),
                      if (page.loadMoreError != null)
                        Padding(
                          padding: const EdgeInsets.only(top: 4, bottom: 8),
                          child: Text(
                            page.loadMoreError is ApiException
                                ? (page.loadMoreError! as ApiException).message
                                : 'Could not load more property sales.',
                            style: TextStyle(
                              color: colorScheme.error,
                              fontSize: 12,
                            ),
                            textAlign: TextAlign.center,
                          ),
                        ),
                      if (page.hasMore || page.isLoadingMore)
                        Padding(
                          padding: const EdgeInsets.only(top: 4),
                          child: OutlinedButton.icon(
                            icon: page.isLoadingMore
                                ? const SizedBox(
                                    width: 16,
                                    height: 16,
                                    child: CircularProgressIndicator(
                                      strokeWidth: 2,
                                    ),
                                  )
                                : const Icon(Icons.expand_more),
                            label: Text(
                              page.isLoadingMore
                                  ? 'Loading property sales...'
                                  : 'Load more property sales',
                            ),
                            onPressed: page.isLoadingMore
                                ? null
                                : () => ref
                                      .read(
                                        propertyDispositionsProvider(
                                          property.id,
                                        ).notifier,
                                      )
                                      .loadMore(),
                          ),
                        ),
                    ],
                  );
                },
              ),

              const SizedBox(height: 24),
            ],

            // ── Leases section ─────────────────────────────────────────────
            if (_section == PropertyWorkspaceSection.rentals) ...[
              Text(
                'Rental relationships',
                style: theme.textTheme.titleMedium?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
              ),
              const SizedBox(height: 8),

              leasesAsync!.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (e, _) => _InlineError(
                  message: e is ApiException ? e.message : e.toString(),
                ),
                data: (page) {
                  if (page.items.isEmpty) {
                    return Padding(
                      padding: const EdgeInsets.symmetric(vertical: 16),
                      child: Text(
                        'No rental relationships on this property.',
                        style: TextStyle(color: colorScheme.onSurfaceVariant),
                      ),
                    );
                  }
                  return Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      ...page.items.map((l) => _LeaseTile(lease: l)),
                      _PropertyPagingBar(
                        label: 'rental relationships',
                        pageStart: page.skip,
                        itemCount: page.items.length,
                        totalCount: page.totalCount,
                        onPrevious: page.hasPrevious
                            ? () => setState(
                                () => _leaseSkip = (_leaseSkip - _pageSize)
                                    .clamp(0, _leaseSkip),
                              )
                            : null,
                        onNext: page.hasNext
                            ? () => setState(() => _leaseSkip += _pageSize)
                            : null,
                      ),
                    ],
                  );
                },
              ),
            ],

            const SizedBox(height: 32),
          ],
        ),
      ),
    );
  }
}

class _PropertyWorkspaceSectionBar extends StatelessWidget {
  const _PropertyWorkspaceSectionBar({
    required this.selected,
    required this.onSelected,
  });

  final PropertyWorkspaceSection selected;
  final ValueChanged<PropertyWorkspaceSection> onSelected;

  @override
  Widget build(BuildContext context) {
    return SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      child: Row(
        children: [
          for (final section in propertyWorkspaceSections)
            Padding(
              padding: const EdgeInsets.only(right: 8),
              child: ChoiceChip(
                key: Key('property-area-${section.name}'),
                label: Text(section.label),
                selected: selected == section,
                onSelected: (_) => onSelected(section),
              ),
            ),
        ],
      ),
    );
  }
}

class _PropertySummaryCard extends StatelessWidget {
  const _PropertySummaryCard({required this.property});

  final Property property;

  @override
  Widget build(BuildContext context) {
    final occupied = property.occupiedUnits ?? 0;
    final total = property.unitCount ?? 0;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Summary',
              style: Theme.of(
                context,
              ).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 24,
              runSpacing: 12,
              children: [
                _KeyValue(label: 'Units', value: '$total'),
                _KeyValue(label: 'Occupied', value: '$occupied'),
                _KeyValue(label: 'Owner', value: _formatOwnerships(property)),
                _KeyValue(label: 'Status', value: property.status),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _PropertyOwnershipCard extends StatelessWidget {
  const _PropertyOwnershipCard({required this.property});

  final Property property;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Ownership & management',
              style: Theme.of(
                context,
              ).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 24,
              runSpacing: 12,
              children: [
                _KeyValue(label: 'Owner', value: _formatOwnerships(property)),
                _KeyValue(
                  label: 'Management fee',
                  value: property.managementFeePercent == null
                      ? 'Not set'
                      : '${property.managementFeePercent}%',
                ),
                _KeyValue(
                  label: 'Address',
                  value:
                      '${property.addressLine1}, ${property.city}, ${property.state} ${property.postalCode}',
                ),
                _KeyValue(
                  label: 'Rental setup',
                  value: property.rentalStructure.wireValue,
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _WorkspaceUnitTile extends StatelessWidget {
  const _WorkspaceUnitTile({required this.unit});

  final PropertyWorkspaceUnit unit;

  @override
  Widget build(BuildContext context) {
    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: ListTile(
        title: Text(
          unit.unitNumber.isEmpty ? 'Unit' : 'Unit ${unit.unitNumber}',
        ),
        subtitle: Text(
          '${_formatCurrency(unit.marketRent)}/mo · ${unit.openWorkOrderCount} open ${unit.openWorkOrderCount == 1 ? 'work order' : 'work orders'}',
        ),
        trailing: Text(unit.status),
        onTap: () => openUnitCommandCenter(context, unitId: unit.id),
      ),
    );
  }
}

class _PropertyPagingBar extends StatelessWidget {
  const _PropertyPagingBar({
    required this.label,
    required this.pageStart,
    required this.itemCount,
    required this.totalCount,
    required this.onPrevious,
    required this.onNext,
  });

  final String label;
  final int pageStart;
  final int itemCount;
  final int totalCount;
  final VoidCallback? onPrevious;
  final VoidCallback? onNext;

  @override
  Widget build(BuildContext context) {
    final first = totalCount == 0 ? 0 : pageStart + 1;
    final last = pageStart + itemCount;
    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        IconButton(
          tooltip: 'Previous $label page',
          onPressed: onPrevious,
          icon: const Icon(Icons.chevron_left),
        ),
        Text('$first–$last of $totalCount'),
        IconButton(
          tooltip: 'Next $label page',
          onPressed: onNext,
          icon: const Icon(Icons.chevron_right),
        ),
      ],
    );
  }
}

// ── Property header card ──────────────────────────────────────────────────────

class _PropertyHeader extends StatelessWidget {
  const _PropertyHeader({
    required this.property,
    required this.theme,
    required this.colorScheme,
  });

  final Property property;
  final ThemeData theme;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(
                  child: Text(
                    property.name,
                    style: theme.textTheme.titleLarge?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                const SizedBox(width: 8),
                _StatusBadge(status: property.status, colorScheme: colorScheme),
              ],
            ),
            const SizedBox(height: 4),
            Text(
              property.addressLine1,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            Text(
              '${property.city}, ${property.state} ${property.postalCode}',
              style: theme.textTheme.bodySmall?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 16,
              runSpacing: 4,
              children: [
                _KeyValue(
                  label: 'Type',
                  value: formatPropertyType(property.type),
                ),
                _KeyValue(label: 'Units', value: '${property.unitCount ?? 0}'),
                _KeyValue(
                  label: 'Occupied',
                  value: '${property.occupiedUnits ?? 0}',
                ),
                if (property.ownerships.isNotEmpty)
                  _KeyValue(label: 'Owner', value: _formatOwnerships(property)),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _StatusBadge extends StatelessWidget {
  const _StatusBadge({required this.status, required this.colorScheme});

  final String status;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    final isActive = status.toLowerCase() == 'active';
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: isActive
            ? colorScheme.primaryContainer
            : colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        status,
        style: TextStyle(
          fontSize: 12,
          fontWeight: FontWeight.w600,
          color: isActive
              ? colorScheme.onPrimaryContainer
              : colorScheme.onSurfaceVariant,
        ),
      ),
    );
  }
}

class _KeyValue extends StatelessWidget {
  const _KeyValue({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          label,
          style: TextStyle(fontSize: 11, color: colorScheme.onSurfaceVariant),
        ),
        Text(
          value,
          style: theme.textTheme.bodyMedium?.copyWith(
            fontWeight: FontWeight.w600,
          ),
        ),
      ],
    );
  }
}

// ── Lease tile (read-only) ────────────────────────────────────────────────────

class _LeaseTile extends StatelessWidget {
  const _LeaseTile({required this.lease});

  final LeaseManagementSummary lease;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: InkWell(
        onTap: () => _openLeaseDetail(context, lease),
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      lease.primaryTenantName ?? lease.relationshipNumber,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                  ),
                  Text(
                    _formatCurrency(lease.baseRentAmount ?? 0),
                    style: theme.textTheme.bodyMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                      color: colorScheme.primary,
                    ),
                  ),
                  const Text('/mo', style: TextStyle(fontSize: 12)),
                  Icon(
                    Icons.chevron_right,
                    size: 18,
                    color: colorScheme.onSurfaceVariant,
                  ),
                ],
              ),
              const SizedBox(height: 4),
              Text(
                'Unit ${lease.unitNumber}  ·  '
                '${lease.termStartOn == null ? 'Agreement not issued' : '${_formatDate(lease.termStartOn!)} – ${lease.termEndOn == null ? 'Month-to-month' : _formatDate(lease.termEndOn!)}'}',
                style: TextStyle(
                  fontSize: 12,
                  color: colorScheme.onSurfaceVariant,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

// ── Capital asset tile ───────────────────────────────────────────────────────

class _CapitalAssetTile extends StatelessWidget {
  const _CapitalAssetTile({
    required this.asset,
    required this.onEdit,
    required this.onDelete,
  });

  final CapitalAsset asset;
  final VoidCallback? onEdit;
  final VoidCallback? onDelete;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final disposed = asset.disposedOnDate != null;

    return Card(
      key: Key('capital-asset-${asset.id}'),
      margin: const EdgeInsets.only(bottom: 8),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(8, 8, 8, 10),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            ListTile(
              contentPadding: const EdgeInsets.only(left: 4),
              leading: CircleAvatar(
                radius: 20,
                backgroundColor: disposed
                    ? colorScheme.surfaceContainerHighest
                    : colorScheme.primaryContainer,
                child: Icon(
                  Icons.home_repair_service_outlined,
                  size: 18,
                  color: disposed
                      ? colorScheme.onSurfaceVariant
                      : colorScheme.onPrimaryContainer,
                ),
              ),
              title: Text(
                asset.description,
                style: theme.textTheme.bodyMedium?.copyWith(
                  fontWeight: FontWeight.w600,
                ),
              ),
              subtitle: Text(
                '${money.moneyFmt(asset.costBasis)} basis  ·  '
                '${asset.method.label} ${_decimalLabel(asset.recoveryYears)} yrs',
                style: TextStyle(
                  fontSize: 12,
                  color: colorScheme.onSurfaceVariant,
                ),
              ),
              trailing: onEdit == null && onDelete == null
                  ? null
                  : Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        if (onEdit != null)
                          IconButton(
                            icon: const Icon(Icons.edit_outlined, size: 18),
                            onPressed: onEdit,
                            tooltip: 'Edit capital asset',
                          ),
                        if (onDelete != null)
                          IconButton(
                            icon: const Icon(Icons.delete_outline, size: 18),
                            onPressed: onDelete,
                            tooltip: 'Delete capital asset',
                          ),
                      ],
                    ),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 4),
              child: Wrap(
                spacing: 16,
                runSpacing: 8,
                children: [
                  _ScheduleMetric(
                    label: 'In service',
                    value: _formatDate(asset.inServiceDate),
                  ),
                  _ScheduleMetric(
                    label: '${asset.depreciationYear} depreciation',
                    value: money.moneyFmt(asset.annualDepreciation),
                  ),
                  _ScheduleMetric(
                    label: 'Accumulated',
                    value: money.moneyFmt(asset.accumulatedDepreciation),
                  ),
                  if (asset.sourceExpenseDescription != null)
                    _ScheduleMetric(
                      label: 'Source expense',
                      value: asset.sourceExpenseDescription!,
                    ),
                  if (asset.disposedOnDate != null)
                    _ScheduleMetric(
                      label: 'Disposed',
                      value: _formatDate(asset.disposedOnDate!),
                    ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

// ── Property disposition tile ────────────────────────────────────────────────

class _DispositionTile extends StatelessWidget {
  const _DispositionTile({
    required this.disposition,
    required this.onEdit,
    required this.onDelete,
  });

  final PropertyDisposition disposition;
  final VoidCallback? onEdit;
  final VoidCallback? onDelete;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final gainIsPositive = disposition.gainLoss >= 0;

    return Card(
      key: Key('property-disposition-${disposition.id}'),
      margin: const EdgeInsets.only(bottom: 8),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(8, 8, 8, 10),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            ListTile(
              contentPadding: const EdgeInsets.only(left: 4),
              leading: CircleAvatar(
                radius: 20,
                backgroundColor: gainIsPositive
                    ? colorScheme.primaryContainer
                    : colorScheme.errorContainer,
                child: Icon(
                  Icons.sell_outlined,
                  size: 18,
                  color: gainIsPositive
                      ? colorScheme.onPrimaryContainer
                      : colorScheme.onErrorContainer,
                ),
              ),
              title: Text(
                'Closed ${_formatDate(disposition.closedOnDate)}',
                style: theme.textTheme.bodyMedium?.copyWith(
                  fontWeight: FontWeight.w600,
                ),
              ),
              subtitle: Text(
                '${money.moneyFmt(disposition.salePrice)} sale price  ·  '
                '${money.moneyFmt(disposition.gainLoss)} ${gainIsPositive ? 'gain' : 'loss'}',
                style: TextStyle(
                  fontSize: 12,
                  color: colorScheme.onSurfaceVariant,
                ),
              ),
              trailing: onEdit == null && onDelete == null
                  ? null
                  : Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        if (onEdit != null)
                          IconButton(
                            icon: const Icon(Icons.edit_outlined, size: 18),
                            onPressed: onEdit,
                            tooltip: 'Edit property sale',
                          ),
                        if (onDelete != null)
                          IconButton(
                            icon: const Icon(Icons.delete_outline, size: 18),
                            onPressed: onDelete,
                            tooltip: 'Delete property sale',
                          ),
                      ],
                    ),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 4),
              child: Wrap(
                spacing: 16,
                runSpacing: 8,
                children: [
                  _ScheduleMetric(
                    label: 'Net proceeds',
                    value: money.moneyFmt(disposition.netSaleProceeds),
                  ),
                  _ScheduleMetric(
                    label: 'Adjusted basis',
                    value: money.moneyFmt(disposition.adjustedBasis),
                  ),
                  _ScheduleMetric(
                    label: 'Sale-year depreciation',
                    value: money.moneyFmt(disposition.saleYearDepreciation),
                  ),
                  _ScheduleMetric(
                    label: 'Total depreciation',
                    value: money.moneyFmt(disposition.totalDepreciation),
                  ),
                  _ScheduleMetric(
                    label: 'Section 1250 est.',
                    value: money.moneyFmt(
                      disposition.unrecapturedSection1250Gain,
                    ),
                  ),
                  if (disposition.sellingCosts > 0)
                    _ScheduleMetric(
                      label: 'Selling costs',
                      value: money.moneyFmt(disposition.sellingCosts),
                    ),
                  if (disposition.buyerName != null &&
                      disposition.buyerName!.isNotEmpty)
                    _ScheduleMetric(
                      label: 'Buyer',
                      value: disposition.buyerName!,
                    ),
                ],
              ),
            ),
            if (disposition.memo != null && disposition.memo!.isNotEmpty)
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 4, 16, 4),
                child: Text(
                  disposition.memo!,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: colorScheme.onSurfaceVariant,
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

// ── Loan tile ────────────────────────────────────────────────────────────────

class _LoanTile extends ConsumerStatefulWidget {
  const _LoanTile({
    required this.loan,
    required this.onEdit,
    required this.onDelete,
  });

  final PropertyLoan loan;
  final VoidCallback? onEdit;
  final VoidCallback? onDelete;

  @override
  ConsumerState<_LoanTile> createState() => _LoanTileState();
}

class _LoanTileState extends ConsumerState<_LoanTile> {
  bool _expanded = false;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final loan = widget.loan;
    final statusIsActive = loan.status.toLowerCase() == 'active';

    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: Column(
        children: [
          ListTile(
            onTap: () => setState(() => _expanded = !_expanded),
            leading: CircleAvatar(
              radius: 20,
              backgroundColor: statusIsActive
                  ? colorScheme.primaryContainer
                  : colorScheme.surfaceContainerHighest,
              child: Icon(
                Icons.account_balance_outlined,
                size: 18,
                color: statusIsActive
                    ? colorScheme.onPrimaryContainer
                    : colorScheme.onSurfaceVariant,
              ),
            ),
            title: Text(
              loan.lender,
              style: theme.textTheme.bodyMedium?.copyWith(
                fontWeight: FontWeight.w600,
              ),
            ),
            subtitle: Text(
              '${_formatCurrency(loan.currentBalance)} balance  ·  '
              '${_formatCurrency(loan.monthlyPrincipalInterest + loan.monthlyEscrow)}/mo  ·  '
              '${_loanStatusLabel(loan.status)}',
              style: TextStyle(
                fontSize: 12,
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            trailing: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                IconButton(
                  key: Key('loan-schedule-toggle-${loan.id}'),
                  icon: Icon(
                    _expanded ? Icons.expand_less : Icons.expand_more,
                    size: 20,
                  ),
                  onPressed: () => setState(() => _expanded = !_expanded),
                  tooltip: _expanded
                      ? 'Hide amortization schedule'
                      : 'View amortization schedule',
                ),
                if (widget.onEdit != null)
                  IconButton(
                    icon: const Icon(Icons.edit_outlined, size: 18),
                    onPressed: widget.onEdit,
                    tooltip: 'Edit loan',
                  ),
                if (widget.onDelete != null)
                  IconButton(
                    icon: const Icon(Icons.delete_outline, size: 18),
                    onPressed: widget.onDelete,
                    tooltip: 'Delete loan',
                  ),
              ],
            ),
          ),
          if (_expanded) ...[
            const Divider(height: 1),
            _LoanPaymentSchedule(
              loanId: loan.id,
              propertyId: loan.propertyId,
              canManage: widget.onEdit != null,
            ),
          ],
        ],
      ),
    );
  }
}

class _LoanPaymentSchedule extends ConsumerStatefulWidget {
  const _LoanPaymentSchedule({
    required this.loanId,
    required this.propertyId,
    required this.canManage,
  });

  final int loanId;
  final int propertyId;
  final bool canManage;

  @override
  ConsumerState<_LoanPaymentSchedule> createState() =>
      _LoanPaymentScheduleState();
}

class _LoanPaymentScheduleState extends ConsumerState<_LoanPaymentSchedule> {
  int? _postingPaymentId;

  Future<void> _postPayment(int paymentId) async {
    if (_postingPaymentId != null) return;
    setState(() => _postingPaymentId = paymentId);
    try {
      await ref
          .read(propertyLoansRepositoryProvider)
          .postLoanPayment(loanId: widget.loanId, paymentId: paymentId);
      ref.invalidate(loanPaymentsProvider(widget.loanId));
      await ref
          .read(propertyLoansProvider(widget.propertyId).notifier)
          .refresh();
      if (mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(const SnackBar(content: Text('Loan payment recorded.')));
      }
    } catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              error is ApiException ? error.message : error.toString(),
            ),
          ),
        );
      }
    } finally {
      if (mounted) setState(() => _postingPaymentId = null);
    }
  }

  @override
  Widget build(BuildContext context) {
    final paymentsAsync = ref.watch(loanPaymentsProvider(widget.loanId));
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Padding(
      key: Key('loan-amortization-schedule-${widget.loanId}'),
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 14),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            'Amortization schedule',
            style: theme.textTheme.labelLarge?.copyWith(
              fontWeight: FontWeight.w700,
              color: colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 8),
          paymentsAsync.when(
            loading: () => const Padding(
              padding: EdgeInsets.symmetric(vertical: 12),
              child: Center(child: CircularProgressIndicator()),
            ),
            error: (e, _) => _InlineError(
              message: e is ApiException ? e.message : e.toString(),
            ),
            data: (payments) {
              if (payments.isEmpty) {
                return Text(
                  'No payments generated yet. The debt-service worker fills this in monthly.',
                  style: TextStyle(color: colorScheme.onSurfaceVariant),
                );
              }
              return Column(
                children: [
                  for (final payment in payments)
                    _LoanPaymentRow(
                      payment: payment,
                      isPosting: _postingPaymentId == payment.id,
                      onRecordPaid:
                          widget.canManage &&
                              payment.status.toLowerCase() != 'paid'
                          ? () => _postPayment(payment.id)
                          : null,
                    ),
                ],
              );
            },
          ),
        ],
      ),
    );
  }
}

class _LoanPaymentRow extends StatelessWidget {
  const _LoanPaymentRow({
    required this.payment,
    required this.isPosting,
    this.onRecordPaid,
  });

  final LoanPayment payment;
  final bool isPosting;
  final VoidCallback? onRecordPaid;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final statusColor = payment.status.toLowerCase() == 'paid'
        ? colorScheme.primary
        : colorScheme.onSurfaceVariant;

    return Container(
      key: Key('loan-payment-row-${payment.id}'),
      padding: const EdgeInsets.symmetric(vertical: 8),
      decoration: BoxDecoration(
        border: Border(top: BorderSide(color: colorScheme.outlineVariant)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  payment.periodKey.isEmpty ? 'Period' : payment.periodKey,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
                ),
              ),
              Text(
                _formatCurrency(payment.totalAmount),
                style: theme.textTheme.bodyMedium?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
              ),
            ],
          ),
          const SizedBox(height: 2),
          Row(
            children: [
              Expanded(
                child: Text(
                  'Due ${_formatDate(payment.dueDate)}',
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: colorScheme.onSurfaceVariant,
                  ),
                ),
              ),
              Text(
                _loanPaymentStatusLabel(payment.status),
                style: theme.textTheme.bodySmall?.copyWith(
                  color: statusColor,
                  fontWeight: FontWeight.w600,
                ),
              ),
            ],
          ),
          const SizedBox(height: 4),
          Wrap(
            spacing: 14,
            runSpacing: 4,
            children: [
              _ScheduleMetric(
                label: 'Interest',
                value: _formatCurrency(payment.interestAmount),
              ),
              _ScheduleMetric(
                label: 'Principal',
                value: _formatCurrency(payment.principalAmount),
              ),
              _ScheduleMetric(
                label: 'Escrow',
                value: _formatCurrency(payment.escrowAmount),
              ),
              _ScheduleMetric(
                label: 'Balance',
                value: _formatCurrency(payment.balanceAfter),
              ),
            ],
          ),
          if (payment.status.toLowerCase() == 'paid')
            AccountingImpactCard(
              sourceType: JournalSourceType.loanPayment,
              sourceId: payment.id,
            ),
          if (onRecordPaid != null) ...[
            const SizedBox(height: 8),
            Align(
              alignment: Alignment.centerRight,
              child: FilledButton.tonal(
                key: Key('loan-payment-post-${payment.id}'),
                onPressed: isPosting ? null : onRecordPaid,
                child: Text(isPosting ? 'Recording…' : 'Record paid'),
              ),
            ),
          ],
          if (payment.paymentDoesNotCoverInterest) ...[
            const SizedBox(height: 6),
            Row(
              children: [
                Icon(
                  Icons.warning_amber_outlined,
                  size: 16,
                  color: colorScheme.tertiary,
                ),
                const SizedBox(width: 6),
                Expanded(
                  child: Text(
                    "Payment didn't cover interest this period.",
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: colorScheme.tertiary,
                    ),
                  ),
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

class _ScheduleMetric extends StatelessWidget {
  const _ScheduleMetric({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          label,
          style: theme.textTheme.labelSmall?.copyWith(
            color: colorScheme.onSurfaceVariant,
          ),
        ),
        Text(
          value,
          style: theme.textTheme.bodySmall?.copyWith(
            fontWeight: FontWeight.w600,
          ),
        ),
      ],
    );
  }
}

String _loanStatusLabel(String status) => switch (status) {
  'PaidOff' => 'Paid off',
  _ => status,
};

String _loanPaymentStatusLabel(String status) => switch (status) {
  'Paid' => 'Paid',
  'Scheduled' => 'Scheduled',
  _ => status,
};

String _decimalLabel(double value) => value == value.roundToDouble()
    ? value.toStringAsFixed(0)
    : value.toString();

// ── Inline error ──────────────────────────────────────────────────────────────

class _InlineError extends StatelessWidget {
  const _InlineError({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 12),
      child: Row(
        children: [
          Icon(Icons.error_outline, size: 18, color: colorScheme.error),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              message,
              style: TextStyle(color: colorScheme.error, fontSize: 13),
            ),
          ),
        ],
      ),
    );
  }
}
