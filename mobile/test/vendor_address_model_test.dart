import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/vendors/vendors_models.dart' as feature;

void main() {
  test(
    'feature vendor model carries structured address fields from the API',
    () {
      final vendor = feature.Vendor.fromJson({
        'id': 12,
        'name': 'Acme HVAC',
        'serviceType': 'HVAC',
        'addressLine1': '123 Service Rd',
        'city': 'Columbus',
        'state': 'OH',
        'postalCode': '43215',
        'website': 'https://acme.example',
      });

      expect(vendor.addressLine1, '123 Service Rd');
      expect(vendor.city, 'Columbus');
      expect(vendor.state, 'OH');
      expect(vendor.postalCode, '43215');
      expect(vendor.website, 'https://acme.example');
    },
  );
}
