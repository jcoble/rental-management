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
    this.email,
    this.phone,
    this.preferred = false,
    this.notes,
    this.averageRating,
    this.ratingCount = 0,
    this.jobsCompleted = 0,
  });

  final int id;
  final String name;
  final String serviceType;
  final String? email;
  final String? phone;
  final bool preferred;
  final String? notes;

  /// Cached average star rating (1–5); null until the vendor has been rated.
  final double? averageRating;

  /// Number of ratings behind [averageRating].
  final int ratingCount;

  /// Count of work orders this vendor has completed.
  final int jobsCompleted;

  /// True when the vendor can be texted a job.
  bool get hasPhone => (phone ?? '').trim().isNotEmpty;

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
      email: asString('email'),
      phone: asString('phone'),
      preferred: json['preferred'] as bool? ?? false,
      notes: asString('notes'),
      averageRating: (json['averageRating'] as num?)?.toDouble(),
      ratingCount: (json['ratingCount'] as num?)?.toInt() ?? 0,
      jobsCompleted: (json['jobsCompleted'] as num?)?.toInt() ?? 0,
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
