package com.unpackvision.mobile

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AppPreferenceMigrationTest {
    @Test
    fun defaultMainCameraOnlyIsOffForNewInstalls() {
        assertFalse(AppPreferenceMigration.migrateMainCameraOnly(null, userSet = false))
    }

    @Test
    fun oldImplicitMainCameraOnlyValueMigratesOff() {
        assertFalse(AppPreferenceMigration.migrateMainCameraOnly(true, userSet = false))
    }

    @Test
    fun explicitUserChoiceIsPreserved() {
        assertTrue(AppPreferenceMigration.migrateMainCameraOnly(true, userSet = true))
        assertFalse(AppPreferenceMigration.migrateMainCameraOnly(false, userSet = true))
    }
}
