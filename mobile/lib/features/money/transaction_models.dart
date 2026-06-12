// Models for the unified accounting ledger
// (`GET /api/v1/accounting/transactions`).

/// One row in the unified ledger — either a Payment or an Expense, normalized
/// to a common shape by the API.
class AccountingTransaction {
  const AccountingTransaction({
    required this.kind,
    required this.id,
    required this.date,
    required this.description,
    required this.category,
    required this.status,
    required this.amount,
    this.propertyId,
    this.propertyName,
    this.counterparty,
    this.detailHref,
    required this.hasReceipt,
    required this.receiptIsImage,
    required this.reconciled,
  });

  /// "Payment" or "Expense".
  final String kind;
  final int id;
  final DateTime date;
  final String description;
  final String category;
  final String status;
  final double amount;
  final int? propertyId;
  final String? propertyName;
  final String? counterparty;
  final String? detailHref;
  final bool hasReceipt;
  final bool receiptIsImage;
  final bool reconciled;

  bool get isPayment => kind.toLowerCase() == 'payment';
  bool get isExpense => kind.toLowerCase() == 'expense';

  factory AccountingTransaction.fromJson(Map<String, dynamic> json) {
    return AccountingTransaction(
      kind: json['kind'] as String? ?? '',
      id: (json['id'] as num?)?.toInt() ?? 0,
      date: DateTime.tryParse(json['date'] as String? ?? '') ?? DateTime(0),
      description: json['description'] as String? ?? '',
      category: json['category'] as String? ?? '',
      status: json['status'] as String? ?? '',
      amount: (json['amount'] as num?)?.toDouble() ?? 0,
      propertyId: (json['propertyId'] as num?)?.toInt(),
      propertyName: json['propertyName'] as String?,
      counterparty: json['counterparty'] as String?,
      detailHref: json['detailHref'] as String?,
      hasReceipt: json['hasReceipt'] as bool? ?? false,
      receiptIsImage: json['receiptIsImage'] as bool? ?? false,
      reconciled: json['reconciled'] as bool? ?? false,
    );
  }
}

/// A page of [AccountingTransaction]s plus paging metadata.
class AccountingTransactionsPage {
  const AccountingTransactionsPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<AccountingTransaction> items;
  final int totalCount;
  final int skip;
  final int take;

  /// True when more rows exist past this page.
  bool get hasMore => skip + items.length < totalCount;

  factory AccountingTransactionsPage.fromJson(Map<String, dynamic> json) {
    return AccountingTransactionsPage(
      items: ((json['items'] as List<dynamic>?) ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(AccountingTransaction.fromJson)
          .toList(),
      totalCount: (json['totalCount'] as num?)?.toInt() ?? 0,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? 0,
    );
  }
}

/// Filter that the ledger UI passes through to the API. `kind` is "Payment",
/// "Expense", or null (all). `from`/`to` bound the transaction date.
class TransactionsFilter {
  const TransactionsFilter({this.kind, this.from, this.to});

  final String? kind;
  final DateTime? from;
  final DateTime? to;

  bool get isEmpty => kind == null && from == null && to == null;

  TransactionsFilter copyWith({
    String? kind,
    DateTime? from,
    DateTime? to,
    bool clearKind = false,
    bool clearFrom = false,
    bool clearTo = false,
  }) {
    return TransactionsFilter(
      kind: clearKind ? null : (kind ?? this.kind),
      from: clearFrom ? null : (from ?? this.from),
      to: clearTo ? null : (to ?? this.to),
    );
  }
}
