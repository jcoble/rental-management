import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';

// ── Models ────────────────────────────────────────────────────────────────────

class TeamMember {
  const TeamMember({
    required this.id,
    required this.email,
    this.displayName,
    required this.role,
    required this.isActive,
    required this.createdAt,
  });

  final int id;
  final String email;
  final String? displayName;
  final String role;
  final bool isActive;
  final DateTime createdAt;

  String get label => displayName ?? email;

  TeamMember copyWith({String? role, bool? isActive}) {
    return TeamMember(
      id: id,
      email: email,
      displayName: displayName,
      role: role ?? this.role,
      isActive: isActive ?? this.isActive,
      createdAt: createdAt,
    );
  }

  factory TeamMember.fromJson(Map<String, dynamic> json) => TeamMember(
        id: (json['id'] as num).toInt(),
        email: json['email'] as String? ?? '',
        displayName: json['displayName'] as String?,
        role: json['role'] as String? ?? '',
        isActive: json['isActive'] as bool? ?? true,
        createdAt: DateTime.tryParse(json['createdAt'] as String? ?? '') ??
            DateTime(0),
      );
}

class InviteResult {
  const InviteResult({
    required this.member,
    this.generatedPassword,
  });

  final TeamMember member;
  final String? generatedPassword;
}

// ── Repository ────────────────────────────────────────────────────────────────

/// Team management API calls.
///
/// Endpoints:
///   GET   /admin/users                    — list [TeamMember]
///   PATCH /admin/users/{id}/role    { role }
///   PATCH /admin/users/{id}/active  { isActive }
///   POST  /admin/users              { email, displayName?, role, temporaryPassword? }
///       → may include generatedPassword in response
class TeamRepository {
  TeamRepository(this._dio);

  final Dio _dio;

  Future<List<TeamMember>> listMembers() async {
    try {
      final response = await _dio.get<List<dynamic>>('/admin/users');
      final data = response.data ?? [];
      return data
          .whereType<Map<String, dynamic>>()
          .map(TeamMember.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<TeamMember> updateRole(int id, String role) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/admin/users/$id/role',
        data: {'role': role},
      );
      return TeamMember.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<TeamMember> setActive(int id, {required bool isActive}) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/admin/users/$id/active',
        data: {'isActive': isActive},
      );
      return TeamMember.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<InviteResult> inviteMember({
    required String email,
    String? displayName,
    required String role,
    String? temporaryPassword,
  }) async {
    try {
      final body = <String, dynamic>{
        'email': email,
        'role': role,
        if (displayName != null && displayName.isNotEmpty)
          'displayName': displayName,
        if (temporaryPassword != null && temporaryPassword.isNotEmpty)
          'temporaryPassword': temporaryPassword,
      };
      final response =
          await _dio.post<Map<String, dynamic>>('/admin/users', data: body);
      final data = response.data!;
      final member = TeamMember.fromJson(data);
      final generatedPassword = data['generatedPassword'] as String?;
      return InviteResult(member: member, generatedPassword: generatedPassword);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final teamRepositoryProvider = Provider<TeamRepository>((ref) {
  return TeamRepository(ref.watch(dioProvider));
});

class TeamNotifier extends Notifier<AsyncValue<List<TeamMember>>> {
  @override
  AsyncValue<List<TeamMember>> build() => const AsyncValue.loading();

  TeamRepository get _repo => ref.read(teamRepositoryProvider);

  Future<void> load() async {
    state = const AsyncValue.loading();
    try {
      final list = await _repo.listMembers();
      state = AsyncValue.data(list);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> refresh() => load();

  void _replace(TeamMember updated) {
    state.whenData((list) {
      state = AsyncValue.data([
        for (final m in list)
          if (m.id == updated.id) updated else m,
      ]);
    });
  }

  Future<void> updateRole(int id, String role) async {
    try {
      final updated = await _repo.updateRole(id, role);
      _replace(updated);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<void> setActive(int id, {required bool isActive}) async {
    try {
      final updated = await _repo.setActive(id, isActive: isActive);
      _replace(updated);
    } on ApiException catch (e) {
      state = AsyncValue.error(e, StackTrace.current);
    }
  }

  Future<InviteResult> invite({
    required String email,
    String? displayName,
    required String role,
    String? temporaryPassword,
  }) async {
    final result = await _repo.inviteMember(
      email: email,
      displayName: displayName,
      role: role,
      temporaryPassword: temporaryPassword,
    );
    state.whenData((list) {
      state = AsyncValue.data([...list, result.member]);
    });
    return result;
  }
}

final teamProvider =
    NotifierProvider<TeamNotifier, AsyncValue<List<TeamMember>>>(
  TeamNotifier.new,
);
