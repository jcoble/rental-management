class Payment {
  final int id;
  final int portfolioId;
  final int leaseId;
  final String type;
  final String status;
  final double amount;
  final DateTime dueDate;
  final DateTime? paidDate;
  final String? method;
  final String? externalReference;
  final String? notes;
  final String? tenantName;
  final String? leaseNumber;
  final DateTime createdAt;
  final DateTime updatedAt;

  const Payment({
    required this.id,
    required this.portfolioId,
    required this.leaseId,
    required this.type,
    required this.status,
    required this.amount,
    required this.dueDate,
    this.paidDate,
    this.method,
    this.externalReference,
    this.notes,
    this.tenantName,
    this.leaseNumber,
    required this.createdAt,
    required this.updatedAt,
  });

  factory Payment.fromJson(Map<String, dynamic> json) {
    return Payment(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num).toInt(),
      leaseId: (json['leaseId'] as num).toInt(),
      type: json['paymentType'] as String? ?? json['type'] as String? ?? '',
      status: json['status'] as String? ?? '',
      amount: (json['amount'] as num?)?.toDouble() ?? 0,
      dueDate: DateTime.tryParse(json['dueDate'] as String? ?? '') ?? DateTime(0),
      paidDate: DateTime.tryParse(json['paidDate'] as String? ?? ''),
      method: json['method'] as String?,
      externalReference: json['externalReference'] as String?,
      notes: json['notes'] as String?,
      tenantName: json['tenantName'] as String?,
      leaseNumber: json['leaseNumber'] as String?,
      createdAt: DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt: DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
    );
  }

  Map<String, dynamic> toJson() {
    return {
      'portfolioId': portfolioId,
      'leaseId': leaseId,
      'type': type,
      'status': status,
      'amount': amount,
      'dueDate': dueDate.toIso8601String().split('T').first,
      if (paidDate != null) 'paidDate': paidDate!.toIso8601String().split('T').first,
      if (method != null) 'method': method,
      if (externalReference != null) 'externalReference': externalReference,
      if (notes != null) 'notes': notes,
    };
  }
}
