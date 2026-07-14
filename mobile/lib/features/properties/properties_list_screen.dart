import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/models.dart';
import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/auth/mobile_access_policy.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'property_form_sheet.dart';
import 'properties_repository.dart';
import 'property_detail_screen.dart';

/// Opens the "New property" bottom sheet and resolves to `true` once a property
/// was created (the sheet pops `true` on save), or `null`/`false` if dismissed.
///
/// Shared by the properties-list FAB and the first-login Live setup screen so
/// both use the same create flow (no duplicate form). [onSaved] still fires on
/// save for callers that want to refresh a list in place.
Future<bool?> showAddPropertySheet(
  BuildContext context, {
  VoidCallback? onSaved,
}) async {
  final saved = await showPropertyFormSheet(
    context,
    onSaved: (_) => onSaved?.call(),
  );
  return saved == null ? null : true;
}

/// Full-page list of properties with pull-to-refresh and an add-property FAB.
class PropertiesListScreen extends ConsumerStatefulWidget {
  const PropertiesListScreen({super.key});

  @override
  ConsumerState<PropertiesListScreen> createState() =>
      _PropertiesListScreenState();
}

class _PropertiesListScreenState extends ConsumerState<PropertiesListScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(propertiesProvider.notifier).load());
  }

  Future<void> _refresh() => ref.read(propertiesProvider.notifier).refresh();

  void _openDetail(BuildContext context, Property property) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => PropertyDetailScreen(property: property),
      ),
    );
  }

  void _showAddSheet(BuildContext context) {
    showAddPropertySheet(
      context,
      onSaved: () => ref.read(propertiesProvider.notifier).refresh(),
    );
  }

  @override
  Widget build(BuildContext context) {
    final propertiesAsync = ref.watch(propertiesProvider);
    final auth = ref.watch(authControllerProvider);
    final canManageRentals =
        auth is AuthStateAuthenticated &&
        canUseMobileCapabilityAction(
          experience: auth.activeExperience,
          capabilities: auth.capabilities,
          capability: 'rentals.manage',
          experiences: const {WorkspaceExperience.management},
        );

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Properties')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'properties-fab',
        primaryAction: canManageRentals
            ? MobileQuickAction(
                label: 'Add property',
                icon: Icons.add,
                onPressed: () => _showAddSheet(context),
              )
            : null,
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: propertiesAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(
            message: e is ApiException ? e.message : e.toString(),
            onRetry: _refresh,
          ),
          data: (list) {
            if (list.isEmpty) {
              return _EmptyBody(
                onAdd: canManageRentals ? () => _showAddSheet(context) : null,
              );
            }
            final bottomInset = MediaQuery.paddingOf(context).bottom;
            return ListView.separated(
              padding: EdgeInsets.fromLTRB(16, 16, 16, 160.0 + bottomInset),
              itemCount: list.length,
              separatorBuilder: (context, index) => const MobileM3ListDivider(),
              itemBuilder: (context, index) {
                final property = list[index];
                return _PropertyCard(
                  property: property,
                  position: MobileM3ListItemPositionForIndex.forIndex(
                    index,
                    list.length,
                  ),
                  onTap: () => _openDetail(context, property),
                );
              },
            );
          },
        ),
      ),
    );
  }
}

// ── Property Card ─────────────────────────────────────────────────────────────

class _PropertyCard extends StatelessWidget {
  const _PropertyCard({
    required this.property,
    required this.position,
    required this.onTap,
  });

  final Property property;
  final MobileM3ListItemPosition position;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final unitCount = property.unitCount ?? 0;
    final occupied = property.occupiedUnits ?? 0;

    return MobileM3ListItem(
      position: position,
      onTap: onTap,
      leading: MobileM3LeadingIcon(
        icon: Icons.apartment_outlined,
        backgroundColor: colorScheme.primaryContainer,
        foregroundColor: colorScheme.onPrimaryContainer,
      ),
      title: Text(
        property.name,
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w600,
        ),
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
      ),
      supporting: [
        Text(
          '${property.addressLine1}, ${property.city}, ${property.state} ${property.postalCode}',
          style: theme.textTheme.bodySmall?.copyWith(
            color: colorScheme.onSurfaceVariant,
          ),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
      ],
      meta: Wrap(
        spacing: 8,
        runSpacing: 4,
        children: [
          _StatusChip(status: property.status, colorScheme: colorScheme),
          _MetaChip(
            icon: Icons.apartment_outlined,
            label: '$unitCount ${unitCount == 1 ? 'unit' : 'units'}',
          ),
          _MetaChip(icon: Icons.person_outline, label: '$occupied occupied'),
          _MetaChip(
            icon: Icons.home_outlined,
            label: _formatPropertyType(property.type),
          ),
        ],
      ),
      trailing: Icon(Icons.chevron_right, color: colorScheme.onSurfaceVariant),
    );
  }
}

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.status, required this.colorScheme});

  final String status;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    final isActive = status.toLowerCase() == 'active';
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: isActive
            ? colorScheme.primaryContainer
            : colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        status,
        style: TextStyle(
          fontSize: 11,
          fontWeight: FontWeight.w600,
          color: isActive
              ? colorScheme.onPrimaryContainer
              : colorScheme.onSurfaceVariant,
        ),
      ),
    );
  }
}

String _formatPropertyType(String type) {
  return switch (type.trim()) {
    'MultiFamily' => 'Multi-family',
    'SingleFamily' => 'Single family',
    final other => other.replaceAll('_', ' '),
  };
}

class _MetaChip extends StatelessWidget {
  const _MetaChip({required this.icon, required this.label});

  final IconData icon;
  final String label;

  @override
  Widget build(BuildContext context) {
    final color = Theme.of(context).colorScheme.onSurfaceVariant;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(icon, size: 14, color: color),
        const SizedBox(width: 3),
        Text(label, style: TextStyle(fontSize: 12, color: color)),
      ],
    );
  }
}

// ── Empty / Error ─────────────────────────────────────────────────────────────

/// A15: a welcoming first-run empty state — a plain sentence explaining what a
/// property is, plus a primary "Add your first property" button (not just a
/// "tap +" hint), so the next step is obvious.
class _EmptyBody extends StatelessWidget {
  const _EmptyBody({required this.onAdd});

  final VoidCallback? onAdd;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              Icons.apartment_outlined,
              size: 48,
              color: colorScheme.onSurfaceVariant,
            ),
            const SizedBox(height: 12),
            Text(
              'No rentals yet',
              style: theme.textTheme.titleMedium?.copyWith(
                color: colorScheme.onSurface,
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 6),
            Text(
              'A property is one building or address. Add your first to get '
              'started.',
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 20),
            if (onAdd != null)
              FilledButton.icon(
                onPressed: onAdd,
                icon: const Icon(Icons.add),
                label: const Text('Add your first property'),
              ),
          ],
        ),
      ),
    );
  }
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.error_outline, size: 40, color: colorScheme.error),
            const SizedBox(height: 12),
            Text(
              message,
              textAlign: TextAlign.center,
              style: TextStyle(color: colorScheme.error),
            ),
            const SizedBox(height: 16),
            FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}
