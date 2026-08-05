package com.unpackvision.mobile

object MobileLegalDocuments {
    const val TERMS = """
拆包智录用户协议
版本：2026-07-29（2.3）

本软件由开发者“五成”提供，是面向中小商家的开源拆包录像、扫码和数据同步工具。软件按现状提供，使用者应确认对拍摄的包裹、面单和工作场所具有合法处理权限。

录像可能包含姓名、地址、电话、快递单号等个人信息。使用者应设置合理的访问权限、保存期限和删除流程，不得用于违法用途。

单号、录像、备注、Excel 和配对信息默认保存在使用者自己的设备。使用者应自行保证磁盘空间、设备安全和必要备份。

自动更新开启时会访问 GitHub Releases；手机协同只连接使用者扫码配对的局域网电脑。赞助由支付宝或微信独立处理，本软件不读取付款结果。

一般问题请通过 GitHub Issues 联系；安全漏洞请使用仓库私密漏洞报告入口。
"""

    const val PRIVACY = """
拆包智录隐私政策
版本：2026-07-29（2.3）

2.3.0 不上传快递单号、录像、备注、Excel 路径、摄像头名称、硬件标识、账号或配对信息。

手机端会在本机保存加密后的工位令牌、应用设置和尚未发送的离线扫码事件。工位令牌由 Android Keystore 保护。

自动更新开启时会访问 GitHub Releases。该请求会向 GitHub 提供互联网通信必需的 IP 地址和常规请求信息。关闭自动更新后仍可手动检查。

手机协同使用证书固定的 HTTPS 连接已配对电脑，视频流使用加密传输和独立设备令牌。软件默认不提供公网入口。

相机权限仅在进入配对、扫码或摄像功能时申请；通知权限只用于版本更新提醒；安装 APK 由安卓系统单独授权。麦克风录音默认关闭，当前版本不申请麦克风权限。

首次协议页提供独立的匿名日活选项。该选项默认勾选，但可以在同意前取消，也可以在设置中随时关闭；拒绝或撤回不会影响任何功能。

启用后每天最多发送一次：北京时间日期、由 Android Keystore 随机密钥按日计算且不可跨日关联的匿名值、Android、软件版本和发布通道。不读取 IMEI、MAC 地址、账号、硬盘或手机序列号。原始匿名日记录保留35天，每日汇总数字保留24个月。

赞助页面不接入支付 SDK，不读取付款人、金额、订单、账号或付款结果。
"""
}

data class MobileDonationProfile(
    val developerName: String = "五成",
    val alipayDrawableName: String = "donation_alipay",
    val alipaySha256: String = "5CB45BCFC0BBCEAEB7ABC600E0BC840BC589185821CF660195E3CA1751DD4364",
    val weChatDrawableName: String = "donation_wechat",
    val weChatSha256: String = "B2967005849581FCA0F329A10D52543BD67728A63911E69B8C245B5A74F0BB2D"
)
