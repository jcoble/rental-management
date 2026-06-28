import 'package:dio/dio.dart';

/// Typed exception for all API errors.
///
/// Maps Dio's error hierarchy and the API's error body shapes:
///   - `{ "error": "message string" }` — plain error string
///   - `{ "error": { "message": "..." } }` — nested error object
///   - ASP.NET ProblemDetails — `{ "title": "...", "status": 4xx }`
class ApiException implements Exception {
  const ApiException({required this.statusCode, required this.message});

  final int statusCode;
  final String message;

  @override
  String toString() => 'ApiException($statusCode): $message';

  /// Converts a [DioException] into an [ApiException].
  factory ApiException.fromDioException(DioException e) {
    final response = e.response;

    if (response == null) {
      // Network-level error (no connectivity, timeout, etc.)
      switch (e.type) {
        case DioExceptionType.connectionTimeout:
        case DioExceptionType.sendTimeout:
        case DioExceptionType.receiveTimeout:
          return const ApiException(
            statusCode: 408,
            message: 'The request is taking longer than expected. Try again.',
          );
        case DioExceptionType.connectionError:
          return const ApiException(
            statusCode: 0,
            message: 'Unable to connect. Check your network connection.',
          );
        default:
          return ApiException(
            statusCode: 0,
            message: e.message ?? 'An unexpected network error occurred.',
          );
      }
    }

    final statusCode = response.statusCode ?? 0;
    final body = response.data;
    final message = _extractMessage(body, statusCode);
    return ApiException(statusCode: statusCode, message: message);
  }

  static String _extractMessage(dynamic body, int statusCode) {
    if (body is Map<String, dynamic>) {
      // { "error": "string" } — optionally accompanied by a { "details": [..] }
      // array of field-level messages (e.g. ASP.NET Identity password rules on
      // /auth/register). Fold the details in so the user sees *why* it failed.
      final errorField = body['error'];
      if (errorField is String && errorField.isNotEmpty) {
        final details = _extractDetails(body['details']);
        return details == null ? errorField : '$errorField $details';
      }
      // { "error": { "message": "..." } }
      if (errorField is Map<String, dynamic>) {
        final msg = errorField['message'];
        if (msg is String && msg.isNotEmpty) return msg;
      }
      // ASP.NET ProblemDetails — { "title": "...", "status": 4xx }
      final title = body['title'];
      final validationDetails = _extractValidationErrors(body['errors']);
      if (title is String && title.isNotEmpty) {
        return validationDetails == null ? title : '$title $validationDetails';
      }
      if (validationDetails != null) return validationDetails;
      // Generic message field
      final message = body['message'];
      if (message is String && message.isNotEmpty) return message;
    }
    return _fallbackMessage(statusCode);
  }

  /// Joins a `details` array of validation strings into one sentence, or null
  /// when there's nothing usable.
  static String? _extractDetails(dynamic details) {
    if (details is List) {
      final messages = details
          .whereType<String>()
          .where((s) => s.isNotEmpty)
          .toList();
      if (messages.isNotEmpty) return messages.join(' ');
    }
    return null;
  }

  static String? _extractValidationErrors(dynamic errors) {
    if (errors is! Map) return null;

    final messages = <String>[];
    for (final entry in errors.entries) {
      final field = entry.key?.toString() ?? '';
      final value = entry.value;
      if (value is List) {
        for (final item in value.whereType<String>()) {
          final message = item.trim();
          if (message.isNotEmpty) {
            messages.add(field.isEmpty ? message : '$field: $message');
          }
        }
      } else if (value is String && value.trim().isNotEmpty) {
        final message = value.trim();
        messages.add(field.isEmpty ? message : '$field: $message');
      }
    }

    return messages.isEmpty ? null : messages.join(' ');
  }

  static String _fallbackMessage(int statusCode) {
    switch (statusCode) {
      case 400:
        return 'Invalid request.';
      case 401:
        return 'Session expired. Please sign in again.';
      case 403:
        return 'You do not have permission to perform this action.';
      case 404:
        return 'The requested resource was not found.';
      case 429:
        return 'Too many requests. Try again shortly.';
      default:
        if (statusCode >= 500) {
          return 'The server encountered an error. Please try again.';
        }
        return 'Request failed ($statusCode).';
    }
  }
}
