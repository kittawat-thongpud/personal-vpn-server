package dev.personalvpn.client

import android.content.Intent
import androidx.core.content.UnusedAppRestrictionsConstants as Unused
import org.junit.Assert.*
import org.junit.Test

class BackgroundChecksTest {
    private fun ready()=BackgroundFacts(true,false,false,false,true,true,Unused.DISABLED,false)
    @Test fun readyStandardSettingsDoNotProduceFalseWarnings() {
        val rows=BackgroundChecks.checks(ready())
        assertTrue(rows.all { it.state==CheckState.Good })
        assertFalse(BackgroundReview(rows,Unused.DISABLED,null).shouldPrompt)
    }
    @Test fun missingReadingsAreUnknownRatherThanSafeOrDisabled() {
        val rows=BackgroundChecks.checks(BackgroundFacts())
        assertTrue(rows.all { it.state==CheckState.Unknown })
        assertFalse(BackgroundReview(rows,Unused.ERROR,null).shouldPrompt)
    }
    @Test fun relevantRestrictionsAreReportedIndependently() {
        val rows=BackgroundChecks.checks(ready().copy(batteryExempt=false,restricted=true,saver=true,dataRestricted=true,notifications=false,channel=false,unused=Unused.API_31,dontKeepActivities=true))
        assertEquals(setOf("doze","background","saver","data","notifications","channel","unused","activities"),rows.filter { it.state==CheckState.Attention }.map { it.id }.toSet())
        assertTrue(BackgroundReview(rows,Unused.API_31,null).shouldPrompt)
    }
    @Test fun oemSettingsRemainManualEvenWhenAndroidExemptionLooksReady() {
        for(facts in listOf(ready().copy(samsung=true),ready().copy(xiaomi=true))) {
            val rows=BackgroundChecks.checks(facts)
            assertTrue(rows.any { it.state==CheckState.Manual })
            assertFalse(rows.any { it.state==CheckState.Attention })
            assertFalse(BackgroundReview(rows,Unused.DISABLED,null).shouldPrompt)
        }
        val samsung=BackgroundChecks.checks(ready().copy(samsung=true)).filter { it.id.startsWith("samsung") }
        assertEquals(setOf("samsung_sleep","samsung_battery","samsung_restart","samsung_memory"),samsung.map { it.id }.toSet())
    }
    @Test fun recentHistoryAndOtherEntryPointsDoNotTriggerLauncherGuidance() {
        val launcher=setOf(Intent.CATEGORY_LAUNCHER)
        assertTrue(BackgroundChecks.isLauncherOpen(Intent.ACTION_MAIN,launcher,0))
        assertFalse(BackgroundChecks.isLauncherOpen(Intent.ACTION_MAIN,launcher,Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY))
        assertFalse(BackgroundChecks.isLauncherOpen(TunnelService.CONNECT,launcher,0))
        assertFalse(BackgroundChecks.isLauncherOpen(Intent.ACTION_MAIN,null,0))
        assertFalse(BackgroundChecks.isLauncherOpen("",null,0))
    }
    @Test fun oldExitEvidenceDoesNotRepeatedlyInterruptAConfiguredDevice() {
        val exit=PreviousExit(1234,"App crash",true)
        assertFalse(BackgroundReview(BackgroundChecks.checks(ready()),Unused.DISABLED,exit).shouldPrompt)
    }
    @Test fun startupContainsOnlyDetectedIssuesNotUnknownOrManualSettings() {
        val rows=BackgroundChecks.checks(ready().copy(saver=true,notifications=null,samsung=true))
        val review=BackgroundReview(rows,Unused.DISABLED,null)
        assertTrue(review.shouldPrompt)
        assertEquals(listOf("saver"),review.detectedIssues.map { it.id })
        assertEquals(4,review.manualChecks.size)
        assertTrue(review.showManualOnLaunch(false))
        assertFalse(review.showManualOnLaunch(true))
        assertEquals(listOf("saver"),review.detectedIssues.map { it.id })
        assertFalse(review.copy(checks=BackgroundChecks.checks(ready().copy(notifications=null,samsung=true))).shouldPrompt)
        assertFalse(BackgroundReview(BackgroundChecks.checks(ready()),Unused.DISABLED,null).showManualOnLaunch(false))
    }
    @Test fun everyCheckHasShortOrderedActionSteps() {
        val rows=BackgroundChecks.checks(ready().copy(samsung=true,xiaomi=true,developer=true))
        assertTrue(rows.all { it.steps.size in 2..3 && it.steps.all { step -> step.isNotBlank() && step.length<=180 } })
    }
}
