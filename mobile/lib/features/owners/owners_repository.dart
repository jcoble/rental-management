import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'owners_models.dart';

class OwnersRepository {
  OwnersRepository(this._dio);

  final Dio _dio;

  Future<OwnerEntityPage> listPage([
    OwnerListQuery query = const OwnerListQuery(),
  ]) async {
    final parameters = <String, dynamic>{
      'skip': query.skip,
      'take': query.take,
      'search': query.search,
      'sort': query.sort,
      'ownerEntityType': query.ownerEntityType?.wireName,
    }..removeWhere((_, value) => value == null || value == '');

    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/owner-entities/page',
        queryParameters: parameters,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return OwnerEntityPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<OwnerEntity> getOwner(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/owner-entities/$id',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return OwnerEntity.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<OwnerEntity> createOwner(Map<String, dynamic> data) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/owner-entities',
        data: data,
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return OwnerEntity.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<OwnerEntity> updateOwner(int id, Map<String, dynamic> data) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/owner-entities/$id',
        data: data,
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return OwnerEntity.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> deleteOwner(
    int id, {
    bool clearPropertyAssignments = false,
  }) async {
    try {
      await _dio.delete<dynamic>(
        '/owner-entities/$id',
        queryParameters: {'clearPropertyAssignments': clearPropertyAssignments},
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final ownersRepositoryProvider = Provider<OwnersRepository>((ref) {
  return OwnersRepository(ref.watch(dioProvider));
});

final ownersPageProvider = FutureProvider.autoDispose
    .family<OwnerEntityPage, OwnerListQuery>((ref, query) {
      return ref.watch(ownersRepositoryProvider).listPage(query);
    });

final ownerDetailProvider = FutureProvider.autoDispose.family<OwnerEntity, int>(
  (ref, ownerId) => ref.watch(ownersRepositoryProvider).getOwner(ownerId),
);
