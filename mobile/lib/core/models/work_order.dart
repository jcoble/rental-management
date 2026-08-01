class WorkOrder {
  final int id;
  final int? portfolioId;
  final int? propertyId;
  final int? unitId;
  final int? tenantId;
  final int? leaseId;
  final int? vendorId;
  final String title;
  final String description;
  final String? technicianAccessInstructions;
  final String category;
  final String priority;
  final String status;
  final DateTime requestedAt;
  final DateTime? scheduledFor;

  /// End of the scheduled arrival window (visit expected between [scheduledFor]
  /// and this time). Null when no window end was set.
  final DateTime? scheduledWindowEnd;

  final DateTime? completedAt;
  final double? estimatedCost;
  final double? actualCost;
  final String? createdBy;
  final DateTime updatedAt;
  final String? propertyName;
  final String? unitNumber;
  final String? tenantName;
  final String? vendorName;
  final String? submittedByLabel;
  final String? requesterName;
  final String? requesterPhone;
  final String? requesterEmail;
  final bool? residentMustBePresent;
  final bool? callBeforeEntry;
  final bool? callIfNotHome;
  final bool? permissionToEnter;
  final String? entryNotes;
  final String? petWarnings;
  final String? accessWarnings;

  const WorkOrder({
    required this.id,
    this.portfolioId,
    this.propertyId,
    this.unitId,
    this.tenantId,
    this.leaseId,
    this.vendorId,
    required this.title,
    required this.description,
    this.technicianAccessInstructions,
    required this.category,
    required this.priority,
    required this.status,
    required this.requestedAt,
    this.scheduledFor,
    this.scheduledWindowEnd,
    this.completedAt,
    this.estimatedCost,
    this.actualCost,
    this.createdBy,
    required this.updatedAt,
    this.propertyName,
    this.unitNumber,
    this.tenantName,
    this.vendorName,
    this.submittedByLabel,
    this.requesterName,
    this.requesterPhone,
    this.requesterEmail,
    this.residentMustBePresent,
    this.callBeforeEntry,
    this.callIfNotHome,
    this.permissionToEnter,
    this.entryNotes,
    this.petWarnings,
    this.accessWarnings,
  });

  factory WorkOrder.fromJson(Map<String, dynamic> json) {
    return WorkOrder(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num?)?.toInt(),
      propertyId: (json['propertyId'] as num?)?.toInt(),
      unitId: (json['unitId'] as num?)?.toInt(),
      tenantId: (json['tenantId'] as num?)?.toInt(),
      leaseId: (json['leaseId'] as num?)?.toInt(),
      vendorId: (json['vendorId'] as num?)?.toInt(),
      title: json['title'] as String? ?? '',
      description: json['description'] as String? ?? '',
      technicianAccessInstructions:
          json['technicianAccessInstructions'] as String?,
      category: json['category'] as String? ?? '',
      priority: json['priority'] as String? ?? '',
      status: json['status'] as String? ?? '',
      requestedAt:
          DateTime.tryParse(json['requestedAt'] as String? ?? '') ??
          DateTime(0),
      scheduledFor: DateTime.tryParse(json['scheduledFor'] as String? ?? ''),
      scheduledWindowEnd: DateTime.tryParse(
        json['scheduledWindowEnd'] as String? ?? '',
      ),
      completedAt: DateTime.tryParse(json['completedAt'] as String? ?? ''),
      estimatedCost: (json['estimatedCost'] as num?)?.toDouble(),
      actualCost: (json['actualCost'] as num?)?.toDouble(),
      createdBy: json['createdBy'] as String?,
      updatedAt:
          DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
      propertyName: json['propertyName'] as String?,
      unitNumber: json['unitNumber'] as String?,
      tenantName: json['tenantName'] as String?,
      vendorName: json['vendorName'] as String?,
      submittedByLabel: json['submittedByLabel'] as String?,
      requesterName: json['requesterName'] as String?,
      requesterPhone: json['requesterPhone'] as String?,
      requesterEmail: json['requesterEmail'] as String?,
      residentMustBePresent: json['residentMustBePresent'] as bool?,
      callBeforeEntry: json['callBeforeEntry'] as bool?,
      callIfNotHome: json['callIfNotHome'] as bool?,
      permissionToEnter: json['permissionToEnter'] as bool?,
      entryNotes: json['entryNotes'] as String?,
      petWarnings: json['petWarnings'] as String?,
      accessWarnings: json['accessWarnings'] as String?,
    );
  }

  Map<String, dynamic> toJson() {
    return {
      if (portfolioId != null) 'portfolioId': portfolioId,
      if (propertyId != null) 'propertyId': propertyId,
      if (unitId != null) 'unitId': unitId,
      if (tenantId != null) 'tenantId': tenantId,
      if (leaseId != null) 'leaseId': leaseId,
      if (vendorId != null) 'vendorId': vendorId,
      'title': title,
      'description': description,
      if (technicianAccessInstructions != null)
        'technicianAccessInstructions': technicianAccessInstructions,
      'category': category,
      'priority': priority,
      'status': status,
      'requestedAt': requestedAt.toIso8601String(),
      if (scheduledFor != null) 'scheduledFor': scheduledFor!.toIso8601String(),
      if (scheduledWindowEnd != null)
        'scheduledWindowEnd': scheduledWindowEnd!.toIso8601String(),
      if (completedAt != null) 'completedAt': completedAt!.toIso8601String(),
      if (estimatedCost != null) 'estimatedCost': estimatedCost,
      if (actualCost != null) 'actualCost': actualCost,
      if (submittedByLabel != null) 'submittedByLabel': submittedByLabel,
      if (requesterName != null) 'requesterName': requesterName,
      if (requesterPhone != null) 'requesterPhone': requesterPhone,
      if (requesterEmail != null) 'requesterEmail': requesterEmail,
      if (residentMustBePresent != null)
        'residentMustBePresent': residentMustBePresent,
      if (callBeforeEntry != null) 'callBeforeEntry': callBeforeEntry,
      if (callIfNotHome != null) 'callIfNotHome': callIfNotHome,
      if (permissionToEnter != null) 'permissionToEnter': permissionToEnter,
      if (entryNotes != null) 'entryNotes': entryNotes,
      if (petWarnings != null) 'petWarnings': petWarnings,
      if (accessWarnings != null) 'accessWarnings': accessWarnings,
    };
  }
}

/// One entry in a work order's live status timeline (oldest → newest).
///
/// Wire shape (camelCase, enums as strings):
///   { id, fromStatus, toStatus, note, changedByLabel, createdAtUtc }
/// [fromStatus] is null for the initial create event.
class WorkOrderStatusEvent {
  final int id;
  final String? fromStatus;
  final String toStatus;
  final String? note;
  final String? changedByLabel;
  final DateTime createdAtUtc;

  const WorkOrderStatusEvent({
    required this.id,
    this.fromStatus,
    required this.toStatus,
    this.note,
    this.changedByLabel,
    required this.createdAtUtc,
  });

  factory WorkOrderStatusEvent.fromJson(Map<String, dynamic> json) {
    return WorkOrderStatusEvent(
      id: (json['id'] as num).toInt(),
      fromStatus: json['fromStatus'] as String?,
      toStatus: json['toStatus'] as String? ?? '',
      note: json['note'] as String?,
      changedByLabel: json['changedByLabel'] as String?,
      createdAtUtc:
          DateTime.tryParse(json['createdAtUtc'] as String? ?? '')?.toLocal() ??
          DateTime(0),
    );
  }
}

/// A work order plus its status [timeline], returned by the single-work-order
/// GET (`/work-orders/{id}` and `/portal/work-orders/{id}`). The list endpoints
/// stay lightweight and omit the timeline.
class WorkOrderDetail {
  final WorkOrder workOrder;
  final List<WorkOrderStatusEvent> timeline;

  const WorkOrderDetail({required this.workOrder, required this.timeline});

  factory WorkOrderDetail.fromJson(Map<String, dynamic> json) {
    final events = (json['timeline'] as List<dynamic>?) ?? const [];
    return WorkOrderDetail(
      workOrder: WorkOrder.fromJson(json),
      timeline: events
          .whereType<Map<String, dynamic>>()
          .map(WorkOrderStatusEvent.fromJson)
          .toList(),
    );
  }
}
