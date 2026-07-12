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

/// Result of an idempotent mutation against an application's pre-tenancy
/// financial account.
class ApplicationFinanceMutation {
  const ApplicationFinanceMutation({
    required this.applicationId,
    required this.accountId,
    required this.entryId,
    this.relatedEntryId,
    required this.entryType,
    required this.direction,
    required this.amount,
    required this.currency,
    required this.effectiveOn,
    required this.occurredAtUtc,
    required this.accountCreated,
    required this.replayed,
  });

  final int applicationId;
  final int accountId;
  final int entryId;
  final int? relatedEntryId;
  final String entryType;
  final String direction;
  final double amount;
  final String currency;
  final DateTime effectiveOn;
  final DateTime occurredAtUtc;
  final bool accountCreated;
  final bool replayed;

  factory ApplicationFinanceMutation.fromJson(Map<String, dynamic> json) {
    return ApplicationFinanceMutation(
      applicationId: (json['applicationId'] as num?)?.toInt() ?? 0,
      accountId: (json['accountId'] as num?)?.toInt() ?? 0,
      entryId: (json['entryId'] as num?)?.toInt() ?? 0,
      relatedEntryId: (json['relatedEntryId'] as num?)?.toInt(),
      entryType: json['entryType'] as String? ?? '',
      direction: json['direction'] as String? ?? '',
      amount: (json['amount'] as num?)?.toDouble() ?? 0,
      currency: json['currency'] as String? ?? '',
      effectiveOn:
          DateTime.tryParse(json['effectiveOn'] as String? ?? '') ??
          DateTime(0),
      occurredAtUtc:
          DateTime.tryParse(json['occurredAtUtc'] as String? ?? '') ??
          DateTime(0),
      accountCreated: json['accountCreated'] as bool? ?? false,
      replayed: json['replayed'] as bool? ?? false,
    );
  }
}

/// A tenant-screening result for an application.
///
/// Mirrors the API `ScreeningResultResponse` (camelCase, enums as strings).
class ScreeningResult {
  const ScreeningResult({
    required this.id,
    required this.applicationId,
    required this.status,
    this.creditScoreBand,
    this.hasCriminalRecord,
    this.hasEvictionRecord,
    this.recommendation,
    this.providerReference,
    this.requestedAtUtc,
    this.completedAtUtc,
  });

  final int id;
  final int applicationId;

  /// One of: Requested, Completed, Failed.
  final String status;
  final String? creditScoreBand;
  final bool? hasCriminalRecord;
  final bool? hasEvictionRecord;

  /// One of: Accept, Conditional, Decline.
  final String? recommendation;
  final String? providerReference;
  final DateTime? requestedAtUtc;
  final DateTime? completedAtUtc;

  bool get isCompleted => status == 'Completed';

  factory ScreeningResult.fromJson(Map<String, dynamic> json) {
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

    return ScreeningResult(
      id: (json['id'] as num?)?.toInt() ?? 0,
      applicationId: (json['applicationId'] as num?)?.toInt() ?? 0,
      status: json['status'] as String? ?? 'Requested',
      creditScoreBand: asString('creditScoreBand'),
      hasCriminalRecord: json['hasCriminalRecord'] as bool?,
      hasEvictionRecord: json['hasEvictionRecord'] as bool?,
      recommendation: asString('recommendation'),
      providerReference: asString('providerReference'),
      requestedAtUtc: parseDate('requestedAtUtc'),
      completedAtUtc: parseDate('completedAtUtc'),
    );
  }
}

/// Human-friendly label for a screening status string.
String friendlyScreeningStatus(String status) {
  switch (status) {
    case 'Requested':
      return 'Requested';
    case 'Completed':
      return 'Completed';
    case 'Failed':
      return 'Failed';
    default:
      return status;
  }
}

/// An FCRA adverse-action notice generated for a declined application.
///
/// Mirrors the API `AdverseActionNoticeResponse`.
class AdverseActionNotice {
  const AdverseActionNotice({
    required this.id,
    required this.applicationId,
    this.reason,
    this.creditReportingAgency,
    this.generatedAtUtc,
    this.storedFileId,
    this.sentAtUtc,
  });

  final int id;
  final int applicationId;
  final String? reason;
  final String? creditReportingAgency;
  final DateTime? generatedAtUtc;
  final int? storedFileId;
  final DateTime? sentAtUtc;

  factory AdverseActionNotice.fromJson(Map<String, dynamic> json) {
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

    return AdverseActionNotice(
      id: (json['id'] as num?)?.toInt() ?? 0,
      applicationId: (json['applicationId'] as num?)?.toInt() ?? 0,
      reason: asString('reason'),
      creditReportingAgency: asString('creditReportingAgency'),
      generatedAtUtc: parseDate('generatedAtUtc'),
      storedFileId: (json['storedFileId'] as num?)?.toInt(),
      sentAtUtc: parseDate('sentAtUtc'),
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
