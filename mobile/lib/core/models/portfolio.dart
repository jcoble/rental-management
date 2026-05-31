class Portfolio {
  final int id;
  final String name;
  final String? description;
  final String managementCompanyName;
  final String timeZone;
  final String status;
  final String? settings;
  final int? propertyCount;
  final int? unitCount;
  final int? activeLeaseCount;
  final DateTime createdAt;
  final DateTime updatedAt;

  const Portfolio({
    required this.id,
    required this.name,
    this.description,
    required this.managementCompanyName,
    required this.timeZone,
    required this.status,
    this.settings,
    this.propertyCount,
    this.unitCount,
    this.activeLeaseCount,
    required this.createdAt,
    required this.updatedAt,
  });

  factory Portfolio.fromJson(Map<String, dynamic> json) {
    return Portfolio(
      id: (json['id'] as num).toInt(),
      name: json['name'] as String? ?? '',
      description: json['description'] as String?,
      managementCompanyName: json['managementCompanyName'] as String? ?? '',
      timeZone: json['timeZone'] as String? ?? '',
      status: json['status'] as String? ?? '',
      settings: json['settings'] as String?,
      propertyCount: (json['propertyCount'] as num?)?.toInt(),
      unitCount: (json['unitCount'] as num?)?.toInt(),
      activeLeaseCount: (json['activeLeaseCount'] as num?)?.toInt(),
      createdAt: DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt: DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
    );
  }
}
