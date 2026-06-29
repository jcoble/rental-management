import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';

// ── Models ────────────────────────────────────────────────────────────────────

/// One row of the notification-type × channel matrix.
///
/// Mirrors the API's `channelPreferences[]` entries. `notificationType` is one
/// of: RentCharge, LateFee, LeaseExpiry, RentConfirmation, NoticeAutopilot,
/// DailyBriefing (serialized as string names).
class ChannelPreference {
  const ChannelPreference({
    required this.notificationType,
    required this.enableInApp,
    required this.enableEmail,
    required this.enableSms,
    required this.enablePush,
  });

  final String notificationType;
  final bool enableInApp;
  final bool enableEmail;
  final bool enableSms;
  final bool enablePush;

  /// Friendly, non-technical label for the notification type.
  String get label {
    switch (notificationType) {
      case 'RentCharge':
        return 'Rent charges';
      case 'LateFee':
        return 'Late fees';
      case 'LeaseExpiry':
        return 'Lease expiry';
      case 'RentConfirmation':
        return 'Rent confirmations';
      case 'NoticeAutopilot':
        return 'Notice autopilot';
      case 'DailyBriefing':
        return 'Daily briefing';
      default:
        return notificationType;
    }
  }

  ChannelPreference copyWith({
    bool? enableInApp,
    bool? enableEmail,
    bool? enableSms,
    bool? enablePush,
  }) {
    return ChannelPreference(
      notificationType: notificationType,
      enableInApp: enableInApp ?? this.enableInApp,
      enableEmail: enableEmail ?? this.enableEmail,
      enableSms: enableSms ?? this.enableSms,
      enablePush: enablePush ?? this.enablePush,
    );
  }

  factory ChannelPreference.fromJson(Map<String, dynamic> json) {
    return ChannelPreference(
      notificationType: json['notificationType'] as String? ?? '',
      enableInApp: json['enableInApp'] as bool? ?? false,
      enableEmail: json['enableEmail'] as bool? ?? false,
      enableSms: json['enableSms'] as bool? ?? false,
      enablePush: json['enablePush'] as bool? ?? false,
    );
  }

  Map<String, dynamic> toJson() => {
        'notificationType': notificationType,
        'enableInApp': enableInApp,
        'enableEmail': enableEmail,
        'enableSms': enableSms,
        'enablePush': enablePush,
      };
}

/// Full notification settings payload for GET/PUT `/notifications/settings`.
///
/// Kept faithful to the API shape so the PUT round-trips the full document.
/// SignalWire secret inputs are not edited on mobile; the provider-status
/// fields are preserved on save so we never clobber the server's config.
class NotificationSettings {
  const NotificationSettings({
    required this.enableRentCharges,
    required this.enableLateFees,
    required this.enableLeaseExpiryReminders,
    required this.notifyTenants,
    required this.autoSendRentReminder,
    required this.autoSendRenewal,
    required this.autoSendMonthToMonth,
    required this.autoSendMoveOut,
    required this.autoSendLateRent,
    required this.rentChargeLeadDays,
    required this.lateFeeGraceDays,
    required this.leaseExpiryReminderDays,
    required this.enableDailyBriefingMessages,
    required this.dailyBriefingSendHourLocal,
    required this.dailyBriefingIncludeEmpty,
    required this.dailyBriefingSmsRecipients,
    required this.dailyBriefingEmailRecipients,
    required this.signalWireProjectId,
    required this.signalWireTokenSet,
    required this.signalWireSpaceUrl,
    required this.signalWireFromNumber,
    required this.channelPreferences,
  });

  final bool enableRentCharges;
  final bool enableLateFees;
  final bool enableLeaseExpiryReminders;
  final bool notifyTenants;

  // Per-notice-type send mode: true = auto-send, false = ask first (draft).
  final bool autoSendRentReminder;
  final bool autoSendRenewal;
  final bool autoSendMonthToMonth;
  final bool autoSendMoveOut;
  final bool autoSendLateRent;

  final int rentChargeLeadDays;
  final int lateFeeGraceDays;
  final int leaseExpiryReminderDays;

  final bool enableDailyBriefingMessages;
  final int dailyBriefingSendHourLocal;
  final bool dailyBriefingIncludeEmpty;
  final List<String> dailyBriefingSmsRecipients;
  final List<String> dailyBriefingEmailRecipients;

  // SignalWire provider config — read-only on mobile, preserved on PUT.
  final String? signalWireProjectId;
  final bool signalWireTokenSet;
  final String? signalWireSpaceUrl;
  final String? signalWireFromNumber;

  final List<ChannelPreference> channelPreferences;

  /// True when SignalWire looks fully configured (used for a read-only status line).
  bool get signalWireConfigured =>
      signalWireTokenSet &&
      (signalWireProjectId?.isNotEmpty ?? false) &&
      (signalWireFromNumber?.isNotEmpty ?? false);

  NotificationSettings copyWith({
    bool? enableRentCharges,
    bool? enableLateFees,
    bool? enableLeaseExpiryReminders,
    bool? notifyTenants,
    bool? autoSendRentReminder,
    bool? autoSendRenewal,
    bool? autoSendMonthToMonth,
    bool? autoSendMoveOut,
    bool? autoSendLateRent,
    int? rentChargeLeadDays,
    int? lateFeeGraceDays,
    int? leaseExpiryReminderDays,
    bool? enableDailyBriefingMessages,
    int? dailyBriefingSendHourLocal,
    bool? dailyBriefingIncludeEmpty,
    List<String>? dailyBriefingSmsRecipients,
    List<String>? dailyBriefingEmailRecipients,
    List<ChannelPreference>? channelPreferences,
  }) {
    return NotificationSettings(
      enableRentCharges: enableRentCharges ?? this.enableRentCharges,
      enableLateFees: enableLateFees ?? this.enableLateFees,
      enableLeaseExpiryReminders:
          enableLeaseExpiryReminders ?? this.enableLeaseExpiryReminders,
      notifyTenants: notifyTenants ?? this.notifyTenants,
      autoSendRentReminder: autoSendRentReminder ?? this.autoSendRentReminder,
      autoSendRenewal: autoSendRenewal ?? this.autoSendRenewal,
      autoSendMonthToMonth: autoSendMonthToMonth ?? this.autoSendMonthToMonth,
      autoSendMoveOut: autoSendMoveOut ?? this.autoSendMoveOut,
      autoSendLateRent: autoSendLateRent ?? this.autoSendLateRent,
      rentChargeLeadDays: rentChargeLeadDays ?? this.rentChargeLeadDays,
      lateFeeGraceDays: lateFeeGraceDays ?? this.lateFeeGraceDays,
      leaseExpiryReminderDays:
          leaseExpiryReminderDays ?? this.leaseExpiryReminderDays,
      enableDailyBriefingMessages:
          enableDailyBriefingMessages ?? this.enableDailyBriefingMessages,
      dailyBriefingSendHourLocal:
          dailyBriefingSendHourLocal ?? this.dailyBriefingSendHourLocal,
      dailyBriefingIncludeEmpty:
          dailyBriefingIncludeEmpty ?? this.dailyBriefingIncludeEmpty,
      dailyBriefingSmsRecipients:
          dailyBriefingSmsRecipients ?? this.dailyBriefingSmsRecipients,
      dailyBriefingEmailRecipients:
          dailyBriefingEmailRecipients ?? this.dailyBriefingEmailRecipients,
      // Provider config is immutable on mobile — always carried through.
      signalWireProjectId: signalWireProjectId,
      signalWireTokenSet: signalWireTokenSet,
      signalWireSpaceUrl: signalWireSpaceUrl,
      signalWireFromNumber: signalWireFromNumber,
      channelPreferences: channelPreferences ?? this.channelPreferences,
    );
  }

  /// Replaces a single channel-preference row, matched by notificationType.
  NotificationSettings withPreference(ChannelPreference updated) {
    return copyWith(
      channelPreferences: [
        for (final p in channelPreferences)
          if (p.notificationType == updated.notificationType) updated else p,
      ],
    );
  }

  factory NotificationSettings.fromJson(Map<String, dynamic> json) {
    List<String> strings(dynamic v) =>
        (v as List<dynamic>?)?.whereType<String>().toList() ?? const [];

    final prefs = (json['channelPreferences'] as List<dynamic>?)
            ?.whereType<Map<String, dynamic>>()
            .map(ChannelPreference.fromJson)
            .toList() ??
        const <ChannelPreference>[];

    return NotificationSettings(
      enableRentCharges: json['enableRentCharges'] as bool? ?? false,
      enableLateFees: json['enableLateFees'] as bool? ?? false,
      enableLeaseExpiryReminders:
          json['enableLeaseExpiryReminders'] as bool? ?? false,
      notifyTenants: json['notifyTenants'] as bool? ?? false,
      autoSendRentReminder: json['autoSendRentReminder'] as bool? ?? false,
      autoSendRenewal: json['autoSendRenewal'] as bool? ?? false,
      autoSendMonthToMonth: json['autoSendMonthToMonth'] as bool? ?? false,
      autoSendMoveOut: json['autoSendMoveOut'] as bool? ?? false,
      autoSendLateRent: json['autoSendLateRent'] as bool? ?? false,
      rentChargeLeadDays: (json['rentChargeLeadDays'] as num?)?.toInt() ?? 0,
      lateFeeGraceDays: (json['lateFeeGraceDays'] as num?)?.toInt() ?? 0,
      leaseExpiryReminderDays:
          (json['leaseExpiryReminderDays'] as num?)?.toInt() ?? 0,
      enableDailyBriefingMessages:
          json['enableDailyBriefingMessages'] as bool? ?? false,
      dailyBriefingSendHourLocal:
          (json['dailyBriefingSendHourLocal'] as num?)?.toInt() ?? 8,
      dailyBriefingIncludeEmpty:
          json['dailyBriefingIncludeEmpty'] as bool? ?? false,
      dailyBriefingSmsRecipients: strings(json['dailyBriefingSmsRecipients']),
      dailyBriefingEmailRecipients:
          strings(json['dailyBriefingEmailRecipients']),
      signalWireProjectId: json['signalWireProjectId'] as String?,
      signalWireTokenSet: json['signalWireTokenSet'] as bool? ?? false,
      signalWireSpaceUrl: json['signalWireSpaceUrl'] as String?,
      signalWireFromNumber: json['signalWireFromNumber'] as String?,
      channelPreferences: prefs,
    );
  }

  /// Serializes the full document for PUT. The SignalWire token is never sent
  /// from mobile (`signalWireToken: null`); `signalWireTokenSet` tells the
  /// server to keep its existing token.
  Map<String, dynamic> toJson() => {
        'enableRentCharges': enableRentCharges,
        'enableLateFees': enableLateFees,
        'enableLeaseExpiryReminders': enableLeaseExpiryReminders,
        'notifyTenants': notifyTenants,
        'autoSendRentReminder': autoSendRentReminder,
        'autoSendRenewal': autoSendRenewal,
        'autoSendMonthToMonth': autoSendMonthToMonth,
        'autoSendMoveOut': autoSendMoveOut,
        'autoSendLateRent': autoSendLateRent,
        'rentChargeLeadDays': rentChargeLeadDays,
        'lateFeeGraceDays': lateFeeGraceDays,
        'leaseExpiryReminderDays': leaseExpiryReminderDays,
        'enableDailyBriefingMessages': enableDailyBriefingMessages,
        'dailyBriefingSendHourLocal': dailyBriefingSendHourLocal,
        'dailyBriefingIncludeEmpty': dailyBriefingIncludeEmpty,
        'dailyBriefingSmsRecipients': dailyBriefingSmsRecipients,
        'dailyBriefingEmailRecipients': dailyBriefingEmailRecipients,
        'signalWireProjectId': signalWireProjectId,
        'signalWireTokenSet': signalWireTokenSet,
        'signalWireToken': null,
        'signalWireSpaceUrl': signalWireSpaceUrl,
        'signalWireFromNumber': signalWireFromNumber,
        'channelPreferences':
            channelPreferences.map((p) => p.toJson()).toList(),
      };
}

// ── Repository ────────────────────────────────────────────────────────────────

/// Notification settings API calls.
///
/// Endpoints (portfolio-scoped, camelCase JSON, enums as string names):
///   GET /notifications/settings  → full [NotificationSettings] matrix
///   PUT /notifications/settings  { full document } → updated settings
class NotificationSettingsRepository {
  NotificationSettingsRepository(this._dio);

  final Dio _dio;

  Future<NotificationSettings> get() async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/notifications/settings');
      return NotificationSettings.fromJson(response.data ?? const {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<NotificationSettings> update(NotificationSettings settings) async {
    try {
      final response = await _dio.put<Map<String, dynamic>>(
        '/notifications/settings',
        data: settings.toJson(),
      );
      // PUT echoes the saved document; fall back to what we sent if empty.
      final data = response.data;
      if (data == null || data.isEmpty) return settings;
      return NotificationSettings.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final notificationSettingsRepositoryProvider =
    Provider<NotificationSettingsRepository>((ref) {
  return NotificationSettingsRepository(ref.watch(dioProvider));
});

/// Loads + holds the editable settings document.
///
/// The screen edits a local working copy (via [update]/[patch]) and persists it
/// with [save]; on success the server's echoed document replaces local state.
class NotificationSettingsNotifier
    extends Notifier<AsyncValue<NotificationSettings>> {
  @override
  AsyncValue<NotificationSettings> build() => const AsyncValue.loading();

  NotificationSettingsRepository get _repo =>
      ref.read(notificationSettingsRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      state = AsyncValue.data(await _repo.get());
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();

  /// Applies a local edit to the working copy without saving.
  void patch(
    NotificationSettings Function(NotificationSettings current) edit,
  ) {
    final current = state.value;
    if (current == null) return;
    state = AsyncValue.data(edit(current));
  }

  /// Persists the current working copy. Returns true on success.
  Future<bool> save() async {
    final current = state.value;
    if (current == null) return false;
    try {
      final saved = await _repo.update(current);
      state = AsyncValue.data(saved);
      return true;
    } on ApiException {
      rethrow;
    }
  }
}

final notificationSettingsProvider = NotifierProvider<
    NotificationSettingsNotifier, AsyncValue<NotificationSettings>>(
  NotificationSettingsNotifier.new,
);
