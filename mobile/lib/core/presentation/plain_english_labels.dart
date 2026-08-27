const _plainEnglishOverrides = <String, String>{
  'PossessionScheduled': 'Move-in scheduled',
  'NotAvailable': 'Not available',
  'NoAccount': 'No tenant account',
  'PastDue': 'Past due',
  'NoticeOpen': 'Open notice',
  'NoGoverningAgreement': 'No signed lease',
  'NoNotices': 'No notices',
  'AgreementOnFile': 'Signed lease on file',
  'AwaitingVacancy': 'Waiting for move-out',
  'MoveOut': 'Move-out',
  'InProgress': 'In progress',
  'WaitingParts': 'Waiting for parts',
  'RentReady': 'Ready to rent',
  'NotStarted': 'Not started',
  'NoLease': 'No lease',
  'MoveIn': 'Move-in',
  'MoveOutInspection': 'Move-out inspection',
  'LeaseAgreement': 'Lease agreement',
  'TenantAccount': 'Tenant account',
};

const _tenantLedgerEntryLabels = <String, String>{
  'OpeningBalance': 'Opening balance',
  'RentCharge': 'Rent charged',
  'AddendumCharge': 'Lease add-on charged',
  'LateFeeCharge': 'Late fee charged',
  'DepositCharge': 'Deposit charged',
  'ManualCharge': 'Manual charge',
  'PaymentReceipt': 'Payment received',
  'Credit': 'Credit',
  'Adjustment': 'Adjustment',
  'Refund': 'Refund',
  'TransferIn': 'Transfer received',
  'TransferOut': 'Transfer sent',
  'Reversal': 'Reversal',
};

/// Converts backend identifiers into sentence-style labels at the UI boundary.
///
/// Explicit domain wording wins. Unknown PascalCase, camelCase, snake_case, or
/// kebab-case values receive a readable fallback without changing the wire value.
String plainEnglishLabel(String value, {String fallback = 'Not set'}) {
  final trimmed = value.trim();
  if (trimmed.isEmpty) return fallback;

  final override = _plainEnglishOverrides[trimmed];
  if (override != null) return override;

  final words = trimmed
      .replaceAll(RegExp(r'[_-]+'), ' ')
      .replaceAllMapped(
        RegExp(r'([a-z0-9])([A-Z])'),
        (match) => '${match.group(1)} ${match.group(2)}',
      )
      .replaceAllMapped(
        RegExp(r'([A-Z]+)([A-Z][a-z])'),
        (match) => '${match.group(1)} ${match.group(2)}',
      )
      .replaceAll(RegExp(r'\s+'), ' ')
      .trim();

  if (words.isEmpty) return fallback;
  final sentence = words.toLowerCase();
  return '${sentence[0].toUpperCase()}${sentence.substring(1)}';
}

/// Understandable labels for every current TenantLedgerEntryType wire value.
String tenantLedgerEntryLabel(String value) {
  return _tenantLedgerEntryLabels[value.trim()] ?? plainEnglishLabel(value);
}
