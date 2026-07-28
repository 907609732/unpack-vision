package com.unpackvision.mobile

class StableBarcodeGate(
    private val requiredHits: Int = 2,
    private val stabilityWindowMillis: Long = 300,
    private val leaveWindowMillis: Long = 1_200
) {
    private var candidate: String? = null
    private var firstSeenAt = 0L
    private var hits = 0
    private var latched: String? = null
    private var emptySince = 0L

    @Synchronized
    fun observe(value: String?, nowMillis: Long = System.currentTimeMillis()): String? {
        val normalized = value?.trim()?.takeIf { it.isNotEmpty() }
        if (normalized == null) {
            candidate = null
            hits = 0
            if (emptySince == 0L) emptySince = nowMillis
            if (latched != null && nowMillis - emptySince >= leaveWindowMillis) latched = null
            return null
        }

        emptySince = 0L
        if (normalized == latched) return null
        if (candidate != normalized || nowMillis - firstSeenAt > stabilityWindowMillis) {
            candidate = normalized
            firstSeenAt = nowMillis
            hits = 1
            return null
        }

        hits++
        if (hits < requiredHits) return null
        latched = normalized
        candidate = null
        hits = 0
        return normalized
    }
}
