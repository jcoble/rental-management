import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/home/mobile_destination.dart';
import 'package:rental_command/features/home/mobile_domain_navigation.dart';

void main() {
  test('maintenance access exposes only assigned work', () {
    final destinations = workDestinationsFor(const {
      'maintenance.assigned-work.read',
      'maintenance.assigned-work.update',
      'maintenance.assigned-work.converse',
    }, assignedWorkExperience: true);

    expect(destinations, hasLength(1));
    expect(canOpenWorkOrders(const {'maintenance.assigned-work.read'}), isTrue);
    expect(destinations.single.id, MobileDestinationId.workOrders);
    expect(destinations.single.label, 'My work');
    expect(
      destinations.single.subtitle,
      'Repairs and maintenance assigned to you',
    );
  });

  test('leasing access exposes showing calendar without management work', () {
    const capabilities = {
      'rentals.read',
      'leasing.applications.manage',
      'leasing.showings.manage',
      'leasing.onboarding.manage',
    };
    final destinations = workDestinationsFor(
      capabilities,
      assignedWorkExperience: false,
    );

    expect(canOpenWorkHub(capabilities), isTrue);
    expect(canOpenWorkOrders(capabilities), isFalse);
    expect(destinations.map((item) => item.id), [
      MobileDestinationId.calendar,
      MobileDestinationId.notices,
    ]);
  });

  test('property manager sees the complete operational work set', () {
    final destinations = workDestinationsFor(const {
      'rentals.manage',
      'work.read',
      'work.manage',
      'responsibility.assign-existing-member',
    }, assignedWorkExperience: false);

    expect(destinations.map((item) => item.id), [
      MobileDestinationId.workOrders,
      MobileDestinationId.calendar,
      MobileDestinationId.inspections,
      MobileDestinationId.vendors,
      MobileDestinationId.automations,
      MobileDestinationId.notices,
    ]);
    expect(destinations.first.label, 'Orders');
  });
}
