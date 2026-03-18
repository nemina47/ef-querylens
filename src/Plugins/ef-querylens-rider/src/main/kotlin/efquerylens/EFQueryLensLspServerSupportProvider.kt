package efquerylens

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.ide.plugins.PluginManagerCore
import com.intellij.openapi.diagnostic.thisLogger
import com.intellij.openapi.extensions.PluginId
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspServerSupportProvider
import com.intellij.platform.lsp.api.ProjectWideLspServerDescriptor
import java.nio.file.Path
import kotlin.io.path.absolutePathString
import kotlin.io.path.exists
import kotlin.io.path.isDirectory
import kotlin.io.path.isRegularFile
import kotlin.io.path.name
import kotlin.io.path.pathString

class EFQueryLensLspServerSupportProvider : LspServerSupportProvider {
    override fun fileOpened(project: Project, file: VirtualFile, serverStarter: LspServerSupportProvider.LspServerStarter) {
        logInfo(project, "[EFQueryLens] fileOpened path='${file.path}' extension='${file.extension}'")
        if (!isSupported(file)) {
            logInfo(project, "[EFQueryLens] fileOpened skipped unsupported file '${file.path}'")
            return
        }

        logInfo(project, "[EFQueryLens] Ensuring LSP server is started for '${file.path}'")
        serverStarter.ensureServerStarted(EFQueryLensServerDescriptor(project))
    }

    private fun isSupported(file: VirtualFile): Boolean {
        return file.extension.equals("cs", ignoreCase = true)
    }

    private fun logInfo(project: Project, message: String) {
        thisLogger().info(message)
    }

    private fun logWarn(project: Project, message: String, error: Throwable? = null) {
        if (error == null) {
            thisLogger().warn(message)
            return
        }

        thisLogger().warn(message, error)
    }
}

private class EFQueryLensServerDescriptor(
    private val hostProject: Project
) : ProjectWideLspServerDescriptor(hostProject, "EF QueryLens") {
    private companion object {
        private const val PluginIdValue = "dev.efquerylens"
        private const val LspDllOverrideEnvVar = "QUERYLENS_LSP_DLL"
    }

    override fun isSupportedFile(file: VirtualFile): Boolean {
        return file.extension.equals("cs", ignoreCase = true)
    }

    override fun createCommandLine(): GeneralCommandLine {
        val projectBasePath = hostProject.basePath
            ?: error("Cannot start EF QueryLens language server: project has no base path.")

        val logIdentity = WorkspaceLogIdentityResolver.fromProjectBasePath(projectBasePath)

        val workspaceRoot = Path.of(projectBasePath).toAbsolutePath().normalize()
        val lspLogFilePath = logIdentity.logFilePath

        logInfo(
            "[EFQueryLens] log identity workspace='${logIdentity.workspacePath.absolutePathString()}' " +
                "hash='${logIdentity.hash}' file='${logIdentity.logFilePath.absolutePathString()}'"
        )

        val lspDllOverride = resolveLspDllOverride()
        if (lspDllOverride != null) {
            logInfo("[EFQueryLens] Starting EF QueryLens LSP from override '${lspDllOverride.pathString}'")
            val workDir = workspaceRoot
            return GeneralCommandLine("dotnet", lspDllOverride.pathString)
                .withWorkDirectory(workDir.toFile())
                .applyQueryLensEnvironment(workspaceRoot, lspLogFilePath)
        }

        val lspDll = resolvePackagedLspDll()
        if (lspDll == null) {
            logWarn(
                "[EFQueryLens] Cannot locate packaged runtime (server/EFQueryLens.Lsp.dll). " +
                    "Check logs above for pluginRoot and candidates. Set $LspDllOverrideEnvVar to override."
            )
            error(
                "Cannot locate EFQueryLens packaged runtime (server/EFQueryLens.Lsp.dll). " +
                    "Set $LspDllOverrideEnvVar to override."
            )
        }

        logInfo("[EFQueryLens] Starting EF QueryLens LSP from packaged runtime '${lspDll.pathString}'")

        return GeneralCommandLine("dotnet", lspDll.pathString)
            .withWorkDirectory(workspaceRoot.toFile())
            .applyQueryLensEnvironment(workspaceRoot, lspLogFilePath)
    }

    private fun resolveLspDllOverride(): Path? {
        val raw = System.getenv(LspDllOverrideEnvVar)
        if (raw.isNullOrBlank()) {
            return null
        }

        val candidate = Path.of(raw).toAbsolutePath().normalize()
        if (candidate.isRegularFile()) {
            return candidate
        }

        logWarn("Ignoring $LspDllOverrideEnvVar='$candidate' because the file does not exist.")
        return null
    }

    private fun resolvePackagedLspDll(): Path? {
        val pluginRoot = resolvePluginRoot()
        if (pluginRoot == null) {
            logWarn("[EFQueryLens] Cannot resolve plugin root (codeSource unavailable or path not under lib/). LSP will not start from packaged runtime.")
            return null
        }
        // Packaged/distributed plugin: server/ next to lib/ inside plugin root.
        // runIde sandbox: server/ is a sibling of the plugin dir (plugins/server, plugins/ef-querylens-rider).
        val serverDirs = listOfNotNull(
            pluginRoot.resolve("server"),
            pluginRoot.parent?.resolve("server")
        ).distinct()
        val candidates = serverDirs.flatMap { serverDir ->
            listOf(
                serverDir.resolve("EFQueryLens.Lsp.dll"),
                serverDir.resolve("win-x64").resolve("EFQueryLens.Lsp.dll"),
                serverDir.resolve("win-x64").resolve("publish").resolve("EFQueryLens.Lsp.dll"),
                serverDir.resolve("publish").resolve("EFQueryLens.Lsp.dll")
            )
        }

        val found = candidates.firstOrNull { it.exists() && it.isRegularFile() }
        if (found == null) {
            logWarn(
                "[EFQueryLens] Packaged runtime not found. pluginRoot=${pluginRoot.absolutePathString()}, " +
                    "serverDirs=${serverDirs.map { it.absolutePathString() }}, " +
                    "candidatesChecked=${candidates.map { it.absolutePathString() to (it.exists() to it.isRegularFile()) }}. " +
                    "Set $LspDllOverrideEnvVar to override."
            )
        }
        return found
    }

    private fun resolvePluginRoot(): Path? {
        val pluginPathFromManager = PluginManagerCore.getPlugin(PluginId.getId(PluginIdValue))?.pluginPath
        if (pluginPathFromManager != null) {
            val resolved = pluginPathFromManager.toAbsolutePath().normalize()
            logInfo("[EFQueryLens] resolvePluginRoot: plugin manager path='${resolved.absolutePathString()}'")
            return resolved
        }

        return try {
            val location = EFQueryLensLspServerSupportProvider::class.java.protectionDomain.codeSource?.location
                ?: run {
                    logWarn("[EFQueryLens] resolvePluginRoot: codeSource.location is null")
                    return null
                }
            val codeSourcePath = Path.of(location.toURI()).toAbsolutePath().normalize()

            if (codeSourcePath.isRegularFile()) {
                val parent = codeSourcePath.parent ?: return null
                val root = if (parent.name.equals("lib", ignoreCase = true) && parent.parent != null) {
                    parent.parent
                } else {
                    parent
                }
                return root
            }

            var current: Path? = codeSourcePath
            while (current != null) {
                if (current.resolve("server").isDirectory()) {
                    return current
                }

                if (current.name.equals("lib", ignoreCase = true) && current.parent != null) {
                    return current.parent
                }

                current = current.parent
            }

            logWarn("[EFQueryLens] resolvePluginRoot: no server/ or lib/ found walking up from codeSource=${codeSourcePath.absolutePathString()}")
            null
        } catch (e: Exception) {
            logWarn("[EFQueryLens] resolvePluginRoot failed", e)
            null
        }
    }

    private fun GeneralCommandLine.applyQueryLensEnvironment(
        workspaceRoot: Path,
        lspLogFilePath: Path
    ): GeneralCommandLine {
        withEnvironment("QUERYLENS_CLIENT", "rider")
        // Keep Rider diagnostics on by default so LSP/daemon logs are available in all runs.
        withEnvironment("QUERYLENS_DEBUG", "1")
        // Rider can cancel/re-issue hover requests aggressively; allow a short grace
        // window so canceled requests can still reuse an in-flight hover computation.
        withEnvironment("QUERYLENS_HOVER_CANCEL_GRACE_MS", "1200")
        // Return queued/starting states immediately in Rider so users see warmup feedback.
        withEnvironment("QUERYLENS_MARKDOWN_QUEUE_ADAPTIVE_WAIT_MS", "0")
        withEnvironment("QUERYLENS_STRUCTURED_QUEUE_ADAPTIVE_WAIT_MS", "0")
        // Show a lightweight progress indicator if hover translation is slow.
        withEnvironment("QUERYLENS_HOVER_PROGRESS_NOTIFY", "1")
        withEnvironment("QUERYLENS_HOVER_PROGRESS_DELAY_MS", "350")
        // Rider cold starts can take longer to bring daemon online than VS Code.
        withEnvironment("QUERYLENS_DAEMON_START_TIMEOUT_MS", "30000")
        withEnvironment("QUERYLENS_DAEMON_CONNECT_TIMEOUT_MS", "10000")
        withEnvironment("QUERYLENS_DAEMON_SHUTDOWN_ON_DISPOSE", "1")
        // Keep rolling-window latency at 20 samples by default, but honor explicit env overrides.
        val avgWindowSamples = System.getenv("QUERYLENS_AVG_WINDOW_SAMPLES")?.takeIf { it.isNotBlank() } ?: "20"
        withEnvironment("QUERYLENS_AVG_WINDOW_SAMPLES", avgWindowSamples)
        withEnvironment("QUERYLENS_LSP_LOG_FILE", lspLogFilePath.absolutePathString())

        val workspacePath = workspaceRoot.absolutePathString()
        withEnvironment("QUERYLENS_WORKSPACE", workspacePath)
        withEnvironment("QUERYLENS_DAEMON_WORKSPACE", workspacePath)

        // Prefer runIde/env daemon paths (like VS Code explicit env), then fall back to packaged resolution
        val daemonExeEnv = System.getenv("QUERYLENS_DAEMON_EXE")?.takeIf { it.isNotBlank() }
        val daemonDllEnv = System.getenv("QUERYLENS_DAEMON_DLL")?.takeIf { it.isNotBlank() }

        val daemonExePath: Pair<String?, String?> = when {
            daemonExeEnv != null && Path.of(daemonExeEnv).let { p: Path -> p.exists() && p.isRegularFile() } -> {
                withEnvironment("QUERYLENS_DAEMON_EXE", daemonExeEnv)
                daemonExeEnv to "runIde env"
            }
            daemonExeEnv != null -> {
                logWarn("[EFQueryLens] QUERYLENS_DAEMON_EXE set but file not found: $daemonExeEnv")
                Pair(null, null)
            }
            else -> resolveDaemonExecutable()?.let { p: Path ->
                withEnvironment("QUERYLENS_DAEMON_EXE", p.absolutePathString())
                p.absolutePathString() to "packaged"
            } ?: Pair(null, null)
        }

        val daemonDllPath: Pair<String?, String?> = when {
            daemonDllEnv != null && Path.of(daemonDllEnv).let { p: Path -> p.exists() && p.isRegularFile() } -> {
                withEnvironment("QUERYLENS_DAEMON_DLL", daemonDllEnv)
                daemonDllEnv to "runIde env"
            }
            daemonDllEnv != null -> {
                logWarn("[EFQueryLens] QUERYLENS_DAEMON_DLL set but file not found: $daemonDllEnv")
                Pair(null, null)
            }
            else -> resolveDaemonAssembly()?.let { p: Path ->
                withEnvironment("QUERYLENS_DAEMON_DLL", p.absolutePathString())
                p.absolutePathString() to "packaged"
            } ?: Pair(null, null)
        }

        val exeLog = daemonExePath.first?.let { "[EFQueryLens] daemon env EXE=$it (${daemonExePath.second})" }
        val dllLog = daemonDllPath.first?.let { "[EFQueryLens] daemon env DLL=$it (${daemonDllPath.second})" }
        if (exeLog != null) logInfo(exeLog)
        if (dllLog != null) logInfo(dllLog)
        if (daemonExePath.first == null && daemonDllPath.first == null) {
            logWarn("[EFQueryLens] No daemon EXE or DLL set; LSP may fail with daemon-launch-target-not-found.")
        }

        return this
    }

    private fun resolveDaemonExecutable(): Path? {
        return resolvePackagedDaemonExecutable()
    }

    private fun resolveDaemonAssembly(): Path? {
        return resolvePackagedDaemonAssembly()
    }

    private fun resolvePackagedDaemonExecutable(): Path? {
        val pluginRoot = resolvePluginRoot() ?: return null
        val daemonDirs = listOfNotNull(
            pluginRoot.resolve("daemon"),
            pluginRoot.parent?.resolve("daemon")
        ).distinct()
        val candidates = daemonDirs.flatMap { daemonDir ->
            listOf(
                daemonDir.resolve("EFQueryLens.Daemon.exe"),
                daemonDir.resolve("win-x64").resolve("EFQueryLens.Daemon.exe"),
                daemonDir.resolve("win-x64").resolve("publish").resolve("EFQueryLens.Daemon.exe")
            )
        }
        return candidates.firstOrNull { it.exists() && it.isRegularFile() }
    }

    private fun resolvePackagedDaemonAssembly(): Path? {
        val pluginRoot = resolvePluginRoot() ?: return null
        val daemonDirs = listOfNotNull(
            pluginRoot.resolve("daemon"),
            pluginRoot.parent?.resolve("daemon")
        ).distinct()
        val candidates = daemonDirs.flatMap { daemonDir ->
            listOf(
                daemonDir.resolve("EFQueryLens.Daemon.dll"),
                daemonDir.resolve("win-x64").resolve("EFQueryLens.Daemon.dll"),
                daemonDir.resolve("win-x64").resolve("publish").resolve("EFQueryLens.Daemon.dll")
            )
        }
        return candidates.firstOrNull { it.exists() && it.isRegularFile() }
    }

    private fun logInfo(message: String) {
        thisLogger().info(message)
    }

    private fun logWarn(message: String, error: Throwable? = null) {
        if (error == null) {
            thisLogger().warn(message)
            return
        }

        thisLogger().warn(message, error)
    }
}
