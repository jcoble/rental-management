import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/api/dio_client.dart';
import 'package:rental_command/core/time/app_clock.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

void main() {
  test('uses the simulation clock when the endpoint is available', () async {
    final dio = Dio()..httpClientAdapter = _ClockAdapter(statusCode: 200);
    final container = ProviderContainer(
      overrides: [dioProvider.overrideWithValue(dio)],
    );
    addTearDown(container.dispose);

    final now = await container.read(appNowProvider.future);

    expect(now, DateTime.utc(2027, 1, 2, 14, 30));
    expect(now.day, 2);
  });

  test('uses device time only when the simulation route is absent', () async {
    final before = DateTime.now();
    final dio = Dio()..httpClientAdapter = _ClockAdapter(statusCode: 404);
    final container = ProviderContainer(
      overrides: [dioProvider.overrideWithValue(dio)],
    );
    addTearDown(container.dispose);

    final now = await container.read(appNowProvider.future);

    expect(now.isBefore(before), isFalse);
    expect(
      now.isAfter(DateTime.now().add(const Duration(seconds: 1))),
      isFalse,
    );
  });

  test('does not hide a simulation clock server failure', () async {
    final dio = Dio()..httpClientAdapter = _ClockAdapter(statusCode: 500);
    final container = ProviderContainer(
      overrides: [dioProvider.overrideWithValue(dio)],
    );
    addTearDown(container.dispose);

    await expectLater(
      container.read(appNowProvider.future),
      throwsA(isA<DioException>()),
    );
  });
}

class _ClockAdapter implements HttpClientAdapter {
  _ClockAdapter({required this.statusCode});

  final int statusCode;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    expect(options.path, '/dev/clock');
    if (statusCode == 200) {
      return ResponseBody.fromString(
        '{"simNowUtc":"2027-01-02T14:30:00Z","mode":"Offset","offsetSeconds":1}',
        200,
        headers: {
          Headers.contentTypeHeader: ['application/json'],
        },
      );
    }
    return ResponseBody.fromString(
      '{}',
      statusCode,
      headers: {
        Headers.contentTypeHeader: ['application/json'],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}
