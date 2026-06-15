import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/models/models.dart';

/// Repository for Appointments.
///
/// Endpoints used:
///   GET    /appointments                    — list (JWT-scoped, no portfolioId param)
///   GET    /appointments/{id}               — single appointment
///   POST   /appointments                    — create
///   PATCH  /appointments/{id}              — update / status change
///   DELETE /appointments/{id}              — delete
class AppointmentsRepository {
  AppointmentsRepository(this._dio);

  final Dio _dio;

  /// List all appointments for the authenticated user's portfolio.
  ///
  /// Optional filters: [propertyId], [tenantId].
  Future<List<Appointment>> listAppointments({
    int? propertyId,
    int? tenantId,
  }) async {
    try {
      final queryParams = <String, dynamic>{};
      if (propertyId != null) queryParams['propertyId'] = propertyId;
      if (tenantId != null) queryParams['tenantId'] = tenantId;

      final response = await _dio.get<List<dynamic>>(
        '/appointments',
        queryParameters: queryParams.isEmpty ? null : queryParams,
      );
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(Appointment.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<Appointment> getAppointment(int id) async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/appointments/$id');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Appointment.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Create body: { title, type, status, scheduledStart, scheduledEnd?,
  ///   propertyId?, tenantId?, prospectName?, prospectEmail?, assignedTo?,
  ///   notes? }
  ///
  /// [scheduledStart] and [scheduledEnd] must be ISO 8601 strings.
  Future<Appointment> createAppointment(Map<String, dynamic> data) async {
    try {
      final response =
          await _dio.post<Map<String, dynamic>>('/appointments', data: data);
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Appointment.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Partial PATCH — send only the fields that changed.
  Future<Appointment> updateAppointment(
      int id, Map<String, dynamic> data) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/appointments/$id',
        data: data,
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Appointment.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Convenience wrapper around [updateAppointment] for status-only changes.
  ///
  /// [status] must be a string matching one of the [AppointmentStatus] enum
  /// values: Scheduled, Confirmed, Completed, Cancelled, NoShow.
  Future<Appointment> updateStatus(int id, String status) =>
      updateAppointment(id, {'status': status});

  Future<void> deleteAppointment(int id) async {
    try {
      await _dio.delete<dynamic>('/appointments/$id');
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final appointmentsRepositoryProvider =
    Provider<AppointmentsRepository>((ref) {
  return AppointmentsRepository(ref.watch(dioProvider));
});

// ── Appointments list notifier ────────────────────────────────────────────────

class AppointmentsNotifier extends Notifier<AsyncValue<List<Appointment>>> {
  @override
  AsyncValue<List<Appointment>> build() => const AsyncValue.loading();

  AppointmentsRepository get _repo =>
      ref.read(appointmentsRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final list = await _repo.listAppointments();
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final appointmentsProvider =
    NotifierProvider<AppointmentsNotifier, AsyncValue<List<Appointment>>>(
  AppointmentsNotifier.new,
);

// ── Single appointment notifier (family by id) ────────────────────────────────

class AppointmentDetailNotifier
    extends Notifier<AsyncValue<Appointment>> {
  AppointmentDetailNotifier(this._id);

  final int _id;

  @override
  AsyncValue<Appointment> build() {
    Future.microtask(load);
    return const AsyncValue.loading();
  }

  AppointmentsRepository get _repo =>
      ref.read(appointmentsRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final appt = await _repo.getAppointment(_id);
      state = AsyncValue.data(appt);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();
}

final appointmentDetailProvider = NotifierProvider.family<
    AppointmentDetailNotifier,
    AsyncValue<Appointment>,
    int>(
  AppointmentDetailNotifier.new,
);
