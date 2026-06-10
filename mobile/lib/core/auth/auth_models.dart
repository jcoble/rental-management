/// Mirrors `UserDto` from `RentalCommand.Api/DTOs/AuthDTOs.cs`.
class AuthUser {
  const AuthUser({
    required this.id,
    required this.email,
    required this.displayName,
    this.portfolioId,
    this.ownerEntityId,
    this.tenantId,
    required this.roles,
    required this.emailVerified,
  });

  final int id;
  final String email;
  final String displayName;
  final int? portfolioId;
  final int? ownerEntityId;
  final int? tenantId;
  final List<String> roles;
  final bool emailVerified;

  factory AuthUser.fromJson(Map<String, dynamic> json) {
    return AuthUser(
      id: (json['id'] as num).toInt(),
      email: json['email'] as String,
      displayName: json['displayName'] as String,
      portfolioId: (json['portfolioId'] as num?)?.toInt(),
      ownerEntityId: (json['ownerEntityId'] as num?)?.toInt(),
      tenantId: (json['tenantId'] as num?)?.toInt(),
      roles: (json['roles'] as List<dynamic>).cast<String>(),
      emailVerified: json['emailVerified'] as bool,
    );
  }

  Map<String, dynamic> toJson() => {
    'id': id,
    'email': email,
    'displayName': displayName,
    'portfolioId': portfolioId,
    'ownerEntityId': ownerEntityId,
    'tenantId': tenantId,
    'roles': roles,
    'emailVerified': emailVerified,
  };

  bool get isStaff =>
      roles.any((r) => r == 'Admin' || r == 'Manager' || r == 'Agent');

  bool get isTenant => roles.contains('Tenant') && !isStaff;
}

/// Mirrors `LoginResponse` from `RentalCommand.Api/DTOs/AuthDTOs.cs`.
///
/// The refresh token is NOT in this response body — the API sets it as an
/// httpOnly cookie (`rc_refresh_token`). The mobile client reads the cookie
/// from the Set-Cookie response header to persist it in secure storage.
///
/// TODO(api): Add a body-based refresh parameter so mobile doesn't need to
/// replicate cookie behaviour. Track with API team.
class LoginResponse {
  const LoginResponse({
    required this.accessToken,
    required this.accessTokenExpiration,
    required this.user,
  });

  final String accessToken;
  final DateTime accessTokenExpiration;
  final AuthUser user;

  factory LoginResponse.fromJson(Map<String, dynamic> json) {
    return LoginResponse(
      accessToken: json['accessToken'] as String,
      accessTokenExpiration: DateTime.parse(
        json['accessTokenExpiration'] as String,
      ),
      user: AuthUser.fromJson(json['user'] as Map<String, dynamic>),
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
