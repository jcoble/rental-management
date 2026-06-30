import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../leases/leases_repository.dart';
import '../notices/create_tenant_notice.dart';
import '../units/unit_command_center_screen.dart';
import '../units/unit_navigation.dart';
import 'tenants_list_screen.dart';
import 'tenants_repository.dart';

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

String _fmt(DateTime d) => '${_monthNames[d.month]} ${d.day}, ${d.year}';

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

/// Loads a tenant by id, then shows [TenantDetailScreen]. Use this when the
/// caller only has a tenant id (e.g. a lease's tenant link, or an approved
/// application that created a tenant).
class TenantDetailLoaderScreen extends ConsumerWidget {
  const TenantDetailLoaderScreen({super.key, required this.tenantId});

  final int tenantId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(tenantDetailProvider(tenantId));
    return async.when(
      loading: () =>
          const Scaffold(body: Center(child: CircularProgressIndicator())),
      error: (e, _) => Scaffold(
        appBar: AppBar(title: const Text('Tenant')),
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
      data: (tenant) => TenantDetailScreen(tenant: tenant),
    );
  }
}

/// Detail screen for a single tenant.
///
/// Shows tenant info with an edit button and their leases, loaded from
/// GET /leases filtered by tenantId.
class TenantDetailScreen extends ConsumerStatefulWidget {
  const TenantDetailScreen({super.key, required this.tenant});

  final Tenant tenant;

  @override
  ConsumerState<TenantDetailScreen> createState() => _TenantDetailScreenState();
}

class _TenantDetailScreenState extends ConsumerState<TenantDetailScreen> {
  late Tenant _tenant;
  bool _portalBusy = false;

  @override
  void initState() {
    super.initState();
    _tenant = widget.tenant;
    // tenantLeasesProvider self-loads on first watch (TenantLeasesNotifier.build
    // calls Future.microtask(load)), so an explicit load() here just double-fetches.
    //
    // The list endpoint omits portalAccess, so a tenant pushed from the list
    // arrives without it; pull the full record to populate the portal card.
    // (Deep-link/loader entries already carry it, so this is skipped for them.)
    if (widget.tenant.portalAccess == null) {
      Future.microtask(_loadFullTenant);
    }
  }

  Future<void> _loadFullTenant() async {
    try {
      final full =
          await ref.read(tenantsRepositoryProvider).getTenant(_tenant.id);
      if (mounted) setState(() => _tenant = full);
    } on ApiException {
      // Non-fatal: the rest of the screen still works from the list-loaded copy.
    }
  }

  Future<void> _refresh() async {
    await Future.wait<void>([
      ref.read(tenantDetailProvider(_tenant.id).notifier).refresh(),
      ref.read(tenantLeasesProvider(_tenant.id).notifier).refresh(),
    ]);
    final updated = ref.read(tenantDetailProvider(_tenant.id));
    updated.whenData((t) {
      if (mounted) setState(() => _tenant = t);
    });
  }

  void _showEditSheet(BuildContext context) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => TenantFormSheet(existing: _tenant, onSaved: _refresh),
    );
  }

  Future<void> _createNotice() async {
    await showCreateTenantNoticeFlow(
      context,
      ref,
      tenantId: _tenant.id,
      tenantName: '${_tenant.firstName} ${_tenant.lastName}'.trim(),
    );
  }

  void _toast(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(message)));
  }

  /// Staff toggle: turn this tenant's portal sign-in on or off.
  Future<void> _togglePortalAccess(bool enabled) async {
    setState(() => _portalBusy = true);
    try {
      final newState = await ref
          .read(tenantsRepositoryProvider)
          .setPortalAccess(_tenant.id, enabled: enabled);
      if (!mounted) return;
      setState(() => _tenant = _tenant.copyWith(portalAccess: newState));
      _toast(newState == 'active'
          ? 'Portal access turned on.'
          : 'Portal access turned off.');
    } on ApiException catch (e) {
      _toast(e.message);
    } finally {
      if (mounted) setState(() => _portalBusy = false);
    }
  }

  /// Staff action: email the tenant their portal invite (provisioning the login
  /// if needed). Re-pulls the tenant afterward since a 'none' tenant becomes
  /// 'active' once provisioned.
  Future<void> _sendPortalInvite() async {
    setState(() => _portalBusy = true);
    try {
      final repo = ref.read(tenantsRepositoryProvider);
      final result = await repo.sendPortalInvite(_tenant.id);
      final full = await repo.getTenant(_tenant.id);
      if (!mounted) return;
      setState(() => _tenant = full);
      final to = (result.email != null && result.email!.isNotEmpty)
          ? result.email!
          : '${_tenant.firstName} ${_tenant.lastName}'.trim();
      _toast(result.alreadyExisted
          ? 'Portal invite resent to $to.'
          : 'Portal invite sent to $to.');
    } on ApiException catch (e) {
      _toast(e.message);
    } finally {
      if (mounted) setState(() => _portalBusy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final leasesAsync = ref.watch(tenantLeasesProvider(_tenant.id));

    return Scaffold(
      appBar: AppBar(
        title: Text(
          '${_tenant.firstName} ${_tenant.lastName}',
          overflow: TextOverflow.ellipsis,
        ),
        actions: [
          IconButton(
            icon: const Icon(Icons.campaign_outlined),
            tooltip: 'Create / send notice',
            onPressed: _createNotice,
          ),
          IconButton(
            icon: const Icon(Icons.edit_outlined),
            tooltip: 'Edit tenant',
            onPressed: () => _showEditSheet(context),
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            // ── Tenant info card ───────────────────────────────────────────
            _TenantInfoCard(
              tenant: _tenant,
              theme: theme,
              colorScheme: colorScheme,
            ),
            const SizedBox(height: 16),

            // ── Resident portal access ─────────────────────────────────────
            _PortalAccessCard(
              portalAccess: _tenant.portalAccess,
              email: _tenant.email,
              busy: _portalBusy,
              onToggle: _portalBusy ? null : _togglePortalAccess,
              onSendInvite: _portalBusy ? null : _sendPortalInvite,
            ),
            const SizedBox(height: 24),

            // ── Leases ─────────────────────────────────────────────────────
            Text(
              'Leases',
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
                if (leases.isEmpty) {
                  return Padding(
                    padding: const EdgeInsets.symmetric(vertical: 16),
                    child: Text(
                      'No leases for this tenant.',
                      style: TextStyle(color: colorScheme.onSurfaceVariant),
                    ),
                  );
                }
                // Active leases first, then the rest.
                final sorted = [...leases]
                  ..sort((a, b) {
                    final aActive = a.status.toLowerCase() == 'active' ? 0 : 1;
                    final bActive = b.status.toLowerCase() == 'active' ? 0 : 1;
                    return aActive.compareTo(bActive);
                  });
                return Column(
                  children: sorted
                      .map((l) => _LeaseSummaryTile(lease: l))
                      .toList(),
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

// ── Tenant info card ──────────────────────────────────────────────────────────

class _TenantInfoCard extends StatelessWidget {
  const _TenantInfoCard({
    required this.tenant,
    required this.theme,
    required this.colorScheme,
  });

  final Tenant tenant;
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
                CircleAvatar(
                  radius: 26,
                  backgroundColor: colorScheme.primaryContainer,
                  child: Text(
                    _initials(tenant.firstName, tenant.lastName),
                    style: TextStyle(
                      fontWeight: FontWeight.w700,
                      fontSize: 16,
                      color: colorScheme.onPrimaryContainer,
                    ),
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        '${tenant.firstName} ${tenant.lastName}',
                        style: theme.textTheme.titleLarge?.copyWith(
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                      if (tenant.email != null)
                        Text(
                          tenant.email!,
                          style: theme.textTheme.bodyMedium?.copyWith(
                            color: colorScheme.onSurfaceVariant,
                          ),
                        ),
                    ],
                  ),
                ),
              ],
            ),
            const SizedBox(height: 16),
            const Divider(height: 1),
            const SizedBox(height: 12),
            Wrap(
              spacing: 24,
              runSpacing: 8,
              children: [
                if (tenant.phone != null)
                  _KeyValue(label: 'Phone', value: tenant.phone!),
                if (tenant.emergencyContact != null)
                  _KeyValue(
                    label: 'Emergency',
                    value: tenant.emergencyContact!,
                  ),
                _KeyValue(label: 'Member since', value: _fmt(tenant.createdAt)),
                if (tenant.activeLeaseCount != null)
                  _KeyValue(
                    label: 'Active leases',
                    value: '${tenant.activeLeaseCount}',
                  ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  static String _initials(String first, String last) {
    final f = first.isNotEmpty ? first[0].toUpperCase() : '';
    final l = last.isNotEmpty ? last[0].toUpperCase() : '';
    return '$f$l';
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

// ── Resident portal access card ───────────────────────────────────────────────

/// Staff-facing card on the tenant detail screen mirroring the web tenant page:
/// shows the tenant's portal-login state and lets staff send/resend the invite
/// and flip access on/off. [portalAccess] is null while the single-GET is in
/// flight (a tenant opened from the list arrives without it).
class _PortalAccessCard extends StatelessWidget {
  const _PortalAccessCard({
    required this.portalAccess,
    required this.email,
    required this.busy,
    required this.onToggle,
    required this.onSendInvite,
  });

  final String? portalAccess;
  final String? email;
  final bool busy;
  final ValueChanged<bool>? onToggle;
  final VoidCallback? onSendInvite;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final hasEmail = email != null && email!.isNotEmpty;
    final loading = portalAccess == null;
    final active = portalAccess == 'active';
    final hasLogin = active || portalAccess == 'disabled';

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(Icons.badge_outlined, size: 20, color: cs.primary),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    'Resident portal',
                    style: theme.textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                if (busy)
                  const SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  ),
              ],
            ),
            const SizedBox(height: 6),
            if (loading)
              Text(
                'Checking portal access…',
                style: theme.textTheme.bodySmall?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              )
            else ...[
              Text(
                _statusText(
                  hasEmail: hasEmail,
                  hasLogin: hasLogin,
                  active: active,
                ),
                style: theme.textTheme.bodySmall?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              ),
              if (hasLogin)
                SwitchListTile(
                  contentPadding: EdgeInsets.zero,
                  title: const Text('Portal access'),
                  subtitle: Text(
                    active
                        ? 'They can sign in to the resident portal.'
                        : 'Sign-in is turned off.',
                  ),
                  value: active,
                  onChanged: onToggle,
                ),
              const SizedBox(height: 8),
              Align(
                alignment: Alignment.centerLeft,
                child: OutlinedButton.icon(
                  onPressed: hasEmail ? onSendInvite : null,
                  icon: const Icon(Icons.send_outlined, size: 18),
                  label: Text(active ? 'Resend invite' : 'Send portal invite'),
                ),
              ),
              if (!hasEmail)
                Padding(
                  padding: const EdgeInsets.only(top: 6),
                  child: Text(
                    'Add an email to this tenant to give them portal access.',
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: cs.onSurfaceVariant,
                    ),
                  ),
                ),
            ],
          ],
        ),
      ),
    );
  }

  String _statusText({
    required bool hasEmail,
    required bool hasLogin,
    required bool active,
  }) {
    if (!hasLogin) {
      return hasEmail
          ? 'This tenant doesn’t have portal access yet. Send them an invite to set it up.'
          : 'This tenant doesn’t have portal access yet.';
    }
    return active ? 'Portal access is on.' : 'Portal access is off.';
  }
}

// ── Lease summary tile (read-only) ────────────────────────────────────────────

class _LeaseSummaryTile extends StatelessWidget {
  const _LeaseSummaryTile({required this.lease});

  final Lease lease;

  void _openLease(BuildContext context) {
    openUnitCommandCenter(
      context,
      unitId: lease.unitId,
      initialTab: UnitCommandCenterTab.lease,
      lease: lease,
      tenantId: lease.tenantId,
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    final isActive = lease.status.toLowerCase() == 'active';

    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: InkWell(
        onTap: () => _openLease(context),
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
                      lease.propertyName ?? 'Lease #${lease.leaseNumber}',
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
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'Unit ${lease.unitNumber ?? lease.unitId}  ·  '
                      '${_fmt(lease.startDate)} – ${_fmt(lease.endDate)}',
                      style: TextStyle(
                        fontSize: 12,
                        color: colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ),
                  if (!isActive)
                    Container(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 7,
                        vertical: 2,
                      ),
                      decoration: BoxDecoration(
                        color: colorScheme.surfaceContainerHighest,
                        borderRadius: BorderRadius.circular(12),
                      ),
                      child: Text(
                        lease.status,
                        style: TextStyle(
                          fontSize: 10,
                          fontWeight: FontWeight.w600,
                          color: colorScheme.onSurfaceVariant,
                        ),
                      ),
                    ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

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
