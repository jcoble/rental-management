import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../ai/ai_tab.dart';
import '../maintenance/work_orders_repository.dart';
import '../scan/scan_capture.dart';
import '../scan/scan_review_screen.dart';
import '../voice/tell_me_screen.dart';

void openMobileAssistant(BuildContext context) {
  Navigator.of(
    context,
  ).push<void>(MaterialPageRoute<void>(builder: (_) => const AiTab()));
}

void openMobileRecord(BuildContext context) {
  Navigator.of(
    context,
  ).push<void>(MaterialPageRoute<void>(builder: (_) => const TellMeScreen()));
}

/// Keeps the global Scan / Add action for technicians without granting a context-free scan.
/// The work-order list is already filtered by the canonical assigned-work SQL authorization.
Future<void> openAuthorizedMobileScan(BuildContext context, WidgetRef ref) async {
  final auth = ref.read(authControllerProvider);
  if (auth is AuthStateAuthenticated &&
      auth.capabilities.contains('maintenance.assigned-work.update') &&
      !auth.capabilities.contains('work.manage')) {
    final assigned = await ref
        .read(workOrdersRepositoryProvider)
        .listWorkOrders(openOnly: true, take: 100);
    if (!context.mounted) return;
    final selected = await showDialog<int>(
      context: context,
      builder: (dialogContext) => SimpleDialog(
        title: const Text('Scan for assigned work order'),
        children: [
          if (assigned.isEmpty)
            const Padding(
              padding: EdgeInsets.all(24),
              child: Text('No current assigned work orders.'),
            ),
          for (final workOrder in assigned)
            SimpleDialogOption(
              onPressed: () => Navigator.pop(dialogContext, workOrder.id),
              child: Text(workOrder.title),
            ),
        ],
      ),
    );
    if (selected == null || !context.mounted) return;
    return openMobileScan(
      context,
      initialTargetEntityType: 'WorkOrder',
      lockTargetEntityType: true,
      workOrderId: selected,
      sourceLabel: 'Assigned work order',
    );
  }
  return openMobileScan(context);
}

Future<void> openMobileScan(
  BuildContext context, {
  String initialTargetEntityType = 'Expense',
  bool lockTargetEntityType = false,
  int? propertyId,
  int? unitId,
  int? leaseManagementId,
  int? leaseAgreementId,
  int? tenantAccountId,
  int? tenantLedgerEntryId,
  int? workOrderId,
  int? applicationId,
  int? rentalListingId,
  String? sourceLabel,
}) async {
  final draftId = await showScanCaptureSheet(
    context,
    initialTargetEntityType: initialTargetEntityType,
    lockTargetEntityType: lockTargetEntityType,
    propertyId: propertyId,
    unitId: unitId,
    leaseManagementId: leaseManagementId,
    leaseAgreementId: leaseAgreementId,
    tenantAccountId: tenantAccountId,
    tenantLedgerEntryId: tenantLedgerEntryId,
    workOrderId: workOrderId,
    applicationId: applicationId,
    rentalListingId: rentalListingId,
    sourceLabel: sourceLabel,
  );
  if (draftId == null || !context.mounted) return;

  await Navigator.of(context).push<void>(
    MaterialPageRoute<void>(builder: (_) => ScanReviewScreen(draftId: draftId)),
  );
}
