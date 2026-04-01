package efquerylens

import com.intellij.ide.browsers.BrowserFamily
import com.intellij.ide.browsers.BrowserSpecificSettings
import com.intellij.ide.browsers.WebBrowser
import javax.swing.Icon
import java.util.UUID
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.test.fail

class EFQueryLensUrlOpenerTest {
    private val opener = EFQueryLensUrlOpener()
    private val browser =
        object : WebBrowser() {
            override fun getId(): UUID = UUID.randomUUID()

            override fun getName(): String = "test"

            override fun getFamily(): BrowserFamily = BrowserFamily.CHROME

            override fun getIcon(): Icon =
                object : Icon {
                    override fun getIconHeight(): Int = 1

                    override fun getIconWidth(): Int = 1

                    override fun paintIcon(c: java.awt.Component?, g: java.awt.Graphics?, x: Int, y: Int) = Unit
                }

            override fun getPath(): String? = null

            override fun getBrowserNotFoundMessage(): String = "not found"

            override fun getSpecificSettings(): BrowserSpecificSettings? = null
        }

    @Test
    fun `extractStructuredPreview returns null for non-map responses`() {
        val result = opener.extractStructuredPreview("invalid", "file:///query.cs", 0)
        assertEquals(null, result)
    }

    @Test
    fun `extractStructuredPreview returns null when hover node is absent`() {
        val result = opener.extractStructuredPreview(mapOf("x" to "y"), "file:///query.cs", 0)
        assertNull(result)
    }

    @Test
    fun `extractStructuredPreview materializes status and warnings`() {
        val response =
            mapOf(
                "hover" to
                    mapOf(
                        "Success" to true,
                        "Status" to 0,
                        "CommandCount" to 2,
                        "SourceFile" to "file:///query.cs",
                        "SourceLine" to 12,
                        "ProviderName" to "SqlServer",
                        "DbContextType" to "BlogContext",
                        "EnrichedSql" to "SELECT * FROM [Blogs]",
                        "Warnings" to listOf("Potential cartesian product"),
                        "AvgTranslationMs" to 8.5,
                    ),
            )

        val preview = opener.extractStructuredPreview(response, "file:///fallback.cs", 1)

        assertNotNull(preview)
        assertTrue(preview.title.contains("2 queries"))
        assertEquals(0, preview.statusCode)
        assertEquals("SELECT * FROM [Blogs]", preview.actionSqlText)
        assertEquals(1, preview.warnings.size)
        assertTrue(preview.subtitle.contains("BlogContext"))
    }

    @Test
    fun `extractStructuredPreview renders split statements with default labels`() {
        val response =
            mapOf(
                "hover" to
                    mapOf(
                        "Success" to true,
                        "Status" to 0,
                        "CommandCount" to 2,
                        "Statements" to
                            listOf(
                                mapOf("Sql" to "select * from orders where id = 1"),
                                mapOf("Sql" to "select * from lines where order_id = 1"),
                            ),
                    ),
            )

        val preview = opener.extractStructuredPreview(response, "file:///fallback.cs", 0)

        assertNotNull(preview)
        assertTrue(preview.sqlText.contains("Split Query 1 of 2"))
        assertTrue(preview.sqlText.contains("Split Query 2 of 2"))
        assertEquals(preview.sqlText, preview.actionSqlText)
    }

    @Test
    fun `extractStructuredPreview reports unavailable when status is ready but success is false`() {
        val response =
            mapOf(
                "hover" to
                    mapOf(
                        "Success" to false,
                        "Status" to 0,
                        "StatusMessage" to "No SQL preview available at this location.",
                    ),
            )

        val preview = opener.extractStructuredPreview(response, "file:///fallback.cs", 9)

        assertNotNull(preview)
        assertEquals("QueryLens · preview unavailable", preview.title)
        assertEquals("", preview.actionSqlText)
        assertEquals("READY", preview.statusText)
    }

    @Test
    fun `extractStructuredPreview returns null when ready response has no sql payload`() {
        val response =
            mapOf(
                "hover" to
                    mapOf(
                        "Success" to true,
                        "Status" to 0,
                        "CommandCount" to 1,
                        "Statements" to emptyList<Map<String, Any?>>(),
                    ),
            )

        val preview = opener.extractStructuredPreview(response, "file:///fallback.cs", 3)

        assertNull(preview)
    }

    @Test
    fun `extractStructuredPreview builds fallback status message for non-ready status`() {
        val response =
            mapOf(
                "hover" to
                    mapOf(
                        "Success" to true,
                        "Status" to 2,
                        "CommandCount" to 1,
                    ),
            )

        val preview = opener.extractStructuredPreview(response, "file:///fallback.cs", 7)

        assertNotNull(preview)
        assertEquals("STARTING", preview.statusText)
        assertEquals(null, preview.statusMessage)
        assertTrue(preview.title.contains("starting", ignoreCase = true))
    }

    @Test
    fun `extractStructuredPreview prefers enriched sql for action text when statements exist`() {
        val response =
            mapOf(
                "hover" to
                    mapOf(
                        "Success" to true,
                        "Status" to 0,
                        "CommandCount" to 2,
                        "Statements" to
                            listOf(
                                mapOf("Sql" to "select 1", "SplitLabel" to "Split Query 1 of 2"),
                                mapOf("Sql" to "select 2", "SplitLabel" to "Split Query 2 of 2"),
                            ),
                        "EnrichedSql" to "/* enriched */\nselect 1;\nselect 2;",
                    ),
            )

        val preview = opener.extractStructuredPreview(response, "file:///fallback.cs", 0)

        assertNotNull(preview)
        assertTrue(preview.sqlText.contains("Split Query 1 of 2"))
        assertFalse(preview.actionSqlText.contains("Split Query 1 of 2"))
        assertTrue(preview.actionSqlText.contains("enriched", ignoreCase = true))
    }

    @Test
    fun `extractStructuredPreview falls back source metadata and filters blank warnings`() {
        val response =
            mapOf(
                "hover" to
                    mapOf(
                        "Success" to true,
                        "Status" to 0,
                        "CommandCount" to 1,
                        "EnrichedSql" to "select now()",
                        "Warnings" to listOf("", "   ", "uses scalar subquery"),
                    ),
            )

        val preview = opener.extractStructuredPreview(response, "file:///fallback.cs", 11)

        assertNotNull(preview)
        assertTrue(preview.subtitle.contains("file:///fallback.cs:12"))
        assertEquals(1, preview.warnings.size)
        assertEquals("uses scalar subquery", preview.warnings.first())
    }

    @Test
    fun `extractStructuredPreview filters malformed statements and non-string warnings`() {
        val response =
            mapOf(
                "hover" to
                    mapOf(
                        "Success" to true,
                        "Status" to 0,
                        "CommandCount" to 1,
                        "Statements" to
                            listOf(
                                "not-a-map",
                                mapOf("Sql" to "   "),
                                mapOf("Sql" to "select 42", "SplitLabel" to "   "),
                            ),
                        "Warnings" to listOf(7, "careful"),
                    ),
            )

        val preview = opener.extractStructuredPreview(response, "file:///fallback.cs", 2)

        assertNotNull(preview)
        assertEquals("select 42", preview.sqlText)
        assertEquals(1, preview.warnings.size)
        assertEquals("careful", preview.warnings.first())
    }

    @Test
    fun `openUrl returns false for non efquerylens scheme`() {
        val opened = opener.openUrl(browser, "https://example.com", null)
        assertFalse(opened)
    }

    @Test
    fun `openUrl returns true for malformed efquerylens uri`() {
        val opened = opener.openUrl(browser, "efquerylens://:::bad-uri", null)
        assertTrue(opened)
    }

    @Test
    fun `openUrl returns true for unsupported efquerylens host`() {
        val opened = opener.openUrl(browser, "efquerylens://unsupported?uri=file:///x.cs&line=1&character=1", null)
        assertTrue(opened)
    }

    @Test
    fun `openUrl returns true when required uri parameter is missing`() {
        val opened = opener.openUrl(browser, "efquerylens://copysql?line=1&character=2", null)
        assertTrue(opened)
    }

    @Test
    fun `parseQueryParams handles blank query and url decoding`() {
        val blank = invokePrivate<Map<String, String>>("parseQueryParams", "")
        assertTrue(blank.isEmpty())

        val decoded =
            invokePrivate<Map<String, String>>(
                "parseQueryParams",
                "uri=file%3A%2F%2F%2Fc%3A%2Frepo%2FProgram.cs&line=42&character=7",
            )

        assertEquals("file:///c:/repo/Program.cs", decoded["uri"])
        assertEquals("42", decoded["line"])
        assertEquals("7", decoded["character"])
    }

    @Test
    fun `parseQueryParams ignores malformed pairs without equals`() {
        val parsed = invokePrivate<Map<String, String>>("parseQueryParams", "uri=file:///x.cs&badpair&line=5")
        assertEquals("file:///x.cs", parsed["uri"])
        assertEquals("5", parsed["line"])
        assertTrue(!parsed.containsKey("badpair"))
    }

    @Test
    fun `extractStructuredPreview falls back to ErrorMessage when StatusMessage is blank`() {
        val response =
            mapOf(
                "hover" to
                    mapOf(
                        "Success" to false,
                        "Status" to 3,
                        "StatusMessage" to "   ",
                        "ErrorMessage" to "Transport unavailable",
                        "CommandCount" to 0,
                        "SourceLine" to 0,
                    ),
            )

        val preview = opener.extractStructuredPreview(response, "file:///fallback.cs", 4)

        assertNotNull(preview)
        assertEquals("ERROR", preview.statusText)
        assertEquals("Transport unavailable", preview.statusMessage)
        assertTrue(preview.title.contains("1 query"))
        assertTrue(preview.subtitle.contains(":1"))
    }

    @Test
    fun `renderStatements handles empty and single statement cases`() {
        val empty = invokePrivate<String>("renderStatements", emptyList<Any>())
        assertEquals("", empty)

        val statementClass =
            EFQueryLensUrlOpener::class.java.declaredClasses.firstOrNull { it.simpleName == "StructuredStatement" }
                ?: fail("StructuredStatement class not found")
        val ctor = statementClass.declaredConstructors.first()
        ctor.isAccessible = true

        val one = ctor.newInstance("select 1", null)
        val single = invokePrivate<String>("renderStatements", listOf(one))
        assertEquals("select 1", single)

        val first = ctor.newInstance("select 1", "first")
        val second = ctor.newInstance("select 2", "second")
        val multi = invokePrivate<String>("renderStatements", listOf(first, second))
        assertTrue(multi.contains("-- first"))
        assertTrue(multi.contains("-- second"))
    }

    @Test
    fun `status helper methods map codes consistently`() {
        assertEquals("READY", invokePrivate<String>("toStatusText", 0))
        assertEquals("QUEUED", invokePrivate<String>("toStatusText", 1))
        assertEquals("STARTING", invokePrivate<String>("toStatusText", 2))
        assertEquals("ERROR", invokePrivate<String>("toStatusText", 3))

        assertTrue(invokePrivate<String>("fallbackStatusMessage", 3).contains("unavailable", ignoreCase = true))
        assertTrue(invokePrivate<String>("fallbackStatusMessage", 2).contains("starting", ignoreCase = true))
        assertTrue(invokePrivate<String>("fallbackStatusMessage", 1).contains("queued", ignoreCase = true))
    }

    @Suppress("UNCHECKED_CAST")
    private fun <T> invokePrivate(name: String, vararg args: Any): T {
        val method = EFQueryLensUrlOpener::class.java.declaredMethods.firstOrNull { it.name == name }
            ?: fail("Method '$name' not found")
        method.isAccessible = true
        return method.invoke(opener, *args) as T
    }

}
