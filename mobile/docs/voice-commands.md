# Voice commands (Google App Actions) — TSK-26

Speak a command and have it happen, without navigating into app screens — part
of the phone-first premise. This is the **base** of that system: the full bridge
from a spoken phrase to in-app action, with four commands wired end-to-end.

> Status: **base / proof of bridge.** Deep-link delivery and in-app routing work
> today and are testable now (see [Testing](#testing)). Live Google Assistant
> recognition additionally requires Play Store publishing + Google review of the
> App Actions (see [Going live](#going-live)).

## How it works

```
"Hey Google, with Rental Command, log a $40 plumbing expense for 123 Main"
        │
        ▼  Google Assistant matches the phrase against a <capability>
res/xml/shortcuts.xml  ──►  builds the deep link from its <url-template>:
        │                   rentalcommand://voice/log-expense?amount=40&category=plumbing&property=123%20Main
        ▼  fired as an Android VIEW intent
AndroidManifest.xml  ──►  <intent-filter> scheme="rentalcommand" host="voice"
        │
        ▼  app_links plugin delivers the URI to Flutter
VoiceLinkService  ──►  parseVoiceCommand(uri)  ──►  pendingVoiceCommandProvider (one-slot bus)
        │
        ▼  HomeShell reads/listens
HomeShell._handleVoiceCommand  ──►  switch tab / push screen + confirmation snackbar
```

Why this shape:

- **Deep-link-first.** App Actions ultimately fire an Android deep link. By
  making the deep link the contract, the whole flow is testable with `adb`
  (no Assistant needed) and the same code path serves a future iOS Siri
  Shortcut or an in-app shortcut.
- **Custom intents, not built-in intents (BIIs).** Rental actions ("log a
  plumbing expense for 123 Main") don't map cleanly onto Google's standard BII
  catalog, so we declare `custom.actions.intent.*` with our own phrasings and
  parameters.
- **app_links, not Flutter engine deep linking.** Flutter's engine-level deep
  linking is left **disabled** (`flutter_deep_linking_enabled=false`) so
  go_router's auth redirect never sees these URIs; `app_links` delivers both
  cold-start and warm (`onNewIntent`, thanks to `launchMode=singleTop`) links.
- **One-slot command bus.** A command that arrives before `HomeShell` mounts
  (cold start / pre-login) simply waits in `pendingVoiceCommandProvider` until
  the shell reads it.

## Supported commands (base set)

| Spoken (examples)                                   | Deep link                                            | Lands on |
|-----------------------------------------------------|------------------------------------------------------|----------|
| "scan a document" / "scan a receipt"                | `rentalcommand://voice/scan`                         | Scan tab (capture flow) |
| "log a $40 plumbing expense for 123 Main"           | `rentalcommand://voice/log-expense?amount=&category=&property=` | Scan tab + understood-params confirmation |
| "show me overdue rent" / "who is behind on rent"    | `rentalcommand://voice/overdue-rent`                 | Payments screen |
| "open a work order for unit 4" / "what needs fixing"| `rentalcommand://voice/work-orders?unit=`            | Work orders screen |

Parameters are lenient: the parser accepts `vendor` as an alias for `category`,
amounts like `$40.00` / `1,200`, `-`/`_` interchangeably, and the `https://`
app-link form for future verified links. See `parseVoiceCommand` in
`lib/core/voice/voice_command.dart`.

`log-expense` deliberately routes to the **capture flow** (the flagship "the
computer does the typing for you" intake) and reads back what it understood —
voice and tap converge on one code path rather than introducing a second,
voice-only expense path.

## Files

| File | Role |
|------|------|
| `lib/core/voice/voice_command.dart` | `VoiceCommand` model + `parseVoiceCommand` (pure Dart, unit-tested) |
| `lib/core/voice/voice_command_controller.dart` | `VoiceLinkService` (app_links) + `pendingVoiceCommandProvider` |
| `lib/features/home/home_shell.dart` | `_handleVoiceCommand` — routing + confirmation |
| `lib/main.dart` | starts `VoiceLinkService` at boot |
| `android/app/src/main/res/xml/shortcuts.xml` | App Actions capabilities (BII→deep-link) |
| `android/app/src/main/res/values/voice_command_queries.xml` | spoken phrase patterns |
| `android/app/src/main/AndroidManifest.xml` | VIEW intent-filter + `android.app.shortcuts` meta-data |
| `android/app/build.gradle.kts` | `androidx.core` (for `app:queryPatterns`) |
| `test/voice_command_test.dart` | parser tests |
| `scripts/voice-test.sh` | fire deep links via `adb` (simulates Assistant) |

## Testing

### 1. Parser (no device)

```bash
flutter test test/voice_command_test.dart
```

### 2. The full bridge on a device/emulator (no Assistant needed)

With the app running (`flutter run`), fire the exact intent Assistant would:

```bash
./scripts/voice-test.sh                 # all sample commands
./scripts/voice-test.sh log-expense     # one command with sample params
```

You should see the app switch to the Scan tab / open the right screen and show a
confirmation snackbar of what it understood. This proves everything except
Google's speech→intent matching.

### 3. Real Assistant matching (Android Studio)

Use the **Google Assistant plugin** (Android Studio ▸ Tools ▸ App Actions Test
Tool). Select a capability, fill sample parameters, and "Run" to have Assistant
invoke it on the connected device. Requires the app installed and signed in with
the same Google account on the device.

## Going live

Custom App Actions are recognized by Assistant in production only after:

1. The app is published on the **Play Store** (at least to internal/closed
   testing) under the same package + signing key.
2. The App Actions are submitted and **approved** in Play Console (App Actions /
   Assistant section).
3. Device locale + Assistant language are **en-US** (current custom-intent limit).

Until then, the App Actions Test Tool and the `adb` deep links above exercise the
identical app-side code path.

## Known limitations / next steps

- **Android only** for now (matches on-device testing); iOS Siri Shortcuts is a
  later parallel — the deep-link contract already supports it.
- Custom intents: **en-US only**, max **2 text parameters** per phrase.
- Flutter + Assistant `shortcuts.xml` recognition can be finicky
  ([flutter#172408](https://github.com/flutter/flutter/issues/172408)); the
  deep-link layer is intentionally independent so it's verifiable on its own.
- Expansion ideas once the bridge is proven: "record a payment", "open
  property X", "show today's briefing", and binding spoken property/unit names
  to real records (inline inventory) instead of free text.
