enum RentalStructure {
  singleRental('SingleRental'),
  multiRental('MultiRental');

  const RentalStructure(this.wireValue);

  final String wireValue;

  static RentalStructure fromJson(Object? value) {
    for (final structure in values) {
      if (structure.wireValue == value) return structure;
    }
    throw FormatException('Unknown rentalStructure: $value');
  }
}

class Property {
  final int id;
  final int portfolioId;
  final List<
    ({
      int id,
      int ownerEntityId,
      String ownerName,
      double ownershipSharePercent,
      DateTime effectiveFromUtc,
      DateTime? effectiveToUtc,
      String statementRecipientName,
      String? statementRecipientEmail,
      String payeeName,
    })
  >
  ownerships;
  final String name;
  final String type;
  final RentalStructure rentalStructure;
  final String status;
  final String addressLine1;
  final String? addressLine2;
  final String city;
  final String state;
  final String postalCode;
  final int? yearBuilt;
  final double? managementFeePercent;
  final String? notes;
  final double? purchasePrice;
  final double? landValue;
  final String? inServiceDate;
  final double? manualAnnualDepreciation;
  final double? accumulatedDepreciation;
  final int? unitCount;
  final int? occupiedUnits;
  final DateTime createdAt;
  final DateTime updatedAt;

  const Property({
    required this.id,
    required this.portfolioId,
    this.ownerships = const [],
    required this.name,
    required this.type,
    required this.rentalStructure,
    required this.status,
    required this.addressLine1,
    this.addressLine2,
    required this.city,
    required this.state,
    required this.postalCode,
    this.yearBuilt,
    this.managementFeePercent,
    this.notes,
    this.purchasePrice,
    this.landValue,
    this.inServiceDate,
    this.manualAnnualDepreciation,
    this.accumulatedDepreciation,
    this.unitCount,
    this.occupiedUnits,
    required this.createdAt,
    required this.updatedAt,
  });

  factory Property.fromJson(Map<String, dynamic> json) {
    return Property(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num).toInt(),
      ownerships: (json['ownerships'] as List<dynamic>? ?? const [])
          .whereType<Map>()
          .map((ownership) {
            final data = Map<String, dynamic>.from(ownership);
            return (
              id: (data['id'] as num).toInt(),
              ownerEntityId: (data['ownerEntityId'] as num).toInt(),
              ownerName: data['ownerName'] as String? ?? '',
              ownershipSharePercent: (data['ownershipSharePercent'] as num)
                  .toDouble(),
              effectiveFromUtc:
                  DateTime.tryParse(
                    data['effectiveFromUtc'] as String? ?? '',
                  ) ??
                  DateTime(0),
              effectiveToUtc: DateTime.tryParse(
                data['effectiveToUtc'] as String? ?? '',
              ),
              statementRecipientName:
                  data['statementRecipientName'] as String? ?? '',
              statementRecipientEmail:
                  data['statementRecipientEmail'] as String?,
              payeeName: data['payeeName'] as String? ?? '',
            );
          })
          .toList(growable: false),
      name: json['name'] as String? ?? '',
      type: json['type'] as String? ?? '',
      rentalStructure: RentalStructure.fromJson(json['rentalStructure']),
      status: json['status'] as String? ?? '',
      addressLine1: json['addressLine1'] as String? ?? '',
      addressLine2: json['addressLine2'] as String?,
      city: json['city'] as String? ?? '',
      state: json['state'] as String? ?? '',
      postalCode: json['postalCode'] as String? ?? '',
      yearBuilt: (json['yearBuilt'] as num?)?.toInt(),
      managementFeePercent: (json['managementFeePercent'] as num?)?.toDouble(),
      notes: json['notes'] as String?,
      purchasePrice: (json['purchasePrice'] as num?)?.toDouble(),
      landValue: (json['landValue'] as num?)?.toDouble(),
      inServiceDate: json['inServiceDate'] as String?,
      manualAnnualDepreciation: (json['manualAnnualDepreciation'] as num?)
          ?.toDouble(),
      accumulatedDepreciation: (json['accumulatedDepreciation'] as num?)
          ?.toDouble(),
      unitCount: (json['unitCount'] as num?)?.toInt(),
      occupiedUnits: (json['occupiedUnits'] as num?)?.toInt(),
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      updatedAt:
          DateTime.tryParse(json['updatedAt'] as String? ?? '') ?? DateTime(0),
    );
  }

  Map<String, dynamic> toJson() {
    return {
      'portfolioId': portfolioId,
      'name': name,
      'type': type,
      'rentalStructure': rentalStructure.wireValue,
      'status': status,
      'addressLine1': addressLine1,
      if (addressLine2 != null) 'addressLine2': addressLine2,
      'city': city,
      'state': state,
      'postalCode': postalCode,
      if (yearBuilt != null) 'yearBuilt': yearBuilt,
      if (managementFeePercent != null)
        'managementFeePercent': managementFeePercent,
      if (notes != null) 'notes': notes,
      if (purchasePrice != null) 'purchasePrice': purchasePrice,
      if (landValue != null) 'landValue': landValue,
      if (inServiceDate != null) 'inServiceDate': inServiceDate,
      if (manualAnnualDepreciation != null)
        'manualAnnualDepreciation': manualAnnualDepreciation,
    };
  }
}
