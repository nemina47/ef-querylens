package efquerylens

import com.intellij.ide.browsers.UrlOpener
import com.intellij.ide.browsers.WebBrowser
import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.thisLogger
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.ex.EditorEx
import com.intellij.openapi.editor.highlighter.EditorHighlighterFactory
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileTypes.FileTypeManager
import com.intellij.openapi.ide.CopyPasteManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.project.ProjectManager
import com.intellij.openapi.ui.popup.JBPopupFactory
import com.intellij.openapi.ui.popup.JBPopupListener
import com.intellij.openapi.ui.popup.LightweightWindowEvent
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.platform.lsp.api.LspServerManager
import org.eclipse.lsp4j.ExecuteCommandParams
import java.awt.Dimension
import java.awt.datatransfer.StringSelection
import java.io.File
import java.net.URI
import java.net.URLDecoder
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

class EFQueryLensUrlOpener : UrlOpener() {
    internal data class StructuredStatement(
        val sql: String,
        val splitLabel: String?,
    )

    internal data class StructuredSqlPreview(
        val title: String,
        val subtitle: String,
        val statusCode: Int,
        val statusText: String,
        val statusMessage: String?,
        val avgTranslationMs: Double,
        val sqlText: String,
        val warnings: List<String>,
    )

    override fun openUrl(
        browser: WebBrowser,
        url: String,
        project: Project?,
    ): Boolean {
        thisLogger().info("[EFQueryLens] UrlOpener.openUrl called url=${url.take(120)}")

        // Intercept efquerylens:// scheme links from the LSP hover popup.
        // Rider calls BrowserLauncher.open() for unknown URI schemes, which invokes
        // this UrlOpener before the OS shell sees it — so we handle the action here
        // and return true to prevent any browser window from opening.
        // (http:// links bypass UrlOpener: Rider uses BrowserUtil.browse() directly.)
        if (!url.startsWith("efquerylens://", ignoreCase = true)) {
            // Also intercept our own localhost URLs as a safety net for when the
            // system browser already opened (e.g. older cached hover markdown).
            return if (url.startsWith("http://127.0.0.1", ignoreCase = true)) {
                handleActionUrl(url, project)
            } else {
                false
            }
        }

        val uri = runCatching { URI(url) }.getOrNull() ?: return true
        val host = uri.host?.lowercase() ?: return true
        if (host != "copysql" && host != "opensql" && host != "opensqleditor" && host != "recalculate") return true

        val params = parseQueryParams(uri.rawQuery ?: "")
        val fileUri = params["uri"] ?: return true
        val line = params["line"]?.toIntOrNull() ?: 0
        val character = params["character"]?.toIntOrNull() ?: 0

        thisLogger().info("[EFQueryLens] UrlOpener intercepted efquerylens://$host line=$line char=$character")

        val effectiveProject = project ?: ProjectManager.getInstance().openProjects.firstOrNull() ?: return true

        if (host == "recalculate") {
            requestPreviewRecalculate(effectiveProject, fileUri, line, character)
            return true
        }

        // Normalise "opensql" (hover link scheme) → "opensqleditor" (action dispatch key)
        val actionType = if (host == "opensql") "opensqleditor" else host
        dispatchSqlAction(actionType, effectiveProject, fileUri, line, character)
        return true
    }

    /**
     * Safety-net for `http://127.0.0.1:{port}/efquerylens/action?…` URLs that reach
     * the browser (e.g. from stale cached hover markdown using the old http scheme).
     * Only matches our own action-server path; unrelated localhost URLs fall through.
     */
    private fun handleActionUrl(
        url: String,
        project: Project?,
    ): Boolean {
        val uri = runCatching { URI(url) }.getOrNull() ?: return false
        if (uri.path != "/efquerylens/action") return false

        val params = parseQueryParams(uri.rawQuery ?: "")
        val type = params["type"] ?: return false
        val fileUri = params["uri"] ?: return false
        val line = params["line"]?.toIntOrNull() ?: 0
        val character = params["character"]?.toIntOrNull() ?: 0

        thisLogger().info("[EFQueryLens] UrlOpener intercepted http-localhost action type=$type line=$line char=$character")

        val effectiveProject = project ?: ProjectManager.getInstance().openProjects.firstOrNull() ?: return false

        if (type == "recalculate") {
            requestPreviewRecalculate(effectiveProject, fileUri, line, character)
            return true
        }

        if (type == "copysql" || type == "opensqleditor") {
            dispatchSqlAction(type, effectiveProject, fileUri, line, character)
            return true
        }

        thisLogger().warn("[EFQueryLens] UrlOpener: unknown action type='$type'")
        return false
    }

    private fun dispatchSqlAction(
        type: String,
        project: Project,
        fileUri: String,
        line: Int,
        character: Int,
    ) {
        ApplicationManager.getApplication().executeOnPooledThread {
            try {
                val preview =
                    buildStructuredPreview(project, fileUri, line, character)
                        ?: return@executeOnPooledThread

                if (preview.statusCode != 0 || preview.sqlText.isBlank()) {
                    val message =
                        preview.statusMessage
                            ?: if (preview.statusCode != 0) {
                                fallbackStatusMessage(preview.statusCode)
                            } else {
                                "No SQL preview available at this location."
                            }
                    showStatusMessage(project, preview.statusCode, message)
                    return@executeOnPooledThread
                }

                when (type) {
                    "copysql" -> {
                        CopyPasteManager.getInstance().setContents(StringSelection(preview.sqlText))
                        thisLogger().info("[EFQueryLens] SQL copied to clipboard (${preview.sqlText.length} chars)")
                        showCopiedNotification(project)
                    }
                    "opensqleditor" -> openSqlInEditor(project, preview)
                }
            } catch (e: Exception) {
                thisLogger().warn("[EFQueryLens] dispatchSqlAction failed for type=$type", e)
            }
        }
    }

    private fun showCopiedNotification(project: Project) {
        ApplicationManager.getApplication().invokeLater {
            NotificationGroupManager
                .getInstance()
                .getNotificationGroup("EF QueryLens")
                .createNotification("SQL copied to clipboard", NotificationType.INFORMATION)
                .notify(project)
        }
    }

    internal fun requestPreviewRecalculate(
        project: Project,
        fileUri: String,
        line: Int,
        character: Int,
    ) {
        val server =
            LspServerManager
                .getInstance(project)
                .getServersForProvider(EFQueryLensLspServerSupportProvider::class.java)
                .firstOrNull() ?: return

        ApplicationManager.getApplication().executeOnPooledThread {
            try {
                val payload =
                    mapOf(
                        "textDocument" to mapOf("uri" to fileUri),
                        "position" to mapOf("line" to line, "character" to character),
                    )

                val response =
                    server.sendRequestSync(10_000) {
                        it.workspaceService.executeCommand(
                            org.eclipse.lsp4j.ExecuteCommandParams(
                                "efquerylens.preview.recalculate",
                                listOf(payload),
                            ),
                        )
                    }

                thisLogger().info("[EFQueryLens] Recalculate response='$response'")
            } catch (e: Exception) {
                thisLogger().warn("[EFQueryLens] Recalculate request failed", e)
            }
        }
    }

    internal fun buildStructuredPreview(
        project: Project,
        fileUri: String,
        line: Int,
        character: Int,
    ): StructuredSqlPreview? {
        val server =
            LspServerManager
                .getInstance(project)
                .getServersForProvider(EFQueryLensLspServerSupportProvider::class.java)
                .firstOrNull() ?: return null

        val payload =
            mapOf(
                "textDocument" to mapOf("uri" to fileUri),
                "position" to mapOf("line" to line, "character" to character),
            )

        val response =
            runCatching {
                server.sendRequestSync(10_000) {
                    it.workspaceService.executeCommand(
                        ExecuteCommandParams(
                            "efquerylens.preview.structuredHover",
                            listOf(payload),
                        ),
                    )
                }
            }.getOrNull() ?: return null

        return extractStructuredPreview(response, fileUri, line)
    }

    @Suppress("UNCHECKED_CAST")
    internal fun extractStructuredPreview(
        response: Any?,
        fallbackFileUri: String,
        fallbackLine: Int,
    ): StructuredSqlPreview? {
        val root = response as? Map<String, Any?> ?: return null
        val hover = root["hover"] as? Map<String, Any?> ?: return null

        val status = (hover["Status"] as? Number)?.toInt() ?: 0
        val success = hover["Success"] as? Boolean ?: false

        val statusMessage =
            (hover["StatusMessage"] as? String)?.takeIf { it.isNotBlank() }
                ?: (hover["ErrorMessage"] as? String)?.takeIf { it.isNotBlank() }

        val statements =
            ((hover["Statements"] as? List<*>) ?: emptyList<Any?>())
                .mapNotNull { statementRaw ->
                    val statement = statementRaw as? Map<String, Any?> ?: return@mapNotNull null
                    val sql = (statement["Sql"] as? String)?.trim()?.takeIf { it.isNotBlank() } ?: return@mapNotNull null
                    val splitLabel = (statement["SplitLabel"] as? String)?.trim()?.takeIf { it.isNotBlank() }
                    StructuredStatement(sql = sql, splitLabel = splitLabel)
                }

        val renderedStatements = renderStatements(statements)
        val enrichedSql = (hover["EnrichedSql"] as? String)?.trim()?.takeIf { it.isNotBlank() }
        val sqlText = renderedStatements.takeIf { it.isNotBlank() } ?: enrichedSql

        val warnings =
            ((hover["Warnings"] as? List<*>) ?: emptyList<Any?>())
                .mapNotNull { warning -> (warning as? String)?.trim()?.takeIf { it.isNotBlank() } }

        if (status == 0 && !success) {
            return StructuredSqlPreview(
                title = "QueryLens · preview unavailable",
                subtitle = "$fallbackFileUri:${fallbackLine + 1}",
                statusCode = status,
                statusText = toStatusText(status),
                statusMessage = statusMessage ?: "No SQL preview available at this location.",
                avgTranslationMs = 0.0,
                sqlText = "",
                warnings = warnings,
            )
        }

        if (status == 0 && sqlText.isNullOrBlank()) {
            return null
        }

        val commandCount = (hover["CommandCount"] as? Number)?.toInt()?.coerceAtLeast(1) ?: 1
        val statementWord = if (commandCount == 1) "query" else "queries"
        val statusText = toStatusText(status)
        val title = "QueryLens · $commandCount $statementWord · ${statusText.lowercase()}"

        val sourceFile =
            (hover["SourceFile"] as? String)
                ?.takeIf { it.isNotBlank() }
                ?: fallbackFileUri
        val sourceLine =
            (hover["SourceLine"] as? Number)?.toInt()?.coerceAtLeast(1)
                ?: (fallbackLine + 1)
        val providerName = (hover["ProviderName"] as? String)?.takeIf { it.isNotBlank() }
        val dbContextType = (hover["DbContextType"] as? String)?.takeIf { it.isNotBlank() }
        val subtitle =
            buildString {
                append(sourceFile)
                append(':')
                append(sourceLine)
                if (!providerName.isNullOrBlank()) {
                    append(" · ")
                    append(providerName)
                }
                if (!dbContextType.isNullOrBlank()) {
                    append(" · ")
                    append(dbContextType)
                }
            }

        val avgTranslationMs = (hover["AvgTranslationMs"] as? Number)?.toDouble() ?: 0.0

        return StructuredSqlPreview(
            title = title,
            subtitle = subtitle,
            statusCode = status,
            statusText = statusText,
            statusMessage = statusMessage,
            avgTranslationMs = avgTranslationMs,
            sqlText = sqlText ?: "",
            warnings = warnings,
        )
    }

    private fun renderStatements(statements: List<StructuredStatement>): String {
        if (statements.isEmpty()) {
            return ""
        }

        if (statements.size == 1) {
            return statements[0].sql
        }

        return statements
            .mapIndexed { index, statement ->
                val label = statement.splitLabel ?: "Split Query ${index + 1} of ${statements.size}"
                "-- $label\n${statement.sql}"
            }.joinToString("\n\n")
    }

    private fun toStatusText(statusCode: Int): String =
        when (statusCode) {
            1 -> "QUEUED"
            2 -> "STARTING"
            3 -> "ERROR"
            else -> "READY"
        }

    private fun fallbackStatusMessage(statusCode: Int): String =
        when (statusCode) {
            3 -> "EF QueryLens services are unavailable and cannot communicate right now."
            2 -> "EF QueryLens is starting up and warming translation services."
            else -> "EF QueryLens queued this query and is still processing it."
        }

    internal fun showStatusMessage(
        project: Project,
        statusCode: Int,
        message: String,
    ) {
        ApplicationManager.getApplication().invokeLater {
            if (statusCode == 3) {
                com.intellij.openapi.ui.Messages
                    .showWarningDialog(project, message, "EF QueryLens")
            } else {
                com.intellij.openapi.ui.Messages
                    .showInfoMessage(project, message, "EF QueryLens")
            }
        }
    }

    /**
     * Opens the SQL for [preview] in a new IDE editor tab, matching VS Code behaviour.
     * A timestamped `.sql` temp file is written and then opened via [FileEditorManager]
     * so the user gets full editor features (syntax highlighting, copy, search, etc.).
     */
    internal fun openSqlInEditor(
        project: Project,
        preview: StructuredSqlPreview,
    ) {
        ApplicationManager.getApplication().invokeLater {
            try {
                val timestamp =
                    LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyy-MM-dd_HHmmss"))
                val content = buildSqlFileContent(preview)
                val tempFile = File(System.getProperty("java.io.tmpdir"), "efquery_$timestamp.sql")
                tempFile.writeText(content, Charsets.UTF_8)
                val virtualFile =
                    LocalFileSystem.getInstance().refreshAndFindFileByIoFile(tempFile)
                        ?: run {
                            thisLogger().warn("[EFQueryLens] Could not resolve virtual file for $tempFile")
                            return@invokeLater
                        }
                FileEditorManager.getInstance(project).openFile(virtualFile, true)
                thisLogger().info("[EFQueryLens] Opened SQL in editor: ${tempFile.name}")
            } catch (e: Exception) {
                thisLogger().warn("[EFQueryLens] openSqlInEditor failed", e)
            }
        }
    }

    /**
     * Shows the SQL for [preview] in a floating popup near the current editor cursor,
     * triggered by clicking the "SQL Preview" code lens.
     */
    internal fun showSqlPopup(
        project: Project,
        preview: StructuredSqlPreview,
    ) {
        ApplicationManager.getApplication().invokeLater {
            try {
                val document = EditorFactory.getInstance().createDocument(preview.sqlText)
                val sqlViewer = EditorFactory.getInstance().createViewer(document, project)

                // Apply SQL syntax highlighting using the .sql file type
                val sqlFileType = FileTypeManager.getInstance().getFileTypeByExtension("sql")
                (sqlViewer as? EditorEx)?.highlighter =
                    EditorHighlighterFactory.getInstance().createEditorHighlighter(project, sqlFileType)

                // Minimal editor chrome — no gutter, no folding
                sqlViewer.settings.apply {
                    isLineNumbersShown = false
                    isFoldingOutlineShown = false
                    isLineMarkerAreaShown = false
                    additionalColumnsCount = 0
                    additionalLinesCount = 0
                }

                val editorComponent = sqlViewer.component
                editorComponent.preferredSize = Dimension(640, 380)

                val popup =
                    JBPopupFactory
                        .getInstance()
                        .createComponentPopupBuilder(editorComponent, sqlViewer.contentComponent)
                        .setTitle(preview.title)
                        .setResizable(true)
                        .setMovable(true)
                        .setRequestFocus(true)
                        .addListener(
                            object : JBPopupListener {
                                override fun onClosed(event: LightweightWindowEvent) {
                                    EditorFactory.getInstance().releaseEditor(sqlViewer)
                                }
                            },
                        ).createPopup()

                val activeEditor = FileEditorManager.getInstance(project).selectedTextEditor
                if (activeEditor != null) {
                    popup.showInBestPositionFor(activeEditor)
                } else {
                    popup.showInFocusCenter()
                }
            } catch (e: Exception) {
                thisLogger().warn("[EFQueryLens] showSqlPopup failed", e)
            }
        }
    }

    private fun buildSqlFileContent(preview: StructuredSqlPreview): String =
        buildString {
            appendLine("-- EF QueryLens")
            if (preview.subtitle.isNotBlank()) {
                // subtitle: "filepath:line · ProviderName · DbContextType"
                val parts = preview.subtitle.split(" · ")
                appendLine("-- Source:    ${parts.getOrElse(0) { "" }}")
                if (parts.size > 1) appendLine("-- Provider:  ${parts[1]}")
                if (parts.size > 2) appendLine("-- DbContext: ${parts[2]}")
            }
            if (preview.warnings.isNotEmpty()) {
                appendLine("--")
                preview.warnings.forEach { appendLine("-- Warning: $it") }
            }
            appendLine()
            append(preview.sqlText)
        }

    private fun parseQueryParams(query: String): Map<String, String> {
        if (query.isBlank()) return emptyMap()
        return query
            .split("&")
            .mapNotNull { pair ->
                val idx = pair.indexOf('=')
                if (idx < 0) {
                    null
                } else {
                    URLDecoder.decode(pair.substring(0, idx), "UTF-8") to
                        URLDecoder.decode(pair.substring(idx + 1), "UTF-8")
                }
            }.toMap()
    }
}
