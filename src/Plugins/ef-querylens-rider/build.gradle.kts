plugins {
    kotlin("jvm") version "2.2.0"
    id("org.jetbrains.intellij.platform") version "2.12.0"
}

import org.gradle.api.GradleException
import org.gradle.api.tasks.Sync

group = providers.gradleProperty("pluginGroup").get()
version = providers.gradleProperty("pluginVersion").get()

kotlin {
    jvmToolchain(21)
}

repositories {
    mavenCentral()

    intellijPlatform {
        defaultRepositories()
    }
}

dependencies {
    intellijPlatform {
        rider(providers.gradleProperty("platformVersion")) {
            useInstaller = false
        }
    }
    // CommonMark spec-compliant Markdown to HTML (replaces custom regex conversion)
    implementation("org.commonmark:commonmark:0.27.1")
}

val bundledRuntimeOutputDir = layout.buildDirectory.dir("generated/querylens-runtime")

fun resolveRuntimeBuildDir(projectName: String, requiredFileName: String): File {
    val releaseDir = projectDir.resolve("../../../src/$projectName/bin/Release/net10.0")
    if (releaseDir.resolve(requiredFileName).exists()) {
        return releaseDir
    }

    val debugDir = projectDir.resolve("../../../src/$projectName/bin/Debug/net10.0")
    if (debugDir.resolve(requiredFileName).exists()) {
        return debugDir
    }

    throw GradleException(
        "Could not find $requiredFileName for $projectName. Build $projectName first (Debug or Release, net10.0)."
    )
}

val bundleQueryLensRuntime by tasks.registering(Sync::class) {
    into(bundledRuntimeOutputDir)

    from(providers.provider { resolveRuntimeBuildDir("EFQueryLens.Lsp", "EFQueryLens.Lsp.dll") }) {
        into("server")
    }

    from(providers.provider { resolveRuntimeBuildDir("EFQueryLens.Daemon", "EFQueryLens.Daemon.dll") }) {
        into("daemon")
    }
}

intellijPlatform {
    pluginConfiguration {
        ideaVersion {
            sinceBuild = providers.gradleProperty("pluginSinceBuild")
            untilBuild = providers.gradleProperty("pluginUntilBuild")
        }
    }
}

tasks {
    prepareSandbox {
        dependsOn(bundleQueryLensRuntime)
        from(bundledRuntimeOutputDir)
    }

    runIde {
        dependsOn(bundleQueryLensRuntime)
        environment("QUERYLENS_CLIENT", "rider")
        environment("QUERYLENS_STARTUP_BROWSER", "true")
        environment("QUERYLENS_DEBUG", "true")
        environment("QUERYLENS_FORCE_CODELENS", "true")
    }

    wrapper {
        gradleVersion = providers.gradleProperty("gradleVersion").get()
    }
}
