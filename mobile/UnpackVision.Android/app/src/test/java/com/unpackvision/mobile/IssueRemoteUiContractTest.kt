package com.unpackvision.mobile

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class IssueRemoteUiContractTest {
    @Test
    fun visibleTagActionsMatchDesktopWireContract() {
        assertEquals(
            listOf("破损", "调包", "少件", "采购"),
            IssueRemoteUiContract.tagActions.map { it.label }
        )
        assertEquals(
            listOf(
                "UV-TAG-DAMAGE01",
                "UV-TAG-SWAPPED1",
                "UV-TAG-MISSING1",
                "UV-TAG-PURCHASE"
            ),
            IssueRemoteUiContract.tagActions.map { it.command }
        )
    }

    @Test
    fun visibleActionsDoNotExposeLegacyUndo() {
        val visibleCommands = IssueRemoteUiContract.tagActions.map { it.command } +
            IssueRemoteUiContract.SNAPSHOT_COMMAND +
            IssueRemoteUiContract.STOP_COMMAND

        assertFalse(visibleCommands.contains("UV-UNDO-TAG"))
    }

    @Test
    fun advancedOptionsKeepExistingDefaults() {
        assertTrue(IssueRemoteUiContract.DEFAULT_START_COMPUTER_RECORDING)
        assertFalse(IssueRemoteUiContract.DEFAULT_TORCH_ENABLED)
    }

    @Test
    fun noteCommandUsesExistingWirePrefix() {
        assertEquals("UV-NOTE:外箱破裂", IssueRemoteUiContract.noteCommand("外箱破裂"))
    }
}
