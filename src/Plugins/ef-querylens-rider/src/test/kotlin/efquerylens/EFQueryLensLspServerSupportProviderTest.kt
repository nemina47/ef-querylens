package efquerylens

import com.intellij.openapi.project.Project
import com.intellij.platform.lsp.api.LspServerDescriptor
import com.intellij.platform.lsp.api.LspServerSupportProvider
import com.intellij.testFramework.LightVirtualFile
import java.lang.reflect.Proxy
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.test.fail

class EFQueryLensLspServerSupportProviderTest {
    private val provider = EFQueryLensLspServerSupportProvider()

    @Test
    fun `fileOpened does not start server for unsupported file`() {
        val project = createProjectStub()
        val file = LightVirtualFile("readme.md", "# docs")
        var starts = 0

        val starter =
            object : LspServerSupportProvider.LspServerStarter {
                override fun ensureServerStarted(descriptor: LspServerDescriptor) {
                starts++
                }
            }

        provider.fileOpened(project, file, starter)

        assertEquals(0, starts)
    }

    @Test
    fun `isSupported recognizes csharp extension only`() {
        val file = LightVirtualFile("Program.cs", "class C {}")
        val other = LightVirtualFile("Program.txt", "class C {}")
        val extensionless = LightVirtualFile("README", "docs")

        val csSupported = invokePrivate<Boolean>("isSupported", file)
        val txtSupported = invokePrivate<Boolean>("isSupported", other)
        val extensionlessSupported = invokePrivate<Boolean>("isSupported", extensionless)

        assertTrue(csSupported)
        assertTrue(!txtSupported)
        assertTrue(!extensionlessSupported)
    }

    private fun createProjectStub(): Project {
        return Proxy.newProxyInstance(
            Project::class.java.classLoader,
            arrayOf(Project::class.java),
        ) { _, method, _ ->
            when (method.name) {
                "getBasePath" -> return@newProxyInstance System.getProperty("java.io.tmpdir")
                "getName" -> return@newProxyInstance "QueryLensTestProject"
            }
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
        } as Project
    }

    @Suppress("UNCHECKED_CAST")
    private fun <T> invokePrivate(name: String, vararg args: Any): T {
        val method = EFQueryLensLspServerSupportProvider::class.java.declaredMethods.firstOrNull { it.name == name }
            ?: fail("Method '$name' not found")
        method.isAccessible = true
        return method.invoke(provider, *args) as T
    }
}
