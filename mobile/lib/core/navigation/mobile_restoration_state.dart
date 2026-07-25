import 'dart:convert';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

const _mobileRestorationStateStorageKey = 'rc_mobile_restoration_state_v1';

const _allowedSorts = <String>{
  'propertyName',
  'unitNumber',
  '-updatedAt',
  '-openWorkOrderCount',
  '-marketRent',
  'status',
};
const _allowedStages = <String>{
  'Active',
  'Renewal',
  'Move-Out',
  'Lease',
  'Vacant',
  'Turnover',
};
const _allowedDestinations = <String>{
  'summary',
  'leasing',
  'tenantLease',
  'money',
  'maintenance',
  'documentsHistory',
};
const _allowedAnchors = <String>{
  'listing',
  'applications',
  'agreements',
  'residents',
  'tenantAccount',
  'operatingCosts',
  'workOrders',
  'inspections',
  'recurring',
  'turnover',
  'documents',
  'history',
};

class MobileRestorationState {
  const MobileRestorationState({
    this.authorityUserId,
    this.authorityContextId,
    this.authorityRevision,
    this.query,
    this.sort = 'propertyName',
    this.stage,
    this.skip = 0,
    this.scrollOffset = 0,
    this.unitId,
    this.destination,
    this.anchor,
  });

  final int? authorityUserId;
  final int? authorityContextId;
  final int? authorityRevision;
  final String? query;
  final String sort;
  final String? stage;
  final int skip;
  final double scrollOffset;
  final int? unitId;
  final String? destination;
  final String? anchor;

  bool get hasAuthority =>
      authorityUserId != null &&
      authorityContextId != null &&
      authorityRevision != null;

  bool matchesAuthority({
    required int userId,
    required int contextId,
    required int revision,
  }) =>
      authorityUserId == userId &&
      authorityContextId == contextId &&
      authorityRevision == revision;

  MobileRestorationState bindAuthority({
    required int userId,
    required int contextId,
    required int revision,
  }) => MobileRestorationState(
    authorityUserId: userId,
    authorityContextId: contextId,
    authorityRevision: revision,
    query: query,
    sort: sort,
    stage: stage,
    skip: skip,
    scrollOffset: scrollOffset,
    unitId: unitId,
    destination: destination,
    anchor: anchor,
  );

  MobileRestorationState updateCollection({
    String? query,
    required String sort,
    String? stage,
    required int skip,
    required double scrollOffset,
  }) => MobileRestorationState.fromPrimitives({
    ...toPrimitives(),
    'query': query,
    'sort': sort,
    'stage': stage,
    'skip': skip,
    'scrollOffset': scrollOffset,
  });

  MobileRestorationState updateUnit({
    required int unitId,
    required String destination,
    String? anchor,
  }) => MobileRestorationState.fromPrimitives({
    ...toPrimitives(),
    'unitId': unitId,
    'destination': destination,
    'anchor': anchor,
  });

  MobileRestorationState clearRichState() => const MobileRestorationState();

  Map<String, Object?> toPrimitives() => <String, Object?>{
    'authorityUserId': authorityUserId,
    'authorityContextId': authorityContextId,
    'authorityRevision': authorityRevision,
    'query': query,
    'sort': sort,
    'stage': stage,
    'skip': skip,
    'scrollOffset': scrollOffset,
    'unitId': unitId,
    'destination': destination,
    'anchor': anchor,
  };

  factory MobileRestorationState.fromPrimitives(Object? raw) {
    final values = raw is Map ? raw : const <Object?, Object?>{};
    final rawQuery = values['query'] is String
        ? (values['query'] as String).trim()
        : '';
    final query = rawQuery.isEmpty
        ? null
        : rawQuery.substring(0, rawQuery.length.clamp(0, 100));
    final rawSort = values['sort'] is String ? values['sort'] as String : '';
    final sort = _allowedSorts.contains(rawSort) ? rawSort : 'propertyName';
    final rawStage = values['stage'] is String
        ? values['stage'] as String
        : null;
    final stage = _allowedStages.contains(rawStage) ? rawStage : null;
    final rawDestination = values['destination'] is String
        ? values['destination'] as String
        : null;
    final destination = _allowedDestinations.contains(rawDestination)
        ? rawDestination
        : null;
    final rawAnchor = values['anchor'] is String
        ? values['anchor'] as String
        : null;
    final anchor = _allowedAnchors.contains(rawAnchor) ? rawAnchor : null;

    return MobileRestorationState(
      authorityUserId: _positiveInt(values['authorityUserId']),
      authorityContextId: _positiveInt(values['authorityContextId']),
      authorityRevision: _nonNegativeInt(values['authorityRevision']),
      query: query,
      sort: sort,
      stage: stage,
      skip: _nonNegativeInt(values['skip'])?.clamp(0, 100000) ?? 0,
      scrollOffset:
          (values['scrollOffset'] is num
                  ? (values['scrollOffset'] as num).toDouble()
                  : 0.0)
              .clamp(0, 1000000),
      unitId: _positiveInt(values['unitId']),
      destination: destination,
      anchor: destination == null ? null : anchor,
    );
  }

  static int? _positiveInt(Object? value) {
    final parsed = value is int ? value : null;
    return parsed != null && parsed > 0 ? parsed : null;
  }

  static int? _nonNegativeInt(Object? value) {
    final parsed = value is int ? value : null;
    return parsed != null && parsed >= 0 ? parsed : null;
  }
}

class RestorableMobileRestorationState
    extends RestorableValue<MobileRestorationState> {
  @override
  MobileRestorationState createDefaultValue() => const MobileRestorationState();

  @override
  void didUpdateValue(MobileRestorationState? oldValue) => notifyListeners();

  @override
  Object? toPrimitives() => value.toPrimitives();

  @override
  MobileRestorationState fromPrimitives(Object? data) =>
      MobileRestorationState.fromPrimitives(data);
}

abstract interface class MobileRestorationStateStore {
  Future<MobileRestorationStateLoadResult> load();
  Future<void> save(MobileRestorationState state);
  Future<void> clear();
}

sealed class MobileRestorationStateLoadResult {
  const MobileRestorationStateLoadResult();
}

final class MobileRestorationStateLoadMissing
    extends MobileRestorationStateLoadResult {
  const MobileRestorationStateLoadMissing();
}

final class MobileRestorationStateLoadSuccess
    extends MobileRestorationStateLoadResult {
  const MobileRestorationStateLoadSuccess(this.state);

  final MobileRestorationState state;
}

enum MobileRestorationStateLoadInvalidReason { malformed, unscoped }

final class MobileRestorationStateLoadInvalid
    extends MobileRestorationStateLoadResult {
  const MobileRestorationStateLoadInvalid(this.reason);

  final MobileRestorationStateLoadInvalidReason reason;
}

class MobileRestorationStateJsonStore implements MobileRestorationStateStore {
  MobileRestorationStateJsonStore({
    required Future<String?> Function() read,
    required Future<void> Function(String value) write,
    required Future<void> Function() delete,
  }) : _read = read,
       _write = write,
       _delete = delete;

  final Future<String?> Function() _read;
  final Future<void> Function(String value) _write;
  final Future<void> Function() _delete;

  @override
  Future<MobileRestorationStateLoadResult> load() async {
    final raw = await _read();
    if (raw == null || raw.isEmpty) {
      return const MobileRestorationStateLoadMissing();
    }
    try {
      final state = MobileRestorationState.fromPrimitives(jsonDecode(raw));
      if (!state.hasAuthority) {
        return const MobileRestorationStateLoadInvalid(
          MobileRestorationStateLoadInvalidReason.unscoped,
        );
      }
      return MobileRestorationStateLoadSuccess(state);
    } on FormatException {
      return const MobileRestorationStateLoadInvalid(
        MobileRestorationStateLoadInvalidReason.malformed,
      );
    }
  }

  @override
  Future<void> save(MobileRestorationState state) {
    if (!state.hasAuthority) return clear();
    return _write(jsonEncode(state.toPrimitives()));
  }

  @override
  Future<void> clear() => _delete();
}

class SecureMobileRestorationStateStore
    extends MobileRestorationStateJsonStore {
  SecureMobileRestorationStateStore(FlutterSecureStorage storage)
    : super(
        read: () => storage.read(key: _mobileRestorationStateStorageKey),
        write: (value) =>
            storage.write(key: _mobileRestorationStateStorageKey, value: value),
        delete: () => storage.delete(key: _mobileRestorationStateStorageKey),
      );
}

final mobileRestorationStateStoreProvider =
    Provider<MobileRestorationStateStore>((ref) {
      const storage = FlutterSecureStorage();
      return SecureMobileRestorationStateStore(storage);
    });

class MobileRestorationScope extends InheritedWidget {
  const MobileRestorationScope({
    super.key,
    required RestorableMobileRestorationState controller,
    required super.child,
  }) : controller = controller;

  final RestorableMobileRestorationState controller;

  static RestorableMobileRestorationState? maybeOf(BuildContext context) =>
      context
          .dependOnInheritedWidgetOfExactType<MobileRestorationScope>()
          ?.controller;

  @override
  bool updateShouldNotify(MobileRestorationScope oldWidget) =>
      controller != oldWidget.controller;
}
