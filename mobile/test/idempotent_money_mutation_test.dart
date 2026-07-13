import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/api/idempotent_mutation.dart';

void main() {
  test(
    'ambiguous retry retains the operation key and success clears it',
    () async {
      const scope = 'idempotency-test:retry';
      IdempotentMutation.cancel(scope);
      late String firstKey;

      await expectLater(
        IdempotentMutation.run<void>(scope, (key) async {
          firstKey = key;
          throw StateError('connection closed before the response arrived');
        }),
        throwsStateError,
      );

      late String retryKey;
      await IdempotentMutation.run<void>(scope, (key) async {
        retryKey = key;
      });
      expect(retryKey, firstKey);

      late String nextKey;
      await IdempotentMutation.run<void>(scope, (key) async {
        nextKey = key;
      });
      expect(nextKey, isNot(firstKey));
    },
  );

  test('explicit cancellation abandons the retained operation key', () async {
    const scope = 'idempotency-test:cancel';
    IdempotentMutation.cancel(scope);
    late String abandonedKey;

    await expectLater(
      IdempotentMutation.run<void>(scope, (key) async {
        abandonedKey = key;
        throw StateError('ambiguous failure');
      }),
      throwsStateError,
    );
    IdempotentMutation.cancel(scope);

    late String replacementKey;
    await IdempotentMutation.run<void>(scope, (key) async {
      replacementKey = key;
    });
    expect(replacementKey, isNot(abandonedKey));
  });

  test('money repositories send the retained Idempotency-Key', () {
    final repositorySources = {
      'money': File(
        'lib/features/money/money_repository.dart',
      ).readAsStringSync(),
      'owner distributions': File(
        'lib/features/owner_reports/owner_reports_repository.dart',
      ).readAsStringSync(),
      'loans': File(
        'lib/features/properties/property_loans_repository.dart',
      ).readAsStringSync(),
    };

    for (final MapEntry(key: name, value: source)
        in repositorySources.entries) {
      expect(
        source,
        contains('IdempotentMutation.run('),
        reason: '$name must use the retained-operation helper',
      );
      expect(
        source,
        contains("Options(headers: {'Idempotency-Key': key})"),
        reason: '$name must send the retained key to the API',
      );
    }
  });
}
