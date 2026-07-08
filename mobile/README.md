# Rental Command Mobile

Flutter client for Rental Command.

## Android Flavors

The Android app has separate package ids so dev installs can live next to the
production app on a real phone:

- `prod`: `com.rentalcommand.rental_command`, label `Rental Command`
- `dev`: `com.rentalcommand.rental_command.dev`, label `Rental Command Dev`

Example local-device build:

```bash
flutter build apk --profile \
  --flavor dev \
  --target-platform android-arm64 \
  --dart-define=FLAVOR=dev \
  --dart-define=API_BASE_URL=https://<LAN-IP>:5666/api/v1

adb install -r build/app/outputs/flutter-apk/app-dev-profile.apk
```

The dev flavor skips Android Google Services processing because the committed
Firebase config only declares the production package. Push notifications fail
soft in dev; Google login does not depend on Firebase.
