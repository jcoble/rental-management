enum WorkspaceExperience { management, leasing, maintenance, owner, tenant }

WorkspaceExperience _experienceFromJson(Object? value) {
  final normalized = value is String ? value.trim().toLowerCase() : '';
  for (final experience in WorkspaceExperience.values) {
    if (experience.name == normalized) return experience;
  }

  throw FormatException('Unknown workspace experience: $value');
}

String _experienceToJson(WorkspaceExperience value) =>
    '${value.name[0].toUpperCase()}${value.name.substring(1)}';

class AccessEnvelope {
  const AccessEnvelope({
    required this.identity,
    required this.selectedContext,
    required this.defaultExperience,
    required this.availableExperiences,
    required this.assignments,
    required this.navigation,
  });

  final AccessIdentity identity;
  final SelectedAccessContext selectedContext;
  final WorkspaceExperience defaultExperience;
  final List<WorkspaceExperience> availableExperiences;
  final List<AccessAssignment> assignments;
  final List<NavigationCapabilities> navigation;

  factory AccessEnvelope.fromJson(Map<String, dynamic> json) => AccessEnvelope(
    identity: AccessIdentity.fromJson(json['identity'] as Map<String, dynamic>),
    selectedContext: SelectedAccessContext.fromJson(
      json['selectedContext'] as Map<String, dynamic>,
    ),
    defaultExperience: _experienceFromJson(json['defaultExperience']),
    availableExperiences: (json['availableExperiences'] as List<dynamic>)
        .map(_experienceFromJson)
        .toList(growable: false),
    assignments: (json['assignments'] as List<dynamic>)
        .map((item) => AccessAssignment.fromJson(item as Map<String, dynamic>))
        .toList(growable: false),
    navigation: (json['navigation'] as List<dynamic>)
        .map(
          (item) =>
              NavigationCapabilities.fromJson(item as Map<String, dynamic>),
        )
        .toList(growable: false),
  );

  Map<String, dynamic> toJson() => {
    'identity': identity.toJson(),
    'selectedContext': selectedContext.toJson(),
    'defaultExperience': _experienceToJson(defaultExperience),
    'availableExperiences': availableExperiences
        .map(_experienceToJson)
        .toList(),
    'assignments': assignments.map((item) => item.toJson()).toList(),
    'navigation': navigation.map((item) => item.toJson()).toList(),
  };

  Set<String> capabilitiesFor(WorkspaceExperience experience) => navigation
      .where((item) => item.experience == experience)
      .expand((item) => item.capabilityKeys)
      .toSet();
}

class AccessIdentity {
  const AccessIdentity({
    required this.userId,
    required this.displayName,
    this.email,
  });
  final int userId;
  final String displayName;
  final String? email;
  factory AccessIdentity.fromJson(Map<String, dynamic> json) => AccessIdentity(
    userId: (json['userId'] as num).toInt(),
    displayName: json['displayName'] as String,
    email: json['email'] as String?,
  );
  Map<String, dynamic> toJson() => {
    'userId': userId,
    'displayName': displayName,
    'email': email,
  };
}

class SelectedAccessContext {
  const SelectedAccessContext({
    required this.accessContextId,
    required this.portfolioId,
    required this.workspaceName,
    required this.accessRevision,
    required this.activeExperience,
  });
  final int accessContextId;
  final int portfolioId;
  final String workspaceName;
  final int accessRevision;
  final WorkspaceExperience activeExperience;
  factory SelectedAccessContext.fromJson(Map<String, dynamic> json) =>
      SelectedAccessContext(
        accessContextId: (json['accessContextId'] as num).toInt(),
        portfolioId: (json['portfolioId'] as num).toInt(),
        workspaceName: json['workspaceName'] as String,
        accessRevision: (json['accessRevision'] as num).toInt(),
        activeExperience: _experienceFromJson(json['activeExperience']),
      );
  Map<String, dynamic> toJson() => {
    'accessContextId': accessContextId,
    'portfolioId': portfolioId,
    'workspaceName': workspaceName,
    'accessRevision': accessRevision,
    'activeExperience': _experienceToJson(activeExperience),
  };
}

class AccessAssignment {
  const AccessAssignment({
    required this.assignmentId,
    required this.roleProfileKey,
    required this.roleProfileName,
    required this.status,
    required this.scope,
  });
  final int assignmentId;
  final String roleProfileKey;
  final String roleProfileName;
  final String status;
  final AccessAssignmentScope scope;
  factory AccessAssignment.fromJson(Map<String, dynamic> json) =>
      AccessAssignment(
        assignmentId: (json['assignmentId'] as num).toInt(),
        roleProfileKey: json['roleProfileKey'] as String,
        roleProfileName: json['roleProfileName'] as String,
        status: json['status'].toString(),
        scope: AccessAssignmentScope.fromJson(
          json['scope'] as Map<String, dynamic>,
        ),
      );
  Map<String, dynamic> toJson() => {
    'assignmentId': assignmentId,
    'roleProfileKey': roleProfileKey,
    'roleProfileName': roleProfileName,
    'status': status,
    'scope': scope.toJson(),
  };
}

class AccessAssignmentScope {
  const AccessAssignmentScope({
    required this.kind,
    required this.selectedPropertyCount,
    required this.selectedProperties,
  });

  final String kind;
  final int selectedPropertyCount;
  final List<ScopedAccessProperty> selectedProperties;

  factory AccessAssignmentScope.fromJson(Map<String, dynamic> json) =>
      AccessAssignmentScope(
        kind: json['kind'].toString(),
        selectedPropertyCount: (json['selectedPropertyCount'] as num).toInt(),
        selectedProperties: (json['selectedProperties'] as List<dynamic>)
            .map(
              (item) =>
                  ScopedAccessProperty.fromJson(item as Map<String, dynamic>),
            )
            .toList(growable: false),
      );

  Map<String, dynamic> toJson() => {
    'kind': kind,
    'selectedPropertyCount': selectedPropertyCount,
    'selectedProperties': selectedProperties
        .map((item) => item.toJson())
        .toList(),
  };
}

class ScopedAccessProperty {
  const ScopedAccessProperty({required this.propertyId, required this.name});

  final int propertyId;
  final String name;

  factory ScopedAccessProperty.fromJson(Map<String, dynamic> json) =>
      ScopedAccessProperty(
        propertyId: (json['propertyId'] as num).toInt(),
        name: json['name'] as String,
      );

  Map<String, dynamic> toJson() => {'propertyId': propertyId, 'name': name};
}

class NavigationCapabilities {
  const NavigationCapabilities({
    required this.experience,
    required this.capabilityKeys,
  });
  final WorkspaceExperience experience;
  final List<String> capabilityKeys;
  factory NavigationCapabilities.fromJson(Map<String, dynamic> json) =>
      NavigationCapabilities(
        experience: _experienceFromJson(json['experience']),
        capabilityKeys: (json['capabilityKeys'] as List<dynamic>)
            .cast<String>(),
      );
  Map<String, dynamic> toJson() => {
    'experience': _experienceToJson(experience),
    'capabilityKeys': capabilityKeys,
  };
}

class EffectiveAccessContextOption {
  const EffectiveAccessContextOption({
    required this.accessContextId,
    required this.portfolioId,
    required this.workspaceName,
    required this.accessRevision,
    required this.defaultExperience,
    required this.totalEffectiveContexts,
  });
  final int accessContextId;
  final int portfolioId;
  final String workspaceName;
  final int accessRevision;
  final WorkspaceExperience defaultExperience;
  final int totalEffectiveContexts;
  factory EffectiveAccessContextOption.fromJson(Map<String, dynamic> json) =>
      EffectiveAccessContextOption(
        accessContextId: (json['accessContextId'] as num).toInt(),
        portfolioId: (json['portfolioId'] as num).toInt(),
        workspaceName: json['workspaceName'] as String,
        accessRevision: (json['accessRevision'] as num).toInt(),
        defaultExperience: _experienceFromJson(json['defaultExperience']),
        totalEffectiveContexts: (json['totalEffectiveContexts'] as num).toInt(),
      );
}

class AccessContextSwitchResponse {
  const AccessContextSwitchResponse({
    required this.accessToken,
    required this.accessTokenExpiration,
    required this.access,
  });
  final String accessToken;
  final DateTime accessTokenExpiration;
  final AccessEnvelope access;
  factory AccessContextSwitchResponse.fromJson(Map<String, dynamic> json) =>
      AccessContextSwitchResponse(
        accessToken: json['accessToken'] as String,
        accessTokenExpiration: DateTime.parse(
          json['accessTokenExpiration'] as String,
        ),
        access: AccessEnvelope.fromJson(json['access'] as Map<String, dynamic>),
      );
}

/// Mirrors `UserDto` from `RentalCommand.Api/DTOs/AuthDTOs.cs`.
class AuthUser {
  const AuthUser({
    required this.id,
    required this.email,
    required this.displayName,
    required this.emailVerified,
  });

  final int id;
  final String email;
  final String displayName;
  final bool emailVerified;

  factory AuthUser.fromJson(Map<String, dynamic> json) {
    return AuthUser(
      id: (json['id'] as num).toInt(),
      email: json['email'] as String,
      displayName: json['displayName'] as String,
      emailVerified: json['emailVerified'] as bool,
    );
  }

  Map<String, dynamic> toJson() => {
    'id': id,
    'email': email,
    'displayName': displayName,
    'emailVerified': emailVerified,
  };
}

/// Mirrors `LoginResponse` from `RentalCommand.Api/DTOs/AuthDTOs.cs`.
///
/// The refresh token is NOT in this response body — the API sets it as an
/// httpOnly cookie (`rc_refresh_token`). The mobile client reads the rotated
/// token from the `Set-Cookie` response header to persist it in secure storage.
/// (Refresh requests now *send* the token in a JSON body, so only the rotated
/// token still relies on the Set-Cookie channel.)
class LoginResponse {
  const LoginResponse({
    required this.accessToken,
    required this.accessTokenExpiration,
    required this.user,
    required this.access,
  });

  final String accessToken;
  final DateTime accessTokenExpiration;
  final AuthUser user;
  final AccessEnvelope access;

  factory LoginResponse.fromJson(Map<String, dynamic> json) {
    return LoginResponse(
      accessToken: json['accessToken'] as String,
      accessTokenExpiration: DateTime.parse(
        json['accessTokenExpiration'] as String,
      ),
      user: AuthUser.fromJson(json['user'] as Map<String, dynamic>),
      access: AccessEnvelope.fromJson(json['access'] as Map<String, dynamic>),
    );
  }
}

/// Result of `POST /auth/register`.
///
/// The backend does NOT auto-login on register: it returns 200 with a generic
/// `{ message }` and (in production) sends an email-confirmation link. There is
/// no access/refresh token in the response, so the user must confirm their
/// email on the web and then sign in. We surface the server's [message] so the
/// register screen can show it verbatim.
class RegisterResult {
  const RegisterResult({required this.message});

  final String message;

  factory RegisterResult.fromJson(Map<String, dynamic> json) {
    final msg = json['message'];
    return RegisterResult(
      message: msg is String && msg.isNotEmpty
          ? msg
          : 'Registration successful. Please check your email to verify your account.',
    );
  }
}
