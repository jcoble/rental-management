plugins {
    id("com.android.application")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
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

    buildTypes {
        release {
            // TODO: Add your own signing config for the release build.
            // Signing with the debug keys for now, so `flutter run --release` works.
            signingConfig = signingConfigs.getByName("debug")
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
