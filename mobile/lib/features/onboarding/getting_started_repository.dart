import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'getting_started_tasks.dart';

class GettingStartedRepository {
  GettingStartedRepository(this._dio);

  final Dio _dio;

  Future<GettingStartedSignals> signals() async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/portfolios/getting-started',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }

      return GettingStartedSignals(
        propertyCount: _readInt(data, 'propertyCount'),
        unitCount: _readInt(data, 'unitCount'),
        tenantCount: _readInt(data, 'tenantCount'),
        leaseCount: _readInt(data, 'leaseCount'),
        hasNotificationEmail: data['hasNotificationEmail'] as bool? ?? false,
        hasTexting: data['hasTexting'] as bool? ?? false,
        hasAutomations: data['hasAutomations'] as bool? ?? false,
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  static int _readInt(Map<String, dynamic> data, String key) =>
      (data[key] as num?)?.toInt() ?? 0;
}

final gettingStartedRepositoryProvider = Provider<GettingStartedRepository>((
  ref,
) {
  return GettingStartedRepository(ref.watch(dioProvider));
});
