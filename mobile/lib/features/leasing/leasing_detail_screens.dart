import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/api/api_exception.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'leasing_workspace_repository.dart';
import '../../core/presentation/formatting.dart';

class LeasingApplicationDetailScreen extends ConsumerStatefulWidget {
  const LeasingApplicationDetailScreen({
    super.key,
    required this.applicationId,
  });

  final int applicationId;

  @override
  ConsumerState<LeasingApplicationDetailScreen> createState() =>
      _LeasingApplicationDetailScreenState();
}

class _LeasingApplicationDetailScreenState
    extends ConsumerState<LeasingApplicationDetailScreen> {
  late Future<LeasingApplicationDetail> _future;

  @override
  void initState() {
    super.initState();
    _future = _load();
  }

  Future<LeasingApplicationDetail> _load() => ref
      .read(leasingWorkspaceRepositoryProvider)
      .application(widget.applicationId);

  Future<void> _refresh() async {
    final next = _load();
    setState(() => _future = next);
    await next;
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Application')),
    body: FutureBuilder<LeasingApplicationDetail>(
      future: _future,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return const Center(child: CircularProgressIndicator());
        }
        if (snapshot.hasError || snapshot.data == null) {
          return _LeasingDetailError(
            message: _leasingError(snapshot.error),
            onRetry: _refresh,
          );
        }

        final application = snapshot.data!;
        return RefreshIndicator(
          onRefresh: _refresh,
          child: ListView(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
            children: [
              Text(
                application.applicantName,
                style: Theme.of(context).textTheme.headlineSmall?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
              ),
              const SizedBox(height: 4),
              Text(
                [
                  application.propertyName,
                  if (application.unitNumber != null)
                    'Unit ${application.unitNumber}',
                ].whereType<String>().join(' · '),
              ),
              const SizedBox(height: 16),
              _LeasingDetailCard(
                title: 'Application details',
                children: [
                  _LeasingDetailRow(label: 'Status', value: application.status),
                  if (application.email?.isNotEmpty == true)
                    _LeasingDetailRow(
                      label: 'Email',
                      value: application.email!,
                    ),
                  if (application.phone?.isNotEmpty == true)
                    _LeasingDetailRow(
                      label: 'Phone',
                      value: application.phone!,
                    ),
                  if (application.monthlyIncome != null)
                    _LeasingDetailRow(
                      label: 'Monthly income',
                      value: moneyFmt(application.monthlyIncome!),
                    ),
                  if (application.desiredMoveInDate != null)
                    _LeasingDetailRow(
                      label: 'Desired move-in',
                      value: _date(application.desiredMoveInDate!),
                    ),
                  _LeasingDetailRow(
                    label: 'Screening consent',
                    value: application.consentGiven
                        ? 'Recorded'
                        : 'Not recorded',
                  ),
                  _LeasingDetailRow(
                    label: 'Submitted',
                    value: _dateTime(application.submittedAtUtc),
                  ),
                  if (application.notes?.isNotEmpty == true)
                    _LeasingDetailRow(
                      label: 'Notes',
                      value: application.notes!,
                    ),
                ],
              ),
              const SizedBox(height: 16),
              FilledButton.icon(
                onPressed: () => openMobileScan(
                  context,
                  initialTargetEntityType: 'Application',
                  lockTargetEntityType: true,
                  propertyId: application.propertyId,
                  unitId: application.unitId,
                  applicationId: application.id,
                  sourceLabel: application.applicantName,
                ),
                icon: const Icon(Symbols.document_scanner_rounded),
                label: const Text('Scan application document'),
              ),
            ],
          ),
        );
      },
    ),
  );
}

class LeasingRentalDetailScreen extends ConsumerStatefulWidget {
  const LeasingRentalDetailScreen({super.key, required this.unitId});

  final int unitId;

  @override
  ConsumerState<LeasingRentalDetailScreen> createState() =>
      _LeasingRentalDetailScreenState();
}

class _LeasingRentalDetailScreenState
    extends ConsumerState<LeasingRentalDetailScreen> {
  late Future<LeasingRentalDetail> _future;

  @override
  void initState() {
    super.initState();
    _future = _load();
  }

  Future<LeasingRentalDetail> _load() =>
      ref.read(leasingWorkspaceRepositoryProvider).rental(widget.unitId);

  Future<void> _refresh() async {
    final next = _load();
    setState(() => _future = next);
    await next;
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Rental & listing')),
    body: FutureBuilder<LeasingRentalDetail>(
      future: _future,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return const Center(child: CircularProgressIndicator());
        }
        if (snapshot.hasError || snapshot.data == null) {
          return _LeasingDetailError(
            message: _leasingError(snapshot.error),
            onRetry: _refresh,
          );
        }

        final rental = snapshot.data!;
        return RefreshIndicator(
          onRefresh: _refresh,
          child: ListView(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
            children: [
              Text(
                '${rental.propertyName} · Unit ${rental.unitNumber}',
                style: Theme.of(context).textTheme.headlineSmall?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
              ),
              const SizedBox(height: 4),
              Text(rental.address),
              const SizedBox(height: 16),
              _LeasingDetailCard(
                title: 'Listing',
                children: [
                  _LeasingDetailRow(
                    label: 'Status',
                    value: rental.listingStatus ?? 'Not started',
                  ),
                  if (rental.listingHeadline?.isNotEmpty == true)
                    _LeasingDetailRow(
                      label: 'Headline',
                      value: rental.listingHeadline!,
                    ),
                  if (rental.askingRent != null)
                    _LeasingDetailRow(
                      label: 'Asking rent',
                      value: moneyFmt(rental.askingRent!),
                    ),
                  if (rental.securityDeposit != null)
                    _LeasingDetailRow(
                      label: 'Security deposit',
                      value: moneyFmt(rental.securityDeposit!),
                    ),
                  if (rental.availableOn != null)
                    _LeasingDetailRow(
                      label: 'Available',
                      value: _date(rental.availableOn!),
                    ),
                  if (rental.listingDescription?.isNotEmpty == true)
                    _LeasingDetailRow(
                      label: 'Description',
                      value: rental.listingDescription!,
                    ),
                ],
              ),
              const SizedBox(height: 12),
              _LeasingDetailCard(
                title: 'Leasing activity',
                children: [
                  if (rental.canViewApplications)
                    _LeasingDetailRow(
                      label: 'Open applications',
                      value: '${rental.openApplicationCount}',
                    ),
                  if (rental.canViewShowings)
                    _LeasingDetailRow(
                      label: 'Next showing',
                      value: rental.nextShowingAtUtc == null
                          ? 'None scheduled'
                          : _dateTime(rental.nextShowingAtUtc!),
                    ),
                  if (rental.leaseTerms?.isNotEmpty == true)
                    _LeasingDetailRow(
                      label: 'Lease terms',
                      value: rental.leaseTerms!,
                    ),
                  if (rental.petPolicy?.isNotEmpty == true)
                    _LeasingDetailRow(
                      label: 'Pet policy',
                      value: rental.petPolicy!,
                    ),
                ],
              ),
              const SizedBox(height: 16),
              FilledButton.icon(
                onPressed: () => openMobileScan(
                  context,
                  propertyId: rental.propertyId,
                  unitId: rental.unitId,
                  rentalListingId: rental.listingId,
                  sourceLabel:
                      '${rental.propertyName} · Unit ${rental.unitNumber}',
                ),
                icon: const Icon(Symbols.document_scanner_rounded),
                label: const Text('Scan / Add'),
              ),
            ],
          ),
        );
      },
    ),
  );
}

class LeasingAppointmentDetailScreen extends ConsumerStatefulWidget {
  const LeasingAppointmentDetailScreen({
    super.key,
    required this.appointmentId,
  });

  final int appointmentId;

  @override
  ConsumerState<LeasingAppointmentDetailScreen> createState() =>
      _LeasingAppointmentDetailScreenState();
}

class _LeasingAppointmentDetailScreenState
    extends ConsumerState<LeasingAppointmentDetailScreen> {
  late Future<LeasingAppointmentDetail> _future;

  @override
  void initState() {
    super.initState();
    _future = _load();
  }

  Future<LeasingAppointmentDetail> _load() => ref
      .read(leasingWorkspaceRepositoryProvider)
      .appointment(widget.appointmentId);

  Future<void> _refresh() async {
    final next = _load();
    setState(() => _future = next);
    await next;
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Showing')),
    body: FutureBuilder<LeasingAppointmentDetail>(
      future: _future,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return const Center(child: CircularProgressIndicator());
        }
        if (snapshot.hasError || snapshot.data == null) {
          return _LeasingDetailError(
            message: _leasingError(snapshot.error),
            onRetry: _refresh,
          );
        }

        final appointment = snapshot.data!;
        return RefreshIndicator(
          onRefresh: _refresh,
          child: ListView(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
            children: [
              Text(
                appointment.title,
                style: Theme.of(context).textTheme.headlineSmall?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
              ),
              const SizedBox(height: 16),
              _LeasingDetailCard(
                title: 'Showing details',
                children: [
                  _LeasingDetailRow(label: 'Status', value: appointment.status),
                  _LeasingDetailRow(
                    label: 'Starts',
                    value: _dateTime(appointment.scheduledStart),
                  ),
                  if (appointment.scheduledEnd != null)
                    _LeasingDetailRow(
                      label: 'Ends',
                      value: _dateTime(appointment.scheduledEnd!),
                    ),
                  if (appointment.propertyName?.isNotEmpty == true)
                    _LeasingDetailRow(
                      label: 'Rental',
                      value:
                          '${appointment.propertyName}${appointment.unitNumber == null ? '' : ' · Unit ${appointment.unitNumber}'}',
                    ),
                  if (appointment.prospectName?.isNotEmpty == true)
                    _LeasingDetailRow(
                      label: 'Prospect',
                      value: appointment.prospectName!,
                    ),
                  if (appointment.prospectEmail?.isNotEmpty == true)
                    _LeasingDetailRow(
                      label: 'Email',
                      value: appointment.prospectEmail!,
                    ),
                  if (appointment.assignedTo?.isNotEmpty == true)
                    _LeasingDetailRow(
                      label: 'Assigned to',
                      value: appointment.assignedTo!,
                    ),
                  if (appointment.notes?.isNotEmpty == true)
                    _LeasingDetailRow(
                      label: 'Notes',
                      value: appointment.notes!,
                    ),
                ],
              ),
              const SizedBox(height: 16),
              FilledButton.icon(
                onPressed: () => openMobileScan(
                  context,
                  propertyId: appointment.propertyId,
                  unitId: appointment.unitId,
                  applicationId: appointment.rentalApplicationId,
                  sourceLabel: appointment.title,
                ),
                icon: const Icon(Symbols.document_scanner_rounded),
                label: const Text('Scan / Add'),
              ),
            ],
          ),
        );
      },
    ),
  );
}

class LeasingMoveInDetailScreen extends ConsumerStatefulWidget {
  const LeasingMoveInDetailScreen({super.key, required this.leaseManagementId});

  final int leaseManagementId;

  @override
  ConsumerState<LeasingMoveInDetailScreen> createState() =>
      _LeasingMoveInDetailScreenState();
}

class LeasingConversationDetailScreen extends ConsumerStatefulWidget {
  const LeasingConversationDetailScreen({
    super.key,
    required this.conversationId,
  });

  final int conversationId;

  @override
  ConsumerState<LeasingConversationDetailScreen> createState() =>
      _LeasingConversationDetailScreenState();
}

class _LeasingConversationDetailScreenState
    extends ConsumerState<LeasingConversationDetailScreen> {
  final _replyController = TextEditingController();
  late Future<LeasingConversationDetail> _future;
  bool _sending = false;

  @override
  void initState() {
    super.initState();
    _future = _load();
  }

  @override
  void dispose() {
    _replyController.dispose();
    super.dispose();
  }

  Future<LeasingConversationDetail> _load() => ref
      .read(leasingWorkspaceRepositoryProvider)
      .conversation(widget.conversationId);

  Future<void> _refresh() async {
    final next = _load();
    setState(() => _future = next);
    await next;
  }

  Future<void> _send() async {
    final body = _replyController.text.trim();
    if (body.isEmpty || _sending) return;
    setState(() => _sending = true);
    try {
      await ref
          .read(leasingWorkspaceRepositoryProvider)
          .replyToConversation(widget.conversationId, body);
      _replyController.clear();
      await _refresh();
    } on ApiException catch (error) {
      if (!mounted) return;
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text(error.message)));
    } finally {
      if (mounted) setState(() => _sending = false);
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Leasing conversation')),
    body: FutureBuilder<LeasingConversationDetail>(
      future: _future,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return const Center(child: CircularProgressIndicator());
        }
        if (snapshot.hasError || snapshot.data == null) {
          return _LeasingDetailError(
            message: _leasingError(snapshot.error),
            onRetry: _refresh,
          );
        }
        final conversation = snapshot.data!;
        return Column(
          children: [
            Material(
              color: Theme.of(context).colorScheme.surfaceContainerLow,
              child: ListTile(
                title: Text(conversation.tenantName),
                subtitle: Text(
                  [
                    conversation.subject,
                    conversation.propertyName,
                  ].whereType<String>().join(' · '),
                ),
              ),
            ),
            Expanded(
              child: RefreshIndicator(
                onRefresh: _refresh,
                child: ListView.builder(
                  reverse: true,
                  physics: const AlwaysScrollableScrollPhysics(),
                  padding: const EdgeInsets.all(16),
                  itemCount: conversation.messages.length,
                  itemBuilder: (context, index) {
                    final message = conversation
                        .messages[conversation.messages.length - index - 1];
                    final fromTenant =
                        message.senderRole.toLowerCase() == 'tenant';
                    return Align(
                      alignment: fromTenant
                          ? Alignment.centerLeft
                          : Alignment.centerRight,
                      child: Card(
                        color: fromTenant
                            ? null
                            : Theme.of(context).colorScheme.primaryContainer,
                        child: ConstrainedBox(
                          constraints: const BoxConstraints(maxWidth: 320),
                          child: Padding(
                            padding: const EdgeInsets.all(12),
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Text(message.body),
                                const SizedBox(height: 4),
                                Text(
                                  _dateTime(message.createdAt),
                                  style: Theme.of(context).textTheme.bodySmall,
                                ),
                              ],
                            ),
                          ),
                        ),
                      ),
                    );
                  },
                ),
              ),
            ),
            SafeArea(
              top: false,
              child: Padding(
                padding: const EdgeInsets.fromLTRB(12, 8, 8, 8),
                child: Row(
                  children: [
                    Expanded(
                      child: TextField(
                        controller: _replyController,
                        enabled: !_sending,
                        minLines: 1,
                        maxLines: 4,
                        textInputAction: TextInputAction.newline,
                        decoration: const InputDecoration(
                          hintText: 'Reply in the tenant portal',
                        ),
                      ),
                    ),
                    IconButton.filled(
                      tooltip: 'Send reply',
                      onPressed: _sending ? null : _send,
                      icon: _sending
                          ? const SizedBox.square(
                              dimension: 18,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : const Icon(Symbols.send_rounded),
                    ),
                  ],
                ),
              ),
            ),
          ],
        );
      },
    ),
  );
}

class _LeasingMoveInDetailScreenState
    extends ConsumerState<LeasingMoveInDetailScreen> {
  late Future<LeasingMoveInDetail> _future;

  @override
  void initState() {
    super.initState();
    _future = _load();
  }

  Future<LeasingMoveInDetail> _load() => ref
      .read(leasingWorkspaceRepositoryProvider)
      .moveIn(widget.leaseManagementId);

  Future<void> _refresh() async {
    final next = _load();
    setState(() => _future = next);
    await next;
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Move-in')),
    body: FutureBuilder<LeasingMoveInDetail>(
      future: _future,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return const Center(child: CircularProgressIndicator());
        }
        if (snapshot.hasError || snapshot.data == null) {
          return _LeasingDetailError(
            message: _leasingError(snapshot.error),
            onRetry: _refresh,
          );
        }

        final moveIn = snapshot.data!;
        return RefreshIndicator(
          onRefresh: _refresh,
          child: ListView(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
            children: [
              Text(
                moveIn.tenantName,
                style: Theme.of(context).textTheme.headlineSmall?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
              ),
              const SizedBox(height: 4),
              Text('${moveIn.propertyName} · Unit ${moveIn.unitNumber}'),
              const SizedBox(height: 16),
              _LeasingDetailCard(
                title: 'Move-in readiness',
                children: [
                  _LeasingDetailRow(
                    label: 'Relationship',
                    value: moveIn.relationshipNumber,
                  ),
                  _LeasingDetailRow(
                    label: 'Agreement',
                    value: moveIn.agreementFullyExecuted
                        ? 'Fully signed'
                        : 'Signature required',
                  ),
                  _LeasingDetailRow(
                    label: 'Possession',
                    value: moveIn.possessionGiven ? 'Given' : 'Not yet given',
                  ),
                  if (moveIn.plannedPossessionAtUtc != null)
                    _LeasingDetailRow(
                      label: 'Planned move-in',
                      value: _dateTime(moveIn.plannedPossessionAtUtc!),
                    ),
                ],
              ),
              const SizedBox(height: 16),
              FilledButton.icon(
                onPressed: () => openMobileScan(
                  context,
                  initialTargetEntityType: 'LeaseAgreement',
                  lockTargetEntityType: true,
                  propertyId: moveIn.propertyId,
                  unitId: moveIn.unitId,
                  leaseManagementId: moveIn.id,
                  sourceLabel:
                      '${moveIn.propertyName} · Unit ${moveIn.unitNumber}',
                ),
                icon: const Icon(Symbols.document_scanner_rounded),
                label: const Text('Scan / Add'),
              ),
            ],
          ),
        );
      },
    ),
  );
}

class _LeasingDetailCard extends StatelessWidget {
  const _LeasingDetailCard({required this.title, required this.children});

  final String title;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            title,
            style: Theme.of(
              context,
            ).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700),
          ),
          const SizedBox(height: 12),
          ...children,
        ],
      ),
    ),
  );
}

class _LeasingDetailRow extends StatelessWidget {
  const _LeasingDetailRow({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 10),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(label, style: Theme.of(context).textTheme.labelMedium),
        const SizedBox(height: 2),
        Text(value),
      ],
    ),
  );
}

class _LeasingDetailError extends StatelessWidget {
  const _LeasingDetailError({required this.message, required this.onRetry});

  final String message;
  final Future<void> Function() onRetry;

  @override
  Widget build(BuildContext context) => Center(
    child: Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(message, textAlign: TextAlign.center),
          const SizedBox(height: 12),
          FilledButton(onPressed: onRetry, child: const Text('Try again')),
        ],
      ),
    ),
  );
}

String _leasingError(Object? error) => error is ApiException
    ? error.message
    : 'Unable to load this leasing record.';

String _date(DateTime value) {
  final local = value.toLocal();
  return '${local.month}/${local.day}/${local.year}';
}

String _dateTime(DateTime value) {
  final local = value.toLocal();
  final minute = local.minute.toString().padLeft(2, '0');
  final hour = local.hour == 0
      ? 12
      : (local.hour > 12 ? local.hour - 12 : local.hour);
  final period = local.hour >= 12 ? 'PM' : 'AM';
  return '${_date(local)} · $hour:$minute $period';
}
