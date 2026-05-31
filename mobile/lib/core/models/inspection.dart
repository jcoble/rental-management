class Inspection {
  final int id;
  final int portfolioId;
  final int propertyId;
  final int? unitId;
  final int? leaseId;
  final String type;
  final String status;
  final DateTime scheduledFor;
  final DateTime? completedAt;
  final String? outcome;
  final String? notes;
  final String? propertyName;
  final String? unitNumber;
  final DateTime createdAt;
  final DateTime updatedAt;

  const Inspection({
    required this.id,
    required this.portfolioId,
    required this.propertyId,
    this.unitId,
    this.leaseId,
    required this.type,
    required this.status,
    required this.scheduledFor,
    this.completedAt,
    this.outcome,
    this.notes,
    this.propertyName,
    this.unitNumber,
    required this.createdAt,
    required this.updatedAt,
  });

  factory Inspection.fromJson(Map<String, dynamic> json) {
    return Inspection(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num).toInt(),
      propertyId: (json['propertyId'] as num).toInt(),
      unitId: (json['unitId'] as num?)?.toInt(),
      leaseId: (json['leaseId'] as num?)?.toInt(),
      type: json['type'] as String? ?? '',
      status: json['status'] as String? ?? '',
      scheduledFor: DateTime.tryParse(json['scheduledFor'] as String? ?? '') ?? DateTime(0),
      completedAt: DateTime.tryParse(json['completedAt'] as String? ?? ''),
      outcome: json['outcome'] as String?,
      notes: json['notes'] as String?,
      propertyName: json['propertyName'] as String?,
      unitNumber: json['unitNumber'] as String?,
      createdAt: DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt: DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
    );
  }
}
