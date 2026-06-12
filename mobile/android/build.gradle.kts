allprojects {
    repositories {
        google()
        mavenCentral()
    }
}

val newBuildDir: Directory =
    rootProject.layout.buildDirectory
        .dir("../../build")
        .get()
rootProject.layout.buildDirectory.value(newBuildDir)

subprojects {
    val newSubprojectBuildDir: Directory = newBuildDir.dir(project.name)
    project.layout.buildDirectory.value(newSubprojectBuildDir)
}
// Some plugins pulled in with the document/Firebase deps (e.g.
// flutter_plugin_android_lifecycle) require their dependents to compile against
// API 36+, but plugin modules otherwise inherit the older Flutter-default
// compileSdk. Reactively force every Android subproject up to 36 to keep AAR
// metadata checks happy until the Flutter SDK default catches up. This must run
// before `evaluationDependsOn(":app")` triggers early evaluation below.
subprojects {
    // Register the override in afterEvaluate from within the reactive plugin
    // callback: the callback fires while the plugin subproject is still being
    // configured (so afterEvaluate is legal), and running last lets it win over
    // the Flutter-default compileSdk that plugin modules otherwise inherit.
    plugins.withId("com.android.library") {
        // finalizeDsl runs after the plugin module's own build.gradle has set
        // compileSdk (to the Flutter default of 34) but before AGP reads it, so
        // this override actually wins.
        @Suppress("UnstableApiUsage")
        extensions.getByType<com.android.build.api.variant.LibraryAndroidComponentsExtension>()
            .finalizeDsl { dsl ->
                dsl.compileSdk = 36
            }
    }
}

subprojects {
    project.evaluationDependsOn(":app")
}

tasks.register<Delete>("clean") {
    delete(rootProject.layout.buildDirectory)
}
