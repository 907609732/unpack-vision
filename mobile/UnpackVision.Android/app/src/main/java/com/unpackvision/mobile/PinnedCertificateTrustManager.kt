package com.unpackvision.mobile

import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.security.cert.X509Certificate
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

class PinnedCertificateTrustManager(fingerprint: String) : X509TrustManager {
    private val expected = normalizeFingerprint(fingerprint)

    init {
        require(expected.length == 64) { "电脑证书指纹无效，请重新配对" }
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val certificate = chain?.firstOrNull()
            ?: throw java.security.cert.CertificateException("电脑没有提供证书")
        val actual = MessageDigest.getInstance("SHA-256")
            .digest(certificate.encoded)
            .joinToString("") { "%02x".format(it) }
        if (!MessageDigest.isEqual(actual.toByteArray(), expected.toByteArray())) {
            throw java.security.cert.CertificateException("电脑证书与配对二维码不一致")
        }
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

    companion object {
        fun normalizeFingerprint(value: String): String =
            value.filter { it.isLetterOrDigit() }.lowercase()
    }
}

internal fun openPinnedConnection(
    endpoint: URL,
    certificateFingerprint: String?
): HttpURLConnection {
    if (endpoint.protocol.equals("http", ignoreCase = true)) {
        require(endpoint.host == "127.0.0.1" || endpoint.host.equals("localhost", ignoreCase = true)) {
            "为保护单号和设备令牌，局域网连接必须使用 HTTPS"
        }
        return endpoint.openConnection() as HttpURLConnection
    }
    require(endpoint.protocol.equals("https", ignoreCase = true)) {
        "不支持的工位连接协议"
    }
    val manager = PinnedCertificateTrustManager(
        certificateFingerprint ?: error("缺少电脑证书指纹，请重新配对")
    )
    val context = SSLContext.getInstance("TLS")
    context.init(null, arrayOf(manager), null)
    return (endpoint.openConnection() as HttpsURLConnection).apply {
        sslSocketFactory = context.socketFactory
        // The SHA-256 pin identifies the exact workstation certificate. This
        // also keeps hotspot/IP changes working without weakening identity.
        hostnameVerifier = javax.net.ssl.HostnameVerifier { _, _ -> true }
    }
}
