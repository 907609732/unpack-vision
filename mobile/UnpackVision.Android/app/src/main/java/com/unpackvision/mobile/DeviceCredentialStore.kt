package com.unpackvision.mobile

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.spec.ECGenParameterSpec
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

data class StoredDeviceCredential(
    val deviceId: String,
    val accessToken: String,
    val stationId: String,
    val stationAddress: String
)

class DeviceCredentialStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)

    fun getOrCreatePublicKey(): String {
        val keyStore = loadKeyStore()
        if (!keyStore.containsAlias(SIGNING_KEY_ALIAS)) {
            val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, ANDROID_KEY_STORE)
            generator.initialize(
                KeyGenParameterSpec.Builder(
                    SIGNING_KEY_ALIAS,
                    KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY
                )
                    .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                    .setDigests(KeyProperties.DIGEST_SHA256)
                    .build()
            )
            generator.generateKeyPair()
        }
        val certificate = keyStore.getCertificate(SIGNING_KEY_ALIAS)
            ?: error("无法读取手机设备密钥")
        return Base64.encodeToString(certificate.publicKey.encoded, Base64.NO_WRAP)
    }

    fun save(credential: StoredDeviceCredential) {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateEncryptionKey())
        val encryptedToken = cipher.doFinal(credential.accessToken.toByteArray(Charsets.UTF_8))
        preferences.edit()
            .putString(KEY_DEVICE_ID, credential.deviceId)
            .putString(KEY_TOKEN, Base64.encodeToString(encryptedToken, Base64.NO_WRAP))
            .putString(KEY_TOKEN_IV, Base64.encodeToString(cipher.iv, Base64.NO_WRAP))
            .putString(KEY_STATION_ID, credential.stationId)
            .putString(KEY_STATION_ADDRESS, credential.stationAddress)
            .apply()
    }

    fun load(): StoredDeviceCredential? = runCatching {
        val deviceId = preferences.getString(KEY_DEVICE_ID, null) ?: return null
        val encryptedToken = Base64.decode(preferences.getString(KEY_TOKEN, null), Base64.NO_WRAP)
        val iv = Base64.decode(preferences.getString(KEY_TOKEN_IV, null), Base64.NO_WRAP)
        val stationId = preferences.getString(KEY_STATION_ID, null) ?: return null
        val stationAddress = preferences.getString(KEY_STATION_ADDRESS, null) ?: return null
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, getOrCreateEncryptionKey(), GCMParameterSpec(128, iv))
        StoredDeviceCredential(
            deviceId,
            cipher.doFinal(encryptedToken).toString(Charsets.UTF_8),
            stationId,
            stationAddress
        )
    }.getOrNull()

    fun clear() {
        preferences.edit().clear().apply()
    }

    private fun getOrCreateEncryptionKey(): SecretKey {
        val keyStore = loadKeyStore()
        (keyStore.getKey(ENCRYPTION_KEY_ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEY_STORE)
        generator.init(
            KeyGenParameterSpec.Builder(
                ENCRYPTION_KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build()
        )
        return generator.generateKey()
    }

    private fun loadKeyStore() = KeyStore.getInstance(ANDROID_KEY_STORE).apply { load(null) }

    private companion object {
        const val ANDROID_KEY_STORE = "AndroidKeyStore"
        const val SIGNING_KEY_ALIAS = "unpackvision-device-signing-v1"
        const val ENCRYPTION_KEY_ALIAS = "unpackvision-device-token-v1"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val PREFERENCES = "station-credential"
        const val KEY_DEVICE_ID = "device-id"
        const val KEY_TOKEN = "access-token"
        const val KEY_TOKEN_IV = "access-token-iv"
        const val KEY_STATION_ID = "station-id"
        const val KEY_STATION_ADDRESS = "station-address"
    }
}
