plugins {
    kotlin("jvm") version "2.2.0"
    id("org.jetbrains.intellij.platform") version "2.12.0"
    id("org.jlleitschuh.gradle.ktlint") version "14.1.0"
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
    val debugDir = projectDir.resolve("../../../src/$projectName/bin/Debug/net10.0")

    val candidates = listOf(releaseDir, debugDir)
        .map { it.resolve(requiredFileName) }
        .filter { it.exists() }

    if (candidates.isNotEmpty()) {
        val newest = candidates.maxByOrNull { it.lastModified() }
            ?: throw GradleException("Unable to select runtime artifact for $projectName/$requiredFileName.")
        return newest.parentFile
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
    publishPlugin {
        channels = listOf(providers.gradleProperty("pluginChannel").get())
    }

    val pluginRootInArchive = project.name

    prepareSandbox {
        dependsOn(bundleQueryLensRuntime)
        from(bundledRuntimeOutputDir) {
            into(pluginRootInArchive)
        }
    }

    runIde {
        dependsOn(bundleQueryLensRuntime)
        environment("QUERYLENS_CLIENT", "rider")
        environment("QUERYLENS_STARTUP_BROWSER", "true")
        environment("QUERYLENS_DEBUG", "true")
        environment("QUERYLENS_FORCE_CODELENS", "true")
        // Point at built runtime so runIde finds LSP/daemon even when sandbox layout differs from plugin root
        val runtimeDir = layout.buildDirectory.dir("generated/querylens-runtime").get().asFile
        environment("QUERYLENS_LSP_DLL", runtimeDir.resolve("server/EFQueryLens.Lsp.dll").absolutePath)
        environment("QUERYLENS_DAEMON_EXE", runtimeDir.resolve("daemon/EFQueryLens.Daemon.exe").absolutePath)
        environment("QUERYLENS_DAEMON_DLL", runtimeDir.resolve("daemon/EFQueryLens.Daemon.dll").absolutePath)
    }

    wrapper {
        gradleVersion = providers.gradleProperty("gradleVersion").get()
    }
}
