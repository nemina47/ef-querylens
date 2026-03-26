package efquerylens

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.ide.plugins.PluginManagerCore
import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.thisLogger
import com.intellij.openapi.extensions.PluginId
import com.intellij.openapi.ide.CopyPasteManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.Lsp4jClient
import com.intellij.platform.lsp.api.LspServerNotificationsHandler
import com.intellij.platform.lsp.api.LspServerSupportProvider
import com.intellij.platform.lsp.api.ProjectWideLspServerDescriptor
import org.eclipse.lsp4j.jsonrpc.services.JsonNotification
import java.awt.datatransfer.StringSelection
import java.nio.file.Path
import kotlin.io.path.absolutePathString
import kotlin.io.path.exists
import kotlin.io.path.isDirectory
import kotlin.io.path.isRegularFile
import kotlin.io.path.name
import kotlin.io.path.pathString

class EFQueryLensLspServerSupportProvider : LspServerSupportProvider {
    companion object {
        /**
         * Singleton action server shared across all projects in this IDE session.
         * Started lazily on first file open; lives for the IDE session lifetime.
         */
        private val actionServer: EFQueryLensActionServer by lazy {
            EFQueryLensActionServer().also { it.start() }
        }
    }

    override fun fileOpened(
        project: Project,
        file: VirtualFile,
        serverStarter: LspServerSupportProvider.LspServerStarter,
    ) {
        logInfo(project, "[EFQueryLens] fileOpened path='${file.path}' extension='${file.extension}'")
        if (!isSupported(file)) {
            logInfo(project, "[EFQueryLens] fileOpened skipped unsupported file '${file.path}'")
            return
        }

        // Ensure the action server is running and get its port before creating
        // the LSP server descriptor so it can be passed as an env var.
        val actionPort = actionServer.port
        logInfo(project, "[EFQueryLens] ActionServer port=$actionPort")

        logInfo(project, "[EFQueryLens] Ensuring LSP server is started for '${file.path}'")
        serverStarter.ensureServerStarted(EFQueryLensServerDescriptor(project, actionPort))
    }

    private fun isSupported(file: VirtualFile): Boolean = file.extension.equals("cs", ignoreCase = true)

    private fun logInfo(
        project: Project,
        message: String,
    ) {
        thisLogger().info(message)
    }

    private fun logWarn(
        project: Project,
        message: String,
        error: Throwable? = null,
    ) {
        if (error == null) {
            thisLogger().warn(message)
            return
        }
        thisLogger().warn(message, error)
    }
}

private class EFQueryLensServerDescriptor(
    private val hostProject: Project,
    private val actionPort: Int = 0,
) : ProjectWideLspServerDescriptor(hostProject, "EF QueryLens") {
    private companion object {
        private const val PLUGIN_ID_VALUE = "dev.efquerylens"
        private const val LSP_DLL_OVERRIDE_ENV_VAR = "QUERYLENS_LSP_DLL"
    }

    override fun isSupportedFile(file: VirtualFile): Boolean = file.extension.equals("cs", ignoreCase = true)

    override fun createLsp4jClient(handler: LspServerNotificationsHandler): Lsp4jClient = EFQueryLensClient(handler, hostProject)

    override fun createCommandLine(): GeneralCommandLine {
        val projectBasePath =
            hostProject.basePath
                ?: error("Cannot start EF QueryLens language server: project has no base path.")

        val logIdentity = WorkspaceLogIdentityResolver.fromProjectBasePath(projectBasePath)
        val workspaceRoot = Path.of(projectBasePath).toAbsolutePath().normalize()
        val lspLogFilePath = logIdentity.logFilePath

        logInfo(
            "[EFQueryLens] log identity workspace='${logIdentity.workspacePath.absolutePathString()}' hash='${logIdentity.hash}' file='${logIdentity.logFilePath.absolutePathString()}'",
        )

        val lspDllOverride = resolveLspDllOverride()
        if (lspDllOverride != null) {
            logInfo("[EFQueryLens] Starting EF QueryLens LSP from override '${lspDllOverride.pathString}'")
            return GeneralCommandLine("dotnet", lspDllOverride.pathString)
                .withWorkDirectory(workspaceRoot.toFile())
                .applyQueryLensEnvironment(workspaceRoot, lspLogFilePath)
        }

        val lspDll = resolvePackagedLspDll()
        if (lspDll == null) {
            error("Cannot locate EFQueryLens packaged runtime (server/EFQueryLens.Lsp.dll). Set $LSP_DLL_OVERRIDE_ENV_VAR to override.")
        }

        logInfo("[EFQueryLens] Starting EF QueryLens LSP from packaged runtime '${lspDll.pathString}'")

        return GeneralCommandLine("dotnet", lspDll.pathString)
            .withWorkDirectory(workspaceRoot.toFile())
            .applyQueryLensEnvironment(workspaceRoot, lspLogFilePath)
    }

    private fun resolveLspDllOverride(): Path? {
        val raw = System.getenv(LSP_DLL_OVERRIDE_ENV_VAR)
        if (raw.isNullOrBlank()) return null
        val candidate = Path.of(raw).toAbsolutePath().normalize()
        return if (candidate.isRegularFile()) candidate else null
    }

    private fun resolvePackagedLspDll(): Path? {
        val pluginRoot = resolvePluginRoot() ?: return null
        val serverDirs = listOfNotNull(pluginRoot.resolve("server"), pluginRoot.parent?.resolve("server")).distinct()
        val candidates =
            serverDirs.flatMap { serverDir ->
                listOf(
                    serverDir.resolve("EFQueryLens.Lsp.dll"),
                    serverDir.resolve("publish").resolve("EFQueryLens.Lsp.dll"),
                )
            }
        return candidates.firstOrNull { it.exists() && it.isRegularFile() }
    }

    private fun resolvePluginRoot(): Path? {
        val pluginPathFromManager = PluginManagerCore.getPlugin(PluginId.getId(PLUGIN_ID_VALUE))?.pluginPath
        if (pluginPathFromManager != null) return pluginPathFromManager.toAbsolutePath().normalize()

        return try {
            val location =
                EFQueryLensLspServerSupportProvider::class.java.protectionDomain.codeSource
                    ?.location ?: return null
            val codeSourcePath = Path.of(location.toURI()).toAbsolutePath().normalize()
            if (codeSourcePath.isRegularFile()) {
                val parent = codeSourcePath.parent ?: return null
                return if (parent.name.equals("lib", ignoreCase = true)) parent.parent else parent
            }
            var current: Path? = codeSourcePath
            while (current != null) {
                if (current.resolve("server").isDirectory()) return current
                if (current.name.equals("lib", ignoreCase = true)) return current.parent
                current = current.parent
            }
            null
        } catch (e: Exception) {
            null
        }
    }

    private fun GeneralCommandLine.applyQueryLensEnvironment(
        workspaceRoot: Path,
        lspLogFilePath: Path,
    ): GeneralCommandLine {
        withEnvironment("QUERYLENS_CLIENT", "rider")
        withEnvironment("QUERYLENS_DEBUG", "1")
        withEnvironment("QUERYLENS_HOVER_CANCEL_GRACE_MS", "1200")
        withEnvironment("QUERYLENS_MARKDOWN_QUEUE_ADAPTIVE_WAIT_MS", "0")
        withEnvironment("QUERYLENS_STRUCTURED_QUEUE_ADAPTIVE_WAIT_MS", "0")
        withEnvironment("QUERYLENS_HOVER_PROGRESS_NOTIFY", "1")
        withEnvironment("QUERYLENS_HOVER_PROGRESS_DELAY_MS", "350")
        withEnvironment("QUERYLENS_DAEMON_START_TIMEOUT_MS", "30000")
        withEnvironment("QUERYLENS_DAEMON_CONNECT_TIMEOUT_MS", "10000")
        withEnvironment("QUERYLENS_DAEMON_SHUTDOWN_ON_DISPOSE", "1")
        withEnvironment("QUERYLENS_AVG_WINDOW_SAMPLES", System.getenv("QUERYLENS_AVG_WINDOW_SAMPLES")?.takeIf { it.isNotBlank() } ?: "20")
        withEnvironment("QUERYLENS_LSP_LOG_FILE", lspLogFilePath.absolutePathString())
        val workspacePath = workspaceRoot.absolutePathString()
        withEnvironment("QUERYLENS_WORKSPACE", workspacePath)
        withEnvironment("QUERYLENS_DAEMON_WORKSPACE", workspacePath)

        resolvePackagedDaemonExecutable()?.let { withEnvironment("QUERYLENS_DAEMON_EXE", it.absolutePathString()) }
        resolvePackagedDaemonAssembly()?.let { withEnvironment("QUERYLENS_DAEMON_DLL", it.absolutePathString()) }

        if (actionPort > 0) {
            withEnvironment("QUERYLENS_ACTION_PORT", actionPort.toString())
        }

        return this
    }

    /**
     * Returns the .NET RID string that matches the JVM's current OS and CPU architecture.
     * Used to select the correct per-platform daemon binary from inside the plugin ZIP.
     */
    private fun currentRid(): String {
        val os = System.getProperty("os.name").lowercase()
        val arch = System.getProperty("os.arch").lowercase()
        val isArm = arch == "aarch64"
        return when {
            os.contains("win") -> if (isArm) "win-arm64" else "win-x64"
            os.contains("mac") -> if (isArm) "osx-arm64" else "osx-x64"
            else -> if (isArm) "linux-arm64" else "linux-x64"
        }
    }

    private fun resolvePackagedDaemonExecutable(): Path? {
        val pluginRoot = resolvePluginRoot() ?: return null
        val rid = currentRid()
        val isWindows = rid.startsWith("win")
        val exeName = if (isWindows) "EFQueryLens.Daemon.exe" else "EFQueryLens.Daemon"
        val daemonDirs = listOfNotNull(pluginRoot.resolve("daemon"), pluginRoot.parent?.resolve("daemon")).distinct()
        // Prefer the platform-specific AppHost inside daemon/<rid>/; fall back to root daemon dir.
        val candidates =
            daemonDirs.flatMap { daemonDir ->
                listOf(
                    daemonDir.resolve(rid).resolve(exeName),
                    daemonDir.resolve(exeName),
                )
            }
        return candidates.firstOrNull { it.exists() && it.isRegularFile() }
    }

    private fun resolvePackagedDaemonAssembly(): Path? {
        val pluginRoot = resolvePluginRoot() ?: return null
        val rid = currentRid()
        val daemonDirs = listOfNotNull(pluginRoot.resolve("daemon"), pluginRoot.parent?.resolve("daemon")).distinct()
        // Prefer the RID-specific directory so EngineDiscovery also finds the adjacent AppHost.
        // Fall back to the root daemon dir (framework-dependent DLL without AppHost).
        val candidates =
            daemonDirs.flatMap { daemonDir ->
                listOf(
                    daemonDir.resolve(rid).resolve("EFQueryLens.Daemon.dll"),
                    daemonDir.resolve("EFQueryLens.Daemon.dll"),
                )
            }
        return candidates.firstOrNull { it.exists() && it.isRegularFile() }
    }

    private fun logInfo(message: String) = thisLogger().info(message)

    private fun logWarn(
        message: String,
        error: Throwable? = null,
    ) {
        if (error == null) thisLogger().warn(message) else thisLogger().warn(message, error)
    }
}

private class EFQueryLensClient(
    handler: LspServerNotificationsHandler,
    private val project: Project,
) : Lsp4jClient(handler) {
    @JsonNotification("efquerylens/showSqlPreview")
    fun showSqlPreview(payload: Any?) {
        val root = payload as? Map<String, Any?> ?: return
        val fallbackFileUri = root["fallbackFileUri"] as? String ?: ""
        val fallbackLine = (root["fallbackLine"] as? Number)?.toInt() ?: 0

        val opener = EFQueryLensUrlOpener()
        val preview = opener.extractStructuredPreview(root, fallbackFileUri, fallbackLine)
        if (preview != null) {
            opener.openSqlInEditor(project, preview)
        }
    }

    @JsonNotification("efquerylens/showSqlPopup")
    fun showSqlPopup(payload: Any?) {
        val root = payload as? Map<String, Any?> ?: return
        val fallbackFileUri = root["fallbackFileUri"] as? String ?: ""
        val fallbackLine = (root["fallbackLine"] as? Number)?.toInt() ?: 0
        val opener = EFQueryLensUrlOpener()
        val preview = opener.extractStructuredPreview(root, fallbackFileUri, fallbackLine) ?: return
        opener.showSqlPopup(project, preview)
    }

    @JsonNotification("efquerylens/copySqlToClipboard")
    fun copySqlToClipboard(payload: Any?) {
        val root = payload as? Map<String, Any?> ?: return
        val sql = root["sql"] as? String ?: return
        CopyPasteManager.getInstance().setContents(StringSelection(sql))
        ApplicationManager.getApplication().invokeLater {
            NotificationGroupManager
                .getInstance()
                .getNotificationGroup("EF QueryLens")
                .createNotification("SQL copied to clipboard", NotificationType.INFORMATION)
                .notify(project)
        }
    }
}
