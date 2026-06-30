// Models for the AI feature: Daily Briefing and portfolio Q&A.

final _assistantActionVerb = RegExp(
  r'\b(log|record|add|create|save|enter|book)\b',
  caseSensitive: false,
);
final _assistantExpenseNoun = RegExp(
  r'\b(expense|receipt|bill|invoice|paid)\b',
  caseSensitive: false,
);

bool looksLikeAssistantActionCommand(String input) {
  final text = input.trim();
  return _assistantActionVerb.hasMatch(text) &&
      _assistantExpenseNoun.hasMatch(text);
}

String formatAssistantMoney(num? amount) {
  if (amount == null || amount.isNaN) {
    return 'unknown amount';
  }
  return '\$${amount.toStringAsFixed(2)}';
}

String assistantActionFieldLabel(String field) {
  switch (field) {
    case 'amount':
      return 'amount';
    case 'description':
      return 'description';
    case 'expense details':
      return 'expense details';
    default:
      return field
          .replaceAllMapped(
            RegExp(r'([a-z])([A-Z])'),
            (m) => '${m.group(1)} ${m.group(2)}',
          )
          .toLowerCase();
  }
}

/// A severity level for a briefing bullet.
enum BulletSeverity { info, warning, critical }

BulletSeverity _parseSeverity(String? raw) {
  switch (raw) {
    case 'critical':
      return BulletSeverity.critical;
    case 'warning':
      return BulletSeverity.warning;
    default:
      return BulletSeverity.info;
  }
}

/// One actionable item in the daily briefing.
class BriefingBullet {
  const BriefingBullet({
    required this.title,
    required this.detail,
    required this.category,
    required this.severity,
    this.entityType,
    this.entityId,
  });

  final String title;
  final String detail;
  final String category;
  final BulletSeverity severity;
  final String? entityType;
  final int? entityId;

  factory BriefingBullet.fromJson(Map<String, dynamic> json) {
    return BriefingBullet(
      title: json['title'] as String? ?? '',
      detail: json['detail'] as String? ?? '',
      category: json['category'] as String? ?? '',
      severity: _parseSeverity(json['severity'] as String?),
      entityType: json['entityType'] as String?,
      entityId: (json['entityId'] as num?)?.toInt(),
    );
  }

  Map<String, dynamic> toJson() => {
    'title': title,
    'detail': detail,
    'category': category,
    'severity': severity.name,
    if (entityType != null) 'entityType': entityType,
    if (entityId != null) 'entityId': entityId,
  };
}

/// Full response from `GET /api/v1/ai/briefing`.
class BriefingResponse {
  const BriefingResponse({
    required this.date,
    required this.generatedAt,
    this.summary,
    required this.llmEnhanced,
    required this.bullets,
  });

  /// Calendar date string, e.g. "2026-05-31".
  final String date;

  /// ISO-8601 timestamp when the briefing was generated.
  final String generatedAt;

  /// Optional AI-composed prose summary (present when [llmEnhanced] is true).
  final String? summary;

  /// True when an LLM was used to enhance the briefing.
  final bool llmEnhanced;

  final List<BriefingBullet> bullets;

  factory BriefingResponse.fromJson(Map<String, dynamic> json) {
    final rawBullets = json['bullets'];
    final bullets = rawBullets is List
        ? rawBullets
              .whereType<Map<String, dynamic>>()
              .map(BriefingBullet.fromJson)
              .toList()
        : <BriefingBullet>[];

    return BriefingResponse(
      date: json['date'] as String? ?? '',
      generatedAt: json['generatedAt'] as String? ?? '',
      summary: json['summary'] as String?,
      llmEnhanced: json['llmEnhanced'] as bool? ?? false,
      bullets: bullets,
    );
  }

  Map<String, dynamic> toJson() => {
    'date': date,
    'generatedAt': generatedAt,
    if (summary != null) 'summary': summary,
    'llmEnhanced': llmEnhanced,
    'bullets': bullets.map((b) => b.toJson()).toList(),
  };
}

/// Optional "text me / email me this answer" delivery options for `POST /api/v1/ai/ask`.
class AskDelivery {
  const AskDelivery({
    this.viaEmail = false,
    this.viaSms = false,
    this.toEmail,
    this.toPhone,
  });

  final bool viaEmail;
  final bool viaSms;

  /// Override email recipient; defaults to the signed-in user's email server-side.
  final String? toEmail;

  /// Override phone recipient; defaults to a configured owner phone server-side.
  final String? toPhone;

  Map<String, dynamic> toJson() => {
    if (viaEmail) 'deliverViaEmail': true,
    if (viaSms) 'deliverViaSms': true,
    if (toEmail != null) 'deliverToEmail': toEmail,
    if (toPhone != null) 'deliverToPhone': toPhone,
  };
}

/// Response from `POST /api/v1/ai/ask`.
class AskResponse {
  const AskResponse({
    required this.answer,
    required this.toolsUsed,
    required this.llmAvailable,
    required this.tokensUsed,
    required this.modelId,
    this.deliveredChannels = const [],
  });

  final String answer;
  final List<String> toolsUsed;
  final bool llmAvailable;
  final int tokensUsed;
  final String modelId;

  /// Channels the answer was queued for delivery on (e.g. `Email`, `Sms`).
  final List<String> deliveredChannels;

  factory AskResponse.fromJson(Map<String, dynamic> json) {
    final rawTools = json['toolsUsed'];
    final tools = rawTools is List
        ? rawTools.whereType<String>().toList()
        : <String>[];
    final rawDelivered = json['deliveredChannels'];
    final delivered = rawDelivered is List
        ? rawDelivered.whereType<String>().toList()
        : <String>[];

    return AskResponse(
      answer: json['answer'] as String? ?? '',
      toolsUsed: tools,
      llmAvailable: json['llmAvailable'] as bool? ?? true,
      tokensUsed: (json['tokensUsed'] as num?)?.toInt() ?? 0,
      modelId: json['modelId'] as String? ?? '',
      deliveredChannels: delivered,
    );
  }

  Map<String, dynamic> toJson() => {
    'answer': answer,
    'toolsUsed': toolsUsed,
    'llmAvailable': llmAvailable,
    'tokensUsed': tokensUsed,
    'modelId': modelId,
    'deliveredChannels': deliveredChannels,
  };
}

/// A single turn in the Q&A conversation history.
class QaTurn {
  const QaTurn({required this.role, required this.content});

  /// Either `'user'` or `'assistant'`.
  final String role;
  final String content;

  factory QaTurn.fromJson(Map<String, dynamic> json) => QaTurn(
    role: json['role'] as String? ?? 'user',
    content: json['content'] as String? ?? '',
  );

  Map<String, dynamic> toJson() => {'role': role, 'content': content};
}

class AssistantExpenseDraft {
  const AssistantExpenseDraft({
    this.propertyId,
    this.propertyName,
    required this.category,
    required this.status,
    required this.description,
    this.amount,
    required this.incurredAt,
    this.paidAt,
    this.notes,
  });

  final int? propertyId;
  final String? propertyName;
  final String category;
  final String status;
  final String description;
  final double? amount;
  final String incurredAt;
  final String? paidAt;
  final String? notes;

  factory AssistantExpenseDraft.fromJson(Map<String, dynamic> json) {
    return AssistantExpenseDraft(
      propertyId: (json['propertyId'] as num?)?.toInt(),
      propertyName: json['propertyName'] as String?,
      category: json['category'] as String? ?? 'Other',
      status: json['status'] as String? ?? 'Paid',
      description: json['description'] as String? ?? '',
      amount: (json['amount'] as num?)?.toDouble(),
      incurredAt: json['incurredAt'] as String? ?? '',
      paidAt: json['paidAt'] as String?,
      notes: json['notes'] as String?,
    );
  }

  Map<String, dynamic> toJson() => {
    if (propertyId != null) 'propertyId': propertyId,
    if (propertyName != null) 'propertyName': propertyName,
    'category': category,
    'status': status,
    'description': description,
    if (amount != null) 'amount': amount,
    'incurredAt': incurredAt,
    if (paidAt != null) 'paidAt': paidAt,
    if (notes != null) 'notes': notes,
  };
}

class AssistantActionDraft {
  const AssistantActionDraft({
    required this.kind,
    required this.risk,
    required this.summary,
    this.expense,
  });

  final String kind;
  final String risk;
  final String summary;
  final AssistantExpenseDraft? expense;

  factory AssistantActionDraft.fromJson(Map<String, dynamic> json) {
    final rawExpense = json['expense'];
    return AssistantActionDraft(
      kind: json['kind'] as String? ?? 'Unsupported',
      risk: json['risk'] as String? ?? 'Medium',
      summary: json['summary'] as String? ?? '',
      expense: rawExpense is Map<String, dynamic>
          ? AssistantExpenseDraft.fromJson(rawExpense)
          : null,
    );
  }

  Map<String, dynamic> toJson() => {
    'kind': kind,
    'risk': risk,
    'summary': summary,
    if (expense != null) 'expense': expense!.toJson(),
  };
}

class AssistantActionDraftResponse {
  const AssistantActionDraftResponse({
    required this.status,
    required this.message,
    required this.requiresWriteMode,
    required this.canExecute,
    this.draft,
    this.missingFields = const [],
  });

  final String status;
  final String message;
  final bool requiresWriteMode;
  final bool canExecute;
  final AssistantActionDraft? draft;
  final List<String> missingFields;

  factory AssistantActionDraftResponse.fromJson(Map<String, dynamic> json) {
    final rawDraft = json['draft'];
    final rawMissing = json['missingFields'];
    return AssistantActionDraftResponse(
      status: json['status'] as String? ?? 'Unsupported',
      message: json['message'] as String? ?? '',
      requiresWriteMode: json['requiresWriteMode'] as bool? ?? false,
      canExecute: json['canExecute'] as bool? ?? false,
      draft: rawDraft is Map<String, dynamic>
          ? AssistantActionDraft.fromJson(rawDraft)
          : null,
      missingFields: rawMissing is List
          ? rawMissing.whereType<String>().toList()
          : const [],
    );
  }
}

class AssistantActionExecuteResponse {
  const AssistantActionExecuteResponse({
    required this.status,
    required this.message,
    required this.kind,
    this.entityId,
    this.detailHref,
  });

  final String status;
  final String message;
  final String kind;
  final int? entityId;
  final String? detailHref;

  factory AssistantActionExecuteResponse.fromJson(Map<String, dynamic> json) {
    return AssistantActionExecuteResponse(
      status: json['status'] as String? ?? 'InvalidDraft',
      message: json['message'] as String? ?? '',
      kind: json['kind'] as String? ?? 'Unsupported',
      entityId: (json['entityId'] as num?)?.toInt(),
      detailHref: json['detailHref'] as String?,
    );
  }
}
