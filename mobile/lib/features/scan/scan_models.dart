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
    required this.createdAt,
    this.reviewedAt,
    this.confirmedAt,
  });

  final int id;
  final int portfolioId;

  /// 'Expense', 'Payment', 'WorkOrder', or 'Lease'
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
  final DateTime createdAt;
  final DateTime? reviewedAt;
  final DateTime? confirmedAt;

  /// Target is a Payment draft (vs. an Expense draft).
  bool get isPayment => targetEntityType == 'Payment';

  /// Target is a maintenance work order draft.
  bool get isWorkOrder => targetEntityType == 'WorkOrder';

  /// Target is a lease draft (scanned/imported lease agreement).
  bool get isLease => targetEntityType == 'Lease';

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
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
      reviewedAt: json['reviewedAt'] != null
          ? DateTime.tryParse(json['reviewedAt'] as String)
          : null,
      confirmedAt: json['confirmedAt'] != null
          ? DateTime.tryParse(json['confirmedAt'] as String)
          : null,
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
