class Expense {
  final int id;
  final int portfolioId;
  final int? propertyId;
  final int? vendorId;
  final int? workOrderId;
  final int? capitalizedAssetId;
  final String category;
  final String description;
  final String status;
  final double amount;
  final double? subtotal;
  final double? taxAmount;
  final DateTime incurredAt;
  final DateTime? dueDate;
  final DateTime? paidAt;
  final bool billableToOwner;
  final String? notes;
  final String? receiptData;
  final String? propertyName;
  final String? vendorName;
  final String? workOrderTitle;
  final DateTime createdAt;
  final DateTime updatedAt;
  final bool? hasReceipt;
  final bool? receiptIsImage;

  const Expense({
    required this.id,
    required this.portfolioId,
    this.propertyId,
    this.vendorId,
    this.workOrderId,
    this.capitalizedAssetId,
    required this.category,
    required this.description,
    required this.status,
    required this.amount,
    this.subtotal,
    this.taxAmount,
    required this.incurredAt,
    this.dueDate,
    this.paidAt,
    required this.billableToOwner,
    this.notes,
    this.receiptData,
    this.propertyName,
    this.vendorName,
    this.workOrderTitle,
    required this.createdAt,
    required this.updatedAt,
    this.hasReceipt,
    this.receiptIsImage,
  });

  factory Expense.fromJson(Map<String, dynamic> json) {
    return Expense(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num).toInt(),
      propertyId: (json['propertyId'] as num?)?.toInt(),
      vendorId: (json['vendorId'] as num?)?.toInt(),
      workOrderId: (json['workOrderId'] as num?)?.toInt(),
      capitalizedAssetId: (json['capitalizedAssetId'] as num?)?.toInt(),
      category: json['category'] as String? ?? '',
      description: json['description'] as String? ?? '',
      status: json['status'] as String? ?? '',
      amount: (json['amount'] as num?)?.toDouble() ?? 0,
      subtotal: (json['subtotal'] as num?)?.toDouble(),
      taxAmount: (json['taxAmount'] as num?)?.toDouble(),
      incurredAt:
          DateTime.tryParse(json['incurredAt'] as String? ?? '') ?? DateTime(0),
      dueDate: DateTime.tryParse(json['dueDate'] as String? ?? ''),
      paidAt: DateTime.tryParse(json['paidAt'] as String? ?? ''),
      billableToOwner: json['billableToOwner'] as bool? ?? false,
      notes: json['notes'] as String?,
      receiptData: json['receiptData'] as String?,
      propertyName: json['propertyName'] as String?,
      vendorName: json['vendorName'] as String?,
      workOrderTitle: json['workOrderTitle'] as String?,
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt:
          DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
      hasReceipt: json['hasReceipt'] as bool?,
      receiptIsImage: json['receiptIsImage'] as bool?,
    );
  }

  Map<String, dynamic> toJson() {
    return {
      'portfolioId': portfolioId,
      if (propertyId != null) 'propertyId': propertyId,
      if (vendorId != null) 'vendorId': vendorId,
      if (workOrderId != null) 'workOrderId': workOrderId,
      if (capitalizedAssetId != null) 'capitalizedAssetId': capitalizedAssetId,
      'category': category,
      'description': description,
      'status': status,
      'amount': amount,
      if (subtotal != null) 'subtotal': subtotal,
      if (taxAmount != null) 'taxAmount': taxAmount,
      'incurredAt': incurredAt.toIso8601String(),
      if (dueDate != null)
        'dueDate': dueDate!.toIso8601String().split('T').first,
      if (paidAt != null) 'paidAt': paidAt!.toIso8601String(),
      'billableToOwner': billableToOwner,
      if (notes != null) 'notes': notes,
      if (receiptData != null) 'receiptData': receiptData,
    };
  }
}
