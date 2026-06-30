import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/api/api_exception.dart';

void main() {
  test('includes ASP.NET validation field errors in the message', () {
    final requestOptions = RequestOptions(path: '/leases');
    final exception = DioException(
      requestOptions: requestOptions,
      response: Response<Map<String, dynamic>>(
        requestOptions: requestOptions,
        statusCode: 400,
        data: {
          'title': 'One or more validation errors occurred.',
          'errors': {
            'PropertyId': [
              'The field PropertyId must be between 1 and 2147483647.',
            ],
            'LeaseNumber': ['The LeaseNumber field is required.'],
          },
        },
      ),
    );

    final apiException = ApiException.fromDioException(exception);

    expect(apiException.statusCode, 400);
    expect(
      apiException.message,
      contains('One or more validation errors occurred.'),
    );
    expect(apiException.message, contains('PropertyId:'));
    expect(apiException.message, contains('LeaseNumber:'));
  });

  test('prefers ASP.NET problem detail over generic title', () {
    final requestOptions = RequestOptions(path: '/properties/42');
    final exception = DioException(
      requestOptions: requestOptions,
      response: Response<Map<String, dynamic>>(
        requestOptions: requestOptions,
        statusCode: 409,
        data: {
          'title': 'Conflict',
          'detail':
              'Property cannot be deleted because one of its units has lease history.',
          'status': 409,
        },
      ),
    );

    final apiException = ApiException.fromDioException(exception);

    expect(apiException.statusCode, 409);
    expect(
      apiException.message,
      'Property cannot be deleted because one of its units has lease history.',
    );
  });
}
