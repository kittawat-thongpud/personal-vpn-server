package dev.personalvpn.client

import kotlin.math.*

/** Shared up/down scale: expand immediately, settle after fifteen quiet samples. */
internal class ChartScale {
    var ceiling = 1024.0; private set
    private var quietSamples = 0
    fun update(peak: Double): Double {
        val wanted = nice(max(1024.0, peak * 1.12))
        if (wanted > ceiling) { ceiling = wanted; quietSamples = 0 }
        else if (wanted < ceiling / 2) { if (++quietSamples >= 15) { ceiling = max(wanted, nice(ceiling * .4)); quietSamples = 0 } }
        else quietSamples = 0
        return ceiling
    }
    companion object {
        fun nice(value: Double): Double {
            val unit = when { value >= 1024.0*1024 -> 1024.0*1024; value >= 1024 -> 1024.0; else -> 1.0 }
            val magnitude = 10.0.pow(floor(log10(max(1.0, value / unit)))) * unit
            return listOf(1.0, 2.0, 5.0, 10.0).first { it * magnitude >= value } * magnitude
        }
    }
}
