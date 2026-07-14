import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/api/api_exception.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'leasing_workspace_repository.dart';

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
                      value: _money(rental.askingRent!),
                    ),
                  if (rental.securityDeposit != null)
                    _LeasingDetailRow(
                      label: 'Security deposit',
                      value: _money(rental.securityDeposit!),
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

String _money(double amount) => '\$${amount.toStringAsFixed(2)}';

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
