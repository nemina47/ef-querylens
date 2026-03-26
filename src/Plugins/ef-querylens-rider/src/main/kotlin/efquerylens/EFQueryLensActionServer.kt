package efquerylens

import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.thisLogger
import com.intellij.openapi.ide.CopyPasteManager
import com.intellij.openapi.project.ProjectManager
import com.sun.net.httpserver.HttpServer
import java.awt.datatransfer.StringSelection
import java.net.InetSocketAddress
import java.net.URLDecoder
import java.util.concurrent.Executors

/**
 * Minimal HTTP server bound to 127.0.0.1 on an OS-assigned port.
 *
 * Rider's LSP hover popup renders markdown in JCEF (embedded Chromium).  When
 * a link with a custom scheme like `efquerylens://` is clicked, JCEF hands the
 * URL directly to the OS shell — bypassing IntelliJ's `UrlOpener` extension
 * point — which produces a "Get an app to open this link" system dialog.
 *
 * By replacing those scheme links with `http://127.0.0.1:{port}/efquerylens/action?…`
 * links, JCEF treats them as ordinary HTTP requests.  This server receives the
 * request, performs the IDE action (copy to clipboard / open dialog /
 * recalculate), and immediately returns **204 No Content** so JCEF does not
 * navigate away from the hover popup.
 *
 * The port is communicated to the LSP server process via the
 * `QUERYLENS_ACTION_PORT` environment variable set in
 * [EFQueryLensLspServerSupportProvider] before the server process starts.
 */
internal class EFQueryLensActionServer {
    private var httpServer: HttpServer? = null

    /** The port the server is listening on, or 0 if not yet started. */
    var port: Int = 0
        private set

    /** Start the HTTP server.  Safe to call multiple times — only starts once. */
    @Synchronized
    fun start() {
        if (httpServer != null) return
        try {
            // backlog=10: more than enough for sequential IDE action clicks
            val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 10)
            server.createContext("/efquerylens/action") { exchange ->
                try {
                    val query = exchange.requestURI.rawQuery ?: ""
                    val params = parseQuery(query)
                    val type = params["type"] ?: ""
                    val fileUri = params["uri"] ?: ""
                    val line = params["line"]?.toIntOrNull() ?: 0
                    val character = params["character"]?.toIntOrNull() ?: 0

                    // Respond immediately with a self-closing page.  The system
                    // browser opens the localhost URL (JCEF hands http:// links to the
                    // OS shell).  window.close() closes the tab in most browsers; the
                    // styled fallback message is shown only if the browser blocks it.
                    val body =
                        """
                        <html>
                        <head>
                        <style>
                          body{font-family:system-ui,sans-serif;display:flex;align-items:center;
                               justify-content:center;height:100vh;margin:0;background:#1e1e1e;color:#ccc}
                          .msg{text-align:center;padding:2rem}
                          .title{font-size:1.1rem;font-weight:600;color:#4ec9b0;margin-bottom:.5rem}
                          .sub{font-size:.85rem;opacity:.7}
                        </style>
                        <script>window.close();</script>
                        </head>
                        <body>
                          <div class="msg">
                            <div class="title">✓ EF QueryLens action completed</div>
                            <div class="sub">You can close this tab.</div>
                          </div>
                        </body>
                        </html>
                        """.trimIndent().toByteArray(Charsets.UTF_8)
                    exchange.responseHeaders["Content-Type"] = listOf("text/html; charset=utf-8")
                    exchange.sendResponseHeaders(200, body.size.toLong())
                    exchange.responseBody.use { it.write(body) }
                    exchange.close()

                    thisLogger().info(
                        "[EFQueryLens] ActionServer request: type=$type line=$line char=$character " +
                            "uri=${fileUri.takeLast(60)}",
                    )

                    // Dispatch the action on a pooled thread so we never block
                    // the HTTP handler thread.
                    ApplicationManager.getApplication().executeOnPooledThread {
                        handleAction(type, fileUri, line, character)
                    }
                } catch (e: Exception) {
                    thisLogger().warn("[EFQueryLens] ActionServer request error", e)
                    runCatching { exchange.sendResponseHeaders(500, -1) }
                    runCatching { exchange.close() }
                }
            }
            // Single daemon thread — actions are dispatched to the IntelliJ
            // thread pool anyway; we only need this to accept connections.
            server.executor =
                Executors.newSingleThreadExecutor { r ->
                    Thread(r, "EFQueryLens-ActionServer").also { it.isDaemon = true }
                }
            server.start()
            httpServer = server
            port = server.address.port
            thisLogger().info("[EFQueryLens] ActionServer started on port $port")
        } catch (e: Exception) {
            thisLogger().warn("[EFQueryLens] ActionServer failed to start", e)
            port = 0
        }
    }

    /** Stop the HTTP server and release the port. */
    @Synchronized
    fun stop() {
        httpServer?.stop(0)
        httpServer = null
        port = 0
    }

    // ------------------------------------------------------------------
    // Action dispatch
    // ------------------------------------------------------------------

    private fun handleAction(
        type: String,
        fileUri: String,
        line: Int,
        character: Int,
    ) {
        val project =
            ProjectManager.getInstance().openProjects.firstOrNull() ?: run {
                thisLogger().warn("[EFQueryLens] ActionServer: no open project to handle type=$type")
                return
            }
        val opener = EFQueryLensUrlOpener()

        when (type) {
            "copysql", "opensqleditor" -> {
                thisLogger().info("[EFQueryLens] ActionServer: fetching structured preview for type=$type")
                val preview =
                    runCatching {
                        opener.buildStructuredPreview(project, fileUri, line, character)
                    }.getOrElse { e ->
                        thisLogger().warn("[EFQueryLens] ActionServer: buildStructuredPreview failed", e)
                        null
                    } ?: run {
                        thisLogger().warn("[EFQueryLens] ActionServer: buildStructuredPreview returned null for type=$type")
                        return
                    }

                thisLogger().info(
                    "[EFQueryLens] ActionServer: preview ready statusCode=${preview.statusCode} " +
                        "sqlLen=${preview.actionSqlText.length} hasText=${preview.actionSqlText.isNotBlank()}",
                )

                if (preview.statusCode != 0 || preview.actionSqlText.isBlank()) {
                    val message =
                        preview.statusMessage
                            ?: if (preview.statusCode != 0) {
                                fallbackMessage(preview.statusCode)
                            } else {
                                "No SQL preview available at this location."
                            }
                    opener.showStatusMessage(project, preview.statusCode, message)
                    return
                }

                when (type) {
                    "copysql" -> {
                        CopyPasteManager.getInstance().setContents(StringSelection(preview.actionSqlText))
                        thisLogger().info("[EFQueryLens] ActionServer: copied ${preview.actionSqlText.length} chars to clipboard")
                        ApplicationManager.getApplication().invokeLater {
                            val notification =
                                NotificationGroupManager
                                    .getInstance()
                                    .getNotificationGroup("EF QueryLens")
                                    ?.createNotification(
                                        "SQL copied to clipboard",
                                        NotificationType.INFORMATION,
                                    )
                            notification?.notify(project)
                        }
                    }
                    "opensqleditor" -> {
                        opener.openSqlInEditor(project, preview)
                        thisLogger().info("[EFQueryLens] ActionServer: opened SQL in editor")
                    }
                }
            }

            "recalculate" -> {
                opener.requestPreviewRecalculate(project, fileUri, line, character)
                thisLogger().info("[EFQueryLens] ActionServer: recalculate dispatched")
            }

            else -> thisLogger().warn("[EFQueryLens] ActionServer: unknown action type='$type'")
        }
    }

    private fun fallbackMessage(statusCode: Int): String =
        when (statusCode) {
            3 -> "EF QueryLens services are unavailable and cannot communicate right now."
            2 -> "EF QueryLens is starting up and warming translation services."
            else -> "EF QueryLens queued this query and is still processing it."
        }

    // ------------------------------------------------------------------
    // Utilities
    // ------------------------------------------------------------------

    private fun parseQuery(query: String): Map<String, String> {
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
