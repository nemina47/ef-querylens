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

    private data class StructuredSqlPreview(
        val title: String,
        val subtitle: String,
        val statusCode: Int,
        val statusText: String,
        val avgTranslationMs: Double,
        val sqlText: String,
    )

    private data class StatusPalette(
        val border: Color,
        val foreground: Color,
        val background: Color,
    )

    override fun openUrl(browser: WebBrowser, url: String, project: Project?): Boolean {
        if (!url.startsWith("efquerylens://", ignoreCase = true)) {
            return false
        }

        val uri = runCatching { URI(url) }.getOrNull() ?: return true
        val host = uri.host?.lowercase() ?: return true
        if (host != "copysql" && host != "opensqleditor" && host != "recalculate") return true

        val params = parseQueryParams(uri.rawQuery ?: "")
        val fileUri = params["uri"] ?: return true
        val line = params["line"]?.toIntOrNull() ?: 0
        val character = params["character"]?.toIntOrNull() ?: 0

        val effectiveProject = project ?: ProjectManager.getInstance().openProjects.firstOrNull() ?: return true

        if (host == "recalculate") {
            requestPreviewRecalculate(effectiveProject, fileUri, line, character)
            return true
        }

        ApplicationManager.getApplication().executeOnPooledThread {
            try {
                val preview = buildStructuredPreview(effectiveProject, fileUri, line, character)
                    ?: return@executeOnPooledThread

                when (host) {
                    "copysql" -> CopyPasteManager.getInstance().setContents(StringSelection(preview.sqlText))
                    "opensqleditor" -> openInPreviewDialog(effectiveProject, preview)
                }
            } catch (e: Exception) {
                thisLogger().warn("[EFQueryLens] URL opener failed for host=$host", e)
            }
        }

        return true
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
        if (status != 0 || !success) {
            return null
        }

        val sqlText = (hover["EnrichedSql"] as? String)?.takeIf { it.isNotBlank() } ?: return null

        val commandCount = (hover["CommandCount"] as? Number)?.toInt()?.coerceAtLeast(1) ?: 1
        val statementWord = if (commandCount == 1) "query" else "queries"
        val title = "QueryLens · $commandCount $statementWord · ready"

        val sourceFile = (hover["SourceFile"] as? String)
            ?.takeIf { it.isNotBlank() }
            ?: fallbackFileUri
        val sourceLine = (hover["SourceLine"] as? Number)?.toInt()?.coerceAtLeast(1)
            ?: (fallbackLine + 1)
        val providerName = (hover["ProviderName"] as? String)?.takeIf { it.isNotBlank() }
        val subtitle = buildString {
            append(sourceFile)
            append(':')
            append(sourceLine)
            if (!providerName.isNullOrBlank()) {
                append(" · ")
                append(providerName)
            }
        }

        val avgTranslationMs = (hover["AvgTranslationMs"] as? Number)?.toDouble() ?: 0.0

        return StructuredSqlPreview(
            title = title,
            subtitle = subtitle,
            statusCode = status,
            statusText = "READY",
            avgTranslationMs = avgTranslationMs,
            sqlText = sqlText
        )
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

            val avgText = if (preview.avgTranslationMs > 0) {
                "avg ${preview.avgTranslationMs.toInt()} ms"
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
