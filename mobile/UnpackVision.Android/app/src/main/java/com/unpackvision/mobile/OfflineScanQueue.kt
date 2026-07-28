package com.unpackvision.mobile

import android.content.Context
import java.time.OffsetDateTime
import java.util.Base64
import java.util.UUID

data class QueuedScanCommand(
    val eventId: String,
    val stationId: String,
    val value: String,
    val detectedAt: String
) {
    companion object {
        fun create(stationId: String, value: String) = QueuedScanCommand(
            eventId = UUID.randomUUID().toString(),
            stationId = stationId.trim(),
            value = value.trim(),
            detectedAt = OffsetDateTime.now().toString()
        )
    }
}

object QueuedScanCommandCodec {
    private val encoder = Base64.getUrlEncoder().withoutPadding()
    private val decoder = Base64.getUrlDecoder()

    fun encode(commands: List<QueuedScanCommand>): String = commands.joinToString("\n") { command ->
        listOf(command.eventId, command.stationId, command.value, command.detectedAt)
            .joinToString(".") { encodeField(it) }
    }

    fun decode(serialized: String?): List<QueuedScanCommand> {
        if (serialized.isNullOrBlank()) return emptyList()
        val seen = mutableSetOf<String>()
        return serialized.lineSequence().mapNotNull { line ->
            runCatching {
                val fields = line.split('.')
                require(fields.size == 4)
                QueuedScanCommand(
                    eventId = decodeField(fields[0]),
                    stationId = decodeField(fields[1]),
                    value = decodeField(fields[2]),
                    detectedAt = decodeField(fields[3])
                )
            }.getOrNull()
        }.filter { it.eventId.isNotBlank() && it.value.isNotBlank() && seen.add(it.eventId) }.toList()
    }

    private fun encodeField(value: String): String =
        encoder.encodeToString(value.toByteArray(Charsets.UTF_8))

    private fun decodeField(value: String): String =
        String(decoder.decode(value), Charsets.UTF_8)
}

class OfflineScanQueueStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)

    @Synchronized
    fun snapshot(): List<QueuedScanCommand> =
        QueuedScanCommandCodec.decode(preferences.getString(KEY_QUEUE, null))

    @Synchronized
    fun enqueue(command: QueuedScanCommand) {
        val commands = snapshot().toMutableList()
        if (commands.none { it.eventId == command.eventId }) commands += command
        save(commands)
    }

    @Synchronized
    fun remove(eventId: String) {
        save(snapshot().filterNot { it.eventId == eventId })
    }

    private fun save(commands: List<QueuedScanCommand>) {
        preferences.edit().putString(KEY_QUEUE, QueuedScanCommandCodec.encode(commands)).apply()
    }

    private companion object {
        const val PREFERENCES = "unpackvision_offline_scans"
        const val KEY_QUEUE = "pending_scan_collection"
    }
}
