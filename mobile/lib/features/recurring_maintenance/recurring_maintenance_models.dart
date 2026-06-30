/// Recurring maintenance task — landlord-facing scheduling model.
///
/// Mirrors the API `RecurringMaintenanceTaskResponse` (camelCase, enums as
/// strings). A task like "HVAC filter every 90 days" auto-creates a work order
/// each time it comes due; `nextDueDate` / `lastGeneratedAtUtc` track that
/// generation cycle on the server.
class RecurringMaintenanceTask {
  const RecurringMaintenanceTask({
    required this.id,
    required this.propertyId,
    this.unitId,
    this.vendorId,
    this.propertyName,
    this.unitNumber,
    this.vendorName,
    required this.title,
    this.description,
    this.category,
    required this.recurrenceInterval,
    required this.nextDueDate,
    this.scheduledTime,
    this.estimatedCost,
    this.monthlyEstimatedCost,
    this.generatedWorkOrderCount = 0,
    this.lastGeneratedWorkOrderId,
    this.lastGeneratedAtUtc,
    required this.isActive,
    required this.priority,
    this.createdAt,
    this.updatedAt,
    this.testId,
  });

  final int id;
  final int propertyId;
  final int? unitId;
  final int? vendorId;
  final String? propertyName;
  final String? unitNumber;
  final String? vendorName;
  final String title;
  final String? description;
  final String? category;
  final String recurrenceInterval;
  final DateTime nextDueDate;
  final String? scheduledTime;
  final double? estimatedCost;
  final double? monthlyEstimatedCost;
  final int generatedWorkOrderCount;
  final int? lastGeneratedWorkOrderId;
  final DateTime? lastGeneratedAtUtc;
  final bool isActive;
  final String priority;
  final DateTime? createdAt;
  final DateTime? updatedAt;
  final String? testId;

  factory RecurringMaintenanceTask.fromJson(Map<String, dynamic> json) {
    DateTime? parseDate(String key) {
      final raw = json[key];
      if (raw is String && raw.isNotEmpty) return DateTime.tryParse(raw);
      return null;
    }

    String? asString(String key) {
      final raw = json[key];
      if (raw is String && raw.isNotEmpty) return raw;
      return null;
    }

    return RecurringMaintenanceTask(
      id: (json['id'] as num).toInt(),
      propertyId: (json['propertyId'] as num?)?.toInt() ?? 0,
      unitId: (json['unitId'] as num?)?.toInt(),
      vendorId: (json['vendorId'] as num?)?.toInt(),
      propertyName: asString('propertyName'),
      unitNumber: asString('unitNumber'),
      vendorName: asString('vendorName'),
      title: json['title'] as String? ?? '',
      description: asString('description'),
      category: asString('category'),
      recurrenceInterval: json['recurrenceInterval'] as String? ?? 'Monthly',
      nextDueDate: parseDate('nextDueDate') ?? DateTime.now(),
      scheduledTime: asString('scheduledTime'),
      estimatedCost: (json['estimatedCost'] as num?)?.toDouble(),
      monthlyEstimatedCost: (json['monthlyEstimatedCost'] as num?)?.toDouble(),
      generatedWorkOrderCount:
          (json['generatedWorkOrderCount'] as num?)?.toInt() ?? 0,
      lastGeneratedWorkOrderId: (json['lastGeneratedWorkOrderId'] as num?)
          ?.toInt(),
      lastGeneratedAtUtc: parseDate('lastGeneratedAtUtc'),
      isActive: json['isActive'] as bool? ?? true,
      priority: json['priority'] as String? ?? 'Normal',
      createdAt: parseDate('createdAt'),
      updatedAt: parseDate('updatedAt'),
      testId: asString('testId'),
    );
  }

  RecurringMaintenanceTask copyWith({bool? isActive}) {
    return RecurringMaintenanceTask(
      id: id,
      propertyId: propertyId,
      unitId: unitId,
      vendorId: vendorId,
      propertyName: propertyName,
      unitNumber: unitNumber,
      vendorName: vendorName,
      title: title,
      description: description,
      category: category,
      recurrenceInterval: recurrenceInterval,
      nextDueDate: nextDueDate,
      scheduledTime: scheduledTime,
      estimatedCost: estimatedCost,
      monthlyEstimatedCost: monthlyEstimatedCost,
      generatedWorkOrderCount: generatedWorkOrderCount,
      lastGeneratedWorkOrderId: lastGeneratedWorkOrderId,
      lastGeneratedAtUtc: lastGeneratedAtUtc,
      isActive: isActive ?? this.isActive,
      priority: priority,
      createdAt: createdAt,
      updatedAt: updatedAt,
      testId: testId,
    );
  }
}

// ── Enum metadata ─────────────────────────────────────────────────────────────

/// Recurrence intervals, in ascending order (matches the API enum).
const recurrenceIntervals = <String>[
  'Weekly',
  'Monthly',
  'Quarterly',
  'SemiAnnually',
  'Annually',
];

/// Priorities, low → high (matches the API enum).
const recurringPriorities = <String>['Low', 'Normal', 'High', 'Emergency'];

/// Common maintenance categories — same set used by the work-order form.
const recurringCategories = <String>[
  'General',
  'Plumbing',
  'Electrical',
  'HVAC',
  'Appliance',
  'Structural',
  'Exterior',
  'Landscaping',
  'Cleaning',
  'Safety',
  'Other',
];

/// Human-friendly label for a recurrence interval, e.g. "every quarter".
String friendlyRecurrence(String interval) {
  switch (interval) {
    case 'Weekly':
      return 'every week';
    case 'Monthly':
      return 'every month';
    case 'Quarterly':
      return 'every quarter';
    case 'SemiAnnually':
      return 'every 6 months';
    case 'Annually':
      return 'every year';
    default:
      return interval;
  }
}

/// Short label for a recurrence interval, e.g. "Quarterly".
String recurrenceLabel(String interval) {
  switch (interval) {
    case 'SemiAnnually':
      return 'Semi-Annually';
    default:
      return interval;
  }
}
