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
      collectedLast30Days: (json['collectedLast30Days'] as num?)?.toDouble() ?? 0,
      spentLast30Days: (json['spentLast30Days'] as num?)?.toDouble() ?? 0,
      netLast30Days: (json['netLast30Days'] as num?)?.toDouble() ?? 0,
      explanations: MoneySnapshotExplanations.fromJson(
        (json['explanations'] as Map<String, dynamic>?) ?? const {},
      ),
    );
  }
}

/// The "Who's behind" list from `GET /api/v1/accounting/past-due`: one row per
/// lease/tenant behind on rent, plus the headline totals. The list shares the
/// snapshot's past-due definition, so [totalCount] always equals the dashboard
/// "tenants behind" KPI and the number of [items].
class PastDueResult {
  const PastDueResult({
    required this.items,
    required this.totalCount,
    required this.totalPastDueAmount,
  });

  final List<PastDueLease> items;
  final int totalCount;
  final double totalPastDueAmount;

  factory PastDueResult.fromJson(Map<String, dynamic> json) {
    return PastDueResult(
      items: (json['items'] as List<dynamic>? ?? [])
          .whereType<Map<String, dynamic>>()
          .map(PastDueLease.fromJson)
          .toList(),
      totalCount: (json['totalCount'] as num?)?.toInt() ?? 0,
      totalPastDueAmount:
          (json['totalPastDueAmount'] as num?)?.toDouble() ?? 0,
    );
  }
}

/// One lease/tenant behind on rent: who they are, how much they owe, how many
/// payments are past due, how long they've waited, and the oldest past-due
/// payment id for a deep-link.
class PastDueLease {
  const PastDueLease({
    required this.leaseId,
    required this.pastDueAmount,
    required this.overduePaymentCount,
    required this.oldestDueDate,
    required this.oldestPaymentId,
    this.tenantName,
    this.tenantPhone,
    this.leaseNumber,
    this.propertyName,
    this.unitNumber,
  });

  final int leaseId;
  final double pastDueAmount;
  final int overduePaymentCount;
  final DateTime oldestDueDate;
  final int oldestPaymentId;
  final String? tenantName;
  final String? tenantPhone;
  final String? leaseNumber;
  final String? propertyName;
  final String? unitNumber;

  /// A friendly label for the row: tenant name, else lease number, else lease id.
  String get displayName =>
      tenantName ??
      (leaseNumber != null ? 'Lease $leaseNumber' : 'Lease #$leaseId');

  factory PastDueLease.fromJson(Map<String, dynamic> json) {
    return PastDueLease(
      leaseId: (json['leaseId'] as num?)?.toInt() ?? 0,
      pastDueAmount: (json['pastDueAmount'] as num?)?.toDouble() ?? 0,
      overduePaymentCount: (json['overduePaymentCount'] as num?)?.toInt() ?? 0,
      oldestDueDate:
          DateTime.tryParse(json['oldestDueDate'] as String? ?? '') ??
              DateTime(0),
      oldestPaymentId: (json['oldestPaymentId'] as num?)?.toInt() ?? 0,
      tenantName: json['tenantName'] as String?,
      tenantPhone: json['tenantPhone'] as String?,
      leaseNumber: json['leaseNumber'] as String?,
      propertyName: json['propertyName'] as String?,
      unitNumber: json['unitNumber'] as String?,
    );
  }
}
