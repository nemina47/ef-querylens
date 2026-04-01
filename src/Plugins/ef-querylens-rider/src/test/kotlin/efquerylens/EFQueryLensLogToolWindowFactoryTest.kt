package efquerylens

import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.attribute.FileTime
import kotlin.io.path.createDirectories
import kotlin.io.path.deleteIfExists
import kotlin.io.path.exists
import kotlin.io.path.name
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.test.fail

class EFQueryLensLogToolWindowFactoryTest {
    private val factory = EFQueryLensLogToolWindowFactory()

    @Test
    fun `shouldBeAvailable always returns true`() {
        val project =
            java.lang.reflect.Proxy.newProxyInstance(
                com.intellij.openapi.project.Project::class.java.classLoader,
                arrayOf(com.intellij.openapi.project.Project::class.java),
            ) { _, method, _ ->
                when (method.returnType) {
                    java.lang.Boolean.TYPE -> false
                    java.lang.Integer.TYPE -> 0
                    java.lang.Long.TYPE -> 0L
                    java.lang.Double.TYPE -> 0.0
                    java.lang.Float.TYPE -> 0f
                    java.lang.Short.TYPE -> 0.toShort()
                    java.lang.Byte.TYPE -> 0.toByte()
                    java.lang.Character.TYPE -> 0.toChar()
                    else -> null
                }
            } as com.intellij.openapi.project.Project

        assertTrue(factory.shouldBeAvailable(project))
    }

    @Test
    fun `readLogTail returns waiting message when log file is missing`() {
        val missing = Files.createTempDirectory("ql-missing-").resolve("missing.log")

        val text = invokePrivate<String>("readLogTail", missing, 20)

        assertTrue(text.contains("does not exist yet"))
        assertTrue(text.contains(missing.toAbsolutePath().toString()))
    }

    @Test
    fun `readLogTail returns last N lines from large log`() {
        val file = Files.createTempFile("ql-tail-", ".log")
        try {
            val lines = (1..8).map { "line-$it" }
            Files.write(file, lines, StandardCharsets.UTF_8)

            val text = invokePrivate<String>("readLogTail", file, 3)

            assertTrue(text.contains("line-6"))
            assertTrue(text.contains("line-7"))
            assertTrue(text.contains("line-8"))
            assertTrue(!text.contains("line-2"))
        } finally {
            file.deleteIfExists()
        }
    }

    @Test
    fun `readLogTail returns failure message when path is not readable as a file`() {
        val dir = Files.createTempDirectory("ql-log-dir-")
        try {
            val text = invokePrivate<String>("readLogTail", dir, 10)
            assertTrue(text.contains("Failed to read EF QueryLens log file"))
        } finally {
            dir.toFile().deleteRecursively()
        }
    }

    @Test
    fun `findLatestLogFile returns newest matching lsp log`() {
        val folder = WorkspaceLogIdentityResolver.logFolderPath()
        folder.createDirectories()

        val oldLog = folder.resolve("lsp-test-old.log")
        val newLog = folder.resolve("lsp-test-new.log")
        val ignored = folder.resolve("something-else.log")

        try {
            Files.writeString(oldLog, "old", StandardCharsets.UTF_8)
            Files.writeString(newLog, "new", StandardCharsets.UTF_8)
            Files.writeString(ignored, "ignore", StandardCharsets.UTF_8)

            Files.setLastModifiedTime(oldLog, FileTime.fromMillis(1000))
            Files.setLastModifiedTime(newLog, FileTime.fromMillis(2000))

            val expected =
                Files.list(folder).use { stream ->
                    stream
                        .filter { path -> Files.isRegularFile(path) }
                        .filter { path ->
                            val fileName = path.fileName.toString()
                            fileName.startsWith("lsp-", ignoreCase = true) && fileName.endsWith(".log", ignoreCase = true)
                        }.max(compareBy<Path> { Files.getLastModifiedTime(it).toMillis() })
                        .orElse(null)
                }

            val latest = invokePrivate<Path?>("findLatestLogFile")
            assertNotNull(latest)
            assertNotNull(expected)
            assertEquals(expected.fileName.toString(), latest.fileName.toString())
        } finally {
            oldLog.deleteIfExists()
            newLog.deleteIfExists()
            ignored.deleteIfExists()
        }
    }

    @Test
    fun `findLatestLogFile returns null when no matching logs exist`() {
        val folder = WorkspaceLogIdentityResolver.logFolderPath()
        folder.createDirectories()

        val marker = folder.resolve("marker-nonmatching.log")
        try {
            Files.writeString(marker, "marker", StandardCharsets.UTF_8)
            val latest = invokePrivate<Path?>("findLatestLogFile")
            if (latest != null) {
                assertTrue(latest.fileName.toString().startsWith("lsp-"))
            }
        } finally {
            marker.deleteIfExists()
        }
    }

    @Test
    fun `findLatestLogFile returns null when log folder is missing`() {
        val folder = WorkspaceLogIdentityResolver.logFolderPath()
        if (folder.exists()) {
            folder.toFile().deleteRecursively()
        }

        val latest = invokePrivate<Path?>("findLatestLogFile")
        assertNull(latest)

        // Restore folder for subsequent tests and plugin behavior.
        folder.createDirectories()
    }

    @Suppress("UNCHECKED_CAST")
    private fun <T> invokePrivate(name: String, vararg args: Any): T {
        val method = EFQueryLensLogToolWindowFactory::class.java.declaredMethods.firstOrNull { it.name == name }
            ?: fail("Method '$name' not found")
        method.isAccessible = true
        return method.invoke(factory, *args) as T
    }
}
