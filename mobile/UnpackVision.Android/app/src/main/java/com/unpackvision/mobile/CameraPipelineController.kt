package com.unpackvision.mobile

import android.content.Context
import android.graphics.Bitmap
import android.media.Image
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.util.Size
import android.view.SurfaceHolder
import android.view.SurfaceView
import androidx.camera.core.ImageAnalysis
import com.pedro.common.ConnectChecker
import com.pedro.encoder.input.sources.audio.NoAudioSource
import com.pedro.encoder.input.video.CameraHelper
import com.pedro.encoder.utils.gl.AspectRatioMode
import com.pedro.encoder.input.video.Camera2ApiManager.ImageCallback
import com.pedro.extrasources.CameraXSource
import com.pedro.library.generic.GenericStream
import zxingcpp.BarcodeReader

class CameraPipelineController(
    context: Context,
    private val mainCameraOnly: Boolean = true,
    certificateFingerprint: String? = null,
    private val onBarcode: (String) -> Unit,
    private val onStatus: (String) -> Unit,
    private val onBitrate: (Long) -> Unit,
    private val onStreamingChanged: (Boolean) -> Unit = {}
) : ConnectChecker, SurfaceHolder.Callback {
    private val appContext = context.applicationContext
    private val mainHandler = Handler(Looper.getMainLooper())
    private val gate = StableBarcodeGate(requiredHits = 2, stabilityWindowMillis = 300)
    private val cameraSource = CameraXSource(appContext).apply {
        setRequiredResolution(Size(STREAM_WIDTH, STREAM_HEIGHT))
    }
    private val stream = GenericStream(
        appContext,
        this,
        cameraSource,
        NoAudioSource()
    ).apply {
        if (!certificateFingerprint.isNullOrBlank()) {
            getStreamClient().addCertificates(PinnedCertificateTrustManager(certificateFingerprint))
        }
    }
    private val barcodeReader = BarcodeReader(
        BarcodeReader.Options(
            formats = setOf(
                BarcodeReader.Format.CODE_128,
                BarcodeReader.Format.CODE_39,
                BarcodeReader.Format.EAN_UPC,
                BarcodeReader.Format.ITF,
                BarcodeReader.Format.QR_CODE
            ),
            tryHarder = false,
            tryRotate = true,
            tryDownscale = true,
            maxNumberOfSymbols = 1
        )
    )
    private var surfaceView: SurfaceView? = null
    private var prepared = false
    private var analyzerAttached = false
    private var lastAnalysisAt = 0L

    fun bind(surfaceView: SurfaceView) {
        if (this.surfaceView === surfaceView) return
        this.surfaceView?.holder?.removeCallback(this)
        this.surfaceView = surfaceView
        surfaceView.holder.addCallback(this)
        if (surfaceView.holder.surface.isValid) surfaceCreated(surfaceView.holder)
    }

    fun startStreaming(endpoint: String, username: String?, password: String?): Boolean {
        if (!prepared || endpoint.isBlank()) return false
        return try {
            if (!stream.isStreaming) {
                stream.getStreamClient().setAuthorization(username, password)
                stream.startStream(endpoint.trim())
                mainHandler.post { onStatus("正在连接视频服务") }
            }
            true
        } catch (error: Exception) {
            Log.e(TAG, "Unable to start stream", error)
            mainHandler.post {
                onStreamingChanged(false)
                onStatus("启动推流失败：${error.message ?: error.javaClass.simpleName}")
            }
            false
        }
    }

    fun stopStreaming() {
        runCatching { if (stream.isStreaming) stream.stopStream() }
            .onFailure { Log.e(TAG, "Unable to stop stream", it) }
        mainHandler.post { onStreamingChanged(false) }
    }

    fun switchCamera() {
        if (mainCameraOnly) {
            mainHandler.post { onStatus("已锁定主摄像头，可在设置中关闭此选项") }
            return
        }
        runCatching { cameraSource.switchCamera() }
            .onFailure {
                Log.e(TAG, "Unable to switch camera", it)
                mainHandler.post { onStatus("切换镜头失败：${it.message ?: it.javaClass.simpleName}") }
            }
    }

    fun setTorch(enabled: Boolean): Boolean {
        return runCatching {
            if (enabled) cameraSource.enableLantern() else cameraSource.disableLantern()
            true
        }.getOrElse {
            Log.w(TAG, "Unable to update torch", it)
            mainHandler.post { onStatus("闪光灯不可用") }
            false
        }
    }

    fun release() {
        runCatching { setTorch(false) }
        runCatching { stopStreaming() }
        runCatching { if (analyzerAttached) cameraSource.removeImageListener() }
        runCatching { if (stream.isOnPreview) stream.stopPreview() }
        runCatching { stream.release() }
        surfaceView?.holder?.removeCallback(this)
        surfaceView = null
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        val view = surfaceView ?: return
        try {
            val isPortrait = CameraHelper.isPortrait(appContext)
            val gl = stream.getGlInterface()
            gl.setAspectRatioMode(AspectRatioMode.Adjust)
            gl.setPreviewIsPortrait(isPortrait)
            gl.setStreamIsPortrait(isPortrait)
            if (!prepared) {
                val videoPrepared = stream.prepareVideo(
                    STREAM_WIDTH,
                    STREAM_HEIGHT,
                    STREAM_BITRATE,
                    STREAM_FPS,
                    2,
                    CameraHelper.getCameraOrientation(appContext)
                )
                // RootEncoder starts its audio encoder even when NoAudioSource is
                // selected. Preparing the silent encoder prevents a main-thread
                // crash while keeping microphone capture disabled.
                val audioPrepared = stream.prepareAudio(
                    AUDIO_SAMPLE_RATE,
                    false,
                    AUDIO_BITRATE,
                    false,
                    false
                )
                prepared = videoPrepared && audioPrepared
            }
            if (!prepared) {
                mainHandler.post { onStatus("摄像头编码器初始化失败") }
                return
            }
            if (!stream.isOnPreview) stream.startPreview(view)
            if (!analyzerAttached) attachAnalyzer()
            runCatching {
                if (mainCameraOnly) cameraSource.setZoom(1f)
                cameraSource.enableAutoFocus()
                cameraSource.enableAutoExposure()
                cameraSource.enableAutoWhiteBalance()
            }.onFailure { Log.w(TAG, "Unable to enable automatic camera controls", it) }
        } catch (error: Exception) {
            prepared = false
            Log.e(TAG, "Unable to prepare camera pipeline", error)
            mainHandler.post { onStatus("摄像头启动失败：${error.message ?: error.javaClass.simpleName}") }
        }
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        val gl = stream.getGlInterface()
        gl.setAspectRatioMode(AspectRatioMode.Adjust)
        gl.setPreviewIsPortrait(height >= width)
        gl.setPreviewResolution(width, height)
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        if (analyzerAttached) {
            cameraSource.removeImageListener()
            analyzerAttached = false
        }
        if (stream.isOnPreview) stream.stopPreview()
    }

    private fun attachAnalyzer() {
        cameraSource.addImageListener(
            ANALYSIS_WIDTH,
            ANALYSIS_HEIGHT,
            ImageAnalysis.OUTPUT_IMAGE_FORMAT_YUV_420_888,
            true,
            object : ImageCallback {
                override fun onImageAvailable(image: Image) {
                    val now = System.currentTimeMillis()
                    if (now - lastAnalysisAt < ANALYSIS_INTERVAL_MILLIS) return
                    lastAnalysisAt = now
                    var bitmap: Bitmap? = null
                    try {
                        bitmap = image.toCenterLumaBitmap()
                        val value = barcodeReader.read(bitmap).firstOrNull()?.text
                        val accepted = gate.observe(value, now)
                        if (accepted != null) mainHandler.post { onBarcode(accepted) }
                    } catch (error: Exception) {
                        Log.e(TAG, "Barcode frame analysis failed", error)
                    } finally {
                        bitmap?.recycle()
                    }
                }
            }
        )
        analyzerAttached = true
    }

    private fun Image.toCenterLumaBitmap(): Bitmap {
        val cropWidth = (width * 0.92f).toInt().coerceAtLeast(1)
        val cropHeight = (height * 0.64f).toInt().coerceAtLeast(1)
        val left = ((width - cropWidth) / 2).coerceAtLeast(0)
        val top = ((height - cropHeight) * 0.42f).toInt().coerceAtLeast(0)
        val plane = planes[0]
        val buffer = plane.buffer.duplicate()
        val pixels = IntArray(cropWidth * cropHeight)
        for (y in 0 until cropHeight) {
            val row = (top + y) * plane.rowStride
            for (x in 0 until cropWidth) {
                val index = row + (left + x) * plane.pixelStride
                val luma = buffer.get(index).toInt() and 0xff
                pixels[y * cropWidth + x] = 0xff000000.toInt() or
                    (luma shl 16) or (luma shl 8) or luma
            }
        }
        return Bitmap.createBitmap(pixels, cropWidth, cropHeight, Bitmap.Config.ARGB_8888)
    }

    override fun onConnectionStarted(url: String) {
        mainHandler.post { onStatus("正在连接工位视频服务") }
    }

    override fun onConnectionSuccess() {
        mainHandler.post {
            onStreamingChanged(true)
            onStatus("视频已连接")
        }
    }

    override fun onConnectionFailed(reason: String) {
        mainHandler.post {
            onStreamingChanged(false)
            onStatus("视频连接失败：$reason")
        }
    }

    override fun onDisconnect() {
        mainHandler.post {
            onStreamingChanged(false)
            onStatus("视频已断开")
        }
    }

    override fun onAuthError() {
        mainHandler.post { onStatus("视频认证失败") }
    }

    override fun onAuthSuccess() {
        mainHandler.post { onStatus("视频认证成功") }
    }

    override fun onNewBitrate(bitrate: Long) {
        mainHandler.post { onBitrate(bitrate) }
    }

    private companion object {
        const val TAG = "CameraPipeline"
        const val STREAM_WIDTH = 1920
        const val STREAM_HEIGHT = 1080
        const val STREAM_FPS = 15
        const val STREAM_BITRATE = 4_000_000
        const val AUDIO_SAMPLE_RATE = 32_000
        const val AUDIO_BITRATE = 64_000
        const val ANALYSIS_WIDTH = 640
        const val ANALYSIS_HEIGHT = 360
        const val ANALYSIS_INTERVAL_MILLIS = 55L
    }
}
