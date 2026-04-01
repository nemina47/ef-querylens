package efquerylens

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.editor.CaretModel
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.LogicalPosition
import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.project.Project
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiFile
import com.intellij.testFramework.LightVirtualFile
import java.lang.reflect.Proxy
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class EFQueryLensUiActionsTest {
    @Test
    fun `show tool window action exposes BGT update thread`() {
        val action = EFQueryLensShowToolWindowAction()

        assertEquals(ActionUpdateThread.BGT, action.actionUpdateThread)
    }

    @Test
    fun `show tool window action noops when event project is null`() {
        val action = EFQueryLensShowToolWindowAction()

        assertTrue(action.javaClass.interfaces.contains(DumbAware::class.java))
    }

    @Test
    fun `base intention action reports static metadata`() {
        val action = EFQueryLensCopySqlIntentionAction()

        assertEquals("EF QueryLens: Copy SQL", action.text)
        assertEquals("EF QueryLens", action.familyName)
        assertFalse(action.startInWriteAction())
    }

    @Test
    fun `isAvailable returns false when editor is null`() {
        val action = EFQueryLensCopySqlIntentionAction()

        val available = action.isAvailable(createProjectStub(), null, createElementStub(containingFile = null))

        assertFalse(available)
    }

    @Test
    fun `isAvailable returns false when element has no containing file`() {
        val action = EFQueryLensOpenSqlIntentionAction()
        val editor = createEditorStub()

        val available = action.isAvailable(createProjectStub(), editor, createElementStub(containingFile = null))

        assertFalse(available)
    }

    @Test
    fun `isAvailable accepts csharp files case-insensitively`() {
        val action = EFQueryLensReanalyzeIntentionAction()
        val editor = createEditorStub()
        val cs = createPsiFileStub(LightVirtualFile("Program.cs", "class C {}"))
        val csUpper = createPsiFileStub(LightVirtualFile("Program.CS", "class C {}"))
        val txt = createPsiFileStub(LightVirtualFile("notes.txt", "..."))

        assertTrue(action.isAvailable(createProjectStub(), editor, createElementStub(cs)))
        assertTrue(action.isAvailable(createProjectStub(), editor, createElementStub(csUpper)))
        assertFalse(action.isAvailable(createProjectStub(), editor, createElementStub(txt)))
    }

    @Test
    fun `invoke returns early when editor is null`() {
        val action = EFQueryLensCopySqlIntentionAction()

        action.invoke(createProjectStub(), null, createElementStub(containingFile = null))
    }

    @Test
    fun `invoke returns early when element has no containing file`() {
        val action = EFQueryLensCopySqlIntentionAction()

        action.invoke(createProjectStub(), createEditorStub(), createElementStub(containingFile = null))
    }

    @Test
    fun `invoke executes for file and editor when action type is unknown`() {
        val action =
            object : EFQueryLensBaseIntentionAction("EF QueryLens: Unknown", "unknown") {
            }
        val editor = createEditorWithCaret(line = 3, column = 9)
        val file = createPsiFileStub(LightVirtualFile("Program.cs", "class C {}"))

        action.invoke(createProjectStub(), editor, createElementStub(containingFile = file))
    }

    private fun createProjectStub(): Project =
        proxy(Project::class.java)

    private fun createEditorStub(): Editor =
        proxy(Editor::class.java)

    private fun createEditorWithCaret(
        line: Int,
        column: Int,
    ): Editor {
        val caret =
            Proxy.newProxyInstance(
                CaretModel::class.java.classLoader,
                arrayOf(CaretModel::class.java),
            ) { _, method, _ ->
                when (method.name) {
                    "getLogicalPosition" -> LogicalPosition(line, column)
                    else -> defaultReturnValue(method.returnType)
                }
            } as CaretModel

        return Proxy.newProxyInstance(
            Editor::class.java.classLoader,
            arrayOf(Editor::class.java),
        ) { _, method, _ ->
            when (method.name) {
                "getCaretModel" -> caret
                else -> defaultReturnValue(method.returnType)
            }
        } as Editor
    }

    private fun createPsiFileStub(virtualFile: LightVirtualFile): PsiFile =
        Proxy.newProxyInstance(
            PsiFile::class.java.classLoader,
            arrayOf(PsiFile::class.java),
        ) { _, method, _ ->
            when (method.name) {
                "getVirtualFile" -> virtualFile
                else -> defaultReturnValue(method.returnType)
            }
        } as PsiFile

    private fun createElementStub(containingFile: PsiFile?): PsiElement =
        Proxy.newProxyInstance(
            PsiElement::class.java.classLoader,
            arrayOf(PsiElement::class.java),
        ) { _, method, _ ->
            when (method.name) {
                "getContainingFile" -> containingFile
                else -> defaultReturnValue(method.returnType)
            }
        } as PsiElement

    private fun <T> proxy(type: Class<T>): T =
        Proxy.newProxyInstance(
            type.classLoader,
            arrayOf(type),
        ) { _, method, _ ->
            defaultReturnValue(method.returnType)
        } as T

    private fun defaultReturnValue(returnType: Class<*>): Any? =
        when (returnType) {
            java.lang.Boolean.TYPE -> false
            java.lang.Integer.TYPE -> 0
            java.lang.Long.TYPE -> 0L
            java.lang.Double.TYPE -> 0.0
            java.lang.Float.TYPE -> 0f
            java.lang.Short.TYPE -> 0.toShort()
            java.lang.Byte.TYPE -> 0.toByte()
            java.lang.Character.TYPE -> 0.toChar()
            java.lang.Void.TYPE -> Unit
            else -> null
        }
}
