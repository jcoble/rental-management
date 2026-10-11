/// Models for the conversational voice intake ("Tell me") flow.
///
/// The server returns a draft (the `ScanDraftResponse` shape) extended with the
/// slot-filling state: which required fields are still missing, the next
/// question to ask, and whether the draft is complete. [VoiceTurn] is the
/// client view of one turn in that conversation.
library;

/// One turn of the voice conversation — the draft so far plus what's still needed.
class VoiceTurn {
  const VoiceTurn({
    required this.draftId,
    required this.recordType,
    required this.fields,
    required this.missingRequired,
    required this.complete,
    required this.ambiguous,
    this.nextPrompt,
  });

  /// Draft id, used to send the next answer and to confirm.
  final int draftId;

  /// Detected record type, e.g. "Expense".
  final String recordType;

  /// Extracted field name → value (e.g. `amount` → "40", `category` → "plumbing").
  final Map<String, String> fields;

  /// Required slots still empty, in ask order.
  final List<String> missingRequired;

  /// True when nothing required is missing — ready to confirm.
  final bool complete;

  /// True when the server needs clarification before this can be saved.
  final bool ambiguous;

  /// Short question for the next missing slot, or null when [complete].
  final String? nextPrompt;

  factory VoiceTurn.fromJson(Map<String, dynamic> json) {
    final rawFields = (json['fields'] as List?) ?? const [];
    final fields = <String, String>{};
    for (final entry in rawFields) {
      if (entry is Map) {
        final name = entry['name'];
        if (name is String) {
          fields[name] = entry['value']?.toString() ?? '';
        }
      }
    }

    return VoiceTurn(
      draftId: (json['id'] as num).toInt(),
      recordType: (json['targetEntityType'] as String?) ?? '',
      fields: fields,
      missingRequired: ((json['missingRequired'] as List?) ?? const [])
          .map((e) => e.toString())
          .toList(),
      complete: (json['complete'] as bool?) ?? false,
      ambiguous: (json['ambiguous'] as bool?) ?? false,
      nextPrompt: json['nextPrompt'] as String?,
    );
  }

  /// What the user said so far (accumulated transcript), if present.
  String? get transcript {
    final t = fields['transcript'];
    return (t == null || t.trim().isEmpty) ? null : t;
  }

  /// Human-friendly (label, value) pairs worth showing on the confirm card.
  /// Internal/raw fields (transcript, target_entity_type, property_id) are
  /// omitted; `amount`/`total` collapse to a single "Amount".
  List<({String label, String value})> get displayFields {
    const labels = <String, String>{
      'amount': 'Amount',
      'total': 'Amount',
      'vendor_name': 'Vendor',
      'category': 'Category',
      'transaction_date': 'Date',
      'notes': 'Notes',
      'title': 'Title',
      'description': 'Details',
    };

    final out = <({String label, String value})>[];
    final seenLabels = <String>{};
    labels.forEach((key, label) {
      final value = fields[key];
      if (value != null && value.trim().isNotEmpty && seenLabels.add(label)) {
        out.add((label: label, value: _pretty(key, value)));
      }
    });
    return out;
  }

  String _pretty(String key, String value) =>
      (key == 'amount' || key == 'total') ? '\$$value' : value;

  /// One-line plain-language summary, e.g. "Amount: $40 · Category: plumbing".
  String get summary {
    final parts = displayFields;
    if (parts.isEmpty) return recordType.isEmpty ? 'New record' : recordType;
    return parts.map((p) => '${p.label}: ${p.value}').join(' · ');
  }
}

/// Phases of the "Tell me" conversation UI.
enum VoicePhase { idle, listening, thinking, asking, review, saving, done, error }

/// Pure mapping from a server turn to the next UI phase: review when there's
/// nothing left to ask, otherwise keep asking. Kept separate so it's unit-tested.
VoicePhase phaseForTurn(VoiceTurn turn) =>
    turn.complete && !turn.ambiguous ? VoicePhase.review : VoicePhase.asking;
