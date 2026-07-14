class BankingSummary {
  const BankingSummary({
    required this.connectionCount,
    required this.transactionCount,
    required this.unmatchedCount,
    required this.suggestedMatchCount,
    this.lastSyncedAt,
    this.connections = const [],
    this.recentTransactions = const [],
  });

  final int connectionCount;
  final int transactionCount;
  final int unmatchedCount;
  final int suggestedMatchCount;
  final DateTime? lastSyncedAt;
  final List<BankConnection> connections;
  final List<BankTransaction> recentTransactions;

  factory BankingSummary.fromJson(Map<String, dynamic> json) {
    return BankingSummary(
      connectionCount: (json['connectionCount'] as num?)?.toInt() ?? 0,
      transactionCount: (json['transactionCount'] as num?)?.toInt() ?? 0,
      unmatchedCount: (json['unmatchedCount'] as num?)?.toInt() ?? 0,
      suggestedMatchCount: (json['suggestedMatchCount'] as num?)?.toInt() ?? 0,
      lastSyncedAt: DateTime.tryParse(json['lastSyncedAt'] as String? ?? ''),
      connections: (json['connections'] as List? ?? [])
          .whereType<Map<String, dynamic>>()
          .map(BankConnection.fromJson)
          .toList(),
      recentTransactions: (json['recentTransactions'] as List? ?? [])
          .whereType<Map<String, dynamic>>()
          .map(BankTransaction.fromJson)
          .toList(),
    );
  }
}

class BankConnection {
  const BankConnection({
    required this.id,
    required this.provider,
    required this.institutionName,
    required this.accountName,
    this.accountMask,
    required this.status,
    this.lastSyncedAt,
  });

  final int id;
  final String provider;
  final String institutionName;
  final String accountName;
  final String? accountMask;
  final String status;
  final DateTime? lastSyncedAt;

  factory BankConnection.fromJson(Map<String, dynamic> json) {
    return BankConnection(
      id: (json['id'] as num).toInt(),
      provider: json['provider'] as String? ?? 'Plaid',
      institutionName: json['institutionName'] as String? ?? '',
      accountName: json['accountName'] as String? ?? '',
      accountMask: json['accountMask'] as String?,
      status: json['status'] as String? ?? 'Active',
      lastSyncedAt: DateTime.tryParse(json['lastSyncedAt'] as String? ?? ''),
    );
  }
}

class BankTransaction {
  const BankTransaction({
    required this.id,
    required this.postedAt,
    required this.description,
    this.merchantName,
    required this.amount,
    required this.matchStatus,
    this.suggestedMatch,
    required this.institutionName,
    required this.accountName,
  });

  final int id;
  final DateTime postedAt;
  final String description;
  final String? merchantName;
  final double amount;
  final String matchStatus;
  final BankMatchSuggestion? suggestedMatch;
  final String institutionName;
  final String accountName;

  factory BankTransaction.fromJson(Map<String, dynamic> json) {
    final rawSuggestion = json['suggestedMatch'];
    return BankTransaction(
      id: (json['id'] as num).toInt(),
      postedAt: DateTime.tryParse(json['postedAt'] as String? ?? '') ?? DateTime(0),
      description: json['description'] as String? ?? '',
      merchantName: json['merchantName'] as String?,
      amount: (json['amount'] as num?)?.toDouble() ?? 0,
      matchStatus: json['matchStatus'] as String? ?? 'Unmatched',
      suggestedMatch: rawSuggestion is Map<String, dynamic>
          ? BankMatchSuggestion.fromJson(rawSuggestion)
          : null,
      institutionName: json['institutionName'] as String? ?? '',
      accountName: json['accountName'] as String? ?? '',
    );
  }
}

class BankMatchSuggestion {
  const BankMatchSuggestion({
    required this.entityType,
    required this.entityId,
    required this.confidence,
    required this.label,
    required this.reason,
  });

  final String entityType;
  final int entityId;
  final double confidence;
  final String label;
  final String reason;

  factory BankMatchSuggestion.fromJson(Map<String, dynamic> json) {
    return BankMatchSuggestion(
      entityType: json['entityType'] as String? ?? '',
      entityId: (json['entityId'] as num?)?.toInt() ?? 0,
      confidence: (json['confidence'] as num?)?.toDouble() ?? 0,
      label: json['label'] as String? ?? '',
      reason: json['reason'] as String? ?? '',
    );
  }
}

/// A single bank line that may be a duplicate of a payment/expense already on
/// record. The backend pairs the raw [transaction] with a [suggestion].
class BankReviewItem {
  const BankReviewItem({
    required this.transaction,
    required this.suggestion,
  });

  final BankReviewTransaction transaction;
  final BankReviewSuggestion suggestion;

  factory BankReviewItem.fromJson(Map<String, dynamic> json) {
    final rawSuggestion = json['suggestion'];
    return BankReviewItem(
      transaction: BankReviewTransaction.fromJson(
        (json['transaction'] as Map<String, dynamic>?) ?? const {},
      ),
      suggestion: BankReviewSuggestion.fromJson(
        rawSuggestion is Map<String, dynamic> ? rawSuggestion : const {},
      ),
    );
  }
}

class BankReviewSuggestion {
  const BankReviewSuggestion({
    required this.confidence,
    required this.label,
    required this.reason,
  });

  final double confidence;
  final String label;
  final String reason;

  factory BankReviewSuggestion.fromJson(Map<String, dynamic> json) {
    return BankReviewSuggestion(
      confidence: (json['confidence'] as num?)?.toDouble() ?? 0,
      label: json['label'] as String? ?? '',
      reason: json['reason'] as String? ?? '',
    );
  }
}

/// Lightweight bank line returned inside the review queue.
class BankReviewTransaction {
  const BankReviewTransaction({
    required this.id,
    required this.postedAt,
    required this.amount,
    required this.description,
    this.merchantName,
    required this.matchStatus,
  });

  final int id;
  final DateTime postedAt;
  final double amount;
  final String description;
  final String? merchantName;
  final String matchStatus;

  factory BankReviewTransaction.fromJson(Map<String, dynamic> json) {
    return BankReviewTransaction(
      id: (json['id'] as num?)?.toInt() ?? 0,
      postedAt: DateTime.tryParse(json['postedAt'] as String? ?? '') ?? DateTime(0),
      amount: (json['amount'] as num?)?.toDouble() ?? 0,
      description: json['description'] as String? ?? '',
      merchantName: json['merchantName'] as String?,
      matchStatus: json['matchStatus'] as String? ?? 'Unmatched',
    );
  }
}

/// The full duplicate-review queue: a [count] and the list of [items].
class BankReviewQueue {
  const BankReviewQueue({
    required this.count,
    required this.items,
  });

  final int count;
  final List<BankReviewItem> items;

  factory BankReviewQueue.fromJson(Map<String, dynamic> json) {
    final items = (json['items'] as List? ?? [])
        .whereType<Map<String, dynamic>>()
        .map(BankReviewItem.fromJson)
        .toList();
    return BankReviewQueue(
      count: (json['count'] as num?)?.toInt() ?? items.length,
      items: items,
    );
  }
}
