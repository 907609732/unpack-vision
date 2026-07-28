package com.unpackvision.mobile

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class OfflineScanQueueTest {
    @Test
    fun codecRoundTripsChineseAndTrackingCharacters() {
        val commands = listOf(
            QueuedScanCommand("event-1", "station-a", "SF-00123", "2026-07-22T12:30:00+08:00"),
            QueuedScanCommand("event-2", "工位-二", "YT测试0001", "2026-07-22T12:31:00+08:00")
        )

        assertEquals(commands, QueuedScanCommandCodec.decode(QueuedScanCommandCodec.encode(commands)))
    }

    @Test
    fun decoderIgnoresDamagedRowsAndDuplicateEvents() {
        val command = QueuedScanCommand("event-1", "station-a", "SF00123", "2026-07-22T12:30:00+08:00")
        val encoded = QueuedScanCommandCodec.encode(listOf(command, command)) + "\nnot-valid"

        val decoded = QueuedScanCommandCodec.decode(encoded)

        assertEquals(listOf(command), decoded)
        assertTrue(QueuedScanCommandCodec.decode(null).isEmpty())
    }
}
