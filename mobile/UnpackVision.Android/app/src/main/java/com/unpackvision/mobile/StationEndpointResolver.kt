package com.unpackvision.mobile

import android.content.Context
import android.net.ConnectivityManager
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.net.URI
import kotlin.coroutines.resume

enum class StationTransport(val displayName: String) {
    LocalNetwork("局域网"),
    AutoDiscovery("自动发现"),
    HotspotOrTethering("热点 / USB / 蓝牙共享"),
    UsbDebug("USB直连")
}

data class ResolvedStationConnection(
    val credential: StoredDeviceCredential,
    val state: StationState,
    val transport: StationTransport
)

class StationEndpointResolver(
    context: Context,
    private val api: StationApiClient,
    private val credentialStore: DeviceCredentialStore
) {
    private val appContext = context.applicationContext
    private val connectivityManager =
        appContext.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
    private val nsdManager =
        appContext.getSystemService(Context.NSD_SERVICE) as NsdManager

    suspend fun resolve(credential: StoredDeviceCredential): ResolvedStationConnection =
        withContext(Dispatchers.IO) {
            tryAddress(
                credential,
                credential.stationAddress,
                inferSavedTransport(credential.stationAddress)
            )?.let { return@withContext it }

            val candidates = linkedMapOf<String, StationTransport>()
            addCandidate(candidates, "http://127.0.0.1:$STATION_PORT", StationTransport.UsbDebug)

            gatewayAddresses().forEach {
                addCandidate(candidates, "http://$it:$STATION_PORT", StationTransport.HotspotOrTethering)
            }

            findNsdStation(credential.stationId)?.let {
                addCandidate(candidates, it, StationTransport.AutoDiscovery)
            }

            stationHostAliases(credential.stationId).forEach {
                addCandidate(candidates, it, StationTransport.AutoDiscovery)
            }

            for ((address, transport) in candidates) {
                tryAddress(credential, address, transport)?.let {
                    return@withContext it
                }
            }

            throw IllegalStateException(
                "未找到电脑工位。请确认电脑端已打开，或使用同一Wi-Fi、手机热点、USB共享网络。"
            )
        }

    private suspend fun tryAddress(
        credential: StoredDeviceCredential,
        address: String,
        transport: StationTransport
    ): ResolvedStationConnection? {
        val state = runCatching {
            api.fetchStationState(
                address,
                credential.stationId,
                credential.deviceId,
                credential.accessToken
            )
        }.getOrNull() ?: return null

        val resolvedCredential = if (address == credential.stationAddress) {
            credential
        } else {
            credential.copy(stationAddress = address).also(credentialStore::save)
        }
        return ResolvedStationConnection(resolvedCredential, state, transport)
    }

    private fun gatewayAddresses(): List<String> {
        val network = connectivityManager.activeNetwork ?: return emptyList()
        return connectivityManager.getLinkProperties(network)
            ?.routes
            ?.mapNotNull { it.gateway?.hostAddress }
            ?.filter { it.contains('.') && it != "0.0.0.0" }
            ?.distinct()
            .orEmpty()
    }

    private fun stationHostAliases(stationId: String): List<String> {
        val host = stationId
            .lowercase()
            .replace(Regex("[^a-z0-9-]"), "-")
            .trim('-')
        if (host.isBlank()) return emptyList()
        return listOf(
            "http://$host.local:$STATION_PORT",
            "http://$host:$STATION_PORT"
        )
    }

    @Suppress("DEPRECATION")
    private suspend fun findNsdStation(stationId: String): String? =
        withTimeoutOrNull(NSD_TIMEOUT_MILLISECONDS) {
            suspendCancellableCoroutine { continuation ->
                var stopped = false
                lateinit var discoveryListener: NsdManager.DiscoveryListener

                fun stopDiscovery() {
                    if (stopped) return
                    stopped = true
                    runCatching { nsdManager.stopServiceDiscovery(discoveryListener) }
                }

                fun complete(address: String?) {
                    if (!continuation.isActive) return
                    stopDiscovery()
                    continuation.resume(address)
                }

                val resolveListener = object : NsdManager.ResolveListener {
                    override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) = Unit

                    override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                        val advertisedStationId =
                            serviceInfo.attributes["stationId"]?.toString(Charsets.UTF_8)
                        if (!advertisedStationId.isNullOrBlank() &&
                            !advertisedStationId.equals(stationId, ignoreCase = true)
                        ) {
                            return
                        }
                        val host = serviceInfo.host?.hostAddress ?: return
                        complete("http://$host:${serviceInfo.port}")
                    }
                }

                discoveryListener = object : NsdManager.DiscoveryListener {
                    override fun onDiscoveryStarted(serviceType: String) = Unit
                    override fun onDiscoveryStopped(serviceType: String) = Unit
                    override fun onServiceLost(serviceInfo: NsdServiceInfo) = Unit
                    override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) =
                        complete(null)
                    override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) = Unit

                    override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                        if (serviceInfo.serviceType.startsWith(SERVICE_TYPE)) {
                            runCatching { nsdManager.resolveService(serviceInfo, resolveListener) }
                        }
                    }
                }

                continuation.invokeOnCancellation { stopDiscovery() }
                runCatching {
                    nsdManager.discoverServices(
                        SERVICE_TYPE,
                        NsdManager.PROTOCOL_DNS_SD,
                        discoveryListener
                    )
                }.onFailure { complete(null) }
            }
        }

    private fun inferSavedTransport(address: String): StationTransport =
        runCatching { URI(address).host }
            .getOrNull()
            ?.let { if (it == "127.0.0.1" || it == "localhost") StationTransport.UsbDebug else null }
            ?: StationTransport.LocalNetwork

    private fun addCandidate(
        candidates: MutableMap<String, StationTransport>,
        address: String,
        transport: StationTransport
    ) {
        val normalized = address.trim().trimEnd('/')
        if (normalized.isNotBlank()) candidates.putIfAbsent(normalized, transport)
    }

    private companion object {
        const val STATION_PORT = 5271
        const val SERVICE_TYPE = "_unpackvision._tcp."
        const val NSD_TIMEOUT_MILLISECONDS = 2_000L
    }
}
