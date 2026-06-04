/// Rental application — landlord-facing review model.
///
/// Mirrors the API `ApplicationResponse` (camelCase, enums as strings). The
/// applicant-facing intake form is web-only; in the mobile app Frank only
/// reviews, approves, declines, withdraws, and shares the apply link.
class RentalApplication {
  const RentalApplication({
    required this.id,
    required this.propertyId,
    this.unitId,
    required this.firstName,
    required this.lastName,
    this.email,
    this.phone,
    this.dateOfBirth,
    this.currentAddress,
    this.employer,
    this.monthlyIncome,
    this.desiredMoveInDate,
    this.notes,
    required this.consentGiven,
    this.consentAtUtc,
    required this.status,
    this.decisionReason,
    this.submittedAtUtc,
    this.reviewedAtUtc,
    this.approvedTenantId,
    this.testId,
  });

  final int id;
  final int propertyId;
  final int? unitId;
  final String firstName;
  final String lastName;
  final String? email;
  final String? phone;
  final DateTime? dateOfBirth;
  final String? currentAddress;
  final String? employer;
  final double? monthlyIncome;
  final DateTime? desiredMoveInDate;
  final String? notes;
  final bool consentGiven;
  final DateTime? consentAtUtc;
  final String status;
  final String? decisionReason;
  final DateTime? submittedAtUtc;
  final DateTime? reviewedAtUtc;
  final int? approvedTenantId;
  final String? testId;

  String get fullName {
    final name = '$firstName $lastName'.trim();
    return name.isEmpty ? 'Applicant' : name;
  }

  factory RentalApplication.fromJson(Map<String, dynamic> json) {
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

    return RentalApplication(
      id: (json['id'] as num).toInt(),
      propertyId: (json['propertyId'] as num?)?.toInt() ?? 0,
      unitId: (json['unitId'] as num?)?.toInt(),
      firstName: json['firstName'] as String? ?? '',
      lastName: json['lastName'] as String? ?? '',
      email: asString('email'),
      phone: asString('phone'),
      dateOfBirth: parseDate('dateOfBirth'),
      currentAddress: asString('currentAddress'),
      employer: asString('employer'),
      monthlyIncome: (json['monthlyIncome'] as num?)?.toDouble(),
      desiredMoveInDate: parseDate('desiredMoveInDate'),
      notes: asString('notes'),
      consentGiven: json['consentGiven'] as bool? ?? false,
      consentAtUtc: parseDate('consentAtUtc'),
      status: json['status'] as String? ?? 'Submitted',
      decisionReason: asString('decisionReason'),
      submittedAtUtc: parseDate('submittedAtUtc'),
      reviewedAtUtc: parseDate('reviewedAtUtc'),
      approvedTenantId: (json['approvedTenantId'] as num?)?.toInt(),
      testId: asString('testId'),
    );
  }
}

/// The shareable apply link returned by `POST /applications/link`.
class ApplicationLink {
  const ApplicationLink({required this.token, required this.applyPath});

  final String token;

  /// Server-relative path, e.g. `/apply/{token}`.
  final String applyPath;

  factory ApplicationLink.fromJson(Map<String, dynamic> json) {
    return ApplicationLink(
      token: json['token'] as String? ?? '',
      applyPath: json['applyPath'] as String? ?? '',
    );
  }
}

/// Result of approving an application — also yields the new tenant.
class ApplicationApproval {
  const ApplicationApproval({
    required this.applicationId,
    required this.status,
    this.tenantId,
  });

  final int applicationId;
  final String status;
  final int? tenantId;

  factory ApplicationApproval.fromJson(Map<String, dynamic> json) {
    return ApplicationApproval(
      applicationId: (json['applicationId'] as num?)?.toInt() ?? 0,
      status: json['status'] as String? ?? 'Approved',
      tenantId: (json['tenantId'] as num?)?.toInt(),
    );
  }
}

// ── Status metadata ───────────────────────────────────────────────────────────

/// Application statuses, in lifecycle order (matches the API enum).
const applicationStatuses = <String>[
  'Submitted',
  'UnderReview',
  'Approved',
  'Declined',
  'Withdrawn',
];

/// Human-friendly label for a status string.
String friendlyApplicationStatus(String status) {
  switch (status) {
    case 'Submitted':
      return 'Submitted';
    case 'UnderReview':
      return 'Under Review';
    case 'Approved':
      return 'Approved';
    case 'Declined':
      return 'Declined';
    case 'Withdrawn':
      return 'Withdrawn';
    default:
      return status;
  }
}

/// True while the application is still actionable (can approve/decline/withdraw).
bool isApplicationOpen(String status) =>
    status == 'Submitted' || status == 'UnderReview';
