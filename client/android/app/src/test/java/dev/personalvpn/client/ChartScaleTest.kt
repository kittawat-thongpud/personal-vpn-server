package dev.personalvpn.client

import org.junit.Assert.*
import org.junit.Test

class ChartScaleTest {
    @Test fun spikesAreNotClippedAndSmallWindowsDoNotPumpTheScale() {
        val scale=ChartScale()
        val ceiling=scale.update(8_000_000.0)
        assertTrue(ceiling>=8_000_000)
        repeat(14) { assertEquals(ceiling,scale.update(0.0),0.0) }
        assertEquals(ceiling/2,scale.update(0.0),0.0)
        assertEquals(10_000.0,ChartScale.nice(5001.0),0.0)
        assertTrue(scale.update(100_000_000.0)>=100_000_000)
    }
}
