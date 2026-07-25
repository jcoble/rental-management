import 'dart:async';
import 'dart:convert';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/navigation/mobile_restoration_state.dart';

void main() {
  test('round trips only bounded primitive Unit navigation state', () {
    final state = const MobileRestorationState()
        .bindAuthority(userId: 7, contextId: 11, revision: 3)
        .updateCollection(
          query: 'Hilliard',
          sort: '-updatedAt',
          stage: 'Active',
          skip: 40,
          scrollOffset: 612.5,
        )
        .updateUnit(
          unitId: 19,
          destination: 'tenantLease',
          anchor: 'residents',
        );

    final encoded = jsonEncode(state.toPrimitives());
    final restored = MobileRestorationState.fromPrimitives(jsonDecode(encoded));

    expect(
      restored.matchesAuthority(userId: 7, contextId: 11, revision: 3),
      isTrue,
    );
    expect(restored.query, 'Hilliard');
    expect(restored.sort, '-updatedAt');
    expect(restored.stage, 'Active');
    expect(restored.skip, 40);
    expect(restored.scrollOffset, 612.5);
    expect(restored.unitId, 19);
    expect(restored.destination, 'tenantLease');
    expect(restored.anchor, 'residents');
    expect(encoded, isNot(contains('token')));
    expect(encoded, isNot(contains('secret')));
    expect(encoded, isNot(contains('dto')));
    expect(encoded, isNot(contains('record')));
  });

  test('rejects unbounded or untyped restoration values', () {
    final restored = MobileRestorationState.fromPrimitives({
      'query': 'x' * 140,
      'sort': 'client-computed',
      'stage': 'Injected',
      'skip': -40,
      'scrollOffset': double.infinity,
      'unitId': -1,
      'destination': 'retired-tab',
      'anchor': 'hidden-record',
      'accessToken': 'must-not-survive',
    });

    expect(restored.query, hasLength(100));
    expect(restored.sort, 'propertyName');
    expect(restored.stage, isNull);
    expect(restored.skip, 0);
    expect(restored.scrollOffset, 1000000);
    expect(restored.unitId, isNull);
    expect(restored.destination, isNull);
    expect(restored.anchor, isNull);
    expect(restored.toPrimitives().keys, isNot(contains('accessToken')));
  });

  test('rejects fractional authority and identifier values', () {
    final restored = MobileRestorationState.fromPrimitives({
      'authorityUserId': 7.9,
      'authorityContextId': 11.5,
      'authorityRevision': 3.1,
      'skip': 20.9,
      'unitId': 19.9,
      'destination': 'documentsHistory',
      'anchor': 'documents',
    });

    expect(restored.hasAuthority, isFalse);
    expect(
      restored.matchesAuthority(userId: 7, contextId: 11, revision: 3),
      isFalse,
    );
    expect(restored.skip, 0);
    expect(restored.unitId, isNull);
    expect(restored.destination, 'documentsHistory');
    expect(restored.anchor, 'documents');
  });

  test('authority mismatch discards collection and Unit restoration state', () {
    final state = const MobileRestorationState()
        .bindAuthority(userId: 7, contextId: 11, revision: 3)
        .updateUnit(unitId: 19, destination: 'money', anchor: 'operatingCosts');

    expect(
      state.matchesAuthority(userId: 7, contextId: 11, revision: 4),
      isFalse,
    );
    expect(
      state.clearRichState().toPrimitives(),
      const MobileRestorationState().toPrimitives(),
    );
  });

  test('restores Unit Inspection and Recurring maintenance anchors', () {
    for (final anchor in ['inspections', 'recurring']) {
      final state = const MobileRestorationState()
          .bindAuthority(userId: 7, contextId: 11, revision: 3)
          .updateUnit(
            unitId: 19,
            destination: 'maintenance',
            anchor: anchor,
          );
      final restored = MobileRestorationState.fromPrimitives(
        jsonDecode(jsonEncode(state.toPrimitives())),
      );

      expect(restored.destination, 'maintenance');
      expect(restored.anchor, anchor);
      expect(jsonEncode(restored.toPrimitives()), isNot(contains('token')));
      expect(jsonEncode(restored.toPrimitives()), isNot(contains('record')));
    }
  });

  test(
    'durable store round trips only authority-scoped primitive state',
    () async {
      String? stored;
      final store = MobileRestorationStateJsonStore(
        read: () async => stored,
        write: (value) async => stored = value,
        delete: () async => stored = null,
      );
      final state = const MobileRestorationState()
          .bindAuthority(userId: 7, contextId: 11, revision: 3)
          .updateCollection(
            query: 'Hilliard',
            sort: '-marketRent',
            stage: 'Active',
            skip: 20,
            scrollOffset: 418,
          )
          .updateUnit(
            unitId: 19,
            destination: 'documentsHistory',
            anchor: 'documents',
          );

      await store.save(state);

      expect(stored, isNotNull);
      expect(stored, isNot(contains('token')));
      expect(stored, isNot(contains('secret')));
      expect(stored, isNot(contains('dto')));
      expect(stored, isNot(contains('record')));

      final result = await store.load();
      expect(result, isA<MobileRestorationStateLoadSuccess>());
      final restored = (result as MobileRestorationStateLoadSuccess).state;

      expect(
        restored.matchesAuthority(userId: 7, contextId: 11, revision: 3),
        isTrue,
      );
      expect(restored.query, 'Hilliard');
      expect(restored.sort, '-marketRent');
      expect(restored.stage, 'Active');
      expect(restored.skip, 20);
      expect(restored.scrollOffset, 418);
      expect(restored.unitId, 19);
      expect(restored.destination, 'documentsHistory');
      expect(restored.anchor, 'documents');
    },
  );

  test(
    'durable store save clears unscoped state and load reports malformed state',
    () async {
      String? stored = '{"unitId":19,"destination":"documentsHistory"}';
      final store = MobileRestorationStateJsonStore(
        read: () async => stored,
        write: (value) async => stored = value,
        delete: () async => stored = null,
      );

      await store.save(
        const MobileRestorationState().updateUnit(
          unitId: 19,
          destination: 'documentsHistory',
        ),
      );

      expect(stored, isNull);

      stored = '{not-json';
      final result = await store.load();

      expect(result, isA<MobileRestorationStateLoadInvalid>());
      expect(
        (result as MobileRestorationStateLoadInvalid).reason,
        MobileRestorationStateLoadInvalidReason.malformed,
      );
      expect(stored, '{not-json');
    },
  );

  test(
    'delayed invalid durable load does not delete newer saved state',
    () async {
      final obsoleteRead = Completer<String?>();
      String? stored = jsonEncode({
        'authorityUserId': 7,
        'authorityContextId': 11,
        'authorityRevision': 3,
        'query': 'Hilliard',
        'sort': '-marketRent',
        'stage': 'Active',
        'skip': 20,
        'scrollOffset': 418,
        'unitId': 19,
        'destination': 'documentsHistory',
        'anchor': 'documents',
      });
      final store = MobileRestorationStateJsonStore(
        read: () => obsoleteRead.future,
        write: (value) async => stored = value,
        delete: () async => stored = null,
      );

      final load = store.load();
      await store.save(
        const MobileRestorationState()
            .bindAuthority(userId: 8, contextId: 12, revision: 4)
            .updateUnit(
              unitId: 20,
              destination: 'documentsHistory',
              anchor: 'documents',
            ),
      );
      final newerStored = stored;
      obsoleteRead.complete(
        jsonEncode({
          'authorityUserId': 7.9,
          'authorityContextId': 11.5,
          'authorityRevision': 3.1,
          'unitId': 19,
          'destination': 'documentsHistory',
          'anchor': 'documents',
        }),
      );

      final result = await load;

      expect(result, isA<MobileRestorationStateLoadInvalid>());
      expect(
        (result as MobileRestorationStateLoadInvalid).reason,
        MobileRestorationStateLoadInvalidReason.unscoped,
      );
      expect(stored, newerStored);
    },
  );

  testWidgets('Unit descendant can bind restoration during build', (
    tester,
  ) async {
    final key = GlobalKey<_RestorationBindingHarnessState>();

    await tester.pumpWidget(_RestorationBindingHarness(key: key));

    expect(tester.takeException(), isNull);
    expect(key.currentState!.controller.value.unitId, 19);
    expect(key.currentState!.controller.value.destination, 'documentsHistory');
    expect(key.currentState!.controller.value.anchor, 'documents');
  });
}

class _RestorationBindingHarness extends StatefulWidget {
  const _RestorationBindingHarness({super.key});

  @override
  State<_RestorationBindingHarness> createState() =>
      _RestorationBindingHarnessState();
}

class _RestorationBindingHarnessState extends State<_RestorationBindingHarness>
    with RestorationMixin {
  final controller = RestorableMobileRestorationState();

  @override
  String? get restorationId => 'home-shell';

  @override
  void restoreState(RestorationBucket? oldBucket, bool initialRestore) {
    registerForRestoration(controller, 'unit-navigation');
  }

  @override
  void dispose() {
    controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return MobileRestorationScope(
      controller: controller,
      child: Builder(
        builder: (context) {
          final restoration = MobileRestorationScope.maybeOf(context)!;
          restoration.value = restoration.value
              .bindAuthority(userId: 7, contextId: 11, revision: 3)
              .updateUnit(
                unitId: 19,
                destination: 'documentsHistory',
                anchor: 'documents',
              );
          return const SizedBox.shrink();
        },
      ),
    );
  }
}
