package com.unpackvision.mobile

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.view.SurfaceView
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.enableEdgeToEdge
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.Image
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay
import java.security.MessageDigest
import java.util.UUID

enum class WorkMode(val title: String, val subtitle: String, val contractName: String) {
    SmartCamera("智能摄像头", "把手机作为电脑摄像头，并自动识别快递单号", "SmartCamera"),
    HandheldScanner("手机扫码器", "全屏扫码，可选择是否同时启动电脑录像", "HandheldScanner"),
    ScanCollection("仅收集单号", "不录像，可靠追加到 Excel 队列", "ScanCollection"),
    IssueRemote("异常遥控", "破损、调包、备注、截图和停止", "IssueRemote")
}

private enum class ComputerConnectionState {
    NotPaired,
    Checking,
    Connected,
    Failed
}

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContent { MaterialTheme { UnpackVisionApp() } }
    }
}

@Composable
private fun UnpackVisionApp() {
    val context = LocalContext.current
    val appPreferences = remember { AppPreferences(context) }
    var consentAccepted by remember { mutableStateOf(appPreferences.hasCurrentConsent) }
    if (!consentAccepted) {
        ConsentWorkspace {
            appPreferences.acceptCurrentLegalDocuments()
            UpdateCheckWorker.schedule(context)
            consentAccepted = true
        }
        return
    }
    LaunchedEffect(Unit) {
        UpdateCheckWorker.schedule(context)
    }
    val lifecycleOwner = LocalLifecycleOwner.current
    val scope = rememberCoroutineScope()
    val api = remember { StationApiClient() }
    val credentialStore = remember { DeviceCredentialStore(context) }
    val endpointResolver = remember { StationEndpointResolver(context, api, credentialStore) }
    var selectedMode by remember { mutableStateOf<WorkMode?>(null) }
    var pairingOpen by remember { mutableStateOf(false) }
    var settingsOpen by remember { mutableStateOf(false) }
    var donationOpen by remember { mutableStateOf(false) }
    var legalDocument by remember { mutableStateOf<Pair<String, String>?>(null) }
    var credential by remember { mutableStateOf(credentialStore.load()) }
    var homeConnectionState by remember {
        mutableStateOf(if (credential == null) ComputerConnectionState.NotPaired else ComputerConnectionState.Checking)
    }
    var homeConnectionMessage by remember { mutableStateOf(credential?.stationId ?: "点击扫描电脑上的配对二维码") }
    var mainCameraOnly by remember { mutableStateOf(appPreferences.mainCameraOnly) }

    suspend fun refreshHomeConnection() {
        val saved = credential
        if (saved == null) {
            homeConnectionState = ComputerConnectionState.NotPaired
            homeConnectionMessage = "点击扫描电脑上的配对二维码"
            return
        }
        homeConnectionState = ComputerConnectionState.Checking
        homeConnectionMessage = "正在检查电脑连接"
        runCatching { endpointResolver.resolve(saved) }.onSuccess { resolved ->
            credential = resolved.credential
            val state = resolved.state
            if (!state.desktopReady) {
                homeConnectionState = ComputerConnectionState.Failed
                homeConnectionMessage = "工位服务在线，但电脑录像程序未打开"
                return@onSuccess
            }
            homeConnectionState = ComputerConnectionState.Connected
            homeConnectionMessage = if (state.trackingNo.isNullOrBlank()) {
                "${saved.stationId} · ${resolved.transport.displayName}"
            } else {
                "正在录制 ${state.trackingNo} · ${resolved.transport.displayName}"
            }
        }.onFailure {
            homeConnectionState = ComputerConnectionState.Failed
            homeConnectionMessage = it.message ?: "连接失败"
        }
    }

    LaunchedEffect(credential) {
        refreshHomeConnection()
    }
    DisposableEffect(lifecycleOwner, credential) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                scope.launch { refreshHomeConnection() }
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    when {
        legalDocument != null -> LegalWorkspace(
            legalDocument!!.first,
            legalDocument!!.second,
            onBack = { legalDocument = null }
        )
        donationOpen -> DonationWorkspace(onBack = { donationOpen = false })
        pairingOpen -> PairingWorkspace(
            onBack = { pairingOpen = false },
            onPaired = {
                credential = it
                homeConnectionState = ComputerConnectionState.Connected
                homeConnectionMessage = it.stationId
                pairingOpen = false
            }
        )
        settingsOpen -> SettingsWorkspace(
            credential = credential,
            mainCameraOnly = mainCameraOnly,
            onMainCameraOnlyChanged = {
                mainCameraOnly = it
                appPreferences.mainCameraOnly = it
            },
            onBack = { settingsOpen = false },
            onPair = { pairingOpen = true; settingsOpen = false },
            onTerms = { legalDocument = "用户协议" to MobileLegalDocuments.TERMS },
            onPrivacy = { legalDocument = "隐私政策" to MobileLegalDocuments.PRIVACY },
            onDonation = { donationOpen = true }
        )
        selectedMode != null -> ModeWorkspace(
            selectedMode!!,
            credential,
            mainCameraOnly,
            onBack = { selectedMode = null }
        )
        else -> HomeWorkspace(
            credential = credential,
            connectionState = homeConnectionState,
            connectionMessage = homeConnectionMessage,
            onPair = { pairingOpen = true },
            onSettings = { settingsOpen = true },
            onMode = { selectedMode = it }
        )
    }
}

@Composable
private fun HomeWorkspace(
    credential: StoredDeviceCredential?,
    connectionState: ComputerConnectionState,
    connectionMessage: String,
    onPair: () -> Unit,
    onSettings: () -> Unit,
    onMode: (WorkMode) -> Unit
) {
    val connected = connectionState == ComputerConnectionState.Connected
    val statusColor = when (connectionState) {
        ComputerConnectionState.Connected -> Color(0xFF34C759)
        ComputerConnectionState.Checking -> Color(0xFF3478F6)
        ComputerConnectionState.Failed -> Color(0xFFFF3B30)
        ComputerConnectionState.NotPaired -> Color(0xFFFF9500)
    }
    val statusTitle = when (connectionState) {
        ComputerConnectionState.Connected -> "已连接电脑"
        ComputerConnectionState.Checking -> "正在检查连接"
        ComputerConnectionState.Failed -> "未连接电脑"
        ComputerConnectionState.NotPaired -> "尚未连接电脑"
    }
    Column(
        Modifier.fillMaxSize().background(Color(0xFFF4F6FA)).statusBarsPadding().navigationBarsPadding().padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)
    ) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("电商拆包智能录像", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold)
                Text("安卓协同端 · ${BuildConfig.VERSION_NAME}", color = Color(0xFF6E6E73))
            }
            OutlinedButton(onClick = onSettings) { Text("设置") }
        }

        Card(
            onClick = onPair,
            shape = RoundedCornerShape(24.dp),
            colors = CardDefaults.cardColors(
                containerColor = if (connected) Color(0xFFE8F7EE) else Color(0xFFFFF3E5)
            ),
            modifier = Modifier.fillMaxWidth()
        ) {
            Row(Modifier.padding(18.dp), verticalAlignment = Alignment.CenterVertically) {
                Surface(
                    color = statusColor,
                    shape = RoundedCornerShape(20.dp),
                    modifier = Modifier.size(12.dp)
                ) {}
                Column(Modifier.padding(start = 12.dp).weight(1f)) {
                    Text(
                        statusTitle,
                        style = MaterialTheme.typography.titleMedium,
                        color = if (connected) Color(0xFF248A3D) else Color(0xFF9A5B00)
                    )
                    Text(connectionMessage, color = Color(0xFF59616F))
                }
                Text(if (credential == null) "连接" else "重新配对", color = Color(0xFF3478F6))
            }
        }

        Text("选择工作方式", style = MaterialTheme.typography.titleMedium, color = Color(0xFF3A3A3C))
        ModeCard(WorkMode.HandheldScanner) { onMode(WorkMode.HandheldScanner) }
        ModeCard(WorkMode.SmartCamera) { onMode(WorkMode.SmartCamera) }
        Spacer(Modifier.weight(1f))
        Text("开源 · 局域网运行 · 开发者 五成", color = Color(0xFF8E8E93), style = MaterialTheme.typography.bodySmall)
    }
}

@Composable
private fun ConsentWorkspace(onAccepted: () -> Unit) {
    var requiredAccepted by remember { mutableStateOf(false) }
    var openedDocument by remember { mutableStateOf<Pair<String, String>?>(null) }

    openedDocument?.let { document ->
        LegalWorkspace(document.first, document.second) { openedDocument = null }
        return
    }

    Column(
        Modifier
            .fillMaxSize()
            .background(Color(0xFFF4F6FA))
            .statusBarsPadding()
            .navigationBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)
    ) {
        Spacer(Modifier.height(12.dp))
        Text("欢迎使用", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold)
        Text("电商拆包智能录像", style = MaterialTheme.typography.titleLarge, color = Color(0xFF3478F6))
        Card(colors = CardDefaults.cardColors(containerColor = Color.White), modifier = Modifier.fillMaxWidth()) {
            Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("请先了解本地数据处理方式", fontWeight = FontWeight.SemiBold)
                Text(
                    "单号、录像、Excel、备注和配对信息默认只保存在您的设备；自动更新只访问 GitHub，手机与电脑只在局域网传输。",
                    color = Color(0xFF6E6E73)
                )
                Text(
                    "面单和录像可能包含个人信息，请自行控制访问权限、保存期限和合法使用范围。",
                    color = Color(0xFFFF9500)
                )
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            OutlinedButton(
                onClick = { openedDocument = "用户协议" to MobileLegalDocuments.TERMS },
                modifier = Modifier.weight(1f)
            ) { Text("用户协议") }
            OutlinedButton(
                onClick = { openedDocument = "隐私政策" to MobileLegalDocuments.PRIVACY },
                modifier = Modifier.weight(1f)
            ) { Text("隐私政策") }
        }
        Card(colors = CardDefaults.cardColors(containerColor = Color.White), modifier = Modifier.fillMaxWidth()) {
            Row(
                Modifier.padding(16.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Checkbox(checked = requiredAccepted, onCheckedChange = { requiredAccepted = it })
                Text("我已阅读并同意《用户协议》和《隐私政策》", modifier = Modifier.weight(1f))
            }
        }
        Text(
            "2.2.0 不启用匿名统计，也不会生成稳定安装标识。今后如增加可选统计，将另行征得同意。",
            color = Color(0xFF6E6E73),
            style = MaterialTheme.typography.bodySmall
        )
        Spacer(Modifier.weight(1f))
        Button(
            onClick = onAccepted,
            enabled = requiredAccepted,
            modifier = Modifier.fillMaxWidth()
        ) { Text("同意并进入应用") }
    }
}

@Composable
private fun LegalWorkspace(title: String, body: String, onBack: () -> Unit) {
    Column(
        Modifier
            .fillMaxSize()
            .background(Color(0xFFF4F6FA))
            .statusBarsPadding()
            .navigationBarsPadding()
            .padding(20.dp)
    ) {
        FeatureHeader(onBack, title, "版本 2026-07-29")
        Spacer(Modifier.height(16.dp))
        Card(
            colors = CardDefaults.cardColors(containerColor = Color.White),
            modifier = Modifier.fillMaxSize()
        ) {
            Text(
                body.trimIndent(),
                modifier = Modifier
                    .fillMaxSize()
                    .verticalScroll(rememberScrollState())
                    .padding(18.dp),
                color = Color(0xFF3A3A3C)
            )
        }
    }
}

@Composable
private fun DonationWorkspace(onBack: () -> Unit) {
    val profile = remember { MobileDonationProfile() }
    Column(
        Modifier
            .fillMaxSize()
            .background(Color(0xFFF4F6FA))
            .statusBarsPadding()
            .navigationBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)
    ) {
        FeatureHeader(onBack, "支持作者", "自愿赞助，不影响任何软件功能")
        InfoCard("开发者", profile.developerName)
        DonationQrCard("支付宝", profile.alipayDrawableName, profile.alipaySha256)
        DonationQrCard("微信", profile.weChatDrawableName, profile.weChatSha256)
        Text(
            "付款完全由支付宝或微信处理。本软件不接入支付 SDK，不读取付款金额、账号、订单或付款结果。",
            color = Color(0xFF6E6E73)
        )
    }
}

@Composable
private fun DonationQrCard(channel: String, drawableName: String, expectedSha256: String) {
    val context = LocalContext.current
    val drawableId = remember(drawableName, expectedSha256) {
        validateDonationDrawable(context, drawableName, expectedSha256)
    }
    Card(colors = CardDefaults.cardColors(containerColor = Color.White), modifier = Modifier.fillMaxWidth()) {
        Column(
            Modifier
                .fillMaxWidth()
                .padding(18.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            Text(channel, fontWeight = FontWeight.SemiBold)
            Box(
                Modifier
                    .size(230.dp)
                    .background(Color(0xFFF2F2F7), RoundedCornerShape(18.dp))
                    .border(1.dp, Color(0xFFD1D1D6), RoundedCornerShape(18.dp)),
                contentAlignment = Alignment.Center
            ) {
                if (drawableId != null) {
                    Image(
                        painter = painterResource(drawableId),
                        contentDescription = "$channel 赞助二维码",
                        contentScale = ContentScale.Fit,
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(12.dp)
                    )
                } else {
                    Text(
                        if (drawableName.isBlank()) "作者暂未配置" else "二维码校验失败",
                        color = Color(0xFF8E8E93)
                    )
                }
            }
        }
    }
}

private fun validateDonationDrawable(
    context: android.content.Context,
    drawableName: String,
    expectedSha256: String
): Int? {
    if (drawableName.isBlank() || expectedSha256.isBlank()) return null
    val resourceId = context.resources.getIdentifier(
        drawableName,
        "drawable",
        context.packageName
    )
    if (resourceId == 0) return null
    return runCatching {
        val actual = context.resources.openRawResource(resourceId).use { input ->
            val digest = MessageDigest.getInstance("SHA-256")
            val buffer = ByteArray(8192)
            while (true) {
                val read = input.read(buffer)
                if (read <= 0) break
                digest.update(buffer, 0, read)
            }
            digest.digest().joinToString("") { "%02x".format(it) }
        }
        resourceId.takeIf { actual.equals(expectedSha256, ignoreCase = true) }
    }.getOrNull()
}

@Composable
private fun SettingsWorkspace(
    credential: StoredDeviceCredential?,
    mainCameraOnly: Boolean,
    onMainCameraOnlyChanged: (Boolean) -> Unit,
    onBack: () -> Unit,
    onPair: () -> Unit,
    onTerms: () -> Unit,
    onPrivacy: () -> Unit,
    onDonation: () -> Unit
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val updateManager = remember { AppUpdateManager(context) }
    var updateStatus by remember { mutableStateOf("每天自动检查一次，也可以立即检查") }
    var availableUpdate by remember { mutableStateOf<MobileUpdateManifest?>(null) }
    var checkingUpdate by remember { mutableStateOf(false) }
    var downloadedApk by remember { mutableStateOf<java.io.File?>(null) }
    val notificationPermissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { }
    val notificationPermissionMissing =
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED

    fun checkUpdate(force: Boolean) {
        if (checkingUpdate) return
        checkingUpdate = true
        updateStatus = "正在检查 GitHub 更新…"
        scope.launch {
            when (val result = updateManager.check(force)) {
                MobileUpdateResult.Skipped -> updateStatus = "今天已经检查过更新"
                MobileUpdateResult.Current -> updateStatus = "当前已经是最新版本"
                is MobileUpdateResult.Available -> {
                    availableUpdate = result.manifest
                    updateStatus = if (result.manifest.critical) {
                        "发现安全更新 ${result.manifest.versionName}，建议尽快安装"
                    } else {
                        "发现新版本 ${result.manifest.versionName}"
                    }
                }
                is MobileUpdateResult.Failed -> updateStatus = "检查失败：${result.message}"
            }
            checkingUpdate = false
        }
    }

    LaunchedEffect(Unit) { checkUpdate(false) }

    Column(
        Modifier
            .fillMaxSize()
            .background(Color(0xFFF4F6FA))
            .statusBarsPadding()
            .navigationBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)
    ) {
        FeatureHeader(onBack, "设置", "连接信息、软件版本与开发者信息")
        InfoCard("软件名称", "电商拆包智能录像")
        InfoCard("版本号", BuildConfig.VERSION_NAME)
        InfoCard("开发者", "五成")
        InfoCard("电脑工位", credential?.stationId ?: "尚未配对")
        InfoCard("连接兜底", "局域网自动发现 / 手机热点 / USB或蓝牙网络共享 / USB调试直连")
        Card(colors = CardDefaults.cardColors(containerColor = Color.White), modifier = Modifier.fillMaxWidth()) {
            Row(Modifier.padding(18.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("只使用主摄像头", fontWeight = FontWeight.SemiBold)
                    Text(
                        "默认锁定手机主后摄，扫码更稳定；关闭后可手动切换前后镜头",
                        color = Color(0xFF6E6E73),
                        style = MaterialTheme.typography.bodySmall
                    )
                }
                Switch(checked = mainCameraOnly, onCheckedChange = onMainCameraOnlyChanged)
            }
        }
        Button(onClick = onPair, modifier = Modifier.fillMaxWidth()) {
            Text(if (credential == null) "连接电脑" else "重新配对电脑")
        }
        Card(colors = CardDefaults.cardColors(containerColor = Color.White), modifier = Modifier.fillMaxWidth()) {
            Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("软件更新", fontWeight = FontWeight.SemiBold)
                Text(updateStatus, color = Color(0xFF6E6E73), style = MaterialTheme.typography.bodySmall)
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    OutlinedButton(
                        onClick = { checkUpdate(true) },
                        enabled = !checkingUpdate,
                        modifier = Modifier.weight(1f)
                    ) { Text("立即检查") }
                    OutlinedButton(
                        onClick = { updateManager.open(AppUpdateManager.RELEASE_URL) },
                        modifier = Modifier.weight(1f)
                    ) { Text("版本页面") }
                }
                if (notificationPermissionMissing) {
                    OutlinedButton(
                        onClick = {
                            notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                        },
                        modifier = Modifier.fillMaxWidth()
                    ) { Text("允许版本更新通知") }
                }
                availableUpdate?.let { manifest ->
                    Button(
                        onClick = {
                            scope.launch {
                                if (downloadedApk == null) {
                                    updateStatus = "正在交给系统下载…"
                                    runCatching {
                                        updateManager.downloadAndVerify(manifest) {
                                            updateStatus = "正在下载 ${it}%"
                                        }
                                    }.onSuccess {
                                        downloadedApk = it
                                        updateStatus = "下载及 SHA256 校验完成"
                                    }.onFailure {
                                        updateStatus = "下载失败：${it.message}"
                                    }
                                }
                                downloadedApk?.let {
                                    if (updateManager.requestInstallPermission()) {
                                        updateManager.install(it)
                                    } else {
                                        updateStatus = "请允许安装未知应用，返回后再次点击安装"
                                    }
                                }
                            }
                        },
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text(if (downloadedApk == null) "下载并校验更新" else "打开系统安装界面")
                    }
                }
            }
        }
        Card(colors = CardDefaults.cardColors(containerColor = Color.White), modifier = Modifier.fillMaxWidth()) {
            Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("开源与下载", fontWeight = FontWeight.SemiBold)
                Text(
                    AppUpdateManager.REPOSITORY_URL,
                    color = Color(0xFF3478F6),
                    style = MaterialTheme.typography.bodySmall
                )
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    OutlinedButton(
                        onClick = { updateManager.open(AppUpdateManager.REPOSITORY_URL) },
                        modifier = Modifier.weight(1f)
                    ) { Text("开源仓库") }
                    OutlinedButton(
                        onClick = { updateManager.open(AppUpdateManager.APK_URL) },
                        modifier = Modifier.weight(1f)
                    ) { Text("下载 APK") }
                }
            }
        }
        Card(colors = CardDefaults.cardColors(containerColor = Color.White), modifier = Modifier.fillMaxWidth()) {
            Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("隐私、安全与支持", fontWeight = FontWeight.SemiBold)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = onTerms, modifier = Modifier.weight(1f)) { Text("用户协议") }
                    OutlinedButton(onClick = onPrivacy, modifier = Modifier.weight(1f)) { Text("隐私政策") }
                }
                Button(onClick = onDonation, modifier = Modifier.fillMaxWidth()) { Text("支持作者") }
                Text(
                    "安全问题请通过 GitHub Private Vulnerability Reporting 私密报告。",
                    color = Color(0xFF6E6E73),
                    style = MaterialTheme.typography.bodySmall
                )
            }
        }
        Text(
            "数据默认仅在局域网和本机保存，不需要云账号。联网更新只访问 GitHub，不上传单号、录像、Excel 或设备信息。",
            color = Color(0xFF6E6E73)
        )
    }
}

@Composable
private fun InfoCard(label: String, value: String) {
    Card(colors = CardDefaults.cardColors(containerColor = Color.White), modifier = Modifier.fillMaxWidth()) {
        Row(Modifier.padding(18.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(label, color = Color(0xFF6E6E73), modifier = Modifier.weight(1f))
            Text(value, fontWeight = FontWeight.SemiBold)
        }
    }
}

@Composable
private fun FeatureHeader(onBack: () -> Unit, title: String, description: String, dark: Boolean = false) {
    val foreground = if (dark) Color.White else Color(0xFF1C1C1E)
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.Top) {
        OutlinedButton(onClick = onBack, modifier = Modifier.width(64.dp)) { Text("←", color = foreground) }
        Column(Modifier.padding(start = 12.dp).weight(1f)) {
            Text(title, style = MaterialTheme.typography.titleLarge, color = foreground, fontWeight = FontWeight.SemiBold)
            Text(description, color = if (dark) Color(0xFFD7D7DC) else Color(0xFF6E6E73), modifier = Modifier.padding(top = 3.dp))
        }
    }
}

@Composable
private fun PairingWorkspace(onBack: () -> Unit, onPaired: (StoredDeviceCredential) -> Unit) {
    val context = LocalContext.current
    var hasCameraPermission by remember {
        mutableStateOf(ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED)
    }
    val permissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        hasCameraPermission = it
    }
    val scope = rememberCoroutineScope()
    val api = remember { StationApiClient() }
    val credentialStore = remember { DeviceCredentialStore(context) }
    val feedback = remember { DeviceFeedback(context) }
    var status by remember { mutableStateOf("请扫描电脑显示的五分钟配对二维码") }
    var processing by remember { mutableStateOf(false) }
    var pairSucceeded by remember { mutableStateOf(false) }
    val controller = remember {
        CameraPipelineController(
            context,
            onBarcode = { payload ->
                if (!processing) {
                    processing = true
                    scope.launch {
                        runCatching {
                            val credential = api.pair(payload, credentialStore.getOrCreatePublicKey())
                            credentialStore.save(credential)
                            credential
                        }.onSuccess {
                            pairSucceeded = true
                            status = "配对成功：${it.stationId}"
                            feedback.success("配对成功")
                            delay(1_200)
                            onPaired(it)
                        }.onFailure {
                            status = "配对失败：${it.message}"
                            processing = false
                        }
                    }
                }
            },
            onStatus = { if (!processing) status = it },
            onBitrate = {}
        )
    }
    DisposableEffect(controller) { onDispose { controller.release() } }
    DisposableEffect(feedback) { onDispose { feedback.close() } }

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        if (hasCameraPermission) {
            AndroidView(
                factory = { SurfaceView(it).also(controller::bind) },
                modifier = Modifier.fillMaxSize()
            )
            Box(
                Modifier.align(Alignment.Center).fillMaxWidth(0.74f).height(280.dp)
                    .border(2.dp, Color.White, RoundedCornerShape(24.dp))
            )
        }
        if (pairSucceeded) {
            Column(
                Modifier.align(Alignment.Center).background(Color(0xE634C759), RoundedCornerShape(28.dp)).padding(30.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                Text("✓", color = Color.White, style = MaterialTheme.typography.displayMedium, fontWeight = FontWeight.SemiBold)
                Text("配对成功", color = Color.White, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
            }
        }
        Column(
            Modifier.align(Alignment.TopCenter).fillMaxWidth().background(Color(0xB3000000))
                .statusBarsPadding().padding(18.dp)
        ) {
            FeatureHeader(onBack, "配对电脑", "扫描电脑上显示的五分钟配对二维码", dark = true)
        }
        Card(
            colors = CardDefaults.cardColors(containerColor = Color(0xEBFFFFFF)),
            modifier = Modifier.align(Alignment.BottomCenter).fillMaxWidth().navigationBarsPadding().padding(18.dp)
        ) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text(status, color = if (status.startsWith("配对失败")) Color(0xFFFF3B30) else Color(0xFF248A3D))
                if (!hasCameraPermission) {
                    Button(onClick = { permissionLauncher.launch(Manifest.permission.CAMERA) }, modifier = Modifier.fillMaxWidth()) {
                        Text("允许使用摄像头")
                    }
                }
                Text("二维码只在五分钟内有效，并且使用一次后立即作废。", color = Color(0xFF6E6E73))
            }
        }
    }
}

@Composable
private fun ModeCard(mode: WorkMode, onClick: () -> Unit) {
    Card(
        onClick = onClick,
        shape = RoundedCornerShape(22.dp),
        colors = CardDefaults.cardColors(containerColor = Color.White),
        modifier = Modifier.fillMaxWidth()
    ) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(5.dp)) {
            Text(mode.title, style = MaterialTheme.typography.titleLarge)
            Text(mode.subtitle, color = Color(0xFF6E6E73))
        }
    }
}

@Composable
private fun ModeWorkspace(
    mode: WorkMode,
    credential: StoredDeviceCredential?,
    mainCameraOnly: Boolean,
    onBack: () -> Unit
) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    var hasCameraPermission by remember {
        mutableStateOf(ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED)
    }
    val permissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        hasCameraPermission = it
    }
    var activeCredential by remember(credential) { mutableStateOf(credential) }
    var stationAddress by remember(credential) { mutableStateOf(credential?.stationAddress.orEmpty()) }
    var stationId by remember { mutableStateOf(credential?.stationId.orEmpty()) }
    var streamAddress by remember { mutableStateOf("") }
    var streamAuthUser by remember { mutableStateOf(credential?.deviceId.orEmpty()) }
    var status by remember { mutableStateOf(if (credential == null) "请返回主页连接电脑" else "正在连接电脑") }
    var lastBarcode by remember { mutableStateOf("") }
    var bitrate by remember { mutableLongStateOf(0L) }
    var currentTrackingNo by remember { mutableStateOf<String?>(null) }
    var remoteRecordingState by remember { mutableStateOf("Idle") }
    var startComputerRecording by remember { mutableStateOf(true) }
    var streamingActive by remember { mutableStateOf(false) }
    var torchEnabled by remember { mutableStateOf(false) }
    var connectionState by remember {
        mutableStateOf(if (credential == null) ComputerConnectionState.NotPaired else ComputerConnectionState.Checking)
    }
    val scope = rememberCoroutineScope()
    val api = remember { StationApiClient() }
    val credentialStore = remember { DeviceCredentialStore(context) }
    val endpointResolver = remember { StationEndpointResolver(context, api, credentialStore) }
    val feedback = remember { DeviceFeedback(context) }
    val offlineQueue = remember { OfflineScanQueueStore(context) }
    var queuedScanCount by remember { mutableStateOf(offlineQueue.snapshot().size) }
    val deviceId = credential?.deviceId ?: "android-${UUID.randomUUID()}"
    val accessToken = credential?.accessToken
    DisposableEffect(feedback) { onDispose { feedback.close() } }
    val computerConnected = connectionState == ComputerConnectionState.Connected
    val currentConnectionState = rememberUpdatedState(connectionState)

    suspend fun refreshRemoteState(preserveStatus: Boolean = false) {
        if (accessToken.isNullOrBlank() || stationId.isBlank()) {
            connectionState = ComputerConnectionState.NotPaired
            status = "请先返回主页连接电脑"
            return
        }
        val wasConnected = connectionState == ComputerConnectionState.Connected
        if (!preserveStatus) {
            connectionState = ComputerConnectionState.Checking
            status = "正在检查电脑连接"
        }
        runCatching {
            val saved = activeCredential ?: error("请先连接电脑")
            endpointResolver.resolve(saved)
        }
            .onSuccess { resolved ->
                activeCredential = resolved.credential
                stationAddress = resolved.credential.stationAddress
                val it = resolved.state
                if (!it.desktopReady) {
                    connectionState = ComputerConnectionState.Failed
                    currentTrackingNo = it.trackingNo
                    remoteRecordingState = it.recordingState
                    status = "工位服务在线，但电脑录像程序未打开"
                    return@onSuccess
                }
                connectionState = ComputerConnectionState.Connected
                currentTrackingNo = it.trackingNo
                remoteRecordingState = it.recordingState
                if (!preserveStatus || !wasConnected) {
                    status = if (it.trackingNo.isNullOrBlank()) {
                        "电脑已连接 · ${resolved.transport.displayName} · 当前空闲"
                    } else {
                        "正在录制 ${it.trackingNo} · ${resolved.transport.displayName}"
                    }
                }
            }
            .onFailure {
                connectionState = ComputerConnectionState.Failed
                currentTrackingNo = null
                remoteRecordingState = "Unknown"
                status = "连接失败：${it.message}"
            }
    }

    suspend fun flushCollectionQueue(): Int {
        if (accessToken.isNullOrBlank() || stationId.isBlank()) return 0
        var sent = 0
        for (command in offlineQueue.snapshot()) {
            if (!command.stationId.equals(stationId, ignoreCase = true)) continue
            api.submitScan(
                stationAddress, command.stationId, deviceId, accessToken, command.value,
                WorkMode.ScanCollection,
                activeCredential?.certificateFingerprint ?: credential.certificateFingerprint,
                eventId = command.eventId,
                detectedAt = command.detectedAt
            )
            offlineQueue.remove(command.eventId)
            sent++
        }
        queuedScanCount = offlineQueue.snapshot().size
        return sent
    }

    LaunchedEffect(mode, credential) {
        if (credential == null) {
            connectionState = ComputerConnectionState.NotPaired
            status = "请返回主页连接电脑"
            return@LaunchedEffect
        }
        connectionState = ComputerConnectionState.Checking
        status = "正在连接电脑"
        runCatching {
            val resolved = endpointResolver.resolve(credential)
            activeCredential = resolved.credential
            stationAddress = resolved.credential.stationAddress
            if (!resolved.state.desktopReady) {
                error("工位服务在线，但电脑录像程序未打开")
            }
            stationId = resolved.state.stationId
            if (mode == WorkMode.SmartCamera) {
                api.createPublishSession(
                    stationAddress,
                    deviceId,
                    accessToken!!,
                    activeCredential?.certificateFingerprint ?: credential.certificateFingerprint
                )
            } else null
        }.onSuccess { endpoint ->
            connectionState = ComputerConnectionState.Connected
            endpoint?.let {
                streamAddress = it.rtspUrl
                streamAuthUser = it.authUser
                status = "视频服务已就绪，点击开始推流"
            } ?: run {
                status = "电脑已连接 · 等待扫码"
                runCatching { flushCollectionQueue() }
                refreshRemoteState()
            }
        }.onFailure {
            connectionState = ComputerConnectionState.Failed
            status = "连接失败：${it.message}"
        }
    }
    DisposableEffect(lifecycleOwner, credential) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                scope.launch { refreshRemoteState() }
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }
    LaunchedEffect("station-poll", mode, credential) {
        if (credential == null || mode != WorkMode.HandheldScanner) return@LaunchedEffect
        while (true) {
            delay(5_000)
            refreshRemoteState(preserveStatus = true)
        }
    }

    fun sendRemoteCommand(command: String) {
        scope.launch {
            if (currentConnectionState.value != ComputerConnectionState.Connected) {
                status = "电脑未连接，请检查同一局域网后刷新"
                feedback.error("电脑未连接")
                return@launch
            }
            Log.i(SCAN_LOG_TAG, "Submitting remote command to paired station")
            val acknowledgement = runCatching {
                api.submitScan(
                    stationAddress,
                    stationId,
                    deviceId,
                    accessToken,
                    command,
                    WorkMode.IssueRemote,
                    activeCredential?.certificateFingerprint ?: credential!!.certificateFingerprint
                )
            }.onSuccess {
                Log.i(SCAN_LOG_TAG, "Computer acknowledged remote command")
            }.onFailure {
                Log.e(SCAN_LOG_TAG, "Remote command failed", it)
            }.getOrElse { "发送失败：${it.message}" }
            refreshRemoteState()
            status = acknowledgement
            if (acknowledgement.startsWith("发送失败")) feedback.error("发送失败")
            else feedback.success(acknowledgement)
        }
    }

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        if (hasCameraPermission) {
            CameraWorkspace(
                mode = mode,
                mainCameraOnly = mainCameraOnly,
                streamAddress = streamAddress,
                streamAuthUser = streamAuthUser,
                streamAuthToken = accessToken,
                certificateFingerprint =
                    activeCredential?.certificateFingerprint ?: credential?.certificateFingerprint.orEmpty(),
                torchEnabled = torchEnabled,
                modifier = Modifier.fillMaxSize(),
                onStreamingChanged = { streamingActive = it },
                onBarcode = { value ->
                    lastBarcode = value
                    if (credential == null ||
                        stationId.isBlank() ||
                        currentConnectionState.value != ComputerConnectionState.Connected
                    ) {
                        status = if (credential == null) "请先返回主页连接电脑" else "电脑未连接，请检查同一局域网后刷新"
                        feedback.error("电脑未连接")
                    } else if (mode == WorkMode.SmartCamera && !streamingActive) {
                        status = "请先开始推流，再扫描快递单号"
                        feedback.error("请先开始推流")
                    } else if (mode == WorkMode.HandheldScanner && !startComputerRecording) {
                        val pending = QueuedScanCommand.create(stationId, value)
                        offlineQueue.enqueue(pending)
                        queuedScanCount = offlineQueue.snapshot().size
                        status = "已收集：$value"
                        scope.launch {
                            status = runCatching {
                                flushCollectionQueue()
                                "电脑已确认收集：$value"
                            }.getOrElse { "已离线保存，联网后自动补发" }
                            if (status.startsWith("电脑已确认")) feedback.success("单号已收集")
                            else feedback.error("已离线保存")
                        }
                    } else {
                        status = "电脑处理中：$value"
                        Log.i(SCAN_LOG_TAG, "Submitting barcode ending ${value.takeLast(4)}")
                        scope.launch {
                            val acknowledgement = runCatching {
                                api.submitScan(
                                    stationAddress,
                                    stationId,
                                    deviceId,
                                    accessToken,
                                    value,
                                    mode,
                                    activeCredential?.certificateFingerprint
                                        ?: credential.certificateFingerprint
                                )
                            }.onSuccess {
                                Log.i(SCAN_LOG_TAG, "Computer acknowledged barcode")
                            }.onFailure {
                                Log.e(SCAN_LOG_TAG, "Barcode submission failed", it)
                            }.getOrElse { "发送失败：${it.message}" }
                            refreshRemoteState(preserveStatus = true)
                            status = acknowledgement
                            if (acknowledgement.startsWith("发送失败")) feedback.error("发送失败")
                            else feedback.success(acknowledgement)
                        }
                    }
                },
                onStatus = { status = it },
                onBitrate = { bitrate = it }
            )
        } else {
            Button(
                onClick = { permissionLauncher.launch(Manifest.permission.CAMERA) },
                modifier = Modifier.align(Alignment.Center)
            ) { Text("允许使用摄像头") }
        }

        Column(
            Modifier.align(Alignment.TopCenter).fillMaxWidth().background(Color(0xA6000000))
                .statusBarsPadding().padding(horizontal = 16.dp, vertical = 10.dp)
        ) {
            FeatureHeader(onBack, mode.title, mode.subtitle, dark = true)
        }

        if (mode == WorkMode.HandheldScanner) {
            RemoteControls(
                status = status,
                trackingNo = currentTrackingNo,
                recordingState = remoteRecordingState,
                lastBarcode = lastBarcode,
                queuedScanCount = queuedScanCount,
                connectionState = connectionState,
                startComputerRecording = startComputerRecording,
                torchEnabled = torchEnabled,
                commandEnabled = computerConnected,
                refreshEnabled = credential != null,
                onStartComputerRecordingChanged = { startComputerRecording = it },
                onTorchChanged = { torchEnabled = it },
                onRefresh = { scope.launch { refreshRemoteState() } },
                send = ::sendRemoteCommand,
                modifier = Modifier.align(Alignment.BottomCenter).fillMaxWidth()
                    .navigationBarsPadding().padding(12.dp)
            )
        } else {
            Card(
                colors = CardDefaults.cardColors(containerColor = Color(0xE6000000)),
                modifier = Modifier.align(Alignment.BottomCenter).fillMaxWidth().navigationBarsPadding().padding(16.dp)
            ) {
                Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    Text(status, color = if (status.contains("失败")) Color(0xFFFF6961) else Color.White)
                    if (lastBarcode.isNotEmpty()) Text("最近单号：$lastBarcode", color = Color(0xFFD7D7DC))
                    if (bitrate > 0) Text("上传码率：${"%.2f".format(bitrate / 1_000_000f)} Mbps", color = Color(0xFFD7D7DC))
                }
            }
        }
    }
}

@Composable
private fun CameraWorkspace(
    mode: WorkMode,
    mainCameraOnly: Boolean,
    streamAddress: String,
    streamAuthUser: String,
    streamAuthToken: String?,
    certificateFingerprint: String,
    torchEnabled: Boolean,
    modifier: Modifier = Modifier,
    onStreamingChanged: (Boolean) -> Unit = {},
    onBarcode: (String) -> Unit,
    onStatus: (String) -> Unit,
    onBitrate: (Long) -> Unit
) {
    val context = LocalContext.current
    var streamingRequested by remember { mutableStateOf(false) }
    val currentOnBarcode = rememberUpdatedState(onBarcode)
    val currentOnStatus = rememberUpdatedState(onStatus)
    val currentOnBitrate = rememberUpdatedState(onBitrate)
    val currentOnStreamingChanged = rememberUpdatedState(onStreamingChanged)
    val streamingController = remember(mainCameraOnly) {
        CameraPipelineController(
            context,
            mainCameraOnly = mainCameraOnly,
            certificateFingerprint = certificateFingerprint,
            onBarcode = { currentOnBarcode.value(it) },
            onStatus = { currentOnStatus.value(it) },
            onBitrate = { currentOnBitrate.value(it) },
            onStreamingChanged = { active ->
                streamingRequested = active
                currentOnStreamingChanged.value(active)
            }
        )
    }
    DisposableEffect(streamingController) {
        onDispose {
            streamingController.setTorch(false)
            streamingController.release()
        }
    }
    LaunchedEffect(torchEnabled, streamingController) {
        streamingController.setTorch(torchEnabled)
    }
    Box(
        modifier = modifier.background(Color.Black),
        contentAlignment = Alignment.Center
    ) {
        AndroidView(
            factory = { SurfaceView(it).also(streamingController::bind) },
            modifier = Modifier.fillMaxSize()
        )
        Box(
            Modifier.align(Alignment.Center).padding(bottom = 72.dp).fillMaxWidth(0.84f).height(252.dp)
                .border(2.dp, Color(0xE6FFFFFF), RoundedCornerShape(24.dp))
        )
        if (mode == WorkMode.SmartCamera) {
            Row(
                Modifier.align(Alignment.BottomCenter).fillMaxWidth().navigationBarsPadding().padding(16.dp, 0.dp, 16.dp, 104.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp)
            ) {
            Button(onClick = {
                if (streamingRequested) {
                    streamingController.stopStreaming()
                    streamingRequested = false
                } else {
                    streamingRequested = streamingController.startStreaming(streamAddress, streamAuthUser, streamAuthToken)
                }
            }, modifier = Modifier.weight(1f)) { Text(if (streamingRequested) "停止推流" else "开始推流") }
            if (!mainCameraOnly) {
                OutlinedButton(onClick = streamingController::switchCamera) { Text("切换镜头") }
            }
            }
        }
    }
}

@Composable
private fun RemoteControls(
    status: String,
    trackingNo: String?,
    recordingState: String,
    lastBarcode: String,
    queuedScanCount: Int,
    connectionState: ComputerConnectionState,
    startComputerRecording: Boolean,
    torchEnabled: Boolean,
    commandEnabled: Boolean,
    refreshEnabled: Boolean,
    onStartComputerRecordingChanged: (Boolean) -> Unit,
    onTorchChanged: (Boolean) -> Unit,
    onRefresh: () -> Unit,
    send: (String) -> Unit,
    modifier: Modifier = Modifier
) {
    var note by remember { mutableStateOf("") }
    val connectionLabel = when (connectionState) {
        ComputerConnectionState.Connected -> "已连接"
        ComputerConnectionState.Checking -> "连接中"
        ComputerConnectionState.Failed -> "连接失败"
        ComputerConnectionState.NotPaired -> "未配对"
    }
    val connectionColor = when (connectionState) {
        ComputerConnectionState.Connected -> Color(0xFF34C759)
        ComputerConnectionState.Checking -> Color(0xFF3478F6)
        ComputerConnectionState.Failed -> Color(0xFFFF3B30)
        ComputerConnectionState.NotPaired -> Color(0xFFFF9500)
    }
    Card(
        modifier = modifier,
        shape = RoundedCornerShape(24.dp),
        colors = CardDefaults.cardColors(containerColor = Color(0xF5FFFFFF))
    ) {
        Column(Modifier.padding(horizontal = 12.dp, vertical = 10.dp), verticalArrangement = Arrangement.spacedBy(7.dp)) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        Surface(color = connectionColor, shape = RoundedCornerShape(8.dp), modifier = Modifier.size(8.dp)) {}
                        Text("电脑$connectionLabel", color = connectionColor, style = MaterialTheme.typography.bodySmall)
                    }
                    Text(
                        if (trackingNo.isNullOrBlank()) "电脑空闲" else "正在录像：$trackingNo",
                        fontWeight = FontWeight.SemiBold
                    )
                    Text(
                        if (lastBarcode.isEmpty()) status else "$status · 最近扫码：$lastBarcode",
                        color = if (status.contains("失败")) Color(0xFFFF3B30) else Color(0xFF6E6E73),
                        style = MaterialTheme.typography.bodySmall
                    )
                }
                OutlinedButton(onClick = onRefresh, enabled = refreshEnabled) { Text("刷新状态") }
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                Button(onClick = { send("UV-TAG-DAMAGE01") }, enabled = commandEnabled, modifier = Modifier.weight(1f)) { Text("破损") }
                Button(onClick = { send("UV-TAG-SWAPPED1") }, enabled = commandEnabled, modifier = Modifier.weight(1f)) { Text("调包") }
                OutlinedButton(onClick = { send("UV-UNDO-TAG") }, enabled = commandEnabled, modifier = Modifier.weight(1f)) { Text("撤销") }
                OutlinedButton(onClick = { send("UV-SNAPSHOT") }, enabled = commandEnabled, modifier = Modifier.weight(1f)) { Text("截图") }
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = note,
                    onValueChange = { if (it.length <= 2000) note = it },
                    label = { Text("问题备注") },
                    singleLine = true,
                    modifier = Modifier.weight(1f)
                )
                Button(onClick = { send("UV-NOTE:${note.trim()}") }, enabled = commandEnabled) { Text("保存") }
                Button(
                    onClick = { send("UV-STOP") },
                    enabled = commandEnabled,
                    colors = ButtonDefaults.buttonColors(containerColor = Color(0xFFFF3B30))
                ) { Text("停止") }
            }
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Checkbox(checked = startComputerRecording, onCheckedChange = onStartComputerRecordingChanged)
                Column(Modifier.weight(1f)) {
                    Text("启动电脑录像", fontWeight = FontWeight.SemiBold)
                    Text(
                        if (startComputerRecording) "扫码将开始、结束或切换电脑录像" else "仅收集单号并同步 Excel",
                        color = Color(0xFF6E6E73),
                        style = MaterialTheme.typography.bodySmall
                    )
                }
                if (queuedScanCount > 0) Text("待同步 $queuedScanCount", color = Color(0xFFFF9500))
            }
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Switch(checked = torchEnabled, onCheckedChange = onTorchChanged)
                Column(Modifier.weight(1f).padding(start = 8.dp)) {
                    Text("闪光灯", fontWeight = FontWeight.SemiBold)
                    Text("光线暗时打开，扫描面单条码更稳定", color = Color(0xFF6E6E73), style = MaterialTheme.typography.bodySmall)
                }
            }
        }
    }
}

private const val SCAN_LOG_TAG = "UnpackVisionScan"
