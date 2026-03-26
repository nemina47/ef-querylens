package efquerylens

import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.thisLogger
import com.intellij.openapi.ide.CopyPasteManager
import com.intellij.openapi.project.ProjectManager
import com.intellij.platform.backend.documentation.ContentUpdater
import com.intellij.platform.backend.documentation.DocumentationLinkHandler
import com.intellij.platform.backend.documentation.DocumentationTarget
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.withContext
import java.awt.datatransfer.StringSelection
import java.net.URI
import java.net.URLDecoder

/**
 * Intercepts `efquerylens://copysql`, `efquerylens://opensql`, and
 * `efquerylens://recalculate` link clicks inside Rider's documentation/hover popup.
 *
 * IntelliJ's documentation popup registers a JBCefJSQuery that captures all
 * anchor clicks from JS before Chromium sees them, routing them through
 * [DocumentationLinkHandler] implementations.  This means our custom scheme
 * never reaches the OS shell — the Windows "Get an app" dialog is avoided.
 *
 * The [UrlOpener] EP is NOT triggered by hover popup link clicks; it only
 * fires when IntelliJ's own code calls BrowserLauncher.browse() explicitly.
 */
class EFQueryLensDocumentationLinkHandler : DocumentationLinkHandler {
    override fun contentUpdater(
        target: DocumentationTarget,
        url: String,
    ): ContentUpdater? {
        if (!url.startsWith("efquerylens://", ignoreCase = true)) return null

        val uri = runCatching { URI(url) }.getOrNull() ?: return null
        val host = uri.host?.lowercase() ?: return null
        if (host != "copysql" && host != "opensql" && host != "recalculate") return null

        val params = parseQueryParams(uri.rawQuery ?: "")
        val fileUri = params["uri"] ?: return null
        val line = params["line"]?.toIntOrNull() ?: 0
        val character = params["character"]?.toIntOrNull() ?: 0

        thisLogger().info("[EFQueryLens] DocLinkHandler: intercepted $host line=$line char=$character")

        return ContentUpdater { existingContent ->
            handleActionAsync(host, fileUri, line, character, existingContent)
        }
    }

    private fun handleActionAsync(
        action: String,
        fileUri: String,
        line: Int,
        character: Int,
        existingContent: String,
    ): Flow<String> =
        flow {
            withContext(Dispatchers.IO) {
                val project = ProjectManager.getInstance().openProjects.firstOrNull()
                if (project == null) {
                    thisLogger().warn("[EFQueryLens] DocLinkHandler: no open project for $action")
                    return@withContext
                }

                try {
                    val opener = EFQueryLensUrlOpener()

                    if (action == "recalculate") {
                        opener.requestPreviewRecalculate(project, fileUri, line, character)
                        thisLogger().info("[EFQueryLens] DocLinkHandler: recalculate dispatched line=$line char=$character")
                        return@withContext
                    }

                    val preview = opener.buildStructuredPreview(project, fileUri, line, character)

                    if (preview == null || preview.sqlText.isBlank()) {
                        thisLogger().warn("[EFQueryLens] DocLinkHandler: no SQL available for $action")
                        return@withContext
                    }

                    when (action) {
                        "copysql" -> {
                            CopyPasteManager.getInstance().setContents(StringSelection(preview.sqlText))
                            thisLogger().info("[EFQueryLens] DocLinkHandler: SQL copied (${preview.sqlText.length} chars)")
                            ApplicationManager.getApplication().invokeLater {
                                NotificationGroupManager
                                    .getInstance()
                                    .getNotificationGroup("EF QueryLens")
                                    .createNotification("SQL copied to clipboard", NotificationType.INFORMATION)
                                    .notify(project)
                            }
                        }
                        "opensql" -> {
                            opener.openSqlInEditor(project, preview)
                            thisLogger().info("[EFQueryLens] DocLinkHandler: opening SQL in editor")
                        }
                    }
                } catch (e: Exception) {
                    thisLogger().warn("[EFQueryLens] DocLinkHandler: error handling $action", e)
                }
            }

            // Emit the existing popup content unchanged — we performed a side-effect action,
            // not a content navigation, so the hover popup stays as-is.
            emit(existingContent)
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
