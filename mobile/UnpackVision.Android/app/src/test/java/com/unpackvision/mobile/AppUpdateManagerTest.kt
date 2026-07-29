package com.unpackvision.mobile

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class AppUpdateManagerTest {
    @Test
    fun parsesReleaseManifestAndNormalizesSha256() {
        val manifest = MobileUpdateManifestParser.parse(
            """
            {
              "versionName": "2.1.1",
              "versionCode": 20101,
              "apkUrl": "https://github.com/907609732/unpack-vision/releases/download/v2.2.0/app.apk",
              "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
              "notesUrl": "",
              "releaseNotesUrl": "https://github.com/907609732/unpack-vision/releases/tag/v2.2.0",
              "minimumSupportedVersion": "2.1.0",
              "critical": true,
              "publishedAt": "2026-07-29T00:00:00Z"
            }
            """.trimIndent()
        )

        assertEquals("2.1.1", manifest.versionName)
        assertEquals(20101, manifest.versionCode)
        assertEquals(
            "https://github.com/907609732/unpack-vision/releases/download/v2.2.0/app.apk",
            manifest.apkUrl
        )
        assertEquals("a".repeat(64), manifest.sha256)
        assertNull(manifest.notesUrl)
        assertEquals("2.1.0", manifest.minimumSupportedVersion)
        assertEquals(true, manifest.critical)
        assertEquals("2026-07-29T00:00:00Z", manifest.publishedAt)
    }

    @Test
    fun rejectsUntrustedOrMalformedUpdateManifest() {
        assertThrows(IllegalArgumentException::class.java) {
            MobileUpdateManifestParser.parse(
                """
                {
                  "versionName": "2.2.1",
                  "versionCode": 20201,
                  "apkUrl": "http://example.test/app.apk",
                  "sha256": "abcdef"
                }
                """.trimIndent()
            )
        }
    }
}
