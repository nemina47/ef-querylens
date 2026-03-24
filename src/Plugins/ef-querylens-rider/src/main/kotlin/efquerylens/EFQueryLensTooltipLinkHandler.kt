package efquerylens

import com.intellij.codeInsight.highlighting.TooltipLinkHandler
import com.intellij.openapi.editor.Editor

/**
 * Handles links rendered in hover markdown as #efquerylens/<command>?<query>.
 */
class EFQueryLensTooltipLinkHandler : TooltipLinkHandler() {
    override fun handleLink(refSuffix: String, editor: Editor): Boolean {
        val normalized = refSuffix
            .trim()
            .removePrefix("/")

        if (normalized.isBlank()) {
            return false
        }

        val actionUrl = "efquerylens://$normalized"
        return EFQueryLensUrlOpener().handleQueryLensActionUrl(actionUrl, editor.project)
    }
}
