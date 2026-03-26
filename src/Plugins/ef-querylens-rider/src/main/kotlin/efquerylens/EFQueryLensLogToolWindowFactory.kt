package efquerylens

import com.intellij.ide.BrowserUtil
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.wm.ToolWindow
import com.intellij.openapi.wm.ToolWindowFactory
import com.intellij.openapi.wm.WindowManager
import com.intellij.ui.content.ContentFactory
import com.intellij.util.Alarm
import java.awt.BorderLayout
import java.nio.charset.StandardCharsets
import java.nio.file.FileSystems
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardWatchEventKinds
import java.nio.file.WatchService
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.ArrayDeque
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import javax.swing.JButton
import javax.swing.JLabel
import javax.swing.JPanel
import javax.swing.JScrollPane
import javax.swing.JTextArea
import kotlin.io.path.absolutePathString
import kotlin.io.path.exists

class EFQueryLensLogToolWindowFactory :
    ToolWindowFactory,
    DumbAware {
    companion object {
        const val TOOL_WINDOW_ID = "EF QueryLens"
    }

    override fun shouldBeAvailable(project: Project): Boolean = true

    override fun createToolWindowContent(
        project: Project,
        toolWindow: ToolWindow,
    ) {
        val contentPanel = JPanel(BorderLayout())
        val toolbar = JPanel()
        val statusLabel = JLabel("Initializing EF QueryLens logs...")
        val textArea =
            JTextArea().apply {
                isEditable = false
                lineWrap = false
                wrapStyleWord = false
            }

        val refreshButton = JButton("Refresh")
        val openFolderButton = JButton("Open Log Folder")
        val openOutputButton = JButton("Open Output")

        toolbar.add(refreshButton)
        toolbar.add(openFolderButton)
        toolbar.add(openOutputButton)

        contentPanel.add(toolbar, BorderLayout.NORTH)
        contentPanel.add(JScrollPane(textArea), BorderLayout.CENTER)
        contentPanel.add(statusLabel, BorderLayout.SOUTH)

        val content = ContentFactory.getInstance().createContent(contentPanel, "", false)
        toolWindow.contentManager.addContent(content)

        val alarm = Alarm(Alarm.ThreadToUse.POOLED_THREAD, content)
        val disposed = AtomicBoolean(false)
        val formatter = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss")
        val projectBasePath = project.basePath
        val logIdentity = projectBasePath?.let { WorkspaceLogIdentityResolver.fromProjectBasePath(it) }
        var watchService: WatchService? = null

        fun refreshNow() {
            val identity = logIdentity
            if (identity == null) {
                ApplicationManager.getApplication().invokeLater {
                    if (!project.isDisposed) {
                        statusLabel.text = "Project base path is unavailable; cannot resolve EF QueryLens log file."
                    }
                }
                return
            }

            val expectedLogFile = identity.logFilePath
            val fallbackLogFile = if (Files.exists(expectedLogFile)) null else findLatestLogFile()
            val logFile = fallbackLogFile ?: expectedLogFile
            val logText = readLogTail(logFile, maxLines = 800)
            val status =
                buildString {
                    append("Workspace: ")
                    append(identity.workspacePath.absolutePathString())
                    append(" | Hash: ")
                    append(identity.hash)
                    append(" | Expected Log: ")
                    append(expectedLogFile.absolutePathString())
                    if (fallbackLogFile != null) {
                        append(" | Active Log: ")
                        append(fallbackLogFile.absolutePathString())
                    }
                    append(" | Reading: ")
                    append(logFile.absolutePathString())
                    if (Files.exists(logFile)) {
                        val modified = Files.getLastModifiedTime(logFile).toInstant()
                        append(" | Updated: ")
                        append(formatter.format(modified.atZone(ZoneId.systemDefault())))
                    } else {
                        append(" | Waiting for log file...")
                    }
                }

            ApplicationManager.getApplication().invokeLater {
                if (project.isDisposed) {
                    return@invokeLater
                }

                textArea.text = logText
                textArea.caretPosition = textArea.document.length
                statusLabel.text = status
            }
        }

        fun scheduleFallbackPolling() {
            if (project.isDisposed) {
                return
            }

            alarm.addRequest({
                refreshNow()
                scheduleFallbackPolling()
            }, 5000)
        }

        fun startWatchService() {
            val identity = logIdentity ?: return

            val watcher =
                try {
                    val folder = WorkspaceLogIdentityResolver.logFolderPath()
                    Files.createDirectories(folder)

                    FileSystems.getDefault().newWatchService().also {
                        watchService = it
                        folder.register(
                            it,
                            StandardWatchEventKinds.ENTRY_CREATE,
                            StandardWatchEventKinds.ENTRY_MODIFY,
                            StandardWatchEventKinds.ENTRY_DELETE,
                        )
                    }
                } catch (ex: Exception) {
                    ApplicationManager.getApplication().invokeLater {
                        if (!project.isDisposed) {
                            statusLabel.text =
                                "Live log watch unavailable (${ex.message ?: ex::class.java.simpleName}); using periodic refresh."
                        }
                    }
                    return
                }

            ApplicationManager.getApplication().executeOnPooledThread {
                while (!project.isDisposed && !disposed.get()) {
                    val key =
                        try {
                            watcher.poll(1200, TimeUnit.MILLISECONDS)
                        } catch (_: InterruptedException) {
                            continue
                        } catch (_: Exception) {
                            break
                        } ?: continue

                    var shouldRefresh = false
                    for (event in key.pollEvents()) {
                        val context = event.context() as? Path ?: continue
                        val fileName = context.fileName.toString()
                        if (fileName.startsWith("lsp-", ignoreCase = true) && fileName.endsWith(".log", ignoreCase = true)) {
                            shouldRefresh = true
                        }
                    }

                    key.reset()

                    if (shouldRefresh) {
                        refreshNow()
                    }
                }
            }
        }

        refreshButton.addActionListener { refreshNow() }
        openFolderButton.addActionListener {
            val folder = WorkspaceLogIdentityResolver.logFolderPath()
            BrowserUtil.browse(folder.toUri())
        }
        openOutputButton.addActionListener {
            WindowManager.getInstance().getStatusBar(project)
            toolWindow.activate(null)
        }

        Disposer.register(content) {
            disposed.set(true)
            alarm.cancelAllRequests()
            watchService?.close()
        }

        refreshNow()
        startWatchService()
        scheduleFallbackPolling()
    }

    private fun readLogTail(
        logFile: Path,
        maxLines: Int,
    ): String {
        if (!Files.exists(logFile)) {
            return "EF QueryLens log file does not exist yet.\n\nExpected path:\n${logFile.absolutePathString()}"
        }

        val ring = ArrayDeque<String>(maxLines)
        try {
            Files.newBufferedReader(logFile, StandardCharsets.UTF_8).use { reader ->
                var line = reader.readLine()
                while (line != null) {
                    if (ring.size >= maxLines) {
                        ring.removeFirst()
                    }
                    ring.addLast(line)
                    line = reader.readLine()
                }
            }
        } catch (ex: Exception) {
            val detail = ex.message ?: ex::class.java.simpleName
            return "Failed to read EF QueryLens log file.\n$detail\n\nPath:\n${logFile.absolutePathString()}"
        }

        return ring.joinToString("\n")
    }

    private fun findLatestLogFile(): Path? {
        val folder = WorkspaceLogIdentityResolver.logFolderPath()
        if (!Files.exists(folder)) {
            return null
        }

        return Files.list(folder).use { stream ->
            stream
                .filter { path -> Files.isRegularFile(path) }
                .filter { path ->
                    val fileName = path.fileName.toString()
                    fileName.startsWith("lsp-", ignoreCase = true) && fileName.endsWith(".log", ignoreCase = true)
                }.max(compareBy<Path> { Files.getLastModifiedTime(it).toMillis() })
                .orElse(null)
        }
    }
}
