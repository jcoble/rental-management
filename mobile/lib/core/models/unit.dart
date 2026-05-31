class Unit {
  final int id;
  final int propertyId;
  final String unitNumber;
  final String? floorPlan;
  final int bedrooms;
  final double bathrooms;
  final double? squareFeet;
  final double marketRent;
  final String status;
  final String? notes;
  final DateTime createdAt;
  final DateTime updatedAt;

  const Unit({
    required this.id,
    required this.propertyId,
    required this.unitNumber,
    this.floorPlan,
    required this.bedrooms,
    required this.bathrooms,
    this.squareFeet,
    required this.marketRent,
    required this.status,
    this.notes,
    required this.createdAt,
    required this.updatedAt,
  });

  factory Unit.fromJson(Map<String, dynamic> json) {
    return Unit(
      id: (json['id'] as num).toInt(),
      propertyId: (json['propertyId'] as num).toInt(),
      unitNumber: json['unitNumber'] as String? ?? '',
      floorPlan: json['floorPlan'] as String?,
      bedrooms: (json['bedrooms'] as num?)?.toInt() ?? 0,
      bathrooms: (json['bathrooms'] as num?)?.toDouble() ?? 0,
      squareFeet: (json['squareFeet'] as num?)?.toDouble(),
      marketRent: (json['marketRent'] as num?)?.toDouble() ?? 0,
      status: json['status'] as String? ?? '',
      notes: json['notes'] as String?,
      createdAt: DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt: DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
    );
  }

  Map<String, dynamic> toJson() {
    return {
      'propertyId': propertyId,
      'unitNumber': unitNumber,
      if (floorPlan != null) 'floorPlan': floorPlan,
      'bedrooms': bedrooms,
      'bathrooms': bathrooms,
      if (squareFeet != null) 'squareFeet': squareFeet,
      'marketRent': marketRent,
      'status': status,
      if (notes != null) 'notes': notes,
    };
  }
}
