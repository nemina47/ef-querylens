package efquerylens

import java.nio.charset.StandardCharsets
import java.nio.file.Path
import java.security.MessageDigest
import kotlin.io.path.absolutePathString
import kotlin.io.path.exists

internal data class WorkspaceLogIdentity(
    val workspacePath: Path,
    val hash: String,
    val logFilePath: Path,
)

internal object WorkspaceLogIdentityResolver {
    fun fromProjectBasePath(projectBasePath: String): WorkspaceLogIdentity {
        val workspacePath = resolveWorkspacePath(projectBasePath)
        val hash = hashWorkspacePath(workspacePath.absolutePathString())
        val logFilePath = logFolderPath().resolve("lsp-$hash.log")
        return WorkspaceLogIdentity(workspacePath, hash, logFilePath)
    }

    private fun resolveWorkspacePath(projectBasePath: String): Path {
        val projectPath = Path.of(projectBasePath).toAbsolutePath().normalize()

        val envRepositoryRoot = System.getenv("QUERYLENS_REPOSITORY_ROOT")
        if (!envRepositoryRoot.isNullOrBlank()) {
            val envPath = Path.of(envRepositoryRoot).toAbsolutePath().normalize()
            if (looksLikeQueryLensRoot(envPath)) {
                return envPath
            }
        }

        var current: Path? = projectPath
        while (current != null) {
            if (looksLikeQueryLensRoot(current)) {
                return current
            }

            current = current.parent
        }

        return projectPath
    }

    private fun looksLikeQueryLensRoot(path: Path): Boolean {
        val hasSolution = path.resolve("EFQueryLens.slnx").exists()
        val hasLspProject =
            path
                .resolve("src")
                .resolve("EFQueryLens.Lsp")
                .resolve("EFQueryLens.Lsp.csproj")
                .exists()

        return hasSolution && hasLspProject
    }

    fun logFolderPath(): Path = Path.of(System.getProperty("java.io.tmpdir"), "EFQueryLens", "rider-logs")

    private fun hashWorkspacePath(path: String): String {
        val bytes = MessageDigest.getInstance("SHA-256").digest(path.toByteArray(StandardCharsets.UTF_8))
        return bytes.joinToString("") { "%02x".format(it) }.take(16)
    }
}
