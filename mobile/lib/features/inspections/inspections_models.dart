// Models for the inspections smart-checklist workflow.
//
// Wire shapes are camelCase; enums arrive as string names (Type, Status,
// Result). The base Inspection list model lives in core/models; these add
// the detail (items), template, and complete-summary shapes used on-site.

/// Item result values (string enum on the wire).
class InspectionItemResults {
  static const pending = 'Pending';
  static const pass = 'Pass';
  static const fail = 'Fail';
  static const notApplicable = 'NotApplicable';

  static const all = [pending, pass, fail, notApplicable];
}

/// Inspection type values (string enum on the wire).
class InspectionTypes {
  static const moveIn = 'MoveIn';
  static const moveOut = 'MoveOut';
  static const routine = 'Routine';
  static const annualSafety = 'AnnualSafety';

  static const all = [routine, moveIn, moveOut, annualSafety];
}

/// Friendly label for an inspection type string.
String friendlyInspectionType(String type) {
  switch (type) {
    case InspectionTypes.moveIn:
      return 'Move-in';
    case InspectionTypes.moveOut:
      return 'Move-out';
    case InspectionTypes.routine:
      return 'Routine';
    case InspectionTypes.annualSafety:
      return 'Annual safety';
    default:
      return type;
  }
}

/// Friendly label for an inspection status string.
String friendlyInspectionStatus(String status) {
  switch (status) {
    case 'NeedsFollowUp':
      return 'Needs follow-up';
    case 'InProgress':
      return 'In progress';
    default:
      return status;
  }
}

/// One checklist item on an inspection detail.
class InspectionItem {
  final int id;
  final int inspectionId;
  final String area;
  final String label;
  final String result;
  final String? note;
  final int? photoStoredFileId;
  final int? spawnedWorkOrderId;
  final int sortOrder;

  const InspectionItem({
    required this.id,
    required this.inspectionId,
    required this.area,
    required this.label,
    required this.result,
    this.note,
    this.photoStoredFileId,
    this.spawnedWorkOrderId,
    required this.sortOrder,
  });

  factory InspectionItem.fromJson(Map<String, dynamic> json) {
    return InspectionItem(
      id: (json['id'] as num).toInt(),
      inspectionId: (json['inspectionId'] as num?)?.toInt() ?? 0,
      area: json['area'] as String? ?? '',
      label: json['label'] as String? ?? '',
      result: json['result'] as String? ?? InspectionItemResults.pending,
      note: json['note'] as String?,
      photoStoredFileId: (json['photoStoredFileId'] as num?)?.toInt(),
      spawnedWorkOrderId: (json['spawnedWorkOrderId'] as num?)?.toInt(),
      sortOrder: (json['sortOrder'] as num?)?.toInt() ?? 0,
    );
  }

  InspectionItem copyWith({
    String? result,
    String? note,
    int? photoStoredFileId,
    int? spawnedWorkOrderId,
  }) {
    return InspectionItem(
      id: id,
      inspectionId: inspectionId,
      area: area,
      label: label,
      result: result ?? this.result,
      note: note ?? this.note,
      photoStoredFileId: photoStoredFileId ?? this.photoStoredFileId,
      spawnedWorkOrderId: spawnedWorkOrderId ?? this.spawnedWorkOrderId,
      sortOrder: sortOrder,
    );
  }
}

/// Inspection detail: the base inspection plus its checklist items.
class InspectionDetail {
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
  final int? templateId;
  final int? reportStoredFileId;
  final String? inspector;
  final List<InspectionItem> items;
  final DateTime createdAt;
  final DateTime updatedAt;

  const InspectionDetail({
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
    this.templateId,
    this.reportStoredFileId,
    this.inspector,
    required this.items,
    required this.createdAt,
    required this.updatedAt,
  });

  bool get isCompleted => status == 'Completed';

  InspectionDetail copyWith({List<InspectionItem>? items}) {
    return InspectionDetail(
      id: id,
      portfolioId: portfolioId,
      propertyId: propertyId,
      unitId: unitId,
      leaseId: leaseId,
      type: type,
      status: status,
      scheduledFor: scheduledFor,
      completedAt: completedAt,
      outcome: outcome,
      notes: notes,
      templateId: templateId,
      reportStoredFileId: reportStoredFileId,
      inspector: inspector,
      items: items ?? this.items,
      createdAt: createdAt,
      updatedAt: updatedAt,
    );
  }

  factory InspectionDetail.fromJson(Map<String, dynamic> json) {
    final rawItems = (json['items'] as List<dynamic>? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(InspectionItem.fromJson)
        .toList();
    return InspectionDetail(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num?)?.toInt() ?? 0,
      propertyId: (json['propertyId'] as num).toInt(),
      unitId: (json['unitId'] as num?)?.toInt(),
      leaseId: (json['leaseId'] as num?)?.toInt(),
      type: json['type'] as String? ?? '',
      status: json['status'] as String? ?? '',
      scheduledFor:
          DateTime.tryParse(json['scheduledFor'] as String? ?? '') ?? DateTime(0),
      completedAt: DateTime.tryParse(json['completedAt'] as String? ?? ''),
      outcome: json['outcome'] as String?,
      notes: json['notes'] as String?,
      templateId: (json['templateId'] as num?)?.toInt(),
      reportStoredFileId: (json['reportStoredFileId'] as num?)?.toInt(),
      inspector: json['inspector'] as String?,
      items: rawItems,
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt:
          DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
    );
  }
}

/// A single item within a template.
class InspectionTemplateItem {
  final String area;
  final String label;
  final int sortOrder;

  const InspectionTemplateItem({
    required this.area,
    required this.label,
    required this.sortOrder,
  });

  factory InspectionTemplateItem.fromJson(Map<String, dynamic> json) {
    return InspectionTemplateItem(
      area: json['area'] as String? ?? '',
      label: json['label'] as String? ?? '',
      sortOrder: (json['sortOrder'] as num?)?.toInt() ?? 0,
    );
  }
}

/// An available checklist template. Built-in templates have a negative [id],
/// which must be passed back to create unchanged.
class InspectionTemplate {
  final int id;
  final int? portfolioId;
  final String name;
  final String inspectionType;
  final bool isBuiltIn;
  final List<InspectionTemplateItem> items;

  const InspectionTemplate({
    required this.id,
    this.portfolioId,
    required this.name,
    required this.inspectionType,
    required this.isBuiltIn,
    required this.items,
  });

  factory InspectionTemplate.fromJson(Map<String, dynamic> json) {
    final rawItems = (json['items'] as List<dynamic>? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(InspectionTemplateItem.fromJson)
        .toList();
    return InspectionTemplate(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num?)?.toInt(),
      name: json['name'] as String? ?? '',
      inspectionType: json['inspectionType'] as String? ?? '',
      isBuiltIn: json['isBuiltIn'] as bool? ?? false,
      items: rawItems,
    );
  }
}

/// Result of completing an inspection.
class CompleteInspectionResult {
  final int inspectionId;
  final String status;
  final int totalItems;
  final int passCount;
  final int failCount;
  final int notApplicableCount;
  final int pendingCount;
  final int? reportStoredFileId;
  final List<int> createdWorkOrderIds;

  const CompleteInspectionResult({
    required this.inspectionId,
    required this.status,
    required this.totalItems,
    required this.passCount,
    required this.failCount,
    required this.notApplicableCount,
    required this.pendingCount,
    this.reportStoredFileId,
    required this.createdWorkOrderIds,
  });

  factory CompleteInspectionResult.fromJson(Map<String, dynamic> json) {
    return CompleteInspectionResult(
      inspectionId: (json['inspectionId'] as num?)?.toInt() ?? 0,
      status: json['status'] as String? ?? '',
      totalItems: (json['totalItems'] as num?)?.toInt() ?? 0,
      passCount: (json['passCount'] as num?)?.toInt() ?? 0,
      failCount: (json['failCount'] as num?)?.toInt() ?? 0,
      notApplicableCount: (json['notApplicableCount'] as num?)?.toInt() ?? 0,
      pendingCount: (json['pendingCount'] as num?)?.toInt() ?? 0,
      reportStoredFileId: (json['reportStoredFileId'] as num?)?.toInt(),
      createdWorkOrderIds: (json['createdWorkOrderIds'] as List<dynamic>? ??
              const [])
          .whereType<num>()
          .map((n) => n.toInt())
          .toList(),
    );
  }
}
