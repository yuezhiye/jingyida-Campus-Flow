# SrunAutoLogin · 深澜校园网自动认证

> **本分支为「景德镇艺术职业大学」定制适配版。**
> 专为该校校园网环境开发并**已在真实环境下实测通过**（联通宽带 + 深澜 eportal）。
> 其他采用深澜 eportal 认证的学校也可通过改配置使用，详见 [迁移到其他深澜学校](#迁移到其他深澜学校)。

**适配学校**：景德镇艺术职业大学（Jingdezhen Ceramic Vocational University of Art）
**适配运营商**：中国联通校园宽带（账号后缀 `@unicom`）
**认证协议**：深澜（Srun）eportal JSONP
**运行环境**：Windows 10 / 11（需 .NET Framework 4.x，系统自带）
**配置方式**：门户地址、账号前后缀等全部在界面上填写，或改 `config.json`

---

## 这个程序解决什么问题

针对**景德镇艺术职业大学**校园网，连上 Wi-Fi 后自动完成 Portal 认证，
不用每次手动打开浏览器输账号密码。登录一次配置，之后开机静默认证。

### 为什么需要专门适配

该校校园网的两个特点，决定了通用工具用不了：

1. **账号格式有强约束** —— 服务端只认 `,0,` + 裸账号 + `@unicom` 的拼接形式
   （裸账号直接提交会被判「账号不存在」）。本程序把这段格式**内置**了，
   你只需要填学号本身。
2. **走的是深澜接口而非网页表单** —— 登录页是纯接口（无 `<form>`），
   靠"自动识别网页表单"的通用工具在这里完全失效。

---

## 快速开始

### 1. 直接运行（推荐）

双击项目根目录的 **`SrunAutoLogin.exe`** 即可，无需编译。

### 2. 编译（改了源码之后）

在项目根目录用 PowerShell 运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

产物：`build\SrunAutoLogin.exe`（同时会同步一份到项目根目录 `SrunAutoLogin.exe`）

> 用 `src\SrunAutoLogin.cs` + Windows 自带的 .NET Framework 编译器生成，
> **不需要安装 Visual Studio 或任何开发工具**。

### 3. 运行配置

双击 `SrunAutoLogin.exe`，在界面上填。

**景德镇艺术职业大学的推荐值**（校内有线 / 联通宽带）：

| 项 | 填什么 | 说明 |
|---|---|---|
| **账号** | 你的学号 | **只填裸账号**（如 `2024507120101`），不要加前缀后缀 |
| **密码** | 你的密码 | 用 DPAPI 加密存储，不落明文 |
| 账号前缀 | `,0,` | 该校服务端强制要求，**保持默认即可** |
| 账号后缀 | `@unicom` | 联通宽带为 `@unicom`；电信 `@telecom`、移动 `@cmcc` |
| 登录接口地址 | `http://172.22.1.46:801/eportal/portal/login` | 该校门户地址（换楼栋/校区可能需要调整） |
| 网卡关键字 | `WLAN` | 用于挑选正确的内网 IP |
| 检查间隔 | `30` 秒 | 未联网时的重试间隔 |

界面会实时显示拼装预览（如 **「提交为：,0,2024507120101@unicom」**），确认格式正确。

点 **「测试认证」** 验证，成功后勾选 **「开机自动启动」** 并点 **「保存并启用」**。

> **换到别的教学楼/宿舍楼失效了？** 重新抓包确认 `wlan_ac_ip`（不同区域 AC 地址可能不同），
> 在 `config.json` 里改 `ExtraParams.wlan_ac_ip`。若服务端不校验该字段，留空也可以。

> **不知道填什么？** 在浏览器里登录一次校园网，按 F12 → Network，
> 找到 `portal/login` 那个请求，把它的完整参数抄下来即可。

### 4. 完成

之后开机即自动在后台认证，联网成功后会**自动停止轮询**，不占资源。

---

## 界面功能

| 按钮 | 作用 |
|---|---|
| 测试认证 | 立即执行一次认证，弹窗显示详细结果 |
| 保存并启用 | 保存配置 + 账号密码，并按勾选设置开机自启 |
| 查看日志 | 用记事本打开日志（不含账号密码） |
| 恢复默认 | 把接口地址等重置为内置默认模板 |
| 刷新 | 重新检测当前网卡 IPv4 |

关闭窗口 = 最小化到托盘（右下角），后台继续运行。真正退出请用托盘菜单的「退出」。

---

## 工作原理

```
读取配置 → 检测本机内网 IPv4 (匹配网卡关键字)
        ↓
拼装 URL：
  http://<门户IP>:801/eportal/portal/login
    ?callback=dr1003
    &login_method=1
    &user_account=,0,<你的账号>@unicom     ← 前缀+账号+后缀 自动拼
    &user_password=***
    &wlan_user_ip=<本机内网IP>             ← 动态替换
    &wlan_user_ipv6=&wlan_user_mac=000000000000
    &wlan_ac_ip=<AC设备IP>&wlan_ac_name=
    &jsVersion=4.28&terminal_type=1&lang=zh-cn
        ↓
GET 请求 → 解析返回 JSONP
        ↓
dr1003({"result":1,"msg":"Portal协议认证成功！"})   → 成功
dr1003({"result":0,"msg":"IP: x.x.x.x 已经在线！"}) → 已在线，视为成功
```

**成功判定基于 `result` 字段**（`1` = 成功），并识别「已经在线」的幂等情况，
比关键词匹配可靠。

---

## 配置文件说明

配置存在 `%LocalAppData%\SrunAutoLogin\`：

| 文件 | 内容 |
|---|---|
| `config.json` | 接口地址、前缀后缀、网卡关键字等（明文，不含密码） |
| `credential.dat` | 账号密码（**DPAPI 加密，绑定当前 Windows 用户**） |
| `srun.log` | 运行日志，保留最近 200 行，**不记录账号密码** |

### 迁移到其他深澜学校

本项目为**景德镇艺术职业大学**定制，但协议层做了全配置化，
其他深澜 eportal 学校改 `config.json` 即可，无需重新编译：

```json
{
  "PortalUrl": "http://其他学校的门户IP:801/eportal/portal/login",
  "AccountPrefix": ",0,",
  "AccountSuffix": "@telecom",
  "AccountField": "user_account",
  "PasswordField": "user_password",
  "ExtraParams": {
    "callback": "dr1003",
    "login_method": "1",
    "wlan_user_ip": "{local_ip}",
    "wlan_user_ipv6": "",
    "wlan_user_mac": "000000000000",
    "wlan_ac_ip": "其他学校的AC地址",
    "wlan_ac_name": "",
    "jsVersion": "4.28",
    "terminal_type": "1",
    "lang": "zh-cn"
  },
  "InterfaceKeyword": "WLAN",
  "ConnectivityUrl": "http://www.msftconnecttest.com/connecttest.txt",
  "ConnectivityExpected": "Microsoft Connect Test",
  "CheckIntervalSeconds": 30,
  "TimeoutMs": 10000
}
```

不同运营商的账号后缀：
- 中国联通 `@unicom`
- 中国电信 `@telecom`
- 中国移动 `@cmcc`

---

## 与原版 Campus-Flow 的区别

参考项目 [zuijiu888/Campus-Flow](https://github.com/zuijiu888/Campus-Flow)（面向河北水利电力学院 / 移动）。
**本项目为景德镇艺术职业大学重新实现**，原因是原项目无法适配该校：

| 维度 | Campus-Flow | 本项目（景德镇艺术职业大学适配版） |
|---|---|---|
| 认证方式 | HTML 表单 POST | 深澜 JSONP 接口 GET |
| 账号处理 | 原样提交，需手填完整账号 | **自动拼 `,0,` 前缀 + `@unicom` 后缀，只填裸账号** |
| 成功判定 | 页面关键词匹配 | **解析 `result` 字段**，精确 |
| 版本兼容 | 逻辑与移动模板耦合 | **全配置化，可移植到任意深澜学校** |
| 表单识别 | 正则扒 form（对接口无效） | 移除（深澜登录页无 `<form>`，用不上） |
| 日志 | — | 记录结果但**不记录账号密码** |

> 直接套用原项目的失败原因：其默认模板硬编码为河北移动的 `showLogin.do` +
> `bpssUSERNAME` 字段，且机制是「填什么发什么」，不具备深澜所需的账号前后缀拼装能力。
> 详见 [为什么需要专门适配](#为什么需要专门适配)。

---

## 常见问题

**Q：测试显示「已经在线」算成功吗？**
算。说明该 IP 当前会话已认证，程序会视作联网正常并停止轮询。

**Q：认证失败提示 `result=0`？**
看弹窗里的 `msg` 内容：
- 「密码错误」→ 检查密码
- 「账号不存在」→ 检查前缀后缀是否需要调整（试 `你的账号@unicom` 不带`,0,`）
- 「已在其他设备登录」→ 深澜限制单设备在线，先在其他设备登出

**Q：换到别的教学楼就失效？**
检查 `config.json` 里的 `wlan_ac_ip`。有的校园网不同区域 AC 地址不同，
如果失效，重新抓包确认新的 `wlan_ac_ip`。若服务端不校验该字段，留空也可以。

**Q：密码存在哪？安全吗？**
用 Windows DPAPI（`ProtectedData.Protect`）加密，**密钥绑定当前 Windows 用户账户**。
换用户或换机器都无法解密，也不会上传到任何服务器。

**Q：怎么完全卸载？**
1. 界面点「退出程序」
2. 取消勾选开机自启（或在界面里取消后保存）
3. 删除 `%LocalAppData%\SrunAutoLogin\` 目录
4. 删除本项目目录

---

## 目录结构

```
SrunAutoLogin/
├── SrunAutoLogin.exe         # 直接双击运行（预编译好，无需编译）
├── README.md                 # 本文件
├── LICENSE                   # Apache License 2.0
├── config.example.json       # 配置模板
├── build.ps1                 # 一键编译脚本（改源码后重新编译用）
├── src/
│   └── SrunAutoLogin.cs      # 主程序源码（单文件）
└── build/
    └── SrunAutoLogin.exe     # 编译产物的原始位置（与根目录同步）
```

---

## 技术细节

- **语言**：C# 5 / .NET Framework 4.8
- **UI**：WinForms
- **依赖**：仅系统库（`System.Web.Extensions` 用于 JSON）
- **单实例**：Mutex 保护，避免重复运行
- **线程模型**：后台轮询线程 + UI 线程，跨线程用 `BeginInvoke`
- **网络事件**：监听 `NetworkAvailabilityChanged` / `NetworkAddressChanged`，
  断网或换网时 2 秒内唤醒重试
- **资源优化**：确认联网后停止轮询，仅在网络状态变化时重新唤醒

---

## 许可证

本项目采用 [Apache License 2.0](LICENSE) 开源。

---

*本工具仅用于个人设备自动完成校园网认证，请遵守学校网络使用规定。*
