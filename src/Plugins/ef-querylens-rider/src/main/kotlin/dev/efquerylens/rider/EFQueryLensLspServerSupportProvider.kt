package dev.efquerylens.rider

import com.intellij.openapi.project.Project

class EFQueryLensLspServerSupportProvider {
    // Scaffold placeholder: implement Rider LSP support provider in next phase.
    fun isEnabled(project: Project): Boolean = project.basePath != null
}
