import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_models.dart';

void main() {
  test('canonical access envelope survives secure-storage JSON round trip', () {
    final source = <String, dynamic>{
      'identity': {
        'userId': 17,
        'displayName': 'Morgan Manager',
        'email': 'morgan@example.test',
      },
      'selectedContext': {
        'accessContextId': 42,
        'portfolioId': 8,
        'workspaceName': 'Lakeview Rentals',
        'accessRevision': 9,
        'activeExperience': 'Management',
      },
      'defaultExperience': 'Management',
      'availableExperiences': ['Management', 'Maintenance'],
      'assignments': [
        {
          'assignmentId': 6,
          'roleProfileKey': 'property-manager',
          'roleProfileName': 'Property Manager',
          'status': 'Active',
          'scope': {
            'kind': 'SelectedProperties',
            'selectedPropertyCount': 1,
            'selectedProperties': [
              {'propertyId': 101, 'name': 'Lakeview'},
            ],
          },
        },
      ],
      'navigation': [
        {
          'experience': 'Management',
          'capabilityKeys': ['rentals.read', 'money.balances.read'],
        },
        {
          'experience': 'Maintenance',
          'capabilityKeys': ['maintenance.assigned-work.read'],
        },
      ],
    };

    final envelope = AccessEnvelope.fromJson(source);
    final restored = AccessEnvelope.fromJson(envelope.toJson());

    expect(restored.selectedContext.accessRevision, 9);
    expect(restored.assignments.single.scope.selectedPropertyCount, 1);
    expect(
      restored.assignments.single.scope.selectedProperties.single.name,
      'Lakeview',
    );
    expect(restored.capabilitiesFor(WorkspaceExperience.management), {
      'rentals.read',
      'money.balances.read',
    });
    expect(restored.capabilitiesFor(WorkspaceExperience.maintenance), {
      'maintenance.assigned-work.read',
    });
  });

  test('unknown active experience fails closed', () {
    final source = <String, dynamic>{
      'identity': {
        'userId': 17,
        'displayName': 'Morgan Manager',
        'email': 'morgan@example.test',
      },
      'selectedContext': {
        'accessContextId': 42,
        'portfolioId': 8,
        'workspaceName': 'Lakeview Rentals',
        'accessRevision': 9,
        'activeExperience': 'FutureExperience',
      },
      'defaultExperience': 'Management',
      'availableExperiences': ['Management'],
      'assignments': <Map<String, dynamic>>[],
      'navigation': <Map<String, dynamic>>[],
    };

    expect(
      () => AccessEnvelope.fromJson(source),
      throwsA(isA<FormatException>()),
    );
  });
}
