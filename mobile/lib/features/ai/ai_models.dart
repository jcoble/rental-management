// Models for the AI feature: Daily Briefing and portfolio Q&A.

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
