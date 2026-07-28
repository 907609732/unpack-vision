package com.unpackvision.mobile

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class StableBarcodeGateTest {
    @Test
    fun emitsAfterTwoStableHits() {
        val gate = StableBarcodeGate()

        assertNull(gate.observe("YT123", 1_000))
        assertEquals("YT123", gate.observe("YT123", 1_100))
        assertNull(gate.observe("YT123", 1_300))
    }

    @Test
    fun sameBarcodeMustLeaveRegionBeforeItCanTriggerAgain() {
        val gate = StableBarcodeGate()
        gate.observe("YT123", 1_000)
        assertEquals("YT123", gate.observe("YT123", 1_100))

        assertNull(gate.observe(null, 1_300))
        assertNull(gate.observe(null, 2_499))
        assertNull(gate.observe("YT123", 2_500))

        assertNull(gate.observe(null, 2_600))
        assertNull(gate.observe(null, 3_800))
        assertNull(gate.observe("YT123", 3_900))
        assertEquals("YT123", gate.observe("YT123", 4_000))
    }

    @Test
    fun unstableSequenceDoesNotEmit() {
        val gate = StableBarcodeGate()

        assertNull(gate.observe("A", 1_000))
        assertNull(gate.observe("B", 1_100))
        assertNull(gate.observe("A", 1_200))
        assertNull(gate.observe("A", 1_700))
    }
}
