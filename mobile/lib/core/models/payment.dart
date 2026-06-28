/// Friendly, landlord-facing label for a payment-type enum value. The schema
/// enums (`SecurityDeposit`, `LateFee`, …) must never be shown to the landlord
/// with their raw PascalCase casing — map them to plain English for display only,
/// keeping the enum as the value that's submitted/stored. Unknown values fall
/// back to the raw string so nothing renders blank.
String paymentTypeLabel(String type) {
  switch (type) {
    case 'Rent':
      return 'Rent';
    case 'SecurityDeposit':
      return 'Security deposit';
    case 'LateFee':
      return 'Late fee';
    case 'Utility':
      return 'Utility';
    case 'Other':
      return 'Other';
    default:
      return type.isEmpty ? '' : type;
  }
}

class Payment {
  final int id;
  final int? portfolioId;
  final int leaseId;
  final String type;
  final String status;
  final double amount;

  /// Cash collected so far on a `Partial` payment (strictly between 0 and
  /// [amount]); null/absent for every other status. Mirrors the web client's
  /// `amountPaid` and the server's `Payment.AmountPaid`.
  final double? amountPaid;
  final DateTime dueDate;
  final DateTime? paidDate;
  final String? method;
  final String? externalReference;
  final String? notes;
  final String? tenantName;
  final String? leaseNumber;
  final String? propertyName;
  final String? unitNumber;
  final bool hasScan;
  final bool scanIsImage;
  final DateTime createdAt;
  final DateTime updatedAt;

  const Payment({
    required this.id,
    this.portfolioId,
    required this.leaseId,
    required this.type,
    required this.status,
    required this.amount,
    this.amountPaid,
    required this.dueDate,
    this.paidDate,
    this.method,
    this.externalReference,
    this.notes,
    this.tenantName,
    this.leaseNumber,
    this.propertyName,
    this.unitNumber,
    this.hasScan = false,
    this.scanIsImage = false,
    required this.createdAt,
    required this.updatedAt,
  });

  factory Payment.fromJson(Map<String, dynamic> json) {
    return Payment(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num?)?.toInt(),
      leaseId: (json['leaseId'] as num).toInt(),
      type: json['paymentType'] as String? ?? json['type'] as String? ?? '',
      status: json['status'] as String? ?? '',
      amount: (json['amount'] as num?)?.toDouble() ?? 0,
      amountPaid: (json['amountPaid'] as num?)?.toDouble(),
      dueDate:
          DateTime.tryParse(json['dueDate'] as String? ?? '') ?? DateTime(0),
      paidDate: DateTime.tryParse(json['paidDate'] as String? ?? ''),
      method: json['method'] as String?,
      externalReference: json['externalReference'] as String?,
      notes: json['notes'] as String?,
      tenantName: json['tenantName'] as String?,
      leaseNumber: json['leaseNumber'] as String?,
      propertyName: json['propertyName'] as String?,
      unitNumber: json['unitNumber'] as String?,
      hasScan: json['hasScan'] as bool? ?? false,
      scanIsImage: json['scanIsImage'] as bool? ?? false,
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt:
          DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
    );
  }

  Map<String, dynamic> toJson() {
    return {
      if (portfolioId != null) 'portfolioId': portfolioId,
      'leaseId': leaseId,
      'type': type,
      'status': status,
      'amount': amount,
      if (amountPaid != null) 'amountPaid': amountPaid,
      'dueDate': dueDate.toIso8601String().split('T').first,
      if (paidDate != null)
        'paidDate': paidDate!.toIso8601String().split('T').first,
      if (method != null) 'method': method,
      if (externalReference != null) 'externalReference': externalReference,
      if (notes != null) 'notes': notes,
    };
  }
}
