class Vendor {
  final int id;
  final int portfolioId;
  final String name;
  final String serviceType;
  final String? email;
  final String? phone;
  final String? taxId;
  final bool is1099Eligible;
  final bool w9OnFile;
  final bool preferred;
  final String? notes;
  final DateTime createdAt;
  final DateTime updatedAt;

  const Vendor({
    required this.id,
    required this.portfolioId,
    required this.name,
    required this.serviceType,
    this.email,
    this.phone,
    this.taxId,
    required this.is1099Eligible,
    required this.w9OnFile,
    required this.preferred,
    this.notes,
    required this.createdAt,
    required this.updatedAt,
  });

  factory Vendor.fromJson(Map<String, dynamic> json) {
    return Vendor(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num).toInt(),
      name: json['name'] as String? ?? '',
      serviceType: json['serviceType'] as String? ?? '',
      email: json['email'] as String?,
      phone: json['phone'] as String?,
      taxId: json['taxId'] as String?,
      is1099Eligible: json['is1099Eligible'] as bool? ?? false,
      w9OnFile: json['w9OnFile'] as bool? ?? false,
      preferred: json['preferred'] as bool? ?? false,
      notes: json['notes'] as String?,
      createdAt: DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt: DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
    );
  }
}
