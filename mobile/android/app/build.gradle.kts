import java.util.Properties
import java.io.FileInputStream

plugins {
    id("com.android.application")
    // Firebase (FCM push) — reads google-services.json at build time.
    id("com.google.gms.google-services")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
}

// Release/upload signing config is read from android/key.properties (gitignored,
// never committed). When the file is absent (e.g. a fresh clone or CI without the
// secret) the release build falls back to debug signing so `flutter run --release`
// still works locally — but the Play Store upload AAB must be produced on a machine
// that has key.properties + upload-keystore.jks present.
val keystoreProperties = Properties()
val keystorePropertiesFile = rootProject.file("key.properties")
val hasReleaseSigning = keystorePropertiesFile.exists()
if (hasReleaseSigning) {
    keystoreProperties.load(FileInputStream(keystorePropertiesFile))
}

android {
    namespace = "com.rentalcommand.rental_command"
    // file_picker / flutter_plugin_android_lifecycle (pulled in with the document
    // + Firebase plugins) require compiling against API 36+. Pin it here rather
    // than relying on flutter.compileSdkVersion (34) until the Flutter SDK default
    // catches up.
    compileSdk = 36
    ndkVersion = flutter.ndkVersion

    compileOptions {
        // flutter_local_notifications relies on java.time APIs that require core
        // library desugaring to run on the project's minSdk.
        isCoreLibraryDesugaringEnabled = true
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    defaultConfig {
        // TODO: Specify your own unique Application ID (https://developer.android.com/studio/build/application-id.html).
        applicationId = "com.rentalcommand.rental_command"
        // You can update the following values to match your application needs.
        // For more information, see: https://flutter.dev/to/review-gradle-config.
        minSdk = flutter.minSdkVersion
        targetSdk = flutter.targetSdkVersion
        versionCode = flutter.versionCode
        versionName = flutter.versionName
    }

    signingConfigs {
        // The `upload` config signs the Play Store AAB with our upload key.
        // Only registered when key.properties is present (see hasReleaseSigning above).
        if (hasReleaseSigning) {
            create("release") {
                keyAlias = keystoreProperties["keyAlias"] as String
                keyPassword = keystoreProperties["keyPassword"] as String
                storeFile = file(keystoreProperties["storeFile"] as String)
                storePassword = keystoreProperties["storePassword"] as String
            }
        }
    }

    buildTypes {
        release {
            // Sign with the upload key when key.properties is present (the only way the
            // Play Store AAB should be built); otherwise fall back to debug keys so a
            // local `flutter run --release` without the secret still works.
            signingConfig = if (hasReleaseSigning) {
                signingConfigs.getByName("release")
            } else {
                signingConfigs.getByName("debug")
            }
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17
    }
}

dependencies {
    // Required for the App Actions `app:queryPatterns` attribute used in
    // res/xml/shortcuts.xml (the Android Shortcuts framework). 1.6.0+ per
    // https://developer.android.com/develop/devices/assistant/action-schema
    implementation("androidx.core:core-ktx:1.13.1")

    // Backports java.time (and other Java 8+) APIs so flutter_local_notifications
    // works below API 26; paired with isCoreLibraryDesugaringEnabled above.
    coreLibraryDesugaring("com.android.tools:desugar_jdk_libs:2.1.4")
}

flutter {
    source = "../.."
}
