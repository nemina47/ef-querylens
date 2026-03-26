package efquerylens

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.wm.ToolWindowManager

/**
 * Menu action to open the EF QueryLens tool window. Makes the plugin visible in the UI.
 */
class EFQueryLensShowToolWindowAction :
    AnAction(),
    DumbAware {
    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val toolWindowManager = ToolWindowManager.getInstance(project)
        val toolWindow =
            toolWindowManager.getToolWindow("EF QueryLens")
                ?: return
        toolWindow.activate(null)
    }
}
