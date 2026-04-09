plugins {
    kotlin("jvm") version "2.3.20"
    id("org.jetbrains.intellij.platform") version "2.13.1"
    id("org.jlleitschuh.gradle.ktlint") version "14.2.0"
    jacoco
}

group = providers.gradleProperty("pluginGroup").get()
version = providers.gradleProperty("pluginVersion").get()

jacoco {
    // Keep JaCoCo current for JDK 21 and IntelliJ-platform test runtimes.
    toolVersion = "0.8.13"
}

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
    testImplementation(kotlin("test"))
    testImplementation("org.junit.jupiter:junit-jupiter:6.0.3")
}

val bundledRuntimeOutputDir = layout.buildDirectory.dir("generated/querylens-runtime")

val bundleQueryLensRuntime by tasks.registering {
    // -------------------------------------------------------------------------
    // IMPORTANT — configuration cache compatibility
    //
    // Gradle's configuration cache serializes task *actions* (doLast lambdas)
    // and all values they capture. If lspCsprojPath / daemonCsprojPath / rids
    // were declared at script (Build_gradle) scope, the doLast lambda would
    // capture Build_gradle as $$implicitReceiver_Project. Gradle nulls that
    // object out before executing actions, causing:
    //   Cannot invoke "Build_gradle.getLspProjectPath()" because "this.this$0" is null
    //
    // By declaring them here (task configuration block, outside doLast), doLast
    // only captures plain String / List<String> values — serializable by the
    // config cache. notCompatibleWithConfigurationCache is also set as a
    // belt-and-suspenders guard so this task is always skipped by the cache even
    // if a linter or merge ever moves the declarations back to script scope.
    // -------------------------------------------------------------------------
    notCompatibleWithConfigurationCache("bundleQueryLensRuntime runs dotnet publish via ProcessBuilder")

    val outputDir = bundledRuntimeOutputDir
    outputs.dir(outputDir)

    val lspSourceDir = projectDir.resolve("../../../src/EFQueryLens.Lsp")
    val daemonSourceDir = projectDir.resolve("../../../src/EFQueryLens.Daemon")
    val coreSourceDir = projectDir.resolve("../../../src/EFQueryLens.Core")
    val formatterSourceDir = projectDir.resolve("../../../src/EFQueryLens.Formatter")

    // Ensure runtime rebundles whenever backend source/config changes.
    inputs.files(
        fileTree(lspSourceDir) {
            include("**/*.cs", "**/*.csproj", "**/*.props", "**/*.targets", "**/*.json")
        },
        fileTree(daemonSourceDir) {
            include("**/*.cs", "**/*.csproj", "**/*.props", "**/*.targets", "**/*.json")
        },
        fileTree(coreSourceDir) {
            include("**/*.cs", "**/*.csproj", "**/*.props", "**/*.targets", "**/*.json")
        },
        fileTree(formatterSourceDir) {
            include("**/*.cs", "**/*.csproj", "**/*.props", "**/*.targets", "**/*.json")
        },
        projectDir.resolve("../../../Directory.Build.props"),
        projectDir.resolve("../../../global.json"),
        projectDir.resolve("../../../nuget.config"),
    )

    // Compute these as plain Strings at configuration time, captured by doLast.
    val lspCsprojPath = projectDir.resolve("../../../src/EFQueryLens.Lsp/EFQueryLens.Lsp.csproj").canonicalPath
    val daemonCsprojPath = projectDir.resolve("../../../src/EFQueryLens.Daemon/EFQueryLens.Daemon.csproj").canonicalPath
    // .NET RIDs to bundle inside the plugin ZIP.
    // dotnet publish produces a native AppHost launcher per RID (framework-dependent, not self-contained),
    // so the daemon runs natively on each platform without needing `dotnet` in PATH explicitly —
    // Rider's own embedded .NET runtime is used via the AppHost.
    val rids = listOf("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")

    doLast {
        val out = outputDir.get().asFile

        fun dotnetPublish(vararg args: String) {
            val cmd = listOf("dotnet", "publish") + args.toList()
            val process = ProcessBuilder(cmd).inheritIO().start()
            val exit = process.waitFor()
            if (exit != 0) throw GradleException("dotnet publish failed (exit $exit): ${args.joinToString(" ")}")
        }

        // LSP: portable framework-dependent DLL, no AppHost.
        // Always launched as `dotnet EFQueryLens.Lsp.dll` by Rider's GeneralCommandLine — no AppHost needed.
        dotnetPublish(
            lspCsprojPath,
            "-c",
            "Release",
            "--no-self-contained",
            "/p:UseAppHost=false",
            "--output",
            out.resolve("server").absolutePath,
        )

        // Daemon: framework-dependent AppHost per RID.
        // --no-self-contained: DLL requires .NET (supplied by Rider).
        // /p:UseAppHost=true -r <rid>: adds the tiny native launcher (.exe / bare) for that platform.
        // EngineDiscovery.cs finds the AppHost adjacent to the DLL and uses it directly.
        for (rid in rids) {
            dotnetPublish(
                daemonCsprojPath,
                "-c",
                "Release",
                "--no-self-contained",
                "-r",
                rid,
                "/p:UseAppHost=true",
                "--output",
                out.resolve("daemon/$rid").absolutePath,
            )
        }
    }
}

/**
 * Parses CHANGELOG.md and returns the content of the most recent released version
 * (the first `## [x.y.z]` section, skipping `## [Unreleased]`) as HTML suitable
 * for the JetBrains Marketplace `changeNotes` field.
 *
 * Converts `### Heading` → `<p><strong>…</strong></p><ul>` and `- item` → `<li>`.
 */
fun extractLatestChangelogAsHtml(changelogFile: File): String {
    if (!changelogFile.exists()) return ""

    val lines = changelogFile.readLines()
    val html = StringBuilder()
    var inVersionSection = false
    var inSubsection = false

    for (line in lines) {
        if (line.startsWith("## [")) {
            if (line.contains("[Unreleased]", ignoreCase = true)) continue
            if (inVersionSection) break // reached the next released version — stop
            inVersionSection = true
            continue
        }
        if (!inVersionSection) continue

        if (line.startsWith("### ")) {
            if (inSubsection) html.append("</ul>")
            html.append("<p><strong>${line.removePrefix("### ").trim()}</strong></p><ul>")
            inSubsection = true
            continue
        }

        val trimmed = line.trim()
        if (trimmed.startsWith("- ") || trimmed.startsWith("* ")) {
            val text = trimmed.removePrefix("- ").removePrefix("* ")
            html.append("<li>$text</li>")
        }
    }

    if (inSubsection) html.append("</ul>")
    return html.toString()
}

intellijPlatform {
    buildSearchableOptions = false

    pluginConfiguration {
        ideaVersion {
            sinceBuild = providers.gradleProperty("pluginSinceBuild")
            untilBuild = providers.gradleProperty("pluginUntilBuild")
        }

        changeNotes =
            providers.provider {
                extractLatestChangelogAsHtml(projectDir.resolve("../../../CHANGELOG.md"))
            }
    }
}

tasks {
    test {
        useJUnitPlatform()

        // Rider/IntelliJ tests use custom classloaders; include no-location classes so
        // JaCoCo can attribute execution to plugin classes instead of reporting 0%.
        extensions.configure<org.gradle.testing.jacoco.plugins.JacocoTaskExtension> {
            isIncludeNoLocationClasses = true
            excludes = listOf("jdk.internal.*")
        }
    }

    jacocoTestReport {
        dependsOn(test)

        reports {
            xml.required = true
            csv.required = true
            html.required = true
        }
    }

    publishPlugin {
        channels = listOf(providers.gradleProperty("pluginChannel").get())
        // Token is supplied via JETBRAINS_PUBLISH_TOKEN env var in CI.
        // For local publishing: set JETBRAINS_PUBLISH_TOKEN in your shell.
        token = providers.environmentVariable("JETBRAINS_PUBLISH_TOKEN")
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

        val runtimeDir = bundledRuntimeOutputDir.get().asFile

        // Detect the RID for the machine running `runIde` so we point at the right daemon binary.
        val os = System.getProperty("os.name").lowercase()
        val arch = System.getProperty("os.arch").lowercase()
        val isArm = arch == "aarch64"
        val currentRid =
            when {
                os.contains("win") -> if (isArm) "win-arm64" else "win-x64"
                os.contains("mac") -> if (isArm) "osx-arm64" else "osx-x64"
                else -> if (isArm) "linux-arm64" else "linux-x64"
            }

        environment("QUERYLENS_LSP_DLL", runtimeDir.resolve("server/EFQueryLens.Lsp.dll").absolutePath)
        // QUERYLENS_DAEMON_DLL points at the RID-specific directory so EngineDiscovery also
        // finds the adjacent AppHost executable (EFQueryLens.Daemon.exe / EFQueryLens.Daemon).
        environment("QUERYLENS_DAEMON_DLL", runtimeDir.resolve("daemon/$currentRid/EFQueryLens.Daemon.dll").absolutePath)
    }

    wrapper {
        gradleVersion = providers.gradleProperty("gradleVersion").get()
    }
}
