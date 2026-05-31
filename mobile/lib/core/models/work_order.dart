class WorkOrder {
  final int id;
  final int portfolioId;
  final int propertyId;
  final int? unitId;
  final int? tenantId;
  final int? leaseId;
  final int? vendorId;
  final String title;
  final String description;
  final String category;
  final String priority;
  final String status;
  final DateTime requestedAt;
  final DateTime? scheduledFor;
  final DateTime? completedAt;
  final double? estimatedCost;
  final double? actualCost;
  final String? createdBy;
  final DateTime updatedAt;
  final String? propertyName;
  final String? unitNumber;
  final String? tenantName;
  final String? vendorName;

  const WorkOrder({
    required this.id,
    required this.portfolioId,
    required this.propertyId,
    this.unitId,
    this.tenantId,
    this.leaseId,
    this.vendorId,
    required this.title,
    required this.description,
    required this.category,
    required this.priority,
    required this.status,
    required this.requestedAt,
    this.scheduledFor,
    this.completedAt,
    this.estimatedCost,
    this.actualCost,
    this.createdBy,
    required this.updatedAt,
    this.propertyName,
    this.unitNumber,
    this.tenantName,
    this.vendorName,
  });

  factory WorkOrder.fromJson(Map<String, dynamic> json) {
    return WorkOrder(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num).toInt(),
      propertyId: (json['propertyId'] as num).toInt(),
      unitId: (json['unitId'] as num?)?.toInt(),
      tenantId: (json['tenantId'] as num?)?.toInt(),
      leaseId: (json['leaseId'] as num?)?.toInt(),
      vendorId: (json['vendorId'] as num?)?.toInt(),
      title: json['title'] as String? ?? '',
      description: json['description'] as String? ?? '',
      category: json['category'] as String? ?? '',
      priority: json['priority'] as String? ?? '',
      status: json['status'] as String? ?? '',
      requestedAt: DateTime.tryParse(json['requestedAt'] as String? ?? '') ?? DateTime(0),
      scheduledFor: DateTime.tryParse(json['scheduledFor'] as String? ?? ''),
      completedAt: DateTime.tryParse(json['completedAt'] as String? ?? ''),
      estimatedCost: (json['estimatedCost'] as num?)?.toDouble(),
      actualCost: (json['actualCost'] as num?)?.toDouble(),
      createdBy: json['createdBy'] as String?,
      updatedAt: DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
      propertyName: json['propertyName'] as String?,
      unitNumber: json['unitNumber'] as String?,
      tenantName: json['tenantName'] as String?,
      vendorName: json['vendorName'] as String?,
    );
  }

  Map<String, dynamic> toJson() {
    return {
      'portfolioId': portfolioId,
      'propertyId': propertyId,
      if (unitId != null) 'unitId': unitId,
      if (tenantId != null) 'tenantId': tenantId,
      if (leaseId != null) 'leaseId': leaseId,
      if (vendorId != null) 'vendorId': vendorId,
      'title': title,
      'description': description,
      'category': category,
      'priority': priority,
      'status': status,
      'requestedAt': requestedAt.toIso8601String(),
      if (scheduledFor != null) 'scheduledFor': scheduledFor!.toIso8601String(),
      if (completedAt != null) 'completedAt': completedAt!.toIso8601String(),
      if (estimatedCost != null) 'estimatedCost': estimatedCost,
      if (actualCost != null) 'actualCost': actualCost,
    };
  }
}
