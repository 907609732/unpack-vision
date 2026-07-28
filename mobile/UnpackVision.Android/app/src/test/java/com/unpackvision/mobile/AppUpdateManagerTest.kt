package com.unpackvision.mobile

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class AppUpdateManagerTest {
    @Test
    fun parsesReleaseManifestAndNormalizesSha256() {
        val manifest = MobileUpdateManifestParser.parse(
            """
            {
              "versionName": "2.1.1",
              "versionCode": 20101,
              "apkUrl": "https://example.test/app.apk",
              "sha256": "ABCDEF",
              "notesUrl": ""
            }
            """.trimIndent()
        )

        assertEquals("2.1.1", manifest.versionName)
        assertEquals(20101, manifest.versionCode)
        assertEquals("https://example.test/app.apk", manifest.apkUrl)
        assertEquals("abcdef", manifest.sha256)
        assertNull(manifest.notesUrl)
    }
}
