class ActivityHistoryScope {
  const ActivityHistoryScope({
    required this.entityType,
    required this.entityId,
  });

  final String entityType;
  final int entityId;

  @override
  bool operator ==(Object other) {
    return other is ActivityHistoryScope &&
        other.entityType == entityType &&
        other.entityId == entityId;
  }

  @override
  int get hashCode => Object.hash(entityType, entityId);
}

class ActivityChange {
  const ActivityChange({
    required this.field,
    required this.oldValue,
    required this.newValue,
  });

  final String field;
  final String oldValue;
  final String newValue;

  factory ActivityChange.fromJson(Map<String, dynamic> json) {
    return ActivityChange(
      field: json['field'] as String? ?? '',
      oldValue: json['oldValue'] as String? ?? '',
      newValue: json['newValue'] as String? ?? '',
    );
  }
}

class ActivityEntry {
  const ActivityEntry({
    required this.id,
    required this.portfolioId,
    required this.operation,
    required this.operationName,
    required this.entityType,
    required this.entityId,
    required this.actor,
    required this.description,
    this.detailHref,
    required this.timestamp,
    required this.testId,
    required this.changes,
  });

  final int id;
  final int portfolioId;
  final String operation;
  final String operationName;
  final String entityType;
  final int entityId;
  final String actor;
  final String description;
  final String? detailHref;
  final DateTime timestamp;
  final String testId;
  final List<ActivityChange> changes;

  factory ActivityEntry.fromJson(Map<String, dynamic> json) {
    final rawChanges = json['changes'] as List<dynamic>? ?? const [];
    final rawOperation = json['operation'];
    return ActivityEntry(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num?)?.toInt() ?? 0,
      operation: rawOperation == null ? '' : rawOperation.toString(),
      operationName: json['operationName'] as String? ?? '',
      entityType: json['entityType'] as String? ?? '',
      entityId: (json['entityId'] as num?)?.toInt() ?? 0,
      actor: json['actor'] as String? ?? '',
      description: json['description'] as String? ?? '',
      detailHref: json['detailHref'] as String?,
      timestamp:
          DateTime.tryParse(json['timestamp'] as String? ?? '') ?? DateTime(0),
      testId: json['testId'] as String? ?? 'audit-${json['id']}',
      changes: rawChanges
          .whereType<Map<String, dynamic>>()
          .map(ActivityChange.fromJson)
          .toList(),
    );
  }
}

class ActivityHistoryFilter {
  const ActivityHistoryFilter({
    this.sort = '-timestamp',
    this.search,
    this.operation,
    this.entityType,
    this.entityId,
  });

  final String sort;
  final String? search;
  final String? operation;
  final String? entityType;
  final int? entityId;

  ActivityHistoryFilter copyWith({
    String? sort,
    String? search,
    String? operation,
    String? entityType,
    int? entityId,
    bool clearSearch = false,
    bool clearOperation = false,
    bool clearEntityType = false,
    bool clearEntityId = false,
  }) {
    return ActivityHistoryFilter(
      sort: sort ?? this.sort,
      search: clearSearch ? null : (search ?? this.search),
      operation: clearOperation ? null : (operation ?? this.operation),
      entityType: clearEntityType ? null : (entityType ?? this.entityType),
      entityId: clearEntityId ? null : (entityId ?? this.entityId),
    );
  }
}

class ActivityHistoryState {
  const ActivityHistoryState({
    this.items = const [],
    this.filter = const ActivityHistoryFilter(),
    this.loading = false,
    this.loadingMore = false,
    this.hasMore = true,
    this.error,
  });

  final List<ActivityEntry> items;
  final ActivityHistoryFilter filter;
  final bool loading;
  final bool loadingMore;
  final bool hasMore;
  final String? error;

  ActivityHistoryState copyWith({
    List<ActivityEntry>? items,
    ActivityHistoryFilter? filter,
    bool? loading,
    bool? loadingMore,
    bool? hasMore,
    String? error,
    bool clearError = false,
  }) {
    return ActivityHistoryState(
      items: items ?? this.items,
      filter: filter ?? this.filter,
      loading: loading ?? this.loading,
      loadingMore: loadingMore ?? this.loadingMore,
      hasMore: hasMore ?? this.hasMore,
      error: clearError ? null : (error ?? this.error),
    );
  }
}
