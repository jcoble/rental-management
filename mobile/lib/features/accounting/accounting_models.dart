// Models for the money-legibility surfaces: the plain-English money snapshot
// (`GET /api/v1/accounting/snapshot`).

/// Ready-to-show, plain-English sentences for each headline number on the
/// money snapshot. Each is a complete sentence a non-technical landlord can
/// read at a glance.
class MoneySnapshotExplanations {
  const MoneySnapshotExplanations({
    required this.collected,
    required this.spent,
    required this.net,
    required this.pastDue,
  });

  final String collected;
  final String spent;
  final String net;
  final String pastDue;

  factory MoneySnapshotExplanations.fromJson(Map<String, dynamic> json) {
    return MoneySnapshotExplanations(
      collected: json['collected'] as String? ?? '',
      spent: json['spent'] as String? ?? '',
      net: json['net'] as String? ?? '',
      pastDue: json['pastDue'] as String? ?? '',
    );
  }
}

/// Full response from `GET /api/v1/accounting/snapshot`.
///
/// The headline numbers are month-to-date; the `*Last30Days` figures cover a
/// trailing 30-day window. Every figure has a matching plain-English sentence
/// in [explanations].
class MoneySnapshot {
  const MoneySnapshot({
    required this.periodLabel,
    required this.collected,
    required this.spent,
    required this.net,
    required this.pastDueAmount,
    required this.pastDueCount,
    required this.collectedLast30Days,
    required this.spentLast30Days,
    required this.netLast30Days,
    required this.explanations,
  });

  /// Human label for the headline period, e.g. "June so far".
  final String periodLabel;

  /// Money collected this period.
  final double collected;

  /// Money spent this period.
  final double spent;

  /// What's kept (collected minus spent) this period.
  final double net;

  /// Total amount currently past due across the portfolio.
  final double pastDueAmount;

  /// How many tenants are behind on payments.
  final int pastDueCount;

  final double collectedLast30Days;
  final double spentLast30Days;
  final double netLast30Days;

  final MoneySnapshotExplanations explanations;

  factory MoneySnapshot.fromJson(Map<String, dynamic> json) {
    return MoneySnapshot(
      periodLabel: json['periodLabel'] as String? ?? '',
      collected: (json['collected'] as num?)?.toDouble() ?? 0,
      spent: (json['spent'] as num?)?.toDouble() ?? 0,
      net: (json['net'] as num?)?.toDouble() ?? 0,
      pastDueAmount: (json['pastDueAmount'] as num?)?.toDouble() ?? 0,
      pastDueCount: (json['pastDueCount'] as num?)?.toInt() ?? 0,
      collectedLast30Days:
          (json['collectedLast30Days'] as num?)?.toDouble() ?? 0,
      spentLast30Days: (json['spentLast30Days'] as num?)?.toDouble() ?? 0,
      netLast30Days: (json['netLast30Days'] as num?)?.toDouble() ?? 0,
      explanations: MoneySnapshotExplanations.fromJson(
        (json['explanations'] as Map<String, dynamic>?) ?? const {},
      ),
    );
  }
}

/// The "Who's behind" list from `GET /api/v1/accounting/past-due`: one row per
/// tenant account behind on rent, plus exact portfolio totals and page metadata.
/// The list shares the snapshot's past-due definition, so [totalCount] always
/// equals the dashboard "tenants behind" KPI while [items] is one server page.
class PastDueResult {
  const PastDueResult({
    required this.items,
    required this.totalCount,
    required this.totalPastDueAmount,
    required this.businessDate,
    required this.skip,
    required this.take,
  });

  final List<PastDueLease> items;
  final int totalCount;
  final double totalPastDueAmount;
  final DateTime? businessDate;
  final int skip;
  final int take;

  bool get hasMore => skip + items.length < totalCount;

  factory PastDueResult.fromJson(Map<String, dynamic> json) {
    final items = (json['items'] as List<dynamic>)
        .map((item) => PastDueLease.fromJson(item as Map<String, dynamic>))
        .toList(growable: false);
    final businessDateValue = json['businessDate'];
    if (items.isNotEmpty && businessDateValue is! String) {
      throw const FormatException(
        'Past-due rows require the portfolio business date.',
      );
    }
    return PastDueResult(
      items: items,
      totalCount: (json['totalCount'] as num).toInt(),
      totalPastDueAmount: (json['totalPastDueAmount'] as num).toDouble(),
      businessDate: businessDateValue is String
          ? DateTime.parse(businessDateValue)
          : null,
      skip: (json['skip'] as num).toInt(),
      take: (json['take'] as num).toInt(),
    );
  }
}

/// One canonical tenant account behind on rent, with its continuous rental
/// relationship, unit context, and oldest open ledger charge.
class PastDueLease {
  const PastDueLease({
    required this.leaseManagementId,
    required this.tenantAccountId,
    required this.unitId,
    required this.pastDueAmount,
    required this.overduePaymentCount,
    required this.oldestDueOn,
    required this.oldestLedgerEntryId,
    required this.oldestLedgerEntryOpenAmount,
    this.currentAgreementId,
    this.tenantName,
    this.tenantPhone,
    this.relationshipNumber,
    this.propertyName,
    this.unitNumber,
  });

  final int leaseManagementId;
  final int tenantAccountId;
  final int? currentAgreementId;
  final int unitId;
  final double pastDueAmount;
  final int overduePaymentCount;
  final DateTime oldestDueOn;
  final int oldestLedgerEntryId;
  final double oldestLedgerEntryOpenAmount;
  final String? tenantName;
  final String? tenantPhone;
  final String? relationshipNumber;
  final String? propertyName;
  final String? unitNumber;

  /// A friendly label for the account without reviving legacy lease identity.
  String get displayName => tenantName?.trim().isNotEmpty == true
      ? tenantName!.trim()
      : relationshipNumber?.trim().isNotEmpty == true
      ? 'Tenancy #${relationshipNumber!.trim()}'
      : unitNumber?.trim().isNotEmpty == true
      ? 'Unit ${unitNumber!.trim()}'
      : 'Tenant account';

  factory PastDueLease.fromJson(Map<String, dynamic> json) {
    return PastDueLease(
      leaseManagementId: (json['leaseManagementId'] as num).toInt(),
      tenantAccountId: (json['tenantAccountId'] as num).toInt(),
      currentAgreementId: (json['currentAgreementId'] as num?)?.toInt(),
      unitId: (json['unitId'] as num).toInt(),
      pastDueAmount: (json['pastDueAmount'] as num).toDouble(),
      overduePaymentCount: (json['overduePaymentCount'] as num).toInt(),
      oldestDueOn: DateTime.parse(json['oldestDueOn'] as String),
      oldestLedgerEntryId: (json['oldestLedgerEntryId'] as num).toInt(),
      oldestLedgerEntryOpenAmount: (json['oldestLedgerEntryOpenAmount'] as num)
          .toDouble(),
      tenantName: json['tenantName'] as String?,
      tenantPhone: json['tenantPhone'] as String?,
      relationshipNumber: json['relationshipNumber'] as String?,
      propertyName: json['propertyName'] as String?,
      unitNumber: json['unitNumber'] as String?,
    );
  }
}
