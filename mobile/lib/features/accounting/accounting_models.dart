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
