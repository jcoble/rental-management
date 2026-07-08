/// Vendor with its cached rating summary.
///
/// Mirrors the API `VendorResponse` (camelCase, enums as strings). The mobile
/// app shows vendors so the landlord can pick one to text a job to, see how a
/// vendor is performing, and leave a star rating after a completed job.
class Vendor {
  const Vendor({
    required this.id,
    required this.name,
    required this.serviceType,
    this.addressLine1,
    this.city,
    this.state,
    this.postalCode,
    this.email,
    this.phone,
    this.website,
    this.taxId,
    this.is1099Eligible = false,
    this.preferred = false,
    this.w9OnFile = false,
    this.notes,
    this.averageRating,
    this.ratingCount = 0,
    this.jobsCompleted = 0,
  });

  final int id;
  final String name;
  final String serviceType;
  final String? addressLine1;
  final String? city;
  final String? state;
  final String? postalCode;
  final String? email;
  final String? phone;
  final String? website;
  final String? taxId;
  final bool is1099Eligible;
  final bool preferred;

  /// True when a signed W-9 has been collected from this vendor (needed before
  /// issuing a 1099). Toggled from the vendor detail screen.
  final bool w9OnFile;

  final String? notes;

  /// Cached average star rating (1–5); null until the vendor has been rated.
  final double? averageRating;

  /// Number of ratings behind [averageRating].
  final int ratingCount;

  /// Count of work orders this vendor has completed.
  final int jobsCompleted;

  /// True when the vendor can be texted a job.
  bool get hasPhone => (phone ?? '').trim().isNotEmpty;

  /// True when the vendor has an email on file (enables the email action).
  bool get hasEmail => (email ?? '').trim().isNotEmpty;

  bool get hasWebsite => (website ?? '').trim().isNotEmpty;

  factory Vendor.fromJson(Map<String, dynamic> json) {
    String? asString(String key) {
      final raw = json[key];
      if (raw is String && raw.trim().isNotEmpty) return raw;
      return null;
    }

    return Vendor(
      id: (json['id'] as num).toInt(),
      name: json['name'] as String? ?? '',
      serviceType: json['serviceType'] as String? ?? '',
      addressLine1: asString('addressLine1'),
      city: asString('city'),
      state: asString('state'),
      postalCode: asString('postalCode'),
      email: asString('email'),
      phone: asString('phone'),
      website: asString('website'),
      taxId: asString('taxId'),
      is1099Eligible: json['is1099Eligible'] as bool? ?? false,
      preferred: json['preferred'] as bool? ?? false,
      w9OnFile: json['w9OnFile'] as bool? ?? false,
      notes: asString('notes'),
      averageRating: (json['averageRating'] as num?)?.toDouble(),
      ratingCount: (json['ratingCount'] as num?)?.toInt() ?? 0,
      jobsCompleted: (json['jobsCompleted'] as num?)?.toInt() ?? 0,
    );
  }

  Vendor copyWith({bool? is1099Eligible, bool? w9OnFile, bool? preferred}) {
    return Vendor(
      id: id,
      name: name,
      serviceType: serviceType,
      addressLine1: addressLine1,
      city: city,
      state: state,
      postalCode: postalCode,
      email: email,
      phone: phone,
      website: website,
      taxId: taxId,
      is1099Eligible: is1099Eligible ?? this.is1099Eligible,
      preferred: preferred ?? this.preferred,
      w9OnFile: w9OnFile ?? this.w9OnFile,
      notes: notes,
      averageRating: averageRating,
      ratingCount: ratingCount,
      jobsCompleted: jobsCompleted,
    );
  }
}

class VendorListQuery {
  const VendorListQuery({
    this.skip = 0,
    this.take = 20,
    this.search,
    this.sort = 'name',
  });

  final int skip;
  final int take;
  final String? search;
  final String sort;

  @override
  bool operator ==(Object other) {
    return other is VendorListQuery &&
        other.skip == skip &&
        other.take == take &&
        other.search == search &&
        other.sort == sort;
  }

  @override
  int get hashCode => Object.hash(skip, take, search, sort);
}

class VendorPage {
  const VendorPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<Vendor> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory VendorPage.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final items = rawItems is List
        ? rawItems
              .whereType<Map<String, dynamic>>()
              .map(Vendor.fromJson)
              .toList()
        : <Vendor>[];

    return VendorPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

/// Vendor performance scorecard from `GET /vendors/{id}/scorecard`.
class VendorScorecard {
  const VendorScorecard({
    required this.vendorId,
    required this.name,
    this.averageRating,
    this.ratingCount = 0,
    this.jobsCompleted = 0,
    this.avgResponseHours,
  });

  final int vendorId;
  final String name;
  final double? averageRating;
  final int ratingCount;
  final int jobsCompleted;

  /// Average hours from texting a job to the vendor's DONE reply. Null until
  /// the vendor has completed at least one dispatch.
  final double? avgResponseHours;

  factory VendorScorecard.fromJson(Map<String, dynamic> json) {
    return VendorScorecard(
      vendorId: (json['vendorId'] as num?)?.toInt() ?? 0,
      name: json['name'] as String? ?? '',
      averageRating: (json['averageRating'] as num?)?.toDouble(),
      ratingCount: (json['ratingCount'] as num?)?.toInt() ?? 0,
      jobsCompleted: (json['jobsCompleted'] as num?)?.toInt() ?? 0,
      avgResponseHours: (json['avgResponseHours'] as num?)?.toDouble(),
    );
  }
}

/// Result of texting a vendor a W-9 request (`POST /vendors/{id}/request-w9`).
class W9RequestResult {
  const W9RequestResult({required this.queued, this.sentTo});

  /// True when the request SMS was queued for delivery.
  final bool queued;

  /// The phone number the request was sent to, when the API reports it.
  final String? sentTo;

  factory W9RequestResult.fromJson(Map<String, dynamic> json) {
    final to = json['sentTo'];
    return W9RequestResult(
      queued: json['queued'] as bool? ?? false,
      sentTo: (to is String && to.isNotEmpty) ? to : null,
    );
  }
}

/// Result of dispatching a work order to a vendor (`POST /work-orders/{id}/dispatch`).
class VendorDispatchResult {
  const VendorDispatchResult({
    required this.id,
    required this.workOrderId,
    required this.vendorId,
    required this.status,
    this.message,
  });

  final int id;
  final int workOrderId;
  final int vendorId;
  final String status;
  final String? message;

  factory VendorDispatchResult.fromJson(Map<String, dynamic> json) {
    final raw = json['message'];
    return VendorDispatchResult(
      id: (json['id'] as num?)?.toInt() ?? 0,
      workOrderId: (json['workOrderId'] as num?)?.toInt() ?? 0,
      vendorId: (json['vendorId'] as num?)?.toInt() ?? 0,
      status: json['status'] as String? ?? 'Dispatched',
      message: (raw is String && raw.isNotEmpty) ? raw : null,
    );
  }
}
