import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../ai/ai_tab.dart';
import '../scan/scan_capture.dart';
import '../scan/scan_review_screen.dart';
import '../technician/technician_assignment_picker_sheet.dart';
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
Future<void> openAuthorizedMobileScan(
  BuildContext context,
  WidgetRef ref,
) async {
  final auth = ref.read(authControllerProvider);
  if (auth is AuthStateAuthenticated &&
      auth.capabilities.contains('maintenance.assigned-work.update') &&
      !auth.capabilities.contains('work.manage')) {
    final selected = await showTechnicianAssignmentPicker(context);
    if (selected == null || !context.mounted) return;
    return openMobileScan(
      context,
      initialTargetEntityType: 'WorkOrder',
      lockTargetEntityType: true,
      workOrderId: selected.id,
      sourceLabel: selected.title,
    );
  }
  return openMobileScan(context);
}

Future<void> openMobileScan(
  BuildContext context, {
  String initialTargetEntityType = '',
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
