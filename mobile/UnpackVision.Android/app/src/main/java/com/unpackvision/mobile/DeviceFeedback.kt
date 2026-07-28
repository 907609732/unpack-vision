package com.unpackvision.mobile

import android.content.Context
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager
import android.speech.tts.TextToSpeech
import java.util.Locale

class DeviceFeedback(context: Context) : TextToSpeech.OnInitListener, AutoCloseable {
    private val applicationContext = context.applicationContext
    private val textToSpeech = TextToSpeech(applicationContext, this)
    private var speechReady = false

    override fun onInit(status: Int) {
        if (status != TextToSpeech.SUCCESS) return
        val result = textToSpeech.setLanguage(Locale.SIMPLIFIED_CHINESE)
        speechReady = result != TextToSpeech.LANG_MISSING_DATA && result != TextToSpeech.LANG_NOT_SUPPORTED
        textToSpeech.setSpeechRate(1.0f)
    }

    fun success(message: String) {
        vibrate(longArrayOf(0, 70))
        speak(message)
    }

    fun error(message: String) {
        vibrate(longArrayOf(0, 100, 80, 160))
        speak(message)
    }

    private fun speak(message: String) {
        if (speechReady && message.isNotBlank()) {
            textToSpeech.speak(message, TextToSpeech.QUEUE_FLUSH, null, "unpackvision-feedback")
        }
    }

    private fun vibrate(pattern: LongArray) {
        val vibrator = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            applicationContext.getSystemService(VibratorManager::class.java)?.defaultVibrator
        } else {
            @Suppress("DEPRECATION")
            applicationContext.getSystemService(Context.VIBRATOR_SERVICE) as? Vibrator
        } ?: return
        if (vibrator.hasVibrator()) {
            vibrator.vibrate(VibrationEffect.createWaveform(pattern, -1))
        }
    }

    override fun close() {
        textToSpeech.stop()
        textToSpeech.shutdown()
    }
}
