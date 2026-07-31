import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/portal/tenant_portal_repository.dart';

void main() {
  test(
    'tenant work-order page parses tenant-safe items and preserves paging metadata',
    () {
      final page = PortalTenantWorkOrderPage.fromJson({
        'items': [
          {
            'id': 41,
            'title': 'No hot water',
            'description': 'Water heater is not working',
            'category': 'Plumbing',
            'priority': 'High',
            'status': 'InProgress',
            'requestedAt': '2026-07-14T12:00:00Z',
            'updatedAt': '2026-07-14T13:00:00Z',
          },
        ],
        'totalCount': 37,
        'skip': 20,
        'take': 20,
      });

      expect(page.items.single.id, 41);
      expect(page.items.single.portfolioId, isNull);
      expect(page.items.single.propertyId, isNull);
      expect(page.items.single.status, 'InProgress');
      expect(page.totalCount, 37);
      expect(page.skip, 20);
      expect(page.take, 20);
    },
  );
}
