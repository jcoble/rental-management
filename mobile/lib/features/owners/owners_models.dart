enum OwnerEntityType {
  person,
  llc,
  trust;

  String get wireName {
    return switch (this) {
      OwnerEntityType.person => 'Person',
      OwnerEntityType.llc => 'LLC',
      OwnerEntityType.trust => 'Trust',
    };
  }

  String get label {
    return switch (this) {
      OwnerEntityType.person => 'Person',
      OwnerEntityType.llc => 'LLC',
      OwnerEntityType.trust => 'Trust',
    };
  }

  static OwnerEntityType fromJson(Object? value) {
    final normalized = value?.toString().trim().toLowerCase();
    return switch (normalized) {
      'llc' => OwnerEntityType.llc,
      'trust' => OwnerEntityType.trust,
      _ => OwnerEntityType.person,
    };
  }
}

class OwnerListQuery {
  const OwnerListQuery({
    this.skip = 0,
    this.take = 20,
    this.search,
    this.sort = 'name',
    this.ownerEntityType,
  });

  final int skip;
  final int take;
  final String? search;
  final String sort;
  final OwnerEntityType? ownerEntityType;

  OwnerListQuery copyWith({
    int? skip,
    int? take,
    String? search,
    String? sort,
    OwnerEntityType? ownerEntityType,
  }) {
    return OwnerListQuery(
      skip: skip ?? this.skip,
      take: take ?? this.take,
      search: search ?? this.search,
      sort: sort ?? this.sort,
      ownerEntityType: ownerEntityType ?? this.ownerEntityType,
    );
  }

  @override
  bool operator ==(Object other) {
    return other is OwnerListQuery &&
        other.skip == skip &&
        other.take == take &&
        other.search == search &&
        other.sort == sort &&
        other.ownerEntityType == ownerEntityType;
  }

  @override
  int get hashCode => Object.hash(skip, take, search, sort, ownerEntityType);
}

class OwnerEntity {
  const OwnerEntity({
    required this.id,
    required this.portfolioId,
    required this.ownerEntityType,
    required this.name,
    this.taxId,
    this.addressLine1,
    this.addressLine2,
    this.city,
    this.state,
    this.postalCode,
    this.address,
    this.phone,
    this.email,
    this.assignedPropertyCount = 0,
    this.isPrimary = false,
    this.createdAt,
    this.updatedAt,
  });

  final int id;
  final int portfolioId;
  final OwnerEntityType ownerEntityType;
  final String name;
  final String? taxId;
  final String? addressLine1;
  final String? addressLine2;
  final String? city;
  final String? state;
  final String? postalCode;
  final String? address;
  final String? phone;
  final String? email;
  final int assignedPropertyCount;
  final bool isPrimary;
  final DateTime? createdAt;
  final DateTime? updatedAt;

  bool get hasPhone => (phone ?? '').trim().isNotEmpty;
  bool get hasEmail => (email ?? '').trim().isNotEmpty;

  String get typeLabel => ownerEntityType.label;

  String? get displayAddress {
    final structured = [
      addressLine1,
      addressLine2,
      [
        city,
        state,
        postalCode,
      ].where((part) => (part ?? '').trim().isNotEmpty).join(', '),
    ].where((part) => (part ?? '').trim().isNotEmpty).join('\n');

    if (structured.trim().isNotEmpty) return structured;
    final fallback = address?.trim();
    return fallback == null || fallback.isEmpty ? null : fallback;
  }

  factory OwnerEntity.fromJson(Map<String, dynamic> json) {
    String? asString(String key) {
      final raw = json[key];
      if (raw is String && raw.trim().isNotEmpty) return raw;
      return null;
    }

    DateTime? asDate(String key) {
      final raw = json[key];
      if (raw is! String || raw.trim().isEmpty) return null;
      return DateTime.tryParse(raw);
    }

    return OwnerEntity(
      id: (json['id'] as num?)?.toInt() ?? 0,
      portfolioId: (json['portfolioId'] as num?)?.toInt() ?? 0,
      ownerEntityType: OwnerEntityType.fromJson(json['ownerEntityType']),
      name: json['name'] as String? ?? '',
      taxId: asString('taxId'),
      addressLine1: asString('addressLine1'),
      addressLine2: asString('addressLine2'),
      city: asString('city'),
      state: asString('state'),
      postalCode: asString('postalCode'),
      address: asString('address'),
      phone: asString('phone'),
      email: asString('email'),
      assignedPropertyCount:
          (json['assignedPropertyCount'] as num?)?.toInt() ?? 0,
      isPrimary: json['isPrimary'] as bool? ?? false,
      createdAt: asDate('createdAt'),
      updatedAt: asDate('updatedAt'),
    );
  }
}

class OwnerEntityPage {
  const OwnerEntityPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<OwnerEntity> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory OwnerEntityPage.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final items = rawItems is List
        ? rawItems
              .whereType<Map<String, dynamic>>()
              .map(OwnerEntity.fromJson)
              .toList()
        : <OwnerEntity>[];

    return OwnerEntityPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}
