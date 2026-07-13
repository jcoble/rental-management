import 'package:flutter/material.dart';

import '../ai/ai_tab.dart';
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
