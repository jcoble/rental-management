import 'package:flutter/material.dart';

import '../../core/config/app_config.dart';
import '../../core/presentation/formatting.dart';

// ── Date formatting ───────────────────────────────────────────────────────────

String _padTwo(int n) => n.toString().padLeft(2, '0');

String formatApplicationDateTime(DateTime d) {
  final local = d.toLocal();
  final hour = local.hour % 12 == 0 ? 12 : local.hour % 12;
  final period = local.hour < 12 ? 'AM' : 'PM';
  return '${dateFmt(local)}  '
      '$hour:${_padTwo(local.minute)} $period';
}

/// Builds the full applicant-facing apply URL from the server-relative
/// [applyPath] (e.g. `/apply/{token}`).
///
/// The applicant web app is served from the same host as the API but without
/// the `/api/v1` suffix, so we derive the base from [kApiBaseUrl]. If that
/// can't be parsed we fall back to just the relative path.
String buildApplyUrl(String applyPath) {
  final path = applyPath.startsWith('/') ? applyPath : '/$applyPath';
  final uri = Uri.tryParse(kApiBaseUrl);
  if (uri == null || uri.host.isEmpty) return path;
  final base = uri.replace(path: '', query: '', fragment: '');
  return '${base.toString().replaceAll(RegExp(r'/+$'), '')}$path';
}

// ── Status chip ───────────────────────────────────────────────────────────────

class ApplicationStatusChip extends StatelessWidget {
  const ApplicationStatusChip({super.key, required this.status});

  final String status;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    Color bg;
    Color fg;
    switch (status) {
      case 'Submitted':
        bg = colorScheme.secondaryContainer;
        fg = colorScheme.onSecondaryContainer;
        break;
      case 'UnderReview':
        bg = colorScheme.primaryContainer;
        fg = colorScheme.onPrimaryContainer;
        break;
      case 'Approved':
        bg = colorScheme.tertiaryContainer;
        fg = colorScheme.onTertiaryContainer;
        break;
      case 'Declined':
        bg = colorScheme.errorContainer;
        fg = colorScheme.onErrorContainer;
        break;
      case 'Withdrawn':
        bg = colorScheme.surfaceContainerHighest;
        fg = colorScheme.onSurfaceVariant;
        break;
      default:
        bg = colorScheme.surfaceContainerHighest;
        fg = colorScheme.onSurfaceVariant;
    }
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        status == 'UnderReview' ? 'Under Review' : status,
        style: TextStyle(fontSize: 11, fontWeight: FontWeight.w600, color: fg),
      ),
    );
  }
}
