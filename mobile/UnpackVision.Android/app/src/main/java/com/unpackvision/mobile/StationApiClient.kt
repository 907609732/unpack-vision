package com.unpackvision.mobile

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.time.OffsetDateTime
import java.util.UUID

class StationApiClient {
    suspend fun pair(
        pairingPayload: String,
        publicKey: String,
        deviceName: String = android.os.Build.MODEL
    ): StoredDeviceCredential = withContext(Dispatchers.IO) {
        val payload = JSONObject(pairingPayload)
        val expiresAt = runCatching { OffsetDateTime.parse(payload.getString("expiresAt")) }
            .getOrElse { throw IllegalStateException("无效的配对二维码，请在电脑端刷新后重试") }
        if (!expiresAt.isAfter(OffsetDateTime.now())) {
            throw IllegalStateException("配对二维码已过期，请在电脑端刷新二维码")
        }
        val stationAddress = payload.getString("stationAddress").trimEnd('/')
        val stationId = payload.getString("stationId")
        val certificateFingerprint = PinnedCertificateTrustManager.normalizeFingerprint(
            payload.getString("certificateFingerprint")
        )
        val endpoint = URL("$stationAddress/device/v1/pair")
        val body = JSONObject()
            .put("sessionId", payload.getString("id"))
            .put("token", payload.getString("token"))
            .put("name", deviceName)
            .put("publicKey", publicKey)
            .put("roles", org.json.JSONArray(listOf("scanner", "camera", "remote")))
            .put("scopes", org.json.JSONArray(listOf("scan:send", "camera:publish", "records:read", "video:read")))
            .toString()
        val response = request(endpoint, "POST", body, certificateFingerprint)
        val json = JSONObject(response)
        StoredDeviceCredential(
            deviceId = json.getJSONObject("device").getString("id"),
            accessToken = json.getString("accessToken"),
            stationId = stationId,
            stationAddress = stationAddress,
            certificateFingerprint = certificateFingerprint
        )
    }

    suspend fun fetchStationId(
        baseAddress: String,
        certificateFingerprint: String
    ): String = withContext(Dispatchers.IO) {
        val endpoint = URL("${baseAddress.trimEnd('/')}/api/v1/health")
        val connection = openPinnedConnection(endpoint, certificateFingerprint).apply {
            requestMethod = "GET"
            connectTimeout = 4_000
            readTimeout = 6_000
        }
        val responseText = (if (connection.responseCode in 200..299) connection.inputStream else connection.errorStream)
            ?.bufferedReader(Charsets.UTF_8)
            ?.use { it.readText() }
            .orEmpty()
        if (connection.responseCode !in 200..299) {
            throw IllegalStateException("工位返回 ${connection.responseCode}：$responseText")
        }
        JSONObject(responseText).getJSONObject("station").getString("stationId")
    }

    suspend fun createPublishSession(
        baseAddress: String,
        deviceId: String,
        accessToken: String,
        certificateFingerprint: String
    ): MediaPublishEndpoint = withContext(Dispatchers.IO) {
        val endpoint = URL("${baseAddress.trimEnd('/')}/api/v1/media/publish-session")
        val response = authenticatedRequest(
            endpoint,
            "POST",
            deviceId,
            accessToken,
            certificateFingerprint
        )
        val json = JSONObject(response)
        MediaPublishEndpoint(
            rtspUrl = json.getString("rtspUrl"),
            authUser = json.getString("authUser")
        )
    }

    suspend fun fetchStationState(
        baseAddress: String,
        stationId: String,
        deviceId: String,
        accessToken: String,
        certificateFingerprint: String
    ): StationState = withContext(Dispatchers.IO) {
        require(stationId.isNotBlank()) { "请先连接电脑获取工位 ID" }
        val encodedStationId = URLEncoder.encode(stationId, Charsets.UTF_8.name())
        val endpoint = URL("${baseAddress.trimEnd('/')}/api/v1/stations/$encodedStationId/state")
        val json = JSONObject(authenticatedRequest(
            endpoint,
            "GET",
            deviceId,
            accessToken,
            certificateFingerprint,
            connectTimeoutMilliseconds = 3_000,
            readTimeoutMilliseconds = 4_000
        ))
        StationState(
            stationId = json.getString("stationId"),
            recordingState = json.getString("recordingState"),
            recordId = json.optString("recordId").takeIf { it.isNotBlank() && it != "null" },
            trackingNo = json.optString("trackingNo").takeIf { it.isNotBlank() && it != "null" },
            desktopReady = json.optBoolean("desktopReady", true)
        )
    }

    suspend fun submitScan(
        baseAddress: String,
        stationId: String,
        deviceId: String,
        accessToken: String?,
        value: String,
        mode: WorkMode,
        certificateFingerprint: String,
        eventId: String = UUID.randomUUID().toString(),
        detectedAt: String = java.time.OffsetDateTime.now().toString()
    ): String = withContext(Dispatchers.IO) {
        require(stationId.isNotBlank()) { "请先连接电脑获取工位 ID" }
        val encodedStationId = URLEncoder.encode(stationId, Charsets.UTF_8.name())
        val endpoint = URL("${baseAddress.trimEnd('/')}/api/v1/stations/$encodedStationId/scans")
        val body = JSONObject()
            .put("eventId", eventId)
            .put("deviceId", deviceId)
            .put("stationId", stationId)
            .put("value", value)
            .put("format", "camera")
            .put("mode", mode.contractName)
            .put("workflow", if (mode == WorkMode.ScanCollection) "ScanCollection" else "Unpacking")
            .put("detectedAt", detectedAt)
            .put("idempotencyKey", eventId)
            .toString()
        val connection = openPinnedConnection(endpoint, certificateFingerprint).apply {
            requestMethod = "POST"
            connectTimeout = 4_000
            readTimeout = 8_000
            doOutput = true
            setRequestProperty("Content-Type", "application/json; charset=utf-8")
            if (!accessToken.isNullOrBlank()) {
                setRequestProperty("X-UnpackVision-Device", deviceId)
                setRequestProperty("Authorization", "Bearer $accessToken")
            }
        }
        connection.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
        val responseText = (if (connection.responseCode in 200..299) connection.inputStream else connection.errorStream)
            ?.bufferedReader(Charsets.UTF_8)
            ?.use { it.readText() }
            .orEmpty()
        if (connection.responseCode !in 200..299) {
            throw IllegalStateException("工位返回 ${connection.responseCode}：$responseText")
        }
        JSONObject(responseText).optString("message", "电脑已确认")
    }

    private fun request(
        endpoint: URL,
        method: String,
        body: String? = null,
        certificateFingerprint: String
    ): String {
        val connection = openPinnedConnection(endpoint, certificateFingerprint).apply {
            requestMethod = method
            connectTimeout = 5_000
            readTimeout = 8_000
            if (body != null) {
                doOutput = true
                setRequestProperty("Content-Type", "application/json; charset=utf-8")
            }
        }
        if (body != null) {
            connection.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
        }
        val responseText = (if (connection.responseCode in 200..299) connection.inputStream else connection.errorStream)
            ?.bufferedReader(Charsets.UTF_8)
            ?.use { it.readText() }
            .orEmpty()
        if (connection.responseCode !in 200..299) {
            val serverMessage = runCatching { JSONObject(responseText).optString("error") }
                .getOrNull()
                ?.takeIf { it.isNotBlank() }
            throw IllegalStateException(serverMessage ?: "工位返回 ${connection.responseCode}：$responseText")
        }
        return responseText
    }

    private fun authenticatedRequest(
        endpoint: URL,
        method: String,
        deviceId: String,
        accessToken: String,
        certificateFingerprint: String,
        connectTimeoutMilliseconds: Int = 8_000,
        readTimeoutMilliseconds: Int = 12_000
    ): String {
        val connection = openPinnedConnection(endpoint, certificateFingerprint).apply {
            requestMethod = method
            connectTimeout = connectTimeoutMilliseconds
            readTimeout = readTimeoutMilliseconds
            doOutput = method == "POST" || method == "PUT"
            setRequestProperty("X-UnpackVision-Device", deviceId)
            setRequestProperty("Authorization", "Bearer $accessToken")
        }
        if (connection.doOutput) connection.outputStream.use { }
        val responseText = (if (connection.responseCode in 200..299) connection.inputStream else connection.errorStream)
            ?.bufferedReader(Charsets.UTF_8)
            ?.use { it.readText() }
            .orEmpty()
        if (connection.responseCode !in 200..299) {
            throw IllegalStateException("工位返回 ${connection.responseCode}：$responseText")
        }
        return responseText
    }
}

data class MediaPublishEndpoint(
    val rtspUrl: String,
    val authUser: String
)

data class StationState(
    val stationId: String,
    val recordingState: String,
    val recordId: String?,
    val trackingNo: String?,
    val desktopReady: Boolean
)
