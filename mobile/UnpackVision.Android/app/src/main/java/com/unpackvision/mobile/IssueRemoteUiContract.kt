package com.unpackvision.mobile

internal data class IssueRemoteTagAction(
    val label: String,
    val command: String
)

/**
 * Keeps the phone's visible issue actions aligned with the desktop tag wire contract.
 * The legacy undo command remains server-side for older clients but is intentionally
 * absent from this visible action catalog.
 */
internal object IssueRemoteUiContract {
    const val DEFAULT_START_COMPUTER_RECORDING = true
    const val DEFAULT_TORCH_ENABLED = false
    const val SNAPSHOT_COMMAND = "UV-SNAPSHOT"
    const val STOP_COMMAND = "UV-STOP"
    private const val NOTE_PREFIX = "UV-NOTE:"

    val tagActions = listOf(
        IssueRemoteTagAction("破损", "UV-TAG-DAMAGE01"),
        IssueRemoteTagAction("调包", "UV-TAG-SWAPPED1"),
        IssueRemoteTagAction("少件", "UV-TAG-MISSING1"),
        IssueRemoteTagAction("采购", "UV-TAG-PURCHASE")
    )

    fun noteCommand(note: String): String = "$NOTE_PREFIX$note"
}
