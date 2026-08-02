// Models for the expenses surfaces (`/api/v1/expenses`).
//
// Enums serialize as STRING names app-wide (matches the API's
// JsonStringEnumConverter): `category` and `status` are the enum names.

/// IRS Schedule E expense categories (string-name serialized).
enum ScheduleECategory {
  advertising('Advertising', 'Advertising'),
  autoTravel('AutoTravel', 'Auto & travel'),
  cleaningMaintenance('CleaningMaintenance', 'Cleaning & maintenance'),
  commissions('Commissions', 'Commissions'),
  insurance('Insurance', 'Insurance'),
  legalProfessional('LegalProfessional', 'Legal & professional'),
  managementFees('ManagementFees', 'Management fees'),
  mortgageInterest('MortgageInterest', 'Mortgage interest'),
  repairs('Repairs', 'Repairs'),
  supplies('Supplies', 'Supplies'),
  taxes('Taxes', 'Taxes'),
  utilities('Utilities', 'Utilities'),
  depreciation('Depreciation', 'Depreciation'),
  other('Other', 'Other');

  const ScheduleECategory(this.wire, this.label);

  /// The string name the API sends/expects.
  final String wire;

  /// Human label for display.
  final String label;

  static ScheduleECategory fromWire(String? wire) {
    return ScheduleECategory.values.firstWhere(
      (c) => c.wire == wire,
      orElse: () => ScheduleECategory.other,
    );
  }
}

/// Expense lifecycle status (string-name serialized).
enum ExpenseStatus {
  pending('Pending', 'Pending'),
  approved('Approved', 'Approved'),
  paid('Paid', 'Paid'),
  rejected('Rejected', 'Rejected'),
  draft('Draft', 'Draft');

  const ExpenseStatus(this.wire, this.label);

  final String wire;
  final String label;

  static ExpenseStatus fromWire(String? wire) {
    return ExpenseStatus.values.firstWhere(
      (s) => s.wire == wire,
      orElse: () => ExpenseStatus.pending,
    );
  }
}

/// One typed line item on an [Expense] (populated on the single-item GET).
class ExpenseLineItem {
  const ExpenseLineItem({
    required this.description,
    this.quantity,
    this.unitPrice,
    this.amount,
    required this.lineNumber,
  });

  final String description;
  final double? quantity;
  final double? unitPrice;
  final double? amount;
  final int lineNumber;

  factory ExpenseLineItem.fromJson(Map<String, dynamic> json) {
    return ExpenseLineItem(
      description: json['description'] as String? ?? '',
      quantity: (json['quantity'] as num?)?.toDouble(),
      unitPrice: (json['unitPrice'] as num?)?.toDouble(),
      amount: (json['amount'] as num?)?.toDouble(),
      lineNumber: (json['lineNumber'] as num?)?.toInt() ?? 0,
    );
  }
}

/// Wire shape for `GET /api/v1/expenses` and `GET /api/v1/expenses/{id}`.
class Expense {
  const Expense({
    required this.id,
    required this.portfolioId,
    this.operationalScope = '',
    this.propertyId,
    this.unitId,
    this.vendorId,
    this.workOrderId,
    this.capitalizedAssetId,
    required this.category,
    required this.description,
    required this.status,
    required this.amount,
    this.allocationTotal = 0,
    required this.incurredAt,
    this.dueDate,
    this.paidAt,
    required this.billableToOwner,
    this.notes,
    this.subtotal,
    this.taxAmount,
    this.paymentMethod,
    this.cardLast4,
    this.documentKind,
    this.propertyName,
    this.vendorName,
    this.accountId,
    this.accountName,
    this.journalEntryPublicId,
    required this.hasReceipt,
    required this.receiptIsImage,
    required this.lineItems,
    required this.createdAt,
    required this.updatedAt,
  });

  final int id;
  final int portfolioId;
  final String operationalScope;
  final int? propertyId;
  final int? unitId;
  final int? vendorId;
  final int? workOrderId;
  final int? capitalizedAssetId;
  final ScheduleECategory category;
  final String description;
  final ExpenseStatus status;
  final double amount;
  final double allocationTotal;
  final DateTime incurredAt;
  final DateTime? dueDate;
  final DateTime? paidAt;
  final bool billableToOwner;
  final String? notes;
  final double? subtotal;
  final double? taxAmount;
  final String? paymentMethod;
  final String? cardLast4;
  final String? documentKind;
  final String? propertyName;
  final String? vendorName;
  final int? accountId;
  final String? accountName;
  final String? journalEntryPublicId;
  final bool hasReceipt;
  final bool receiptIsImage;
  final List<ExpenseLineItem> lineItems;
  final DateTime createdAt;
  final DateTime updatedAt;

  factory Expense.fromJson(Map<String, dynamic> json) {
    return Expense(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num?)?.toInt() ?? 0,
      operationalScope: json['operationalScope'] as String? ?? '',
      propertyId: (json['propertyId'] as num?)?.toInt(),
      unitId: (json['unitId'] as num?)?.toInt(),
      vendorId: (json['vendorId'] as num?)?.toInt(),
      workOrderId: (json['workOrderId'] as num?)?.toInt(),
      capitalizedAssetId: (json['capitalizedAssetId'] as num?)?.toInt(),
      category: ScheduleECategory.fromWire(json['category'] as String?),
      description: json['description'] as String? ?? '',
      status: ExpenseStatus.fromWire(json['status'] as String?),
      amount: (json['amount'] as num?)?.toDouble() ?? 0,
      allocationTotal: (json['allocationTotal'] as num?)?.toDouble() ?? 0,
      incurredAt:
          DateTime.tryParse(json['incurredAt'] as String? ?? '') ?? DateTime(0),
      dueDate: DateTime.tryParse(json['dueDate'] as String? ?? ''),
      paidAt: DateTime.tryParse(json['paidAt'] as String? ?? ''),
      billableToOwner: json['billableToOwner'] as bool? ?? false,
      notes: json['notes'] as String?,
      subtotal: (json['subtotal'] as num?)?.toDouble(),
      taxAmount: (json['taxAmount'] as num?)?.toDouble(),
      paymentMethod: json['paymentMethod'] as String?,
      cardLast4: json['cardLast4'] as String?,
      documentKind: json['documentKind'] as String?,
      propertyName: json['propertyName'] as String?,
      vendorName: json['vendorName'] as String?,
      accountId: (json['accountId'] as num?)?.toInt(),
      accountName: json['accountName'] as String?,
      journalEntryPublicId: json['journalEntryPublicId'] as String?,
      hasReceipt: json['hasReceipt'] as bool? ?? false,
      receiptIsImage: json['receiptIsImage'] as bool? ?? false,
      lineItems: ((json['lineItems'] as List<dynamic>?) ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(ExpenseLineItem.fromJson)
          .toList(),
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt:
          DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
    );
  }
}
