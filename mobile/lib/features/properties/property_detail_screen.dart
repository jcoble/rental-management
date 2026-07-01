import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/models.dart';
import '../../core/api/api_exception.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import '../scan/scan_capture.dart';
import '../scan/scan_review_screen.dart';
import '../units/unit_command_center_screen.dart';
import '../units/unit_navigation.dart';
import 'properties_repository.dart';
import 'property_form_sheet.dart';
import 'property_labels.dart';
import 'property_loan_form_sheet.dart';
import 'property_loans_repository.dart';

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

String _unitTitle(Unit unit, {required bool propertyIsUnit}) {
  final number = unit.unitNumber.trim();
  if (propertyIsUnit) return number.isEmpty ? 'Rental space' : number;
  return number.isEmpty ? 'Unit' : 'Unit $number';
}

void _openLeaseDetail(BuildContext context, Lease lease) {
  openUnitCommandCenter(
    context,
    unitId: lease.unitId,
    initialTab: UnitCommandCenterTab.lease,
    lease: lease,
  );
}

void _openUnitOverview(BuildContext context, Unit unit) {
  openUnitCommandCenter(
    context,
    unitId: unit.id,
    initialTab: UnitCommandCenterTab.overview,
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
    final async = ref.watch(propertyDetailProvider(propertyId));
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
      data: (property) => PropertyDetailScreen(property: property),
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
  late Property _property;

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

  // unitsProvider and propertyLeasesProvider both self-load on first watch
  // (their notifier build() calls Future.microtask(load)), so no explicit
  // initState load is needed — adding one just double-fetches.

  Future<void> _refresh() async {
    final propertyId = _property.id;
    await Future.wait<void>([
      ref.read(unitsProvider(propertyId).notifier).refresh(),
      ref.read(propertyLeasesProvider(propertyId).notifier).refresh(),
      ref.read(propertiesRepositoryProvider).getProperty(propertyId).then((
        property,
      ) {
        if (mounted) setState(() => _property = property);
      }),
      ref.read(propertyLoansProvider(propertyId).notifier).refresh(),
    ]);
  }

  /// The active lease for [unitId] from the property's leases, or null. Used to
  /// drill from an occupied unit to its current lease.
  Lease? _activeLeaseForUnit(List<Lease> leases, int unitId) {
    for (final l in leases) {
      if (l.unitId == unitId && l.status.toLowerCase() == 'active') return l;
    }
    return null;
  }

  void _showAddUnitSheet(BuildContext context) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _AddUnitSheet(
        propertyId: _property.id,
        onSaved: () => ref.read(unitsProvider(_property.id).notifier).refresh(),
      ),
    );
  }

  void _showEditUnitSheet(BuildContext context, Unit unit) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _EditUnitSheet(
        unit: unit,
        onSaved: () => ref.read(unitsProvider(_property.id).notifier).refresh(),
      ),
    );
  }

  Future<void> _showEditPropertySheet(BuildContext context) async {
    final saved = await showPropertyFormSheet(
      context,
      property: _property,
      onSaved: (property) {
        ref.invalidate(propertyDetailProvider(property.id));
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
    final propertyIsUnit = isPropertyUnitType(property.type);
    final message = propertyIsUnit && unitCount == 1
        ? 'This will also remove the generated rental space if it is still empty. If it has leases, work orders, expenses, inspections, applications, appointments, or documents, the server will stop the delete.'
        : unitCount > 0
        ? 'This property still has $unitCount ${unitCount == 1 ? 'unit' : 'units'}. Remove units first unless this is an empty generated rental space.'
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

  @override
  Widget build(BuildContext context) {
    final property = _property;
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final unitsAsync = ref.watch(unitsProvider(property.id));
    final leasesAsync = ref.watch(propertyLeasesProvider(property.id));
    final loansAsync = ref.watch(propertyLoansProvider(property.id));
    final propertyIsUnit = isPropertyUnitType(property.type);

    return Scaffold(
      appBar: AppBar(
        title: Text(property.name, overflow: TextOverflow.ellipsis),
        actions: [
          IconButton(
            icon: const Icon(Icons.edit_outlined),
            tooltip: 'Edit property',
            onPressed: () => _showEditPropertySheet(context),
          ),
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
            const SizedBox(height: 24),

            // ── Units section ──────────────────────────────────────────────
            Row(
              children: [
                Expanded(
                  child: Text(
                    propertyIsUnit ? 'Rental space' : 'Units',
                    style: theme.textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                TextButton.icon(
                  onPressed: () => _showAddUnitSheet(context),
                  icon: const Icon(Icons.add, size: 18),
                  label: Text(propertyIsUnit ? 'Add another' : 'Add unit'),
                ),
              ],
            ),
            const SizedBox(height: 8),

            unitsAsync.when(
              loading: () => const Center(child: CircularProgressIndicator()),
              error: (e, _) => _InlineError(
                message: e is ApiException ? e.message : e.toString(),
              ),
              data: (units) {
                if (units.isEmpty) {
                  return Padding(
                    padding: const EdgeInsets.symmetric(vertical: 16),
                    child: Text(
                      propertyIsUnit
                          ? 'No rental space yet. Standalone homes get one automatically when the property is created.'
                          : 'No units yet. Tap "Add unit" to create one.',
                      style: TextStyle(color: colorScheme.onSurfaceVariant),
                    ),
                  );
                }
                // The property's leases (when loaded) let an occupied unit drill
                // through to its active lease.
                final leases = leasesAsync.asData?.value ?? const <Lease>[];
                return Column(
                  children: units
                      .map(
                        (u) => _UnitTile(
                          unit: u,
                          activeLease: _activeLeaseForUnit(leases, u.id),
                          propertyIsUnit: propertyIsUnit,
                          onEdit: () => _showEditUnitSheet(context, u),
                        ),
                      )
                      .toList(),
                );
              },
            ),

            const SizedBox(height: 24),

            // ── Mortgage / Loans section ──────────────────────────────────
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
            ),
            const SizedBox(height: 8),

            loansAsync.when(
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
                        onEdit: () => _showEditLoanSheet(context, loan),
                        onDelete: () => _confirmDeleteLoan(context, loan),
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

            // ── Leases section ─────────────────────────────────────────────
            Text(
              'Active Leases',
              style: theme.textTheme.titleMedium?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 8),

            leasesAsync.when(
              loading: () => const Center(child: CircularProgressIndicator()),
              error: (e, _) => _InlineError(
                message: e is ApiException ? e.message : e.toString(),
              ),
              data: (leases) {
                final active = leases
                    .where((l) => l.status.toLowerCase() == 'active')
                    .toList();
                if (active.isEmpty) {
                  return Padding(
                    padding: const EdgeInsets.symmetric(vertical: 16),
                    child: Text(
                      'No active leases on this property.',
                      style: TextStyle(color: colorScheme.onSurfaceVariant),
                    ),
                  );
                }
                return Column(
                  children: active.map((l) => _LeaseTile(lease: l)).toList(),
                );
              },
            ),

            const SizedBox(height: 32),
          ],
        ),
      ),
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
                if (property.ownerName != null)
                  _KeyValue(label: 'Owner', value: property.ownerName!),
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

// ── Unit tile ─────────────────────────────────────────────────────────────────

class _UnitTile extends StatelessWidget {
  const _UnitTile({
    required this.unit,
    required this.propertyIsUnit,
    required this.onEdit,
    this.activeLease,
  });

  final Unit unit;
  final bool propertyIsUnit;
  final VoidCallback onEdit;

  /// The unit's active lease, when occupied — enables drill-through to it.
  final Lease? activeLease;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final isOccupied = unit.status.toLowerCase() == 'occupied';
    final lease = activeLease;

    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: ListTile(
        onTap: () => lease == null
            ? _openUnitOverview(context, unit)
            : _openLeaseDetail(context, lease),
        leading: CircleAvatar(
          radius: 20,
          backgroundColor: isOccupied
              ? colorScheme.primaryContainer
              : colorScheme.surfaceContainerHighest,
          child: Icon(
            isOccupied ? Icons.person : Icons.home_outlined,
            size: 18,
            color: isOccupied
                ? colorScheme.onPrimaryContainer
                : colorScheme.onSurfaceVariant,
          ),
        ),
        title: Text(
          _unitTitle(unit, propertyIsUnit: propertyIsUnit),
          style: theme.textTheme.bodyMedium?.copyWith(
            fontWeight: FontWeight.w600,
          ),
        ),
        subtitle: Text(
          lease != null && lease.tenantName != null
              ? '${unit.bedrooms} bd / ${unit.bathrooms} ba  ·  '
                    '${lease.tenantName} · tap for lease'
              : '${unit.bedrooms} bd / ${unit.bathrooms} ba  ·  '
                    '${_formatCurrency(unit.marketRent)}/mo',
          style: TextStyle(fontSize: 12, color: colorScheme.onSurfaceVariant),
        ),
        trailing: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
              decoration: BoxDecoration(
                color: isOccupied
                    ? colorScheme.primaryContainer
                    : colorScheme.surfaceContainerHighest,
                borderRadius: BorderRadius.circular(12),
              ),
              child: Text(
                unit.status,
                style: TextStyle(
                  fontSize: 10,
                  fontWeight: FontWeight.w600,
                  color: isOccupied
                      ? colorScheme.onPrimaryContainer
                      : colorScheme.onSurfaceVariant,
                ),
              ),
            ),
            IconButton(
              icon: const Icon(Icons.edit_outlined, size: 18),
              onPressed: onEdit,
              tooltip: 'Edit unit',
            ),
          ],
        ),
      ),
    );
  }
}

// ── Lease tile (read-only) ────────────────────────────────────────────────────

class _LeaseTile extends StatelessWidget {
  const _LeaseTile({required this.lease});

  final Lease lease;

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
                      lease.tenantName ?? 'Lease #${lease.leaseNumber}',
                      style: theme.textTheme.bodyMedium?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                  ),
                  Text(
                    _formatCurrency(lease.monthlyRent),
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
                'Unit ${lease.unitNumber ?? lease.unitId}  ·  '
                '${_formatDate(lease.startDate)} – ${_formatDate(lease.endDate)}',
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

// ── Loan tile ────────────────────────────────────────────────────────────────

class _LoanTile extends StatelessWidget {
  const _LoanTile({
    required this.loan,
    required this.onEdit,
    required this.onDelete,
  });

  final PropertyLoan loan;
  final VoidCallback onEdit;
  final VoidCallback onDelete;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final statusIsActive = loan.status.toLowerCase() == 'active';

    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: ListTile(
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
          style: TextStyle(fontSize: 12, color: colorScheme.onSurfaceVariant),
        ),
        trailing: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            IconButton(
              icon: const Icon(Icons.edit_outlined, size: 18),
              onPressed: onEdit,
              tooltip: 'Edit loan',
            ),
            IconButton(
              icon: const Icon(Icons.delete_outline, size: 18),
              onPressed: onDelete,
              tooltip: 'Delete loan',
            ),
          ],
        ),
      ),
    );
  }
}

String _loanStatusLabel(String status) => switch (status) {
  'PaidOff' => 'Paid off',
  _ => status,
};

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

// ── Add Unit bottom sheet ─────────────────────────────────────────────────────

class _AddUnitSheet extends ConsumerStatefulWidget {
  const _AddUnitSheet({required this.propertyId, required this.onSaved});

  final int propertyId;
  final VoidCallback onSaved;

  @override
  ConsumerState<_AddUnitSheet> createState() => _AddUnitSheetState();
}

class _AddUnitSheetState extends ConsumerState<_AddUnitSheet> {
  final _formKey = GlobalKey<FormState>();

  final _numberCtrl = TextEditingController();
  final _bedsCtrl = TextEditingController(text: '1');
  final _bathsCtrl = TextEditingController(text: '1');
  final _rentCtrl = TextEditingController(text: '1200');

  bool _saving = false;
  String? _error;

  static const _statuses = ['Vacant', 'Occupied', 'Maintenance'];
  String _selectedStatus = 'Vacant';

  @override
  void dispose() {
    _numberCtrl.dispose();
    _bedsCtrl.dispose();
    _bathsCtrl.dispose();
    _rentCtrl.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      await ref
          .read(propertiesRepositoryProvider)
          .createUnit(widget.propertyId, {
            'unitNumber': _numberCtrl.text.trim(),
            'bedrooms': int.tryParse(_bedsCtrl.text) ?? 1,
            'bathrooms': double.tryParse(_bathsCtrl.text) ?? 1.0,
            'marketRent': double.tryParse(_rentCtrl.text) ?? 0.0,
            'status': _selectedStatus,
          });

      widget.onSaved();
      if (mounted) Navigator.of(context).pop();
    } on ApiException catch (e) {
      setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    const gap = SizedBox(height: 12);

    return Form(
      key: _formKey,
      child: TabbedFormSheet(
        title: 'Add Unit',
        saveLabel: 'Add Unit',
        saving: _saving,
        error: _error,
        onSave: _submit,
        tabs: [
          TabbedFormStepSpec(
            label: 'Details',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  controller: _numberCtrl,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(labelText: 'Unit number'),
                  validator: (v) => (v == null || v.trim().isEmpty)
                      ? 'Unit number is required'
                      : null,
                ),
                gap,
                Row(
                  children: [
                    Expanded(
                      child: TextFormField(
                        controller: _bedsCtrl,
                        keyboardType: TextInputType.number,
                        textInputAction: TextInputAction.next,
                        decoration: const InputDecoration(labelText: 'Beds'),
                        validator: (v) => (v == null || int.tryParse(v) == null)
                            ? 'Enter a number'
                            : null,
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: TextFormField(
                        controller: _bathsCtrl,
                        keyboardType: const TextInputType.numberWithOptions(
                          decimal: true,
                        ),
                        textInputAction: TextInputAction.next,
                        decoration: const InputDecoration(labelText: 'Baths'),
                        validator: (v) =>
                            (v == null || double.tryParse(v) == null)
                            ? 'Enter a number'
                            : null,
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Rent',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  controller: _rentCtrl,
                  keyboardType: const TextInputType.numberWithOptions(
                    decimal: true,
                  ),
                  textInputAction: TextInputAction.done,
                  decoration: const InputDecoration(
                    labelText: 'Market rent (\$)',
                  ),
                  validator: (v) => (v == null || double.tryParse(v) == null)
                      ? 'Enter an amount'
                      : null,
                ),
                gap,
                DropdownButtonFormField<String>(
                  initialValue: _selectedStatus,
                  decoration: const InputDecoration(labelText: 'Status'),
                  items: _statuses
                      .map((s) => DropdownMenuItem(value: s, child: Text(s)))
                      .toList(),
                  onChanged: (v) {
                    if (v != null) setState(() => _selectedStatus = v);
                  },
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

// ── Edit Unit bottom sheet ────────────────────────────────────────────────────

class _EditUnitSheet extends ConsumerStatefulWidget {
  const _EditUnitSheet({required this.unit, required this.onSaved});

  final Unit unit;
  final VoidCallback onSaved;

  @override
  ConsumerState<_EditUnitSheet> createState() => _EditUnitSheetState();
}

class _EditUnitSheetState extends ConsumerState<_EditUnitSheet> {
  final _formKey = GlobalKey<FormState>();

  late final TextEditingController _numberCtrl;
  late final TextEditingController _bedsCtrl;
  late final TextEditingController _bathsCtrl;
  late final TextEditingController _rentCtrl;

  bool _saving = false;
  String? _error;

  static const _statuses = ['Vacant', 'Occupied', 'Maintenance'];
  late String _selectedStatus;

  @override
  void initState() {
    super.initState();
    _numberCtrl = TextEditingController(text: widget.unit.unitNumber);
    _bedsCtrl = TextEditingController(text: widget.unit.bedrooms.toString());
    _bathsCtrl = TextEditingController(text: widget.unit.bathrooms.toString());
    _rentCtrl = TextEditingController(text: widget.unit.marketRent.toString());
    _selectedStatus = _statuses.contains(widget.unit.status)
        ? widget.unit.status
        : _statuses.first;
  }

  @override
  void dispose() {
    _numberCtrl.dispose();
    _bedsCtrl.dispose();
    _bathsCtrl.dispose();
    _rentCtrl.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      await ref.read(propertiesRepositoryProvider).updateUnit(widget.unit.id, {
        'unitNumber': _numberCtrl.text.trim(),
        'bedrooms': int.tryParse(_bedsCtrl.text) ?? widget.unit.bedrooms,
        'bathrooms': double.tryParse(_bathsCtrl.text) ?? widget.unit.bathrooms,
        'marketRent': double.tryParse(_rentCtrl.text) ?? widget.unit.marketRent,
        'status': _selectedStatus,
      });

      widget.onSaved();
      if (mounted) Navigator.of(context).pop();
    } on ApiException catch (e) {
      setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    const gap = SizedBox(height: 12);

    return Form(
      key: _formKey,
      child: TabbedFormSheet(
        title: 'Edit Unit ${widget.unit.unitNumber}',
        saveLabel: 'Save Changes',
        saving: _saving,
        error: _error,
        onSave: _submit,
        tabs: [
          TabbedFormStepSpec(
            label: 'Details',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  controller: _numberCtrl,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(labelText: 'Unit number'),
                  validator: (v) => (v == null || v.trim().isEmpty)
                      ? 'Unit number is required'
                      : null,
                ),
                gap,
                Row(
                  children: [
                    Expanded(
                      child: TextFormField(
                        controller: _bedsCtrl,
                        keyboardType: TextInputType.number,
                        textInputAction: TextInputAction.next,
                        decoration: const InputDecoration(labelText: 'Beds'),
                        validator: (v) => (v == null || int.tryParse(v) == null)
                            ? 'Enter a number'
                            : null,
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: TextFormField(
                        controller: _bathsCtrl,
                        keyboardType: const TextInputType.numberWithOptions(
                          decimal: true,
                        ),
                        textInputAction: TextInputAction.next,
                        decoration: const InputDecoration(labelText: 'Baths'),
                        validator: (v) =>
                            (v == null || double.tryParse(v) == null)
                            ? 'Enter a number'
                            : null,
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Rent',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  controller: _rentCtrl,
                  keyboardType: const TextInputType.numberWithOptions(
                    decimal: true,
                  ),
                  textInputAction: TextInputAction.done,
                  decoration: const InputDecoration(
                    labelText: 'Market rent (\$)',
                  ),
                  validator: (v) => (v == null || double.tryParse(v) == null)
                      ? 'Enter an amount'
                      : null,
                ),
                gap,
                DropdownButtonFormField<String>(
                  initialValue: _selectedStatus,
                  decoration: const InputDecoration(labelText: 'Status'),
                  items: _statuses
                      .map((s) => DropdownMenuItem(value: s, child: Text(s)))
                      .toList(),
                  onChanged: (v) {
                    if (v != null) setState(() => _selectedStatus = v);
                  },
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
