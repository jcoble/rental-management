import 'dart:convert';

/// Plain Dart models for the scan-draft API.
///
/// Mirrors [ScanDraftResponse], [ScanFieldDto], and the line-items sub-shape
/// from the API's ScanController and web/src/lib/api/scan.ts.

// ---------------------------------------------------------------------------
// ScanField
// ---------------------------------------------------------------------------

enum ConfidenceLevel { high, medium, low }

class ScanField {
  const ScanField({
    required this.name,
    required this.value,
    required this.confidence,
  });

  final String name;
  final String value;

  /// 0.0–1.0 confidence from the LLM extraction.
  final double confidence;

  factory ScanField.fromJson(Map<String, dynamic> json) {
    return ScanField(
      name: json['name'] as String? ?? '',
      value: json['value'] as String? ?? '',
      confidence: (json['confidence'] as num?)?.toDouble() ?? 1.0,
    );
  }

  ConfidenceLevel get confidenceLevel {
    if (confidence >= 0.8) return ConfidenceLevel.high;
    if (confidence >= 0.5) return ConfidenceLevel.medium;
    return ConfidenceLevel.low;
  }
}

// ---------------------------------------------------------------------------
// Lease-import proposal (lease drafts only)
// ---------------------------------------------------------------------------

/// What confirming a scanned lease will do with one entity (the property or the
/// unit). Mirrors the API's `ProposedRecord` (see IScanService.cs).
///
///   - [action] == 'link'   → an existing in-portfolio record was matched
///     ([existingId] is set); confirm links to it.
///   - [action] == 'create' → no match, but the document has enough to create
///     one ([label]/[detail] describe it).
///   - [action] == 'select' → not enough on the document to match or create, so
///     the reviewer must choose manually.
class ProposedRecord {
  const ProposedRecord({
    required this.action,
    this.existingId,
    this.label,
    this.detail,
  });

  final String action;
  final int? existingId;
  final String? label;
  final String? detail;

  bool get isLink => action == 'link';
  bool get isCreate => action == 'create';
  bool get isSelect => action == 'select';

  factory ProposedRecord.fromJson(Map<String, dynamic> json) {
    return ProposedRecord(
      action: (json['action'] as String?)?.toLowerCase() ?? 'select',
      existingId: (json['existingId'] as num?)?.toInt(),
      label: json['label'] as String?,
      detail: json['detail'] as String?,
    );
  }
}

/// The property + unit import preview attached to a lease draft, so the review
/// screen can show what confirming will do (and let a brand-new landlord scan
/// into an empty portfolio — the property/unit are created from the document).
class LeaseImportProposal {
  const LeaseImportProposal({required this.property, required this.unit});

  final ProposedRecord property;
  final ProposedRecord unit;

  factory LeaseImportProposal.fromJson(Map<String, dynamic> json) {
    final prop = json['property'];
    final unit = json['unit'];
    return LeaseImportProposal(
      property: prop is Map<String, dynamic>
          ? ProposedRecord.fromJson(prop)
          : const ProposedRecord(action: 'select'),
      unit: unit is Map<String, dynamic>
          ? ProposedRecord.fromJson(unit)
          : const ProposedRecord(action: 'select'),
    );
  }
}

// ---------------------------------------------------------------------------
// ScanLineItem
// ---------------------------------------------------------------------------

class ScanLineItem {
  const ScanLineItem({
    this.description,
    this.quantity,
    this.unitPrice,
    this.amount,
  });

  final String? description;
  final double? quantity;
  final double? unitPrice;
  final double? amount;

  factory ScanLineItem.fromJson(Map<String, dynamic> json) {
    return ScanLineItem(
      description: json['description'] as String?,
      quantity: (json['quantity'] as num?)?.toDouble(),
      unitPrice: (json['unit_price'] as num?)?.toDouble(),
      amount: (json['amount'] as num?)?.toDouble(),
    );
  }
}

// ---------------------------------------------------------------------------
// ScanDraft
// ---------------------------------------------------------------------------

class ScanCaptureContext {
  const ScanCaptureContext({
    this.experience,
    this.accessContextId,
    this.accessRevision,
    this.propertyId,
    this.unitId,
    this.leaseManagementId,
    this.leaseAgreementId,
    this.tenantAccountId,
    this.tenantLedgerEntryId,
    this.workOrderId,
    this.applicationId,
    this.rentalListingId,
    this.sourceLabel,
  });

  final String? experience;
  final int? accessContextId;
  final int? accessRevision;
  final int? propertyId;
  final int? unitId;
  final int? leaseManagementId;
  final int? leaseAgreementId;
  final int? tenantAccountId;
  final int? tenantLedgerEntryId;
  final int? workOrderId;
  final int? applicationId;
  final int? rentalListingId;
  final String? sourceLabel;

  bool get hasBusinessContext =>
      propertyId != null ||
      unitId != null ||
      leaseManagementId != null ||
      leaseAgreementId != null ||
      tenantAccountId != null ||
      tenantLedgerEntryId != null ||
      workOrderId != null ||
      applicationId != null ||
      rentalListingId != null;

  List<String> get userFacingParts => [
    if (propertyId != null) 'Property #$propertyId',
    if (unitId != null) 'Unit #$unitId',
    if (leaseManagementId != null) 'Rental #$leaseManagementId',
    if (leaseAgreementId != null) 'Agreement #$leaseAgreementId',
    if (tenantAccountId != null) 'Account #$tenantAccountId',
    if (tenantLedgerEntryId != null) 'Ledger entry #$tenantLedgerEntryId',
    if (workOrderId != null) 'Work order #$workOrderId',
    if (applicationId != null) 'Application #$applicationId',
    if (rentalListingId != null) 'Listing #$rentalListingId',
  ];

  factory ScanCaptureContext.fromJson(Map<String, dynamic> json) {
    return ScanCaptureContext(
      experience: json['experience'] as String?,
      accessContextId: (json['accessContextId'] as num?)?.toInt(),
      accessRevision: (json['accessRevision'] as num?)?.toInt(),
      propertyId: (json['propertyId'] as num?)?.toInt(),
      unitId: (json['unitId'] as num?)?.toInt(),
      leaseManagementId: (json['leaseManagementId'] as num?)?.toInt(),
      leaseAgreementId: (json['leaseAgreementId'] as num?)?.toInt(),
      tenantAccountId: (json['tenantAccountId'] as num?)?.toInt(),
      tenantLedgerEntryId: (json['tenantLedgerEntryId'] as num?)?.toInt(),
      workOrderId: (json['workOrderId'] as num?)?.toInt(),
      applicationId: (json['applicationId'] as num?)?.toInt(),
      rentalListingId: (json['rentalListingId'] as num?)?.toInt(),
      sourceLabel: json['sourceLabel'] as String?,
    );
  }
}

class ScanDraft {
  const ScanDraft({
    required this.id,
    required this.portfolioId,
    required this.targetEntityType,
    required this.status,
    required this.fileUrl,
    required this.fields,
    this.modelId,
    this.tokensUsed,
    this.costUsd,
    this.failureReason,
    required this.createdAt,
    this.reviewedAt,
    this.confirmedAt,
    this.leaseProposal,
    this.captureContext,
    this.createdEntityType,
    this.createdEntityId,
    this.createdUnitId,
  });

  final int id;
  final int portfolioId;

  /// 'Expense', 'Payment', 'WorkOrder', 'LeaseAgreement', 'Application', or 'Loan'
  final String targetEntityType;

  /// Lifecycle: 'Pending' -> 'Processing' -> 'Reviewing' -> 'Confirmed'
  /// (or 'Failed' / 'Rejected').
  final String status;

  /// Relative path returned by the API, e.g. /api/v1/scans/{id}/file
  final String fileUrl;

  final List<ScanField> fields;
  final String? modelId;
  final int? tokensUsed;
  final double? costUsd;
  final String? failureReason;
  final DateTime createdAt;
  final DateTime? reviewedAt;
  final DateTime? confirmedAt;

  /// Property/unit import preview for a Lease draft (link-existing vs create-new).
  /// Null for non-lease drafts, or when the server didn't attach one.
  final LeaseImportProposal? leaseProposal;

  /// Server-preserved launch context used to preselect the exact account when
  /// a payment scan starts from a Unit or account surface.
  final ScanCaptureContext? captureContext;

  /// Canonical destination populated after confirmation.
  final String? createdEntityType;
  final int? createdEntityId;
  final int? createdUnitId;

  /// Target is a Payment draft (vs. an Expense draft).
  bool get isPayment => targetEntityType == 'Payment';

  /// Target is a maintenance work order draft.
  bool get isWorkOrder => targetEntityType == 'WorkOrder';

  /// Target is a lease draft (scanned/imported lease agreement).
  bool get isLease => targetEntityType == 'LeaseAgreement';

  /// Target is a scanned completed paper rental application; confirming creates
  /// a RentalApplication (applicant), mirroring the public apply form.
  bool get isApplication => targetEntityType == 'Application';

  /// Target is a scanned mortgage statement or closing disclosure.
  bool get isLoan => targetEntityType == 'Loan';

  /// Queued for extraction; the Engine has not picked it up yet.
  bool get isPending => status == 'Pending';

  /// The Engine is actively extracting fields from the document.
  bool get isProcessing => status == 'Processing';

  /// Still being worked (queued or processing) — not yet reviewable.
  /// Use this for "show a spinner / keep polling" decisions.
  bool get isInFlight => isPending || isProcessing;

  /// Extraction finished; awaiting user review/confirmation.
  bool get isReviewing => status == 'Reviewing';

  /// Reached a final state — confirmed, rejected, or failed (no more work).
  bool get isTerminal =>
      status == 'Confirmed' || status == 'Rejected' || status == 'Failed';

  /// True when the server ran in no-op mode (no OpenAI key configured).
  bool get isNoOp => modelId == 'noop';

  factory ScanDraft.fromJson(Map<String, dynamic> json) {
    final rawFields = (json['fields'] as List<dynamic>?) ?? [];
    return ScanDraft(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num).toInt(),
      targetEntityType: json['targetEntityType'] as String? ?? 'Expense',
      status: json['status'] as String? ?? 'Pending',
      fileUrl: json['fileUrl'] as String? ?? '',
      fields: rawFields
          .cast<Map<String, dynamic>>()
          .map(ScanField.fromJson)
          .toList(),
      modelId: json['modelId'] as String?,
      tokensUsed: (json['tokensUsed'] as num?)?.toInt(),
      costUsd: (json['costUsd'] as num?)?.toDouble(),
      failureReason: json['failureReason'] as String?,
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      reviewedAt: json['reviewedAt'] != null
          ? DateTime.tryParse(json['reviewedAt'] as String)
          : null,
      confirmedAt: json['confirmedAt'] != null
          ? DateTime.tryParse(json['confirmedAt'] as String)
          : null,
      leaseProposal: json['leaseProposal'] is Map<String, dynamic>
          ? LeaseImportProposal.fromJson(
              json['leaseProposal'] as Map<String, dynamic>,
            )
          : null,
      captureContext: json['captureContext'] is Map<String, dynamic>
          ? ScanCaptureContext.fromJson(
              json['captureContext'] as Map<String, dynamic>,
            )
          : null,
      createdEntityType: json['createdEntityType'] as String?,
      createdEntityId: (json['createdEntityId'] as num?)?.toInt(),
      createdUnitId: (json['createdUnitId'] as num?)?.toInt(),
    );
  }

  /// Parses the 'line_items' field value (a JSON-encoded array) into typed objects.
  List<ScanLineItem> get lineItems {
    try {
      final f = fields.firstWhere(
        (f) => f.name == 'line_items',
        orElse: () => const ScanField(name: '', value: '', confidence: 1),
      );
      if (f.name.isEmpty || f.value.isEmpty) return [];
      final parsed = jsonDecode(f.value);
      if (parsed is List) {
        return parsed
            .whereType<Map<String, dynamic>>()
            .map(ScanLineItem.fromJson)
            .toList();
      }
      return [];
    } catch (_) {
      return [];
    }
  }

  /// All scalar fields (everything except 'line_items').
  List<ScanField> get scalarFields =>
      fields.where((f) => f.name != 'line_items').toList();
}

// ---------------------------------------------------------------------------
// ScanCreatedResponse  (upload response)
// ---------------------------------------------------------------------------

class ScanCreatedResponse {
  const ScanCreatedResponse({
    required this.draftId,
    required this.status,
    required this.fileUrl,
  });

  final int draftId;
  final String status;
  final String fileUrl;

  factory ScanCreatedResponse.fromJson(Map<String, dynamic> json) {
    return ScanCreatedResponse(
      draftId: (json['draftId'] as num).toInt(),
      status: json['status'] as String? ?? '',
      fileUrl: json['fileUrl'] as String? ?? '',
    );
  }
}
