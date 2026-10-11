import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:uuid/uuid.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';

class TeamMember {
  const TeamMember({
    required this.userId,
    required this.accessContextId,
    required this.workspaceMembershipId,
    required this.email,
    required this.displayName,
    required this.accessStatus,
    required this.membershipStatus,
    required this.accessRevision,
    required this.assignmentCount,
    required this.roleSummary,
    required this.requiresAccountActivation,
    required this.createdAtUtc,
  });

  final int userId;
  final int accessContextId;
  final int workspaceMembershipId;
  final String email;
  final String displayName;
  final String accessStatus;
  final String membershipStatus;
  final int accessRevision;
  final int assignmentCount;
  final String roleSummary;
  final bool requiresAccountActivation;
  final DateTime createdAtUtc;

  bool get isActive =>
      accessStatus.toLowerCase() == 'active' &&
      membershipStatus.toLowerCase() == 'active';

  factory TeamMember.fromJson(Map<String, dynamic> json) => TeamMember(
    userId: (json['userId'] as num).toInt(),
    accessContextId: (json['accessContextId'] as num).toInt(),
    workspaceMembershipId: (json['workspaceMembershipId'] as num).toInt(),
    email: json['email'] as String? ?? '',
    displayName: json['displayName'] as String? ?? '',
    accessStatus: json['accessStatus'] as String? ?? '',
    membershipStatus: json['membershipStatus'] as String? ?? '',
    accessRevision: (json['accessRevision'] as num).toInt(),
    assignmentCount: (json['assignmentCount'] as num).toInt(),
    roleSummary: json['roleSummary'] as String? ?? '',
    requiresAccountActivation:
        json['requiresAccountActivation'] as bool? ?? false,
    createdAtUtc:
        DateTime.tryParse(json['createdAtUtc'] as String? ?? '') ??
        DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
  );
}

class TeamMemberPage {
  const TeamMemberPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
    required this.search,
  });

  final List<TeamMember> items;
  final int totalCount;
  final int skip;
  final int take;
  final String search;

  bool get hasMore => items.length < totalCount;

  factory TeamMemberPage.fromJson(
    Map<String, dynamic> json, {
    required String search,
  }) => TeamMemberPage(
    items: (json['items'] as List<dynamic>? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(TeamMember.fromJson)
        .toList(growable: false),
    totalCount: (json['totalCount'] as num?)?.toInt() ?? 0,
    skip: (json['skip'] as num?)?.toInt() ?? 0,
    take: (json['take'] as num?)?.toInt() ?? 20,
    search: search,
  );
}

class TeamAssignment {
  const TeamAssignment({
    required this.assignmentId,
    required this.roleProfileKey,
    required this.roleProfileName,
    required this.status,
    required this.scopeKind,
    required this.selectedPropertyCount,
    required this.effectiveFromUtc,
    required this.effectiveToUtc,
  });

  final int assignmentId;
  final String roleProfileKey;
  final String roleProfileName;
  final String status;
  final String scopeKind;
  final int selectedPropertyCount;
  final DateTime effectiveFromUtc;
  final DateTime? effectiveToUtc;

  bool get isActive => status.toLowerCase() == 'active';

  factory TeamAssignment.fromJson(Map<String, dynamic> json) => TeamAssignment(
    assignmentId: (json['assignmentId'] as num).toInt(),
    roleProfileKey: json['roleProfileKey'] as String? ?? '',
    roleProfileName: json['roleProfileName'] as String? ?? '',
    status: json['status'] as String? ?? '',
    scopeKind: json['scopeKind'] as String? ?? '',
    selectedPropertyCount:
        (json['selectedPropertyCount'] as num?)?.toInt() ?? 0,
    effectiveFromUtc:
        DateTime.tryParse(json['effectiveFromUtc'] as String? ?? '') ??
        DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
    effectiveToUtc: DateTime.tryParse(json['effectiveToUtc'] as String? ?? ''),
  );
}

class TeamAssignmentPage {
  const TeamAssignmentPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<TeamAssignment> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasMore => skip + items.length < totalCount;

  factory TeamAssignmentPage.fromJson(Map<String, dynamic> json) =>
      TeamAssignmentPage(
        items: (json['items'] as List<dynamic>? ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(TeamAssignment.fromJson)
            .toList(growable: false),
        totalCount: (json['totalCount'] as num?)?.toInt() ?? 0,
        skip: (json['skip'] as num?)?.toInt() ?? 0,
        take: (json['take'] as num?)?.toInt() ?? 50,
      );
}

class TeamMutationResult {
  const TeamMutationResult({required this.accessRevision});

  final int accessRevision;

  factory TeamMutationResult.fromJson(Map<String, dynamic> json) =>
      TeamMutationResult(
        accessRevision: (json['accessRevision'] as num).toInt(),
      );
}

class TeamRoleProfile {
  const TeamRoleProfile({
    required this.key,
    required this.displayName,
    required this.description,
    required this.defaultExperience,
    required this.defaultScopeKind,
  });

  final String key;
  final String displayName;
  final String description;
  final String defaultExperience;
  final String defaultScopeKind;

  factory TeamRoleProfile.fromJson(Map<String, dynamic> json) =>
      TeamRoleProfile(
        key: json['key'] as String? ?? '',
        displayName: json['displayName'] as String? ?? '',
        description: json['description'] as String? ?? '',
        defaultExperience: json['defaultExperience'] as String? ?? '',
        defaultScopeKind: json['defaultScopeKind'] as String? ?? '',
      );
}

class CreateTeamMemberResult {
  const CreateTeamMemberResult({required this.requiresAccountActivation});

  final bool requiresAccountActivation;

  factory CreateTeamMemberResult.fromJson(Map<String, dynamic> json) =>
      CreateTeamMemberResult(
        requiresAccountActivation:
            json['requiresAccountActivation'] as bool? ?? false,
      );
}

class TeamRepository {
  TeamRepository(this._dio);

  static const _uuid = Uuid();
  final Dio _dio;

  Future<TeamMemberPage> listMembers({
    int skip = 0,
    int take = 20,
    String search = '',
    String sort = '-createdAt',
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/team/members',
        queryParameters: {
          'skip': skip,
          'take': take,
          'sort': sort,
          if (search.trim().isNotEmpty) 'search': search.trim(),
        },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return TeamMemberPage.fromJson(data, search: search.trim());
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<List<TeamRoleProfile>> listRoleProfiles() async {
    try {
      final response = await _dio.get<List<dynamic>>('/team/role-profiles');
      return (response.data ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(TeamRoleProfile.fromJson)
          .toList(growable: false);
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<TeamAssignmentPage> listAssignments(
    int accessContextId, {
    int skip = 0,
    int take = 50,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/team/members/$accessContextId/assignments',
        queryParameters: {'skip': skip, 'take': take, 'sort': '-effectiveFrom'},
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return TeamAssignmentPage.fromJson(data);
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<CreateTeamMemberResult> createMembership({
    required String email,
    required String displayName,
    required String roleProfileKey,
    required String scopeKind,
    required List<int> selectedPropertyIds,
  }) async {
    try {
      final sortedPropertyIds = [...selectedPropertyIds]..sort();
      final response = await _dio.post<Map<String, dynamic>>(
        '/team/memberships',
        options: Options(headers: {'Idempotency-Key': _uuid.v4()}),
        data: {
          'email': email.trim(),
          'displayName': displayName.trim(),
          'roleProfileKey': roleProfileKey,
          'scopeKind': scopeKind,
          'selectedPropertyIds': sortedPropertyIds,
          'effectiveFromUtc': DateTime.now().toUtc().toIso8601String(),
        },
      );
      final value = response.data?['value'] as Map<String, dynamic>?;
      if (value == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return CreateTeamMemberResult.fromJson(value);
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<void> changeStatus(TeamMember member) async {
    try {
      await _dio.patch<Map<String, dynamic>>(
        '/team/members/${member.accessContextId}/status',
        options: Options(headers: {'Idempotency-Key': _uuid.v4()}),
        data: {
          'expectedAccessRevision': member.accessRevision,
          'action': member.isActive ? 'Suspend' : 'Reactivate',
        },
      );
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<TeamMutationResult> addAssignment({
    required int accessContextId,
    required int expectedAccessRevision,
    required String roleProfileKey,
    required String scopeKind,
    required List<int> selectedPropertyIds,
  }) async {
    final sortedPropertyIds = [...selectedPropertyIds]..sort();
    return _assignmentMutation(
      path: '/team/members/$accessContextId/assignments',
      method: 'POST',
      data: {
        'expectedAccessRevision': expectedAccessRevision,
        'roleProfileKey': roleProfileKey,
        'scopeKind': scopeKind,
        'selectedPropertyIds': sortedPropertyIds,
        'effectiveFromUtc': DateTime.now().toUtc().toIso8601String(),
      },
    );
  }

  Future<TeamMutationResult> endAssignment({
    required int accessContextId,
    required int assignmentId,
    required int expectedAccessRevision,
  }) => _assignmentMutation(
    path: '/team/members/$accessContextId/assignments/$assignmentId/end',
    method: 'PATCH',
    data: {
      'expectedAccessRevision': expectedAccessRevision,
      'effectiveToUtc': DateTime.now().toUtc().toIso8601String(),
    },
  );

  Future<TeamMutationResult> replaceAssignmentProperties({
    required int accessContextId,
    required int assignmentId,
    required int expectedAccessRevision,
    required List<int> propertyIds,
  }) async {
    final sortedPropertyIds = [...propertyIds]..sort();
    return _assignmentMutation(
      path:
          '/team/members/$accessContextId/assignments/$assignmentId/properties',
      method: 'PUT',
      data: {
        'expectedAccessRevision': expectedAccessRevision,
        'propertyIds': sortedPropertyIds,
      },
    );
  }

  Future<TeamMutationResult> _assignmentMutation({
    required String path,
    required String method,
    required Map<String, dynamic> data,
  }) async {
    try {
      final response = await _dio.request<Map<String, dynamic>>(
        path,
        options: Options(
          method: method,
          headers: {'Idempotency-Key': _uuid.v4()},
        ),
        data: data,
      );
      final value = response.data?['value'] as Map<String, dynamic>?;
      if (value == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return TeamMutationResult.fromJson(value);
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }
}

final teamRepositoryProvider = Provider<TeamRepository>((ref) {
  return TeamRepository(ref.watch(dioProvider));
});

final teamRoleProfilesProvider = FutureProvider<List<TeamRoleProfile>>((ref) {
  return ref.watch(teamRepositoryProvider).listRoleProfiles();
});

class TeamNotifier extends Notifier<AsyncValue<TeamMemberPage>> {
  static const _pageSize = 20;

  @override
  AsyncValue<TeamMemberPage> build() => const AsyncValue.loading();

  TeamRepository get _repository => ref.read(teamRepositoryProvider);

  Future<void> load({String search = ''}) async {
    state = const AsyncValue.loading();
    state = await AsyncValue.guard(
      () => _repository.listMembers(take: _pageSize, search: search),
    );
  }

  Future<void> refresh() => load(search: state.value?.search ?? '');

  Future<void> loadMore() async {
    final current = state.value;
    if (current == null || !current.hasMore) return;
    final next = await _repository.listMembers(
      skip: current.items.length,
      take: _pageSize,
      search: current.search,
    );
    state = AsyncValue.data(
      TeamMemberPage(
        items: [...current.items, ...next.items],
        totalCount: next.totalCount,
        skip: 0,
        take: _pageSize,
        search: current.search,
      ),
    );
  }

  Future<CreateTeamMemberResult> createMembership({
    required String email,
    required String displayName,
    required String roleProfileKey,
    required String scopeKind,
    required List<int> selectedPropertyIds,
  }) async {
    final result = await _repository.createMembership(
      email: email,
      displayName: displayName,
      roleProfileKey: roleProfileKey,
      scopeKind: scopeKind,
      selectedPropertyIds: [...selectedPropertyIds],
    );
    await refresh();
    return result;
  }

  Future<void> changeStatus(TeamMember member) async {
    await _repository.changeStatus(member);
    await refresh();
  }
}

final teamProvider = NotifierProvider<TeamNotifier, AsyncValue<TeamMemberPage>>(
  TeamNotifier.new,
);
