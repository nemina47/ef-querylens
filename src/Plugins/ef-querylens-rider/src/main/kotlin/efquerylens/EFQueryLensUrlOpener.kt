package efquerylens

import com.intellij.ide.browsers.UrlOpener
import com.intellij.ide.browsers.WebBrowser
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.thisLogger
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.ide.CopyPasteManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.project.ProjectManager
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.platform.lsp.api.LspServerManager
import org.eclipse.lsp4j.ExecuteCommandParams
import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.TextDocumentIdentifier
import java.awt.datatransfer.StringSelection
import java.io.File
import java.net.URI
import java.net.URLDecoder
import java.nio.charset.StandardCharsets
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

class EFQueryLensUrlOpener : UrlOpener() {

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
                val content = buildEnrichedContent(effectiveProject, fileUri, line, character)
                    ?: return@executeOnPooledThread
                when (host) {
                    "copysql" -> CopyPasteManager.getInstance().setContents(StringSelection(content))
                    "opensqleditor" -> openInEditor(effectiveProject, content)
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

    private fun buildEnrichedContent(project: Project, fileUri: String, line: Int, character: Int): String? {
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

        return extractStructuredEnrichedSql(response)
    }

    @Suppress("UNCHECKED_CAST")
    private fun extractStructuredEnrichedSql(response: Any?): String? {
        val root = response as? Map<String, Any?> ?: return null
        val hover = root["hover"] as? Map<String, Any?> ?: return null
        val status = (hover["Status"] as? Number)?.toInt() ?: 0
        val success = hover["Success"] as? Boolean ?: false
        if (status != 0 || !success) {
            return null
        }

        val enrichedSql = hover["EnrichedSql"] as? String
        return enrichedSql?.takeIf { it.isNotBlank() }
    }

    private fun openInEditor(project: Project, content: String) {
        val tempDir = File(System.getProperty("java.io.tmpdir"), "EFQueryLens")
        tempDir.mkdirs()
        val stamp = LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyyMMdd_HHmmss"))
        val tempFile = File(tempDir, "preview_$stamp.sql")
        tempFile.writeText(content, StandardCharsets.UTF_8)

        ApplicationManager.getApplication().invokeLater {
            val vFile = LocalFileSystem.getInstance().refreshAndFindFileByIoFile(tempFile)
                ?: return@invokeLater
            FileEditorManager.getInstance(project).openFile(vFile, true)
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
}
