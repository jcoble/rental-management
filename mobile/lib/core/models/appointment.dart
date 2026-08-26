class Appointment {
  final int id;
  final int portfolioId;
  final int? propertyId;
  final int? unitId;
  final int? leaseId;
  final int? tenantId;
  final String title;
  final String? prospectName;
  final String? prospectEmail;
  final String type;
  final String status;
  final DateTime scheduledStart;
  final DateTime? scheduledEnd;
  final String? assignedTo;
  final String? notes;
  final String? propertyName;
  final String? unitNumber;
  final String? tenantName;
  final DateTime createdAt;
  final DateTime updatedAt;

  const Appointment({
    required this.id,
    required this.portfolioId,
    this.propertyId,
    this.unitId,
    this.leaseId,
    this.tenantId,
    required this.title,
    this.prospectName,
    this.prospectEmail,
    required this.type,
    required this.status,
    required this.scheduledStart,
    this.scheduledEnd,
    this.assignedTo,
    this.notes,
    this.propertyName,
    this.unitNumber,
    this.tenantName,
    required this.createdAt,
    required this.updatedAt,
  });

  factory Appointment.fromJson(Map<String, dynamic> json) {
    return Appointment(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num).toInt(),
      propertyId: (json['propertyId'] as num?)?.toInt(),
      unitId: (json['unitId'] as num?)?.toInt(),
      leaseId: (json['leaseId'] as num?)?.toInt(),
      tenantId: (json['tenantId'] as num?)?.toInt(),
      title: json['title'] as String? ?? '',
      prospectName: json['prospectName'] as String?,
      prospectEmail: json['prospectEmail'] as String?,
      type: json['type'] as String? ?? '',
      status: json['status'] as String? ?? '',
      scheduledStart:
          (DateTime.tryParse(json['scheduledStart'] as String? ?? '') ??
                  DateTime(0))
              .toLocal(),
      scheduledEnd: DateTime.tryParse(
        json['scheduledEnd'] as String? ?? '',
      )?.toLocal(),
      assignedTo: json['assignedTo'] as String?,
      notes: json['notes'] as String?,
      propertyName: json['propertyName'] as String?,
      unitNumber: json['unitNumber'] as String?,
      tenantName: json['tenantName'] as String?,
      createdAt: DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt: DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
    );
  }

  Map<String, dynamic> toJson() {
    return {
      'portfolioId': portfolioId,
      if (propertyId != null) 'propertyId': propertyId,
      if (unitId != null) 'unitId': unitId,
      if (leaseId != null) 'leaseId': leaseId,
      if (tenantId != null) 'tenantId': tenantId,
      'title': title,
      if (prospectName != null) 'prospectName': prospectName,
      if (prospectEmail != null) 'prospectEmail': prospectEmail,
      'type': type,
      'status': status,
      'scheduledStart': scheduledStart.toIso8601String(),
      if (scheduledEnd != null) 'scheduledEnd': scheduledEnd!.toIso8601String(),
      if (assignedTo != null) 'assignedTo': assignedTo,
      if (notes != null) 'notes': notes,
    };
  }
}
