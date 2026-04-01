package efquerylens

import kotlin.io.path.absolutePathString
import kotlin.io.path.createDirectories
import kotlin.io.path.createFile
import kotlin.io.path.pathString
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.test.fail
import java.nio.file.Files

class WorkspaceLogIdentityResolverTest {
    @Test
    fun `fromProjectBasePath computes stable hash and file location`() {
        val basePath = "/tmp/querylens/rider-project"

        val first = WorkspaceLogIdentityResolver.fromProjectBasePath(basePath)
        val second = WorkspaceLogIdentityResolver.fromProjectBasePath(basePath)

        assertEquals(first.hash, second.hash)
        assertEquals(16, first.hash.length)
        assertEquals(first.logFilePath.fileName.toString(), "lsp-${first.hash}.log")
        assertTrue(first.workspacePath.absolutePathString().contains("querylens", ignoreCase = true))
    }

    @Test
    fun `logFolderPath points under temp directory`() {
        val path = WorkspaceLogIdentityResolver.logFolderPath().toString().replace('\\', '/')
        assertTrue(path.contains("EFQueryLens/rider-logs"))
    }

    @Test
    fun `fromProjectBasePath walks parent directories to detect QueryLens root markers`() {
        val tempRoot = Files.createTempDirectory("ql-rider-detect-")
        try {
            val repoRoot = tempRoot.resolve("repo")
            repoRoot.createDirectories()
            repoRoot.resolve("EFQueryLens.slnx").createFile()
            repoRoot.resolve("src/EFQueryLens.Lsp").createDirectories()
            repoRoot.resolve("src/EFQueryLens.Lsp/EFQueryLens.Lsp.csproj").createFile()

            val deepProject = repoRoot.resolve("src/Plugins/ef-querylens-rider")
            deepProject.createDirectories()

            val identity = WorkspaceLogIdentityResolver.fromProjectBasePath(deepProject.pathString)

            assertEquals(repoRoot.toAbsolutePath().normalize().pathString, identity.workspacePath.pathString)
        } finally {
            tempRoot.toFile().deleteRecursively()
        }
    }

    @Test
    fun `fromProjectBasePath falls back to project path when no QueryLens markers are found`() {
        val tempProject = Files.createTempDirectory("ql-rider-project-only-")
        try {
            val identity = WorkspaceLogIdentityResolver.fromProjectBasePath(tempProject.pathString)
            assertEquals(tempProject.toAbsolutePath().normalize().pathString, identity.workspacePath.pathString)
            assertTrue(identity.logFilePath.toString().contains("lsp-"))
            assertTrue(identity.logFilePath.toString().endsWith(".log"))
        } finally {
            tempProject.toFile().deleteRecursively()
        }
    }

    @Test
    fun `looksLikeQueryLensRoot requires both solution and lsp markers`() {
        val tempRoot = Files.createTempDirectory("ql-root-markers-")
        try {
            val onlySolution = tempRoot.resolve("only-solution")
            onlySolution.createDirectories()
            onlySolution.resolve("EFQueryLens.slnx").createFile()

            val onlyLsp = tempRoot.resolve("only-lsp")
            onlyLsp.resolve("src/EFQueryLens.Lsp").createDirectories()
            onlyLsp.resolve("src/EFQueryLens.Lsp/EFQueryLens.Lsp.csproj").createFile()

            val both = tempRoot.resolve("both")
            both.resolve("src/EFQueryLens.Lsp").createDirectories()
            both.resolve("src/EFQueryLens.Lsp/EFQueryLens.Lsp.csproj").createFile()
            both.resolve("EFQueryLens.slnx").createFile()

            assertTrue(!invokePrivate<Boolean>("looksLikeQueryLensRoot", onlySolution))
            assertTrue(!invokePrivate<Boolean>("looksLikeQueryLensRoot", onlyLsp))
            assertTrue(invokePrivate("looksLikeQueryLensRoot", both))
        } finally {
            tempRoot.toFile().deleteRecursively()
        }
    }

    @Suppress("UNCHECKED_CAST")
    private fun <T> invokePrivate(name: String, vararg args: Any): T {
        val method = WorkspaceLogIdentityResolver::class.java.declaredMethods.firstOrNull { it.name == name }
            ?: fail("Method '$name' not found")
        method.isAccessible = true
        return method.invoke(WorkspaceLogIdentityResolver, *args) as T
    }
}
