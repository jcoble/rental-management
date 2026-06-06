import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/models/models.dart';
import '../../core/realtime/realtime_providers.dart';
import '../../core/voice/voice_command.dart';
import '../../core/voice/voice_command_controller.dart';
import '../accounting/accounting_models.dart';
import '../accounting/accounting_repository.dart';
import '../ai/ai_models.dart';
import '../ai/ai_repository.dart';
import '../ai/ai_tab.dart';
import '../leases/leases_list_screen.dart';
import '../maintenance/work_order_detail_screen.dart';
import '../maintenance/work_orders_repository.dart';
import '../maintenance/work_orders_screen.dart';
import '../messages/message_detail_screen.dart';
import '../messages/message_models.dart';
import '../messages/messages_list_screen.dart';
import '../messages/messages_repository.dart';
import '../payments/payments_screen.dart';
import '../portal/tenant_account_history_screen.dart';
import '../portal/tenant_portal_repository.dart';
import '../portal/tenant_work_order_detail_screen.dart';
import '../properties/properties_tab.dart';
import '../scan/scan_tab.dart';
import '../tenants/tenants_list_screen.dart';
import '../voice/tell_me_screen.dart';
import 'more_tab.dart';

// ---------------------------------------------------------------------------
// Briefing provider (home-tab only, autoDispose)
// ---------------------------------------------------------------------------

final _briefingProvider = FutureProvider.autoDispose<BriefingResponse>((ref) {
  return ref.watch(aiRepositoryProvider).briefing();
});

final _latestMessagesProvider = FutureProvider.autoDispose<List<Conversation>>((
  ref,
) async {
  final conversations = await ref
      .watch(messagesRepositoryProvider)
      .listConversations();
  return conversations.take(5).toList();
});

final _fieldQueueProvider = FutureProvider.autoDispose<List<WorkOrder>>((
  ref,
) async {
  final orders = await ref.watch(workOrdersRepositoryProvider).listWorkOrders();
  final open = orders
      .where((w) => !{'Completed', 'Cancelled'}.contains(w.status))
      .toList();

  int priorityRank(WorkOrder w) {
    switch (w.priority.toLowerCase()) {
      case 'emergency':
        return 0;
      case 'high':
        return 1;
      case 'normal':
        return 2;
      default:
        return 3;
    }
  }

  open.sort((a, b) {
    final priority = priorityRank(a).compareTo(priorityRank(b));
    if (priority != 0) return priority;
    return a.requestedAt.compareTo(b.requestedAt);
  });

  return open.take(5).toList();
});

// ---------------------------------------------------------------------------
// HomeShell
// ---------------------------------------------------------------------------

/// Bottom-navigation app shell.
///
/// Tabs: Home · Scan · Properties · Messages · More
class HomeShell extends ConsumerStatefulWidget {
  const HomeShell({super.key});

  @override
  ConsumerState<HomeShell> createState() => _HomeShellState();
}

class _HomeShellState extends ConsumerState<HomeShell> {
  int _selectedIndex = 0;

  /// Index of the Scan tab in the landlord [_tabs] (Home · Scan · …).
  static const _scanTabIndex = 1;

  static const _tabs = [
    _TabItem(label: 'Home', icon: Icons.home_outlined, activeIcon: Icons.home),
    _TabItem(
      label: 'Scan',
      icon: Icons.document_scanner_outlined,
      activeIcon: Icons.document_scanner,
    ),
    _TabItem(
      label: 'Properties',
      icon: Icons.apartment_outlined,
      activeIcon: Icons.apartment,
    ),
    _TabItem(
      label: 'Messages',
      icon: Icons.forum_outlined,
      activeIcon: Icons.forum,
    ),
    _TabItem(label: 'More', icon: Icons.more_horiz, activeIcon: Icons.menu),
  ];

  static const _tenantTabs = [
    _TabItem(label: 'Home', icon: Icons.home_outlined, activeIcon: Icons.home),
    _TabItem(
      label: 'Messages',
      icon: Icons.forum_outlined,
      activeIcon: Icons.forum,
    ),
    _TabItem(
      label: 'Maintenance',
      icon: Icons.build_outlined,
      activeIcon: Icons.build,
    ),
    _TabItem(label: 'More', icon: Icons.more_horiz, activeIcon: Icons.menu),
  ];

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      // Initialise the realtime watcher so it stays alive for the shell.
      ref.read(realtimeWatcherProvider);
      // A voice command may have cold-started the app (Assistant launched us)
      // before this shell built — pick up anything already waiting in the bus.
      final pending = ref.read(pendingVoiceCommandProvider);
      if (pending != null) _handleVoiceCommand(pending);
    });
  }

  /// Lands the landlord in the right place for a parsed voice command and shows
  /// a plain-language confirmation of what was understood. Scheduled post-frame
  /// so it can navigate / setState safely even when invoked from a build-time
  /// listener.
  void _handleVoiceCommand(VoiceCommand command) {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;

      final messenger = ScaffoldMessenger.of(context);
      void toast(String message) => messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(message)));

      // Voice commands are landlord-facing for now (matches on-device testing).
      final authState = ref.read(authControllerProvider);
      final isTenant =
          authState is AuthStateAuthenticated && authState.user.isTenant;
      if (isTenant) {
        toast("Voice commands aren't available for tenant accounts yet.");
        ref.read(pendingVoiceCommandProvider.notifier).consume();
        return;
      }

      final navigator = Navigator.of(context);
      switch (command.action) {
        case VoiceAction.scanDocument:
        case VoiceAction.logExpense:
          // Voice and tap converge on the capture flow (the flagship intake).
          navigator.popUntil((route) => route.isFirst);
          setState(() => _selectedIndex = _scanTabIndex);
        case VoiceAction.showOverdueRent:
          navigator.push<void>(
            MaterialPageRoute<void>(builder: (_) => const PaymentsScreen()),
          );
        case VoiceAction.openWorkOrders:
          navigator.push<void>(
            MaterialPageRoute<void>(builder: (_) => const WorkOrdersScreen()),
          );
      }

      toast(command.understoodSummary);
      ref.read(pendingVoiceCommandProvider.notifier).consume();
    });
  }

  @override
  Widget build(BuildContext context) {
    // Keep the watcher alive while the shell is in the tree.
    ref.watch(realtimeWatcherProvider);

    // React to voice commands that arrive while the shell is already running.
    ref.listen<VoiceCommand?>(pendingVoiceCommandProvider, (_, next) {
      if (next != null) _handleVoiceCommand(next);
    });

    final authState = ref.watch(authControllerProvider);
    final user = authState is AuthStateAuthenticated ? authState.user : null;
    final tenantMode = user?.isTenant ?? false;
    final tabs = tenantMode ? _tenantTabs : _tabs;
    final selectedIndex = _selectedIndex >= tabs.length
        ? tabs.length - 1
        : _selectedIndex;

    return Scaffold(
      body: IndexedStack(
        index: selectedIndex,
        children: tenantMode
            ? [
                _TenantHomeTab(user: user),
                const MessagesListScreen(),
                const _TenantMaintenanceTab(),
                const _TenantMoreTab(),
              ]
            : [
                _HomeTab(
                  user: user,
                  onSwitchToTab: (index) =>
                      setState(() => _selectedIndex = index),
                  onOpenAssistant: () {
                    Navigator.of(context).push<void>(
                      MaterialPageRoute<void>(builder: (_) => const AiTab()),
                    );
                  },
                ),
                const ScanTab(),
                const PropertiesTab(),
                const MessagesListScreen(),
                const MoreTab(),
              ],
      ),
      bottomNavigationBar: NavigationBar(
        selectedIndex: selectedIndex,
        onDestinationSelected: (index) =>
            setState(() => _selectedIndex = index),
        destinations: tabs
            .map(
              (tab) => NavigationDestination(
                icon: Icon(tab.icon),
                selectedIcon: Icon(tab.activeIcon ?? tab.icon),
                label: tab.label,
              ),
            )
            .toList(),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _HomeTab — the actual dashboard
// ---------------------------------------------------------------------------

class _TenantHomeTab extends ConsumerStatefulWidget {
  const _TenantHomeTab({required this.user});

  final AuthUser? user;

  @override
  ConsumerState<_TenantHomeTab> createState() => _TenantHomeTabState();
}

class _TenantHomeTabState extends ConsumerState<_TenantHomeTab> {
  /// Payment id currently starting a Checkout session (button shows a spinner).
  int? _payingPaymentId;

  /// Lease id whose autopay enroll/cancel is in flight.
  int? _busyAutopayLeaseId;

  AuthUser? get user => widget.user;

  /// Rent items the tenant can pay online: anything not already settled.
  static const _settledStatuses = {'Paid', 'Waived', 'Refunded', 'Cancelled'};

  Future<void> _open(String url) async {
    final uri = Uri.tryParse(url);
    if (uri == null) return;
    await launchUrl(uri, mode: LaunchMode.externalApplication);
  }

  /// Starts hosted Checkout for one rent item and opens it in the browser.
  /// A 503 (Stripe off) shows a gentle, non-error message.
  Future<void> _payNow(Payment payment) async {
    if (_payingPaymentId != null) return;
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _payingPaymentId = payment.id);
    try {
      final url = await ref
          .read(tenantPortalRepositoryProvider)
          .payCheckout(payment.id);
      if (url.isEmpty) return;
      await _open(url);
    } on ApiException catch (e) {
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(
              e.statusCode == 503
                  ? "Online payments aren't set up yet."
                  : e.message,
            ),
          ),
        );
    } finally {
      if (mounted) setState(() => _payingPaymentId = null);
    }
  }

  /// Enrolls the lease in autopay and opens the setup Checkout in the browser.
  Future<void> _enrollAutopay(int leaseId) async {
    if (_busyAutopayLeaseId != null) return;
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _busyAutopayLeaseId = leaseId);
    try {
      final url = await ref
          .read(tenantPortalRepositoryProvider)
          .autopayEnroll(leaseId);
      if (url.isNotEmpty) await _open(url);
      // The tenant finishes setup in the browser; refresh status on return.
      ref.invalidate(tenantAutopayStatusProvider(leaseId));
    } on ApiException catch (e) {
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(
              e.statusCode == 503
                  ? "Online payments aren't set up yet."
                  : e.message,
            ),
          ),
        );
    } finally {
      if (mounted) setState(() => _busyAutopayLeaseId = null);
    }
  }

  /// Turns autopay off for the lease, then refreshes the status.
  Future<void> _cancelAutopay(int leaseId) async {
    if (_busyAutopayLeaseId != null) return;
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _busyAutopayLeaseId = leaseId);
    try {
      await ref.read(tenantPortalRepositoryProvider).autopayCancel(leaseId);
      ref.invalidate(tenantAutopayStatusProvider(leaseId));
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(
          const SnackBar(content: Text('Autopay turned off.')),
        );
    } on ApiException catch (e) {
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    } finally {
      if (mounted) setState(() => _busyAutopayLeaseId = null);
    }
  }

  @override
  Widget build(BuildContext context) {
    final snapshot = ref.watch(tenantPortalSnapshotProvider);
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Tenant Dashboard'),
        actions: [
          IconButton(
            icon: const Icon(Icons.logout_outlined),
            tooltip: 'Sign out',
            onPressed: () async {
              await ref.read(authControllerProvider.notifier).logout();
            },
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(tenantPortalSnapshotProvider);
          // Refresh autopay state too; the tenant may have just returned from
          // a hosted Checkout in the browser.
          final leaseId = ref
              .read(tenantPortalSnapshotProvider)
              .value
              ?.leases
              .firstOrNull
              ?.id;
          if (leaseId != null) {
            ref.invalidate(tenantAutopayStatusProvider(leaseId));
          }
        },
        child: snapshot.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (err, _) => ListView(
            padding: const EdgeInsets.all(20),
            children: [
              Text(
                'Could not load your dashboard.',
                style: theme.textTheme.titleMedium,
              ),
              const SizedBox(height: 8),
              Text('$err'),
            ],
          ),
          data: (data) {
            final openOrders = data.workOrders
                .where(
                  (w) => !{
                    'Completed',
                    'Cancelled',
                    'Archived',
                  }.contains(w.status),
                )
                .toList();
            final unpaid =
                data.payments
                    .where((p) => !_settledStatuses.contains(p.status))
                    .toList()
                  ..sort((a, b) => a.dueDate.compareTo(b.dueDate));
            final nextPayment = unpaid.isEmpty ? null : unpaid.first;
            final unreadNotifications = data.notifications
                .where((n) => !n.isRead)
                .length;
            final primaryLeaseId = data.leases.firstOrNull?.id;

            return ListView(
              padding: const EdgeInsets.all(20),
              children: [
                Text(
                  user?.displayName ?? 'My home',
                  style: theme.textTheme.headlineSmall?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
                ),
                const SizedBox(height: 16),
                if (data.notifications.isNotEmpty)
                  _TenantCard(
                    icon: Icons.notifications_outlined,
                    title: 'Notifications',
                    value: '$unreadNotifications unread',
                    subtitle: data.notifications.first.title,
                  ),
                _TenantCard(
                  icon: Icons.warning_amber_outlined,
                  title: 'Overdue',
                  value: _money(data.balance.overdue),
                  subtitle: '${data.balance.overdueCount} overdue item(s)',
                ),
                if (data.leases.isNotEmpty)
                  _TenantCard(
                    icon: Icons.receipt_long_outlined,
                    title: 'Account history',
                    value: 'View',
                    subtitle: 'Every charge and payment, explained',
                    onTap: () => Navigator.of(context).push<void>(
                      MaterialPageRoute<void>(
                        builder: (_) => const TenantAccountHistoryScreen(),
                      ),
                    ),
                  ),
                _TenantCard(
                  icon: Icons.payments_outlined,
                  title: 'Next rent due',
                  value: nextPayment == null
                      ? 'None'
                      : '${nextPayment.dueDate.difference(DateTime.now()).inDays} days',
                  subtitle: nextPayment == null
                      ? 'No unpaid rent scheduled'
                      : '${_money(nextPayment.amount)} due',
                ),

                // ── Pay rent ──────────────────────────────────────────────
                if (unpaid.isNotEmpty) ...[
                  const SizedBox(height: 8),
                  Text(
                    'Pay rent',
                    style: theme.textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  const SizedBox(height: 8),
                  for (final payment in unpaid)
                    _PayItemCard(
                      payment: payment,
                      busy: _payingPaymentId == payment.id,
                      // Disable other buttons while one Checkout is starting.
                      enabled:
                          _payingPaymentId == null ||
                          _payingPaymentId == payment.id,
                      onPay: () => _payNow(payment),
                    ),
                ],

                // ── Autopay ───────────────────────────────────────────────
                if (primaryLeaseId != null) ...[
                  const SizedBox(height: 8),
                  _AutopayCard(
                    statusAsync: ref.watch(
                      tenantAutopayStatusProvider(primaryLeaseId),
                    ),
                    busy: _busyAutopayLeaseId == primaryLeaseId,
                    onEnroll: () => _enrollAutopay(primaryLeaseId),
                    onCancel: () => _cancelAutopay(primaryLeaseId),
                  ),
                ],

                const SizedBox(height: 8),
                _TenantCard(
                  icon: Icons.build_outlined,
                  title: 'Open maintenance',
                  value: '${openOrders.length}',
                  subtitle: openOrders.isEmpty
                      ? 'No open requests'
                      : openOrders.first.title,
                ),
              ],
            );
          },
        ),
      ),
    );
  }
}

/// A single unpaid/scheduled/late rent item with a "Pay now" action that opens
/// a hosted Stripe Checkout in the browser.
class _PayItemCard extends StatelessWidget {
  const _PayItemCard({
    required this.payment,
    required this.busy,
    required this.enabled,
    required this.onPay,
  });

  final Payment payment;
  final bool busy;
  final bool enabled;
  final VoidCallback onPay;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final isLate = payment.dueDate.isBefore(
      DateTime.now().subtract(const Duration(days: 1)),
    );
    final dueLabel = isLate
        ? 'Past due'
        : 'Due ${_shortDate(payment.dueDate)}';

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(
              isLate ? Icons.warning_amber_outlined : Icons.payments_outlined,
              color: isLate ? cs.error : cs.primary,
            ),
            const SizedBox(width: 14),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    _money(payment.amount),
                    style: theme.textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  Text(
                    '${payment.type.isEmpty ? 'Rent' : payment.type} · $dueLabel',
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: isLate ? cs.error : cs.onSurfaceVariant,
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(width: 12),
            FilledButton(
              onPressed: enabled && !busy ? onPay : null,
              child: busy
                  ? const SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Text('Pay now'),
            ),
          ],
        ),
      ),
    );
  }
}

/// Autopay enrollment card — plain language, with set-up / turn-off actions.
class _AutopayCard extends StatelessWidget {
  const _AutopayCard({
    required this.statusAsync,
    required this.busy,
    required this.onEnroll,
    required this.onCancel,
  });

  final AsyncValue<AutopayStatus> statusAsync;
  final bool busy;
  final VoidCallback onEnroll;
  final VoidCallback onCancel;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(Icons.autorenew, color: cs.primary),
            const SizedBox(width: 14),
            Expanded(
              child: statusAsync.when(
                loading: () => Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Autopay',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      'Checking…',
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: cs.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
                error: (_, _) => Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Autopay',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      "Couldn't load autopay status.",
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: cs.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
                data: (status) => Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Autopay',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      status.active
                          ? "You're set up. Rent is paid automatically each month."
                          : 'Set up autopay so rent is paid automatically each month.',
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: cs.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
              ),
            ),
            const SizedBox(width: 12),
            statusAsync.maybeWhen(
              data: (status) => busy
                  ? const SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : status.active
                  ? OutlinedButton(
                      onPressed: onCancel,
                      child: const Text('Turn off'),
                    )
                  : FilledButton(
                      onPressed: onEnroll,
                      child: const Text('Set up'),
                    ),
              orElse: () => const SizedBox.shrink(),
            ),
          ],
        ),
      ),
    );
  }
}

String _shortDate(DateTime date) {
  const months = [
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
  if (date.year <= 1) return '';
  return '${months[date.month - 1]} ${date.day}';
}

class _TenantMaintenanceTab extends ConsumerStatefulWidget {
  const _TenantMaintenanceTab();

  @override
  ConsumerState<_TenantMaintenanceTab> createState() =>
      _TenantMaintenanceTabState();
}

class _TenantMaintenanceTabState extends ConsumerState<_TenantMaintenanceTab> {
  final _title = TextEditingController();
  final _description = TextEditingController();
  String _priority = 'Normal';
  Uint8List? _photoBytes;
  String? _photoName;
  String? _photoContentType;
  bool _saving = false;

  @override
  void dispose() {
    _title.dispose();
    _description.dispose();
    super.dispose();
  }

  Future<void> _pickPhoto(ImageSource source) async {
    final picked = await ImagePicker().pickImage(
      source: source,
      imageQuality: 80,
      maxWidth: 1600,
      maxHeight: 1600,
    );
    if (picked == null) return;
    final bytes = Uint8List.fromList(await picked.readAsBytes());
    if (!mounted) return;
    setState(() {
      _photoBytes = bytes;
      _photoName = picked.name;
      _photoContentType = _mimeFromExtension(picked.name);
    });
  }

  String _mimeFromExtension(String filename) {
    final lower = filename.toLowerCase();
    if (lower.endsWith('.png')) return 'image/png';
    if (lower.endsWith('.webp')) return 'image/webp';
    if (lower.endsWith('.heic')) return 'image/heic';
    return 'image/jpeg';
  }

  Future<void> _submit() async {
    if (_title.text.trim().isEmpty || _description.text.trim().isEmpty) return;
    setState(() => _saving = true);
    try {
      final repo = ref.read(tenantPortalRepositoryProvider);
      final created = await repo.createWorkOrder(
        title: _title.text.trim(),
        description: _description.text.trim(),
        priority: _priority,
      );
      final photoBytes = _photoBytes;
      final photoName = _photoName;
      final photoContentType = _photoContentType;
      if (photoBytes != null && photoName != null && photoContentType != null) {
        await repo.uploadWorkOrderPhoto(
          workOrderId: created.id,
          bytes: photoBytes,
          fileName: photoName,
          contentType: photoContentType,
        );
      }
      ref.invalidate(tenantPortalSnapshotProvider);
      _title.clear();
      _description.clear();
      _photoBytes = null;
      _photoName = null;
      _photoContentType = null;
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Maintenance request submitted.')),
        );
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final snapshot = ref.watch(tenantPortalSnapshotProvider);

    return Scaffold(
      appBar: AppBar(title: const Text('Maintenance')),
      body: ListView(
        padding: const EdgeInsets.all(20),
        children: [
          snapshot.maybeWhen(
            data: (data) {
              final open = data.workOrders
                  .where(
                    (w) => !{
                      'Completed',
                      'Cancelled',
                      'Archived',
                    }.contains(w.status),
                  )
                  .toList();
              if (open.isEmpty) {
                return const Text('No open maintenance requests.');
              }
              return Column(
                children: open
                    .map(
                      (w) => Card(
                        child: ListTile(
                          title: Text(w.title),
                          subtitle: Text('${w.status} · ${w.priority}'),
                          trailing: const Icon(Icons.chevron_right),
                          onTap: () => Navigator.of(context).push<void>(
                            MaterialPageRoute<void>(
                              builder: (_) =>
                                  TenantWorkOrderDetailScreen(workOrderId: w.id),
                            ),
                          ),
                        ),
                      ),
                    )
                    .toList(),
              );
            },
            orElse: () => const SizedBox.shrink(),
          ),
          const SizedBox(height: 16),
          TextField(
            controller: _title,
            decoration: const InputDecoration(labelText: 'Issue title'),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _description,
            minLines: 3,
            maxLines: 5,
            decoration: const InputDecoration(labelText: 'Description'),
          ),
          const SizedBox(height: 12),
          DropdownButtonFormField<String>(
            initialValue: _priority,
            decoration: const InputDecoration(labelText: 'Priority'),
            items: const [
              'Low',
              'Normal',
              'High',
              'Emergency',
            ].map((p) => DropdownMenuItem(value: p, child: Text(p))).toList(),
            onChanged: (value) => setState(() => _priority = value ?? 'Normal'),
          ),
          const SizedBox(height: 12),
          if (_photoBytes != null) ...[
            ClipRRect(
              borderRadius: BorderRadius.circular(12),
              child: Image.memory(
                _photoBytes!,
                height: 140,
                width: double.infinity,
                fit: BoxFit.cover,
              ),
            ),
            const SizedBox(height: 8),
          ],
          Row(
            children: [
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: _saving
                      ? null
                      : () => _pickPhoto(ImageSource.camera),
                  icon: const Icon(Icons.camera_alt_outlined),
                  label: Text(_photoBytes == null ? 'Take photo' : 'Retake'),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: _saving
                      ? null
                      : () => _pickPhoto(ImageSource.gallery),
                  icon: const Icon(Icons.photo_library_outlined),
                  label: const Text('Choose'),
                ),
              ),
            ],
          ),
          const SizedBox(height: 16),
          FilledButton(
            onPressed: _saving ? null : _submit,
            child: Text(_saving ? 'Submitting...' : 'Submit Request'),
          ),
        ],
      ),
    );
  }
}

class _TenantMoreTab extends ConsumerWidget {
  const _TenantMoreTab();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Scaffold(
      appBar: AppBar(title: const Text('More')),
      body: ListView(
        children: [
          ListTile(
            leading: const Icon(Icons.receipt_long_outlined),
            title: const Text('Account history'),
            subtitle: const Text('Every charge and payment, explained.'),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => Navigator.of(context).push<void>(
              MaterialPageRoute<void>(
                builder: (_) => const TenantAccountHistoryScreen(),
              ),
            ),
          ),
          ListTile(
            leading: const Icon(Icons.description_outlined),
            title: const Text('Lease'),
            subtitle: const Text('Lease details appear on the dashboard.'),
            onTap: () {},
          ),
          ListTile(
            leading: const Icon(Icons.event_outlined),
            title: const Text('Appointments'),
            subtitle: const Text('Upcoming appointments will appear here.'),
            onTap: () {},
          ),
          ListTile(
            leading: const Icon(Icons.logout_outlined),
            title: const Text('Sign out'),
            onTap: () async {
              await ref.read(authControllerProvider.notifier).logout();
            },
          ),
        ],
      ),
    );
  }
}

class _TenantCard extends StatelessWidget {
  const _TenantCard({
    required this.icon,
    required this.title,
    required this.value,
    required this.subtitle,
    this.onTap,
  });

  final IconData icon;
  final String title;
  final String value;
  final String subtitle;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final content = Padding(
      padding: const EdgeInsets.all(16),
      child: Row(
        children: [
          Icon(icon),
          const SizedBox(width: 14),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title, style: theme.textTheme.labelLarge),
                const SizedBox(height: 4),
                Text(value, style: theme.textTheme.headlineSmall),
                Text(subtitle, style: theme.textTheme.bodySmall),
              ],
            ),
          ),
          if (onTap != null)
            Icon(
              Icons.chevron_right,
              color: theme.colorScheme.onSurfaceVariant,
            ),
        ],
      ),
    );

    if (onTap == null) {
      return Card(child: content);
    }

    return Card(
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: content,
      ),
    );
  }
}

String _money(num value) =>
    '\$${value.toStringAsFixed(2).replaceAllMapped(RegExp(r'\B(?=(\d{3})+(?!\d))'), (m) => ',')}';

class _HomeTab extends ConsumerWidget {
  const _HomeTab({
    required this.user,
    required this.onSwitchToTab,
    required this.onOpenAssistant,
  });

  final AuthUser? user;

  /// Callback to switch the shell's active tab (0-based index).
  final void Function(int index) onSwitchToTab;
  final VoidCallback onOpenAssistant;

  // Tab indices
  static const _scanTabIndex = 1;

  String get _greeting {
    final hour = DateTime.now().hour;
    if (hour < 12) return 'Good morning';
    if (hour < 17) return 'Good afternoon';
    return 'Good evening';
  }

  String get _displayName {
    if (user == null) return '';
    final name = user!.displayName.trim();
    if (name.isEmpty) return user!.email;
    // First name only keeps it friendly.
    return name.split(' ').first;
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final briefingAsync = ref.watch(_briefingProvider);
    final messagesAsync = ref.watch(_latestMessagesProvider);
    final fieldQueueAsync = ref.watch(_fieldQueueProvider);
    final moneyAsync = ref.watch(moneySnapshotProvider);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Rental Command'),
        actions: [
          IconButton(
            icon: const Icon(Icons.auto_awesome_outlined),
            tooltip: 'AI Assistant',
            onPressed: onOpenAssistant,
          ),
          IconButton(
            icon: const Icon(Icons.logout_outlined),
            tooltip: 'Sign out',
            onPressed: () async {
              await ref.read(authControllerProvider.notifier).logout();
            },
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(_briefingProvider);
          ref.invalidate(moneySnapshotProvider);
        },
        child: CustomScrollView(
          physics: const AlwaysScrollableScrollPhysics(),
          slivers: [
            SliverPadding(
              padding: const EdgeInsets.fromLTRB(20, 24, 20, 0),
              sliver: SliverToBoxAdapter(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    // ── Greeting ──────────────────────────────────────────
                    Text(
                      '$_greeting${_displayName.isNotEmpty ? ", $_displayName" : ""}!',
                      style: theme.textTheme.headlineSmall?.copyWith(
                        fontWeight: FontWeight.w700,
                        color: cs.onSurface,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      _formattedDate(),
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color: cs.onSurfaceVariant,
                      ),
                    ),
                    const SizedBox(height: 28),

                    // ── Quick actions ─────────────────────────────────────
                    _QuickActions(
                      onTellMe: () => Navigator.of(context).push<void>(
                        MaterialPageRoute<void>(
                          builder: (_) => const TellMeScreen(),
                        ),
                      ),
                      onScan: () => onSwitchToTab(_scanTabIndex),
                      onAskAi: onOpenAssistant,
                      onAddExpense: () => onSwitchToTab(_scanTabIndex),
                    ),
                    const SizedBox(height: 32),

                    // ── Today section header ──────────────────────────────
                    Text(
                      'Today',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                        color: cs.onSurface,
                      ),
                    ),
                    const SizedBox(height: 12),
                  ],
                ),
              ),
            ),

            // ── Briefing content ──────────────────────────────────────────
            SliverPadding(
              padding: const EdgeInsets.fromLTRB(20, 0, 20, 32),
              sliver: briefingAsync.when(
                loading: () => const SliverToBoxAdapter(
                  child: Center(
                    child: Padding(
                      padding: EdgeInsets.symmetric(vertical: 32),
                      child: CircularProgressIndicator(),
                    ),
                  ),
                ),
                error: (e, _) => SliverToBoxAdapter(
                  child: _BriefingError(
                    onRetry: () => ref.invalidate(_briefingProvider),
                  ),
                ),
                data: (briefing) => _BriefingContent(briefing: briefing),
              ),
            ),

            // ── Money snapshot ────────────────────────────────────────────
            SliverPadding(
              padding: const EdgeInsets.fromLTRB(20, 0, 20, 32),
              sliver: SliverToBoxAdapter(
                child: _MoneySnapshotSection(
                  snapshotAsync: moneyAsync,
                  onRetry: () => ref.invalidate(moneySnapshotProvider),
                ),
              ),
            ),

            SliverPadding(
              padding: const EdgeInsets.fromLTRB(20, 0, 20, 32),
              sliver: SliverList(
                delegate: SliverChildListDelegate([
                  _HomeSectionHeader(
                    title: 'Latest messages',
                    actionLabel: 'Open inbox',
                    onAction: () => onSwitchToTab(3),
                  ),
                  const SizedBox(height: 8),
                  _LatestMessagesSection(messagesAsync: messagesAsync),
                  const SizedBox(height: 24),
                  _HomeSectionHeader(
                    title: 'Field queue',
                    actionLabel: 'Open maintenance',
                    onAction: () => Navigator.of(context).push<void>(
                      MaterialPageRoute<void>(
                        builder: (_) => const WorkOrdersScreen(),
                      ),
                    ),
                  ),
                  const SizedBox(height: 8),
                  _FieldQueueSection(queueAsync: fieldQueueAsync),
                ]),
              ),
            ),
          ],
        ),
      ),
    );
  }

  String _formattedDate() {
    final now = DateTime.now();
    const months = [
      'January',
      'February',
      'March',
      'April',
      'May',
      'June',
      'July',
      'August',
      'September',
      'October',
      'November',
      'December',
    ];
    const weekdays = [
      'Monday',
      'Tuesday',
      'Wednesday',
      'Thursday',
      'Friday',
      'Saturday',
      'Sunday',
    ];
    final weekday = weekdays[now.weekday - 1];
    final month = months[now.month - 1];
    return '$weekday, $month ${now.day}';
  }
}

class _HomeSectionHeader extends StatelessWidget {
  const _HomeSectionHeader({
    required this.title,
    required this.actionLabel,
    required this.onAction,
  });

  final String title;
  final String actionLabel;
  final VoidCallback onAction;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Row(
      children: [
        Expanded(
          child: Text(
            title,
            style: theme.textTheme.titleMedium?.copyWith(
              fontWeight: FontWeight.w700,
            ),
          ),
        ),
        TextButton(onPressed: onAction, child: Text(actionLabel)),
      ],
    );
  }
}

class _LatestMessagesSection extends StatelessWidget {
  const _LatestMessagesSection({required this.messagesAsync});

  final AsyncValue<List<Conversation>> messagesAsync;

  @override
  Widget build(BuildContext context) {
    return messagesAsync.when(
      loading: () => const _LoadingCard(label: 'Loading messages...'),
      error: (_, _) => const _EmptyInlineCard(
        icon: Icons.forum_outlined,
        text: "Couldn't load messages.",
      ),
      data: (messages) {
        if (messages.isEmpty) {
          return const _EmptyInlineCard(
            icon: Icons.forum_outlined,
            text: 'No recent messages.',
          );
        }

        return Column(
          children: [
            for (final message in messages) ...[
              _MessageCard(conversation: message),
              const SizedBox(height: 8),
            ],
          ],
        );
      },
    );
  }
}

class _FieldQueueSection extends StatelessWidget {
  const _FieldQueueSection({required this.queueAsync});

  final AsyncValue<List<WorkOrder>> queueAsync;

  @override
  Widget build(BuildContext context) {
    return queueAsync.when(
      loading: () => const _LoadingCard(label: 'Loading field queue...'),
      error: (_, _) => const _EmptyInlineCard(
        icon: Icons.build_outlined,
        text: "Couldn't load work orders.",
      ),
      data: (queue) {
        if (queue.isEmpty) {
          return const _EmptyInlineCard(
            icon: Icons.check_circle_outline,
            text: 'No open field work.',
          );
        }

        return Column(
          children: [
            for (final order in queue) ...[
              _FieldQueueCard(workOrder: order),
              const SizedBox(height: 8),
            ],
          ],
        );
      },
    );
  }
}

class _MessageCard extends StatelessWidget {
  const _MessageCard({required this.conversation});

  final Conversation conversation;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final preview = conversation.lastMessagePreview ?? '';

    return Card(
      child: ListTile(
        onTap: () => Navigator.of(context).push<void>(
          MaterialPageRoute<void>(
            builder: (_) => MessageDetailScreen(
              conversationId: conversation.id,
              title: conversation.tenantName,
              subtitle: conversation.subject,
            ),
          ),
        ),
        leading: CircleAvatar(
          backgroundColor: conversation.hasUnread
              ? cs.primaryContainer
              : cs.surfaceContainerHighest,
          child: Icon(
            conversation.hasUnread ? Icons.mark_chat_unread : Icons.forum,
            color: conversation.hasUnread
                ? cs.onPrimaryContainer
                : cs.onSurfaceVariant,
            size: 18,
          ),
        ),
        title: Text(
          conversation.tenantName,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: theme.textTheme.bodyMedium?.copyWith(
            fontWeight: conversation.hasUnread
                ? FontWeight.w700
                : FontWeight.w600,
          ),
        ),
        subtitle: Text(
          preview.isEmpty ? conversation.subject : preview,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
        trailing: conversation.hasUnread
            ? Badge(label: Text('${conversation.unreadCount}'))
            : const Icon(Icons.chevron_right),
      ),
    );
  }
}

class _FieldQueueCard extends StatelessWidget {
  const _FieldQueueCard({required this.workOrder});

  final WorkOrder workOrder;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Card(
      child: ListTile(
        onTap: () => Navigator.of(context).push<void>(
          MaterialPageRoute<void>(
            builder: (_) => WorkOrderDetailScreen(workOrderId: workOrder.id),
          ),
        ),
        leading: CircleAvatar(
          backgroundColor: _priorityBg(workOrder.priority, cs),
          child: Icon(
            Icons.build_outlined,
            color: _priorityFg(workOrder.priority, cs),
            size: 18,
          ),
        ),
        title: Text(
          workOrder.title,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: theme.textTheme.bodyMedium?.copyWith(
            fontWeight: FontWeight.w600,
          ),
        ),
        subtitle: Text(
          [
            if (workOrder.propertyName != null) workOrder.propertyName!,
            workOrder.status,
            workOrder.priority,
          ].join(' / '),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
        trailing: const Icon(Icons.chevron_right),
      ),
    );
  }

  Color _priorityBg(String priority, ColorScheme cs) {
    switch (priority.toLowerCase()) {
      case 'emergency':
      case 'high':
        return cs.errorContainer;
      case 'normal':
        return cs.secondaryContainer;
      default:
        return cs.surfaceContainerHighest;
    }
  }

  Color _priorityFg(String priority, ColorScheme cs) {
    switch (priority.toLowerCase()) {
      case 'emergency':
      case 'high':
        return cs.onErrorContainer;
      case 'normal':
        return cs.onSecondaryContainer;
      default:
        return cs.onSurfaceVariant;
    }
  }
}

class _LoadingCard extends StatelessWidget {
  const _LoadingCard({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            const SizedBox(
              width: 18,
              height: 18,
              child: CircularProgressIndicator(strokeWidth: 2),
            ),
            const SizedBox(width: 12),
            Text(label),
          ],
        ),
      ),
    );
  }
}

class _EmptyInlineCard extends StatelessWidget {
  const _EmptyInlineCard({required this.icon, required this.text});

  final IconData icon;
  final String text;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(icon, color: cs.onSurfaceVariant, size: 20),
            const SizedBox(width: 12),
            Expanded(child: Text(text)),
          ],
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// Quick-action buttons
// ---------------------------------------------------------------------------

class _QuickActions extends StatelessWidget {
  const _QuickActions({
    required this.onTellMe,
    required this.onScan,
    required this.onAskAi,
    required this.onAddExpense,
  });

  final VoidCallback onTellMe;
  final VoidCallback onScan;
  final VoidCallback onAskAi;
  final VoidCallback onAddExpense;

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Expanded(
          child: _QuickActionButton(
            icon: Icons.mic_none_outlined,
            label: 'Tell\nme',
            onTap: onTellMe,
          ),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: _QuickActionButton(
            icon: Icons.document_scanner_outlined,
            label: 'Scan a\ndocument',
            onTap: onScan,
          ),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: _QuickActionButton(
            icon: Icons.auto_awesome_outlined,
            label: 'Ask\nAI',
            onTap: onAskAi,
          ),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: _QuickActionButton(
            icon: Icons.receipt_long_outlined,
            label: 'Add\nexpense',
            onTap: onAddExpense,
          ),
        ),
      ],
    );
  }
}

class _QuickActionButton extends StatelessWidget {
  const _QuickActionButton({
    required this.icon,
    required this.label,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Material(
      color: cs.surfaceContainerHighest,
      borderRadius: BorderRadius.circular(14),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(14),
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 14, horizontal: 8),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(icon, color: cs.primary, size: 26),
              const SizedBox(height: 6),
              Text(
                label,
                textAlign: TextAlign.center,
                style: TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w600,
                  color: cs.onSurface,
                  height: 1.25,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// Money snapshot — plain-English "money in / out / kept" + who's behind
// ---------------------------------------------------------------------------

class _MoneySnapshotSection extends StatelessWidget {
  const _MoneySnapshotSection({
    required this.snapshotAsync,
    required this.onRetry,
  });

  final AsyncValue<MoneySnapshot> snapshotAsync;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return snapshotAsync.when(
      loading: () => const _LoadingCard(label: 'Loading your money...'),
      error: (_, _) => Card(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              Icon(Icons.wifi_off_outlined, color: cs.error, size: 22),
              const SizedBox(width: 12),
              const Expanded(child: Text("Couldn't load your money.")),
              TextButton(onPressed: onRetry, child: const Text('Retry')),
            ],
          ),
        ),
      ),
      data: (snapshot) {
        return Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Icon(Icons.savings_outlined, color: cs.primary, size: 20),
                    const SizedBox(width: 8),
                    Text(
                      'Your money',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    const Spacer(),
                    if (snapshot.periodLabel.isNotEmpty)
                      Text(
                        snapshot.periodLabel,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: cs.onSurfaceVariant,
                        ),
                      ),
                  ],
                ),
                const SizedBox(height: 16),
                _MoneyRow(
                  icon: Icons.south_west,
                  iconColor: Colors.green.shade700,
                  label: 'Collected',
                  amount: snapshot.collected,
                  explanation: snapshot.explanations.collected,
                ),
                const SizedBox(height: 14),
                _MoneyRow(
                  icon: Icons.north_east,
                  iconColor: cs.error,
                  label: 'Spent',
                  amount: snapshot.spent,
                  explanation: snapshot.explanations.spent,
                ),
                const SizedBox(height: 14),
                _MoneyRow(
                  icon: Icons.account_balance_wallet_outlined,
                  iconColor: cs.primary,
                  label: 'Kept',
                  amount: snapshot.net,
                  explanation: snapshot.explanations.net,
                  emphasize: true,
                ),
                if (snapshot.pastDueCount > 0 ||
                    snapshot.pastDueAmount > 0) ...[
                  const SizedBox(height: 14),
                  const Divider(height: 1),
                  const SizedBox(height: 14),
                  _PastDueRow(
                    count: snapshot.pastDueCount,
                    amount: snapshot.pastDueAmount,
                    explanation: snapshot.explanations.pastDue,
                  ),
                ],
              ],
            ),
          ),
        );
      },
    );
  }
}

class _MoneyRow extends StatelessWidget {
  const _MoneyRow({
    required this.icon,
    required this.iconColor,
    required this.label,
    required this.amount,
    required this.explanation,
    this.emphasize = false,
  });

  final IconData icon;
  final Color iconColor;
  final String label;
  final double amount;
  final String explanation;
  final bool emphasize;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.only(top: 2),
          child: Icon(icon, size: 18, color: iconColor),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.baseline,
                textBaseline: TextBaseline.alphabetic,
                children: [
                  Expanded(
                    child: Text(
                      label,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        fontWeight: FontWeight.w600,
                        color: cs.onSurface,
                      ),
                    ),
                  ),
                  const SizedBox(width: 8),
                  Text(
                    _money(amount),
                    style:
                        (emphasize
                                ? theme.textTheme.headlineSmall
                                : theme.textTheme.titleLarge)
                            ?.copyWith(
                              fontWeight: FontWeight.w700,
                              fontFeatures: const [
                                FontFeature.tabularFigures(),
                              ],
                              color: emphasize ? cs.primary : cs.onSurface,
                            ),
                  ),
                ],
              ),
              if (explanation.isNotEmpty) ...[
                const SizedBox(height: 2),
                Text(
                  explanation,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: cs.onSurfaceVariant,
                    height: 1.4,
                  ),
                ),
              ],
            ],
          ),
        ),
      ],
    );
  }
}

class _PastDueRow extends StatelessWidget {
  const _PastDueRow({
    required this.count,
    required this.amount,
    required this.explanation,
  });

  final int count;
  final double amount;
  final String explanation;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final tenantWord = count == 1 ? 'tenant' : 'tenants';

    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Container(
          padding: const EdgeInsets.all(6),
          decoration: BoxDecoration(
            color: cs.errorContainer,
            borderRadius: BorderRadius.circular(8),
          ),
          child: Icon(
            Icons.warning_amber_rounded,
            size: 16,
            color: cs.onErrorContainer,
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                '$count $tenantWord behind, owing ${_money(amount)}',
                style: theme.textTheme.bodyMedium?.copyWith(
                  fontWeight: FontWeight.w700,
                  color: cs.onSurface,
                ),
              ),
              if (explanation.isNotEmpty) ...[
                const SizedBox(height: 2),
                Text(
                  explanation,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: cs.onSurfaceVariant,
                    height: 1.4,
                  ),
                ),
              ],
            ],
          ),
        ),
      ],
    );
  }
}

// ---------------------------------------------------------------------------
// Briefing content
// ---------------------------------------------------------------------------

class _BriefingContent extends StatelessWidget {
  const _BriefingContent({required this.briefing});

  final BriefingResponse briefing;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final bullets = briefing.bullets.take(5).toList();

    if (briefing.summary == null && bullets.isEmpty) {
      return SliverToBoxAdapter(child: _AllClearCard());
    }

    return SliverList(
      delegate: SliverChildListDelegate([
        // Optional AI-composed summary
        if (briefing.summary != null && briefing.summary!.isNotEmpty) ...[
          Card(
            child: Padding(
              padding: const EdgeInsets.all(14),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Icon(Icons.auto_awesome, color: cs.primary, size: 18),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      briefing.summary!,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color: cs.onSurface,
                        height: 1.5,
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(height: 10),
        ],

        // Bullet items
        if (bullets.isEmpty) _AllClearCard(),

        for (final bullet in bullets) ...[
          _BulletRow(bullet: bullet),
          const SizedBox(height: 8),
        ],
      ]),
    );
  }
}

class _AllClearCard extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(
              Icons.check_circle_outline,
              color: Colors.green.shade600,
              size: 22,
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                'All clear — nothing urgent today.',
                style: Theme.of(
                  context,
                ).textTheme.bodyMedium?.copyWith(color: cs.onSurface),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _BulletRow extends StatelessWidget {
  const _BulletRow({required this.bullet});

  final BriefingBullet bullet;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final (iconData, iconColor, bgColor) = _severityStyle(bullet.severity, cs);

    final destination = _destinationFor(bullet);

    final content = Padding(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Container(
            padding: const EdgeInsets.all(6),
            decoration: BoxDecoration(
              color: bgColor,
              borderRadius: BorderRadius.circular(8),
            ),
            child: Icon(iconData, color: iconColor, size: 16),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  bullet.title,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    fontWeight: FontWeight.w600,
                    color: cs.onSurface,
                  ),
                ),
                if (bullet.detail.isNotEmpty) ...[
                  const SizedBox(height: 2),
                  Text(
                    bullet.detail,
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: cs.onSurfaceVariant,
                      height: 1.4,
                    ),
                  ),
                ],
              ],
            ),
          ),
          if (destination != null) ...[
            const SizedBox(width: 8),
            Icon(Icons.chevron_right, color: cs.onSurfaceVariant, size: 18),
          ],
        ],
      ),
    );

    if (destination == null) {
      return Card(child: content);
    }

    return Card(
      child: InkWell(
        onTap: () => Navigator.of(
          context,
        ).push<void>(MaterialPageRoute<void>(builder: destination)),
        borderRadius: BorderRadius.circular(12),
        child: content,
      ),
    );
  }

  /// Maps a briefing bullet's referenced entity to the screen that shows it.
  ///
  /// Returns `null` when the bullet has no entity reference or the type isn't
  /// navigable, in which case the card is rendered without a tap handler.
  /// Only [WorkOrder] has a detail screen that can be opened from an id alone;
  /// the others (which need a fully-loaded model) fall back to their list
  /// screen so the landlord still lands in the right place.
  WidgetBuilder? _destinationFor(BriefingBullet bullet) {
    final type = bullet.entityType;
    final id = bullet.entityId;
    if (type == null) return null;

    switch (type) {
      case 'WorkOrder':
        if (id == null) return null;
        return (_) => WorkOrderDetailScreen(workOrderId: id);
      case 'Payment':
        return (_) => const PaymentsScreen();
      case 'Lease':
        return (_) => const LeasesListScreen();
      case 'Tenant':
        return (_) => const TenantsListScreen();
      default:
        return null;
    }
  }

  (IconData, Color, Color) _severityStyle(
    BulletSeverity severity,
    ColorScheme cs,
  ) {
    switch (severity) {
      case BulletSeverity.critical:
        return (Icons.warning_rounded, cs.error, cs.errorContainer);
      case BulletSeverity.warning:
        return (
          Icons.info_outline,
          Colors.amber.shade700,
          Colors.amber.shade100,
        );
      case BulletSeverity.info:
        return (Icons.info_outline, cs.primary, cs.primaryContainer);
    }
  }
}

// ---------------------------------------------------------------------------
// Error state
// ---------------------------------------------------------------------------

class _BriefingError extends StatelessWidget {
  const _BriefingError({required this.onRetry});

  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(Icons.wifi_off_outlined, color: cs.error, size: 22),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                "Couldn't load today's briefing.",
                style: Theme.of(context).textTheme.bodyMedium,
              ),
            ),
            TextButton(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _TabItem helper
// ---------------------------------------------------------------------------

class _TabItem {
  const _TabItem({required this.label, required this.icon, this.activeIcon});

  final String label;
  final IconData icon;
  final IconData? activeIcon;
}
