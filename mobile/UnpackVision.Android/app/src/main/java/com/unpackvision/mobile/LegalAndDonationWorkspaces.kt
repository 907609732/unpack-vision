package com.unpackvision.mobile

import android.content.Context
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import java.security.MessageDigest

@Composable
internal fun LegalWorkspace(title: String, body: String, onBack: () -> Unit) {
    Column(
        Modifier
            .fillMaxSize()
            .background(Color(0xFFF4F6FA))
            .statusBarsPadding()
            .navigationBarsPadding()
            .padding(20.dp)
    ) {
        FeatureHeader(onBack, title, "版本 2026-07-29（2.3）")
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
internal fun DonationWorkspace(onBack: () -> Unit) {
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
                    .fillMaxWidth()
                    .height(430.dp)
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
    context: Context,
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

    // The bundled QR code is accepted only when it matches the profile hash,
    // so a resource replacement cannot silently redirect donations.
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
