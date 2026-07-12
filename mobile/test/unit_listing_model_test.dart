import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/units/units_repository.dart';

void main() {
  test('ListingWorkspace parses canonical listing and publications', () {
    final listing = ListingWorkspace.fromJson({
      'id': 10,
      'portfolioId': 1,
      'propertyId': 7,
      'unitId': 42,
      'status': 'ReadyToPublish',
      'contentVersion': 3,
      'headline': '2 bed at Maple Ridge',
      'description': 'Ready to copy into Zillow.',
      'rent': 1450,
      'securityDeposit': 1450,
      'bedrooms': 2,
      'bathrooms': 1.5,
      'squareFeet': 925,
      'photoManifest': [
        {
          'id': 4,
          'position': 1,
          'category': 'Exterior',
          'fileName': 'front.jpg',
        },
      ],
      'publications': [
        {
          'id': 9,
          'providerKey': 'Zillow',
          'mode': 'Guided',
          'status': 'Ready',
          'listingUrl': 'https://www.zillow.com/homedetails/example',
          'copyConfirmed': true,
          'termsConfirmed': false,
          'photosConfirmed': false,
          'providerWorkspaceOpened': true,
          'needsRepublish': true,
          'unconfirmedSignals': [],
        },
      ],
      'signedLeaseImportUrl': '/scan?type=Lease&unitId=42',
      'createdAt': '2026-06-30T12:00:00Z',
      'updatedAt': '2026-06-30T13:00:00Z',
    });

    expect(listing.status, 'ReadyToPublish');
    expect(listing.contentVersion, 3);
    expect(listing.rent, 1450);
    expect(listing.bathrooms, 1.5);
    expect(listing.photoManifest.single.category, 'Exterior');
    expect(listing.publications.single.listingUrl, contains('zillow.com'));
    expect(listing.publications.single.needsRepublish, isTrue);
  });

  test('SaveListingWorkspaceRequest emits guided handoff fields', () {
    final payload = const SaveListingWorkspaceRequest(
      status: 'Published',
      headline: 'Updated headline',
      description: 'Updated description',
      rent: 1500,
      zillowGuided: SaveGuidedPublicationRequest(
        status: 'Published',
        listingUrl: 'https://www.zillow.com/homedetails/example',
        copyConfirmed: true,
        markCurrentVersionPublished: true,
      ),
    ).toJson();

    expect(payload['status'], 'Published');
    expect(payload['headline'], 'Updated headline');
    expect(payload['description'], 'Updated description');
    expect(payload['rent'], 1500);
    final guided = payload['zillowGuided'] as Map<String, dynamic>;
    expect(guided['listingUrl'], contains('zillow.com'));
    expect(guided['copyConfirmed'], isTrue);
    expect(guided['markCurrentVersionPublished'], isTrue);
  });
}
