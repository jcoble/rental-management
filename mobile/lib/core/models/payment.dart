/// One immutable receipt posted to a continuous tenant account.
///
/// Charges and their open balances are separate ledger projections. A receipt
/// is never edited, marked paid, or deleted after it is posted.
class PaymentReceipt {
  const PaymentReceipt({
    required this.id,
    required this.publicId,
    required this.portfolioId,
    required this.tenantAccountId,
    required this.leaseManagementId,
    required this.propertyId,
    required this.unitId,
    required this.accountNumber,
    required this.relationshipNumber,
    this.tenantName,
    this.propertyName,
    this.unitNumber,
    required this.amount,
    required this.currency,
    required this.receivedOn,
    required this.postedAtUtc,
    required this.description,
    this.provider,
    this.providerReference,
    this.providerState,
    this.paymentMethodSummary,
    this.payerName,
    this.checkNumber,
    this.bankName,
    this.sourceStoredFileId,
  });

  final int id;
  final String publicId;
  final int portfolioId;
  final int tenantAccountId;
  final int leaseManagementId;
  final int propertyId;
  final int unitId;
  final String accountNumber;
  final String relationshipNumber;
  final String? tenantName;
  final String? propertyName;
  final String? unitNumber;
  final double amount;
  final String currency;
  final DateTime receivedOn;
  final DateTime postedAtUtc;
  final String description;
  final String? provider;
  final String? providerReference;
  final String? providerState;
  final String? paymentMethodSummary;
  final String? payerName;
  final String? checkNumber;
  final String? bankName;
  final int? sourceStoredFileId;

  factory PaymentReceipt.fromJson(Map<String, dynamic> json) {
    return PaymentReceipt(
      id: (json['id'] as num).toInt(),
      publicId: json['publicId'] as String? ?? '',
      portfolioId: (json['portfolioId'] as num).toInt(),
      tenantAccountId: (json['tenantAccountId'] as num).toInt(),
      leaseManagementId: (json['leaseManagementId'] as num).toInt(),
      propertyId: (json['propertyId'] as num).toInt(),
      unitId: (json['unitId'] as num).toInt(),
      accountNumber: json['accountNumber'] as String? ?? '',
      relationshipNumber: json['relationshipNumber'] as String? ?? '',
      tenantName: json['tenantName'] as String?,
      propertyName: json['propertyName'] as String?,
      unitNumber: json['unitNumber'] as String?,
      amount: (json['amount'] as num).toDouble(),
      currency: json['currency'] as String? ?? 'USD',
      receivedOn:
          DateTime.tryParse(json['receivedOn'] as String? ?? '') ?? DateTime(0),
      postedAtUtc:
          DateTime.tryParse(json['postedAtUtc'] as String? ?? '') ??
          DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
      description: json['description'] as String? ?? '',
      provider: json['provider'] as String?,
      providerReference: json['providerReference'] as String?,
      providerState: json['providerState'] as String?,
      paymentMethodSummary: json['paymentMethodSummary'] as String?,
      payerName: json['payerName'] as String?,
      checkNumber: json['checkNumber'] as String?,
      bankName: json['bankName'] as String?,
      sourceStoredFileId: (json['sourceStoredFileId'] as num?)?.toInt(),
    );
  }
}
