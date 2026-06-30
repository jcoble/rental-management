# Mobile App Links

Rental Command sends HTTPS links for password reset and email verification:

- `https://rc.coblesolutions.com/reset-password?userId=...&token=...`
- `https://rc.coblesolutions.com/verify-email?userId=...&token=...`

The mobile app can handle both paths in-app. The web pages remain the fallback when
the app is not installed.

## Android

Android App Links are declared in:

- `mobile/android/app/src/main/AndroidManifest.xml`
- `web/src/routes/.well-known/assetlinks.json/+server.ts`

Before production verification, set:

```text
MOBILE_ANDROID_SHA256_CERT_FINGERPRINTS=<colon-separated signing key fingerprint>,<upload key fingerprint>
MOBILE_ANDROID_PACKAGE_NAME=com.rentalcommand.rental_command
```

Use the SHA-256 fingerprints from Google Play Console:

- App integrity -> App signing key certificate
- App integrity -> Upload key certificate

`MOBILE_ANDROID_PACKAGE_NAME` can be omitted while the Android `applicationId`
remains `com.rentalcommand.rental_command`.

## iOS

iOS Universal Links are declared in:

- `mobile/ios/Runner/Runner.entitlements`
- `web/src/routes/.well-known/apple-app-site-association/+server.ts`

Before production verification, set:

```text
MOBILE_APPLE_APP_ID=<Apple Team ID>.com.rentalcommand.rentalCommand
```

The deployed file must be served from
`https://rc.coblesolutions.com/.well-known/apple-app-site-association` without a
`.json` extension and with JSON content.

If the required environment variables are missing, the `.well-known` routes
return 404 instead of serving placeholder association data.
