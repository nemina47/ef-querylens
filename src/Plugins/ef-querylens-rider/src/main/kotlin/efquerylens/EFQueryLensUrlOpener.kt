package efquerylens

import com.intellij.ide.browsers.UrlOpener
import com.intellij.ide.browsers.WebBrowser
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.thisLogger
import com.intellij.openapi.ide.CopyPasteManager
import com.intellij.openapi.ui.DialogWrapper
import com.intellij.openapi.project.Project
import com.intellij.openapi.project.ProjectManager
import com.intellij.platform.lsp.api.LspServerManager
import com.intellij.ui.JBColor
import org.eclipse.lsp4j.ExecuteCommandParams
import java.awt.datatransfer.StringSelection
import java.awt.BorderLayout
import java.awt.Color
import java.awt.Dimension
import java.awt.Font
import java.net.URI
import java.net.URLDecoder
import javax.swing.BorderFactory
import javax.swing.JComponent
import javax.swing.JLabel
import javax.swing.JPanel
import javax.swing.JScrollPane
import javax.swing.JTextArea

class EFQueryLensUrlOpener : UrlOpener() {
    private companion object {
        private const val BrowserSafeHost = "efquerylens.local"
    }

    private data class StructuredStatement(
        val sql: String,
        val splitLabel: String?,
    )

    private data class StructuredSqlPreview(
        val title: String,
        val subtitle: String,
        val statusCode: Int,
        val statusText: String,
        val statusMessage: String?,
        val translationMs: Double,
        val sqlText: String,
        val warnings: List<String>,
    )

    private data class StatusPalette(
        val border: Color,
        val foreground: Color,
        val background: Color,
    )

    override fun openUrl(browser: WebBrowser, url: String, project: Project?): Boolean {
        if (!isQueryLensActionUrl(url)) {
            return false
        }

        return handleQueryLensActionUrl(url, project)
    }

    internal fun handleQueryLensActionUrl(url: String, project: Project?): Boolean {
        if (!isQueryLensActionUrl(url)) {
            return false
        }

        val uri = runCatching { URI(url) }.getOrNull() ?: return true
        val command = extractCommand(uri) ?: return true

        val params = parseQueryParams(uri.rawQuery ?: "")
        val fileUri = params["uri"]
        if (fileUri.isNullOrBlank()) {
            thisLogger().warn("[EFQueryLens] URL opener missing uri parameter for command=$command")
            return true
        }
        val line = params["line"]?.toIntOrNull() ?: 0
        val character = params["character"]?.toIntOrNull() ?: 0

        val effectiveProject = resolveProject(project, fileUri)
        if (effectiveProject == null) {
            thisLogger().warn("[EFQueryLens] URL opener could not resolve project for command=$command uri=$fileUri")
            return true
        }

        thisLogger().info("[EFQueryLens] URL opener command=$command uri=$fileUri line=$line char=$character")

        if (command == "recalculate") {
            requestPreviewRecalculate(effectiveProject, fileUri, line, character)
            return true
        }

        ApplicationManager.getApplication().executeOnPooledThread {
            try {
                val preview = buildStructuredPreview(effectiveProject, fileUri, line, character)
                if (preview == null) {
                    showStatusMessage(effectiveProject, 3, "EF QueryLens could not fetch SQL for this location. Try hovering once and retry.")
                    return@executeOnPooledThread
                }

                if (preview.statusCode != 0 || preview.sqlText.isBlank()) {
                    val message = preview.statusMessage
                        ?: if (preview.statusCode != 0) fallbackStatusMessage(preview.statusCode)
                        else "No SQL preview available at this location."
                    showStatusMessage(effectiveProject, preview.statusCode, message)
                    return@executeOnPooledThread
                }

                when (command) {
                    "copysql" -> CopyPasteManager.getInstance().setContents(StringSelection(preview.sqlText))
                    "opensqleditor" -> openInPreviewDialog(effectiveProject, preview)
                }
            } catch (e: Exception) {
                thisLogger().warn("[EFQueryLens] URL opener failed for command=$command", e)
            }
        }

        return true
    }

    private fun extractCommand(uri: URI): String? {
        val host = uri.host?.trim()?.lowercase()?.trimEnd('/')
        val path = uri.path
            ?.trim()
            ?.trim('/')
            ?.lowercase()
            ?.trimEnd('/')

        val isLegacyScheme = uri.scheme.equals("efquerylens", ignoreCase = true)
        val raw = when {
            isLegacyScheme && !host.isNullOrBlank() -> host
            !path.isNullOrBlank() -> path
            !host.isNullOrBlank() -> host
            else -> null
        } ?: return null

        return when (raw) {
            "copysql", "copy" -> "copysql"
            "opensqleditor", "opensql", "open" -> "opensqleditor"
            "recalculate", "reanalyze", "reanalyse" -> "recalculate"
            else -> null
        }
    }

    private fun isQueryLensActionUrl(url: String): Boolean {
        if (url.startsWith("efquerylens://", ignoreCase = true)) {
            return true
        }

        val uri = runCatching { URI(url) }.getOrNull() ?: return false
        val isHttp = uri.scheme.equals("http", ignoreCase = true) || uri.scheme.equals("https", ignoreCase = true)
        return isHttp && uri.host.equals(BrowserSafeHost, ignoreCase = true)
    }

    private fun resolveProject(project: Project?, fileUri: String): Project? {
        if (project != null && !project.isDisposed) {
            return project
        }

        val openProjects = ProjectManager.getInstance().openProjects.filter { !it.isDisposed }
        if (openProjects.isEmpty()) {
            return null
        }

        val normalizedFilePath = runCatching { URI(fileUri).path }
            .getOrNull()
            ?.replace('\\', '/')
            ?.trimEnd('/')

        if (!normalizedFilePath.isNullOrBlank()) {
            val match = openProjects
                .mapNotNull { current ->
                    val basePath = current.basePath?.replace('\\', '/')?.trimEnd('/')
                    if (basePath.isNullOrBlank()) {
                        null
                    } else {
                        current to basePath
                    }
                }
                .filter { (_, basePath) ->
                    normalizedFilePath.startsWith(basePath, ignoreCase = true)
                }
                .maxByOrNull { (_, basePath) -> basePath.length }
                ?.first

            if (match != null) {
                return match
            }
        }

        return openProjects.firstOrNull()
    }

    private fun requestPreviewRecalculate(project: Project, fileUri: String, line: Int, character: Int) {
        val server = LspServerManager.getInstance(project)
            .getServersForProvider(EFQueryLensLspServerSupportProvider::class.java)
            .firstOrNull() ?: return

        ApplicationManager.getApplication().executeOnPooledThread {
            try {
                val payload = mapOf(
                    "textDocument" to mapOf("uri" to fileUri),
                    "position" to mapOf("line" to line, "character" to character)
                )

                val response = server.sendRequestSync(10_000) {
                    it.workspaceService.executeCommand(
                        org.eclipse.lsp4j.ExecuteCommandParams(
                            "efquerylens.preview.recalculate",
                            listOf(payload)
                        )
                    )
                }

                thisLogger().info("[EFQueryLens] Recalculate response='$response'")
            } catch (e: Exception) {
                thisLogger().warn("[EFQueryLens] Recalculate request failed", e)
            }
        }
    }

    private fun buildStructuredPreview(project: Project, fileUri: String, line: Int, character: Int): StructuredSqlPreview? {
        val server = LspServerManager.getInstance(project)
            .getServersForProvider(EFQueryLensLspServerSupportProvider::class.java)
            .firstOrNull() ?: return null

        val payload = mapOf(
            "textDocument" to mapOf("uri" to fileUri),
            "position" to mapOf("line" to line, "character" to character)
        )

        val response = runCatching {
            server.sendRequestSync(10_000) {
                it.workspaceService.executeCommand(
                    ExecuteCommandParams(
                        "efquerylens.preview.structuredHover",
                        listOf(payload)
                    )
                )
            }
        }.getOrNull() ?: return null

        return extractStructuredPreview(response, fileUri, line)
    }

    @Suppress("UNCHECKED_CAST")
    private fun extractStructuredPreview(response: Any?, fallbackFileUri: String, fallbackLine: Int): StructuredSqlPreview? {
        val root = response as? Map<String, Any?> ?: return null
        val hover = root["hover"] as? Map<String, Any?> ?: return null

        val status = (hover["Status"] as? Number)?.toInt() ?: 0
        val success = hover["Success"] as? Boolean ?: false

        val statusMessage = (hover["StatusMessage"] as? String)?.takeIf { it.isNotBlank() }
            ?: (hover["ErrorMessage"] as? String)?.takeIf { it.isNotBlank() }

        val statements = ((hover["Statements"] as? List<*>) ?: emptyList<Any?>())
            .mapNotNull { statementRaw ->
                val statement = statementRaw as? Map<String, Any?> ?: return@mapNotNull null
                val sql = (statement["Sql"] as? String)?.trim()?.takeIf { it.isNotBlank() } ?: return@mapNotNull null
                val splitLabel = (statement["SplitLabel"] as? String)?.trim()?.takeIf { it.isNotBlank() }
                StructuredStatement(sql = sql, splitLabel = splitLabel)
            }

        val renderedStatements = renderStatements(statements)
        val enrichedSql = (hover["EnrichedSql"] as? String)?.trim()?.takeIf { it.isNotBlank() }
        val sqlText = renderedStatements.takeIf { it.isNotBlank() } ?: enrichedSql

        val warnings = ((hover["Warnings"] as? List<*>) ?: emptyList<Any?>())
            .mapNotNull { warning -> (warning as? String)?.trim()?.takeIf { it.isNotBlank() } }

        if (status == 0 && !success) {
            return StructuredSqlPreview(
                title = "QueryLens · preview unavailable",
                subtitle = "$fallbackFileUri:${fallbackLine + 1}",
                statusCode = status,
                statusText = toStatusText(status),
                statusMessage = statusMessage ?: "No SQL preview available at this location.",
                translationMs = 0.0,
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

        val sourceFile = (hover["SourceFile"] as? String)
            ?.takeIf { it.isNotBlank() }
            ?: fallbackFileUri
        val sourceLine = (hover["SourceLine"] as? Number)?.toInt()?.coerceAtLeast(1)
            ?: (fallbackLine + 1)
        val providerName = (hover["ProviderName"] as? String)?.takeIf { it.isNotBlank() }
        val dbContextType = (hover["DbContextType"] as? String)?.takeIf { it.isNotBlank() }
        val subtitle = buildString {
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

        val lastTranslationMs = (hover["LastTranslationMs"] as? Number)?.toDouble() ?: 0.0
        val avgTranslationMs = (hover["AvgTranslationMs"] as? Number)?.toDouble() ?: 0.0
        val effectiveTranslationMs = if (lastTranslationMs > 0) lastTranslationMs else avgTranslationMs

        return StructuredSqlPreview(
            title = title,
            subtitle = subtitle,
            statusCode = status,
            statusText = statusText,
            statusMessage = statusMessage,
            translationMs = effectiveTranslationMs,
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

        return statements.mapIndexed { index, statement ->
            val label = statement.splitLabel ?: "Split Query ${index + 1} of ${statements.size}"
            "-- $label\n${statement.sql}"
        }.joinToString("\n\n")
    }

    private fun toStatusText(statusCode: Int): String = when (statusCode) {
        1 -> "QUEUED"
        2 -> "STARTING"
        3 -> "ERROR"
        else -> "READY"
    }

    private fun fallbackStatusMessage(statusCode: Int): String = when (statusCode) {
        3 -> "EF QueryLens services are unavailable and cannot communicate right now."
        2 -> "EF QueryLens is starting up and warming translation services."
        else -> "EF QueryLens queued this query and is still processing it."
    }

    private fun showStatusMessage(project: Project, statusCode: Int, message: String) {
        ApplicationManager.getApplication().invokeLater {
            if (statusCode == 3) {
                com.intellij.openapi.ui.Messages.showWarningDialog(project, message, "EF QueryLens")
            } else {
                com.intellij.openapi.ui.Messages.showInfoMessage(project, message, "EF QueryLens")
            }
        }
    }

    private fun openInPreviewDialog(project: Project, preview: StructuredSqlPreview) {
        ApplicationManager.getApplication().invokeLater {
            SqlPreviewDialog(project, preview).show()
        }
    }

    private fun parseQueryParams(query: String): Map<String, String> {
        if (query.isBlank()) return emptyMap()
        return query.split("&").mapNotNull { pair ->
            val idx = pair.indexOf('=')
            if (idx < 0) null
            else URLDecoder.decode(pair.substring(0, idx), "UTF-8") to
                    URLDecoder.decode(pair.substring(idx + 1), "UTF-8")
        }.toMap()
    }

    private class SqlPreviewDialog(project: Project, private val preview: StructuredSqlPreview) : DialogWrapper(project, true) {
        init {
            title = "EF QueryLens SQL Preview"
            setOKButtonText("Close")
            init()
        }

        override fun createCenterPanel(): JComponent {
            val root = JPanel(BorderLayout())

            val header = JPanel(BorderLayout())
            header.border = BorderFactory.createEmptyBorder(10, 12, 10, 12)

            val titleLabel = JLabel(preview.title)
            titleLabel.font = titleLabel.font.deriveFont(Font.BOLD)

            val subtitleLabel = JLabel(preview.subtitle)

            val palette = statusPalette(preview.statusCode)

            val statusLabel = JLabel(preview.statusText).apply {
                isOpaque = true
                border = BorderFactory.createCompoundBorder(
                    BorderFactory.createLineBorder(palette.border),
                    BorderFactory.createEmptyBorder(2, 8, 2, 8)
                )
                font = font.deriveFont(Font.BOLD, 10f)
                foreground = palette.foreground
                background = palette.background
            }

            val avgText = if (preview.translationMs > 0) {
                "SQL generation time ${preview.translationMs.toInt()} ms"
            } else {
                ""
            }
            val avgLabel = JLabel(avgText)

            val metaRow = JPanel(BorderLayout())
            metaRow.border = BorderFactory.createEmptyBorder(6, 0, 0, 0)
            metaRow.add(statusLabel, BorderLayout.WEST)
            if (avgText.isNotBlank()) {
                metaRow.add(avgLabel, BorderLayout.CENTER)
            }

            val titleStack = JPanel()
            titleStack.layout = javax.swing.BoxLayout(titleStack, javax.swing.BoxLayout.Y_AXIS)
            titleStack.add(titleLabel)
            titleStack.add(subtitleLabel)
            titleStack.add(metaRow)

            header.add(titleStack, BorderLayout.CENTER)

            val sqlArea = JTextArea(preview.sqlText).apply {
                isEditable = false
                lineWrap = false
                wrapStyleWord = false
                font = Font(Font.MONOSPACED, Font.PLAIN, 12)
                caretPosition = 0
            }

            root.add(header, BorderLayout.NORTH)
            root.add(JScrollPane(sqlArea), BorderLayout.CENTER)

            if (preview.warnings.isNotEmpty()) {
                val notesPanel = JPanel(BorderLayout())
                notesPanel.border = BorderFactory.createEmptyBorder(8, 12, 10, 12)

                val notesLabel = JLabel("Notes")
                notesLabel.font = notesLabel.font.deriveFont(Font.BOLD)

                val notesArea = JTextArea(preview.warnings.joinToString("\n") { "- $it" }).apply {
                    isEditable = false
                    lineWrap = true
                    wrapStyleWord = true
                    border = BorderFactory.createEmptyBorder(4, 0, 0, 0)
                    background = root.background
                }

                notesPanel.add(notesLabel, BorderLayout.NORTH)
                notesPanel.add(notesArea, BorderLayout.CENTER)
                root.add(notesPanel, BorderLayout.SOUTH)
            }

            root.preferredSize = Dimension(1000, 640)
            return root
        }

        override fun createActions() = arrayOf(okAction)

        private fun statusPalette(statusCode: Int): StatusPalette {
            return when (statusCode) {
                1 -> StatusPalette(
                    border = JBColor(Color(0x0969DA), Color(0x58A6FF)),
                    foreground = JBColor(Color(0x0969DA), Color(0x58A6FF)),
                    background = JBColor(Color(0xEAF2FF), Color(0x0D223A))
                )

                2 -> StatusPalette(
                    border = JBColor(Color(0xBC4C00), Color(0xF2A65A)),
                    foreground = JBColor(Color(0xBC4C00), Color(0xF2A65A)),
                    background = JBColor(Color(0xFFF2E6), Color(0x2D1C0D))
                )

                3 -> StatusPalette(
                    border = JBColor(Color(0xCF222E), Color(0xFF7B72)),
                    foreground = JBColor(Color(0xCF222E), Color(0xFF7B72)),
                    background = JBColor(Color(0xFFEDEF), Color(0x2D1418))
                )

                else -> StatusPalette(
                    border = JBColor(Color(0x2EA043), Color(0x3FB950)),
                    foreground = JBColor(Color(0x2EA043), Color(0x3FB950)),
                    background = JBColor(Color(0xEAF8EE), Color(0x102015))
                )
            }
        }
    }
}
