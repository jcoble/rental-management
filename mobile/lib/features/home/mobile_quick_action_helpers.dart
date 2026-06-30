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

Future<void> openMobileScan(BuildContext context) async {
  final draftId = await showScanCaptureSheet(context);
  if (draftId == null || !context.mounted) return;

  await Navigator.of(context).push<void>(
    MaterialPageRoute<void>(builder: (_) => ScanReviewScreen(draftId: draftId)),
  );
}
