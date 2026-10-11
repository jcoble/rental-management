import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  final screenSource = File(
    'lib/features/team/team_screen.dart',
  ).readAsStringSync();
  final repositorySource = File(
    'lib/features/team/team_repository.dart',
  ).readAsStringSync();

  test('Team assignment mutations use canonical endpoints and revisions', () {
    expect(
      repositorySource,
      contains("'/team/members/\$accessContextId/assignments'"),
    );
    expect(repositorySource, contains('/\$assignmentId/end'));
    expect(repositorySource, contains('/\$assignmentId/properties'));
    expect(repositorySource, contains("'expectedAccessRevision'"));
    expect(screenSource, contains('_accessRevision = result.accessRevision'));
  });

  test(
    'Team mutation controls are capability-gated without role-name gates',
    () {
      expect(screenSource, contains("auth.hasCapability('team.manage')"));
      expect(screenSource, contains('if (widget.canManageTeam)'));
      expect(screenSource, isNot(contains("auth.user.role")));
      expect(screenSource, isNot(contains("role == 'Admin'")));
    },
  );

  test(
    'Team explains property and assigned-work scopes and protects self access',
    () {
      expect(screenSource, contains('Property scope is broader access'));
      expect(screenSource, contains('Assigned-work scope'));
      expect(
        screenSource,
        contains('Owners and tenants use their own experiences'),
      );
      expect(screenSource, contains('widget.isCurrentUser || saving'));
    },
  );
}
