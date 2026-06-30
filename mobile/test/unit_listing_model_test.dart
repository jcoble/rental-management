import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/units/units_repository.dart';

void main() {
  test('UnitListing parses Zillow manual listing payloads', () {
    final listing = UnitListing.fromJson({
      'id': 10,
      'portfolioId': 1,
      'propertyId': 7,
      'unitId': 42,
      'channel': 'ZillowManual',
      'status': 'ReadyToPost',
      'headline': '2 bed at Maple Ridge',
      'description': 'Ready to copy into Zillow.',
      'rent': 1450,
      'securityDeposit': 1450,
      'bedrooms': 2,
      'bathrooms': 1.5,
      'squareFeet': 925,
      'zillowListingUrl': 'https://www.zillow.com/homedetails/example',
      'isPosted': false,
      'createdAt': '2026-06-30T12:00:00Z',
      'updatedAt': '2026-06-30T13:00:00Z',
    });

    expect(listing.channel, 'ZillowManual');
    expect(listing.status, 'ReadyToPost');
    expect(listing.rent, 1450);
    expect(listing.bathrooms, 1.5);
    expect(listing.zillowListingUrl, contains('zillow.com'));
  });

  test('SaveUnitListingRequest emits changed listing handoff fields', () {
    final payload = const SaveUnitListingRequest(
      status: 'Posted',
      headline: 'Updated headline',
      description: 'Updated description',
      rent: 1500,
      zillowListingUrl: 'https://www.zillow.com/homedetails/example',
    ).toJson();

    expect(payload['status'], 'Posted');
    expect(payload['headline'], 'Updated headline');
    expect(payload['description'], 'Updated description');
    expect(payload['rent'], 1500);
    expect(payload['zillowListingUrl'], contains('zillow.com'));
  });
}
