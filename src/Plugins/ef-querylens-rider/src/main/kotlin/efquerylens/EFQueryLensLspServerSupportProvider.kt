package efquerylens

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.thisLogger
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspServer
import com.intellij.platform.lsp.api.LspServerManager
import com.intellij.platform.lsp.api.LspServerSupportProvider
import com.intellij.platform.lsp.api.ProjectWideLspServerDescriptor
import org.eclipse.lsp4j.ExecuteCommandParams
import java.nio.charset.StandardCharsets
import java.nio.file.Path
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import kotlin.io.path.absolutePathString
import kotlin.io.path.exists
import kotlin.io.path.isDirectory
import kotlin.io.path.isRegularFile
import kotlin.io.path.name
import kotlin.io.path.pathString

class EFQueryLensLspServerSupportProvider : LspServerSupportProvider {
    private val warmedDocumentUrls = ConcurrentHashMap.newKeySet<String>()
    @Volatile
    private var startupRestartRequested = false
    private val startupRestartLock = Any()

    override fun fileOpened(project: Project, file: VirtualFile, serverStarter: LspServerSupportProvider.LspServerStarter) {
        logInfo(project, "[EFQueryLens] fileOpened path='${file.path}' extension='${file.extension}'")
        if (!isSupported(file)) {
            logInfo(project, "[EFQueryLens] fileOpened skipped unsupported file '${file.path}'")
            return
        }

        logInfo(project, "[EFQueryLens] Ensuring LSP server is started for '${file.path}'")
        serverStarter.ensureServerStarted(EFQueryLensServerDescriptor(project))
        
        val server = LspServerManager.getInstance(project).getServersForProvider(EFQueryLensLspServerSupportProvider::class.java).firstOrNull()
        if (server != null) {
            scheduleStartupPlumbing(server, file)
        }
    }

    private fun scheduleStartupPlumbing(server: LspServer, file: VirtualFile) {
        ApplicationManager.getApplication().executeOnPooledThread {
            try {
                // Wait a bit for the server to be fully ready if it just started
                Thread.sleep(1000)
                requestDaemonRestartOnActivate(server)
                requestWarmup(server, file)
            } catch (e: Exception) {
                logWarn(server.project, "[EFQueryLens] Startup plumbing failed", e)
            }
        }
    }

    private fun requestDaemonRestartOnActivate(server: LspServer) {
        if (startupRestartRequested) {
            return
        }

        synchronized(startupRestartLock) {
            if (startupRestartRequested) {
                return
            }

            startupRestartRequested = true
        }

        try {
            val response = server.sendRequestSync(10000) {
                it.workspaceService.executeCommand(
                    ExecuteCommandParams("efquerylens.daemon.restart", emptyList())
                )
            }

            logInfo(server.project, "[EFQueryLens] Startup daemon restart response='$response'")
        } catch (e: Exception) {
            logWarn(server.project, "[EFQueryLens] Startup daemon restart failed", e)
        }
    }

    private fun requestWarmup(server: LspServer, file: VirtualFile) {
        if (!warmedDocumentUrls.add(file.url)) {
            return
        }

        try {
            val warmupPayload = mapOf(
                "textDocument" to mapOf("uri" to file.url),
                "position" to mapOf("line" to 0, "character" to 0)
            )

            val response = server.sendRequestSync(5000) {
                it.workspaceService.executeCommand(
                    ExecuteCommandParams("efquerylens.warmup", listOf(warmupPayload))
                )
            }

            logInfo(server.project, "[EFQueryLens] Warmup command completed for '${file.path}' response='$response'")
        } catch (e: Exception) {
            warmedDocumentUrls.remove(file.url)
            logWarn(server.project, "[EFQueryLens] Warmup command failed for '${file.path}'", e)
        }
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
        private const val LspDllOverrideEnvVar = "QUERYLENS_LSP_DLL"
    }

    override fun isSupportedFile(file: VirtualFile): Boolean {
        return file.extension.equals("cs", ignoreCase = true)
    }

    override fun createCommandLine(): GeneralCommandLine {
        val projectBasePath = hostProject.basePath
            ?: error("Cannot start EF QueryLens language server: project has no base path.")

        val workspaceRoot = Path.of(projectBasePath).toAbsolutePath().normalize()
        val lspLogFilePath = buildLspLogFilePath(projectBasePath)

        val lspDllOverride = resolveLspDllOverride()
        if (lspDllOverride != null) {
            logInfo("[EFQueryLens] Starting EF QueryLens LSP from override '${lspDllOverride.pathString}'")
            val workDir = workspaceRoot
            return GeneralCommandLine("dotnet", lspDllOverride.pathString)
                .withWorkDirectory(workDir.toFile())
                .applyQueryLensEnvironment(workspaceRoot, lspLogFilePath)
        }

        val lspDll = resolvePackagedLspDll()
            ?: error(
                "Cannot locate EFQueryLens packaged runtime under '/server'. " +
                    "Set $LspDllOverrideEnvVar to override.")

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
        val pluginRoot = resolvePluginRoot() ?: return null
        val candidates = listOf(
            pluginRoot.resolve("server").resolve("EFQueryLens.Lsp.dll")
        )

        return candidates.firstOrNull { it.exists() && it.isRegularFile() }
    }

    private fun resolvePluginRoot(): Path? {
        return try {
            val location = EFQueryLensLspServerSupportProvider::class.java.protectionDomain.codeSource?.location
                ?: return null
            val codeSourcePath = Path.of(location.toURI()).toAbsolutePath().normalize()

            if (codeSourcePath.isRegularFile()) {
                val parent = codeSourcePath.parent ?: return null
                return if (parent.name.equals("lib", ignoreCase = true) && parent.parent != null) {
                    parent.parent
                } else {
                    parent
                }
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

            null
        } catch (_: Exception) {
            null
        }
    }

    private fun buildLspLogFilePath(projectBasePath: String): Path {
        val workspacePath = Path.of(projectBasePath).toAbsolutePath().normalize()
        val hash = hashWorkspacePath(workspacePath.absolutePathString())
        return Path.of(System.getProperty("java.io.tmpdir"), "EFQueryLens", "rider-logs", "lsp-$hash.log")
    }

    private fun hashWorkspacePath(path: String): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(path.toByteArray(StandardCharsets.UTF_8))
        return digest.joinToString("") { "%02x".format(it) }.take(16)
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
        // Show a lightweight progress indicator if hover translation is slow.
        withEnvironment("QUERYLENS_HOVER_PROGRESS_NOTIFY", "1")
        withEnvironment("QUERYLENS_HOVER_PROGRESS_DELAY_MS", "350")
        // Rider cold starts can take longer to bring daemon online than VS Code.
        withEnvironment("QUERYLENS_DAEMON_START_TIMEOUT_MS", "30000")
        withEnvironment("QUERYLENS_DAEMON_CONNECT_TIMEOUT_MS", "10000")
        withEnvironment("QUERYLENS_DAEMON_SHUTDOWN_ON_DISPOSE", "1")
        withEnvironment("QUERYLENS_LSP_LOG_FILE", lspLogFilePath.absolutePathString())

        val workspacePath = workspaceRoot.absolutePathString()
        withEnvironment("QUERYLENS_WORKSPACE", workspacePath)
        withEnvironment("QUERYLENS_DAEMON_WORKSPACE", workspacePath)

        resolveDaemonExecutable()?.let {
            withEnvironment("QUERYLENS_DAEMON_EXE", it.absolutePathString())
        }

        resolveDaemonAssembly()?.let {
            withEnvironment("QUERYLENS_DAEMON_DLL", it.absolutePathString())
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
        val candidates = listOf(
            pluginRoot.resolve("daemon").resolve("EFQueryLens.Daemon.exe")
        )

        return candidates.firstOrNull { it.exists() && it.isRegularFile() }
    }

    private fun resolvePackagedDaemonAssembly(): Path? {
        val pluginRoot = resolvePluginRoot() ?: return null
        val candidates = listOf(
            pluginRoot.resolve("daemon").resolve("EFQueryLens.Daemon.dll")
        )

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
