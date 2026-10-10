// ============================================================================
//  SrunAutoLogin - 深澜 (Srun) eportal 校园网自动认证工具
//  适配学校：景德镇艺术职业大学（联通宽带，账号格式 ,0,<学号>@unicom）
//  协议    ：深澜 eportal JSONP (GET)
//
//  特性：
//    - 只填裸账号 + 密码，程序自动拼装账号前缀与运营商后缀
//    - 认证成功判定基于返回 JSON 的 result 字段（精确，非关键词猜测）
//    - 选卡逻辑分级挑选：排除虚拟网卡 → 只认「有默认网关」的真实出口
//      （关键字写错、装了 VirtualBox/VPN 也不会再挑到 192.168.56.1 这类地址）
//    - 登录接口地址防呆：手填成门户首页（a79.htm 等）时自动还原为登录接口
//    - 联网后转入低频探活，会话被踢掉也能自动重新认证
//    - 密码用 Windows DPAPI 加密存储（绑定当前用户，不落明文）
//    - 日志不记录账号密码
//    - 所有可变项均从 config.json 读取，可移植到其他深澜学校
//
//  部署：门户地址、账号前后缀等均在 config.json 中配置，
//        或在程序「恢复默认」后手动填写。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("SrunAutoLogin")]
[assembly: System.Reflection.AssemblyProduct("SrunAutoLogin")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

namespace SrunAutoLogin
{
    // ========================================================================
    //  配置模型
    // ========================================================================
    public sealed class AppConfig
    {
        /// <summary>门户登录接口地址（不含账号密码参数）</summary>
        public string PortalUrl { get; set; }
        /// <summary>本地 IP 占位符名，运行时替换为当前网卡 IPv4</summary>
        public string LocalIpToken { get; set; }
        /// <summary>账号前缀，例如 ",0,"  可为空</summary>
        public string AccountPrefix { get; set; }
        /// <summary>账号后缀（运营商标识，如 @unicom / @telecom / @cmcc）可为空</summary>
        public string AccountSuffix { get; set; }
        /// <summary>账号参数字段名</summary>
        public string AccountField { get; set; }
        /// <summary>密码参数字段名</summary>
        public string PasswordField { get; set; }
        /// <summary>固定附加参数（键值对）</summary>
        public Dictionary<string, string> ExtraParams { get; set; }
        /// <summary>指定使用的网卡名关键字，空=自动挑真实出口网卡（推荐）</summary>
        public string InterfaceKeyword { get; set; }
        /// <summary>联网检测地址</summary>
        public string ConnectivityUrl { get; set; }
        /// <summary>联网检测期望内容</summary>
        public string ConnectivityExpected { get; set; }
        /// <summary>轮询间隔（秒）</summary>
        public int CheckIntervalSeconds { get; set; }
        /// <summary>请求超时（毫秒）</summary>
        public int TimeoutMs { get; set; }

        public static AppConfig Default()
        {
            // 默认值已按「景德镇艺术职业大学」的实测抓包结果预置，开箱即用。
            // 其他深澜学校：改 PortalUrl 与 wlan_ac_ip（或整个 config.json）即可，无需重新编译。
            return new AppConfig
            {
                PortalUrl = "http://172.22.1.46:801/eportal/portal/login",
                LocalIpToken = "{local_ip}",
                AccountPrefix = ",0,",
                AccountSuffix = "@unicom",
                AccountField = "user_account",
                PasswordField = "user_password",
                ExtraParams = new Dictionary<string, string>
                {
                    { "callback", "dr1003" },
                    { "login_method", "1" },
                    { "wlan_user_ip", "{local_ip}" },
                    { "wlan_user_ipv6", "" },
                    { "wlan_user_mac", "000000000000" },
                    { "wlan_ac_ip", "172.22.1.46" },
                    { "wlan_ac_name", "" },
                    { "jsVersion", "4.28" },
                    { "terminal_type", "1" },
                    { "lang", "zh-cn" }
                },
                InterfaceKeyword = "",
                ConnectivityUrl = "http://www.msftconnecttest.com/connecttest.txt",
                ConnectivityExpected = "Microsoft Connect Test",
                CheckIntervalSeconds = 30,
                TimeoutMs = 10000
            };
        }
    }

    // ========================================================================
    //  路径与配置存取
    // ========================================================================
    internal static class AppData
    {
        internal const string AppName = "SrunAutoLogin";
        internal const string MutexName = @"Local\SrunAutoLogin.SingleInstance";
        internal const string StopEventName = @"Local\SrunAutoLogin.Stop";
        internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string RunName = "SrunAutoLogin";
        /// <summary>日志最多保留的行数</summary>
        internal const int MaxLogLines = 200;

        internal static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        internal static readonly string ConfigPath = Path.Combine(Dir, "config.json");
        internal static readonly string CredPath = Path.Combine(Dir, "credential.dat");
        internal static readonly string LogPath = Path.Combine(Dir, "srun.log");

        internal static string ToJson(object o)
        {
            return new JavaScriptSerializer().Serialize(o);
        }

        internal static T FromJson<T>(string json) where T : class
        {
            return new JavaScriptSerializer().Deserialize<T>(json);
        }

        private static void EnsureDir()
        {
            try { if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir); }
            catch { }
        }

        internal static AppConfig LoadConfig()
        {
            EnsureDir();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    var cfg = FromJson<AppConfig>(json);
                    if (cfg != null)
                    {
                        if (cfg.ExtraParams == null) cfg.ExtraParams = new Dictionary<string, string>();
                        if (string.IsNullOrEmpty(cfg.LocalIpToken)) cfg.LocalIpToken = "{local_ip}";
                        if (cfg.CheckIntervalSeconds <= 0) cfg.CheckIntervalSeconds = 30;
                        if (cfg.TimeoutMs <= 0) cfg.TimeoutMs = 10000;

                        // 防呆：config.json 被手改成门户首页（a79.htm 之类）时，运行时自动还原成登录接口，
                        // 保证「即使配置写错，程序也不会拿着一个只会返回 HTML 的地址去认证」。
                        string note;
                        cfg.PortalUrl = SrunClient.NormalizePortalUrl(cfg.PortalUrl, out note);
                        return cfg;
                    }
                }
            }
            catch (Exception ex) { Log("读取配置失败，改用默认配置：" + ex.Message); }
            return AppConfig.Default();
        }

        internal static void SaveConfig(AppConfig cfg)
        {
            EnsureDir();
            File.WriteAllText(ConfigPath, ToJson(cfg), new UTF8Encoding(false));
        }

        /// <summary>用 DPAPI 加密保存凭据，绑定当前 Windows 用户</summary>
        internal static void SaveCredential(string user, string pass)
        {
            EnsureDir();
            try
            {
                string raw = user + "\n" + pass;
                byte[] plain = Encoding.UTF8.GetBytes(raw);
                byte[] cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(CredPath, cipher);
                Array.Clear(plain, 0, plain.Length);
            }
            catch (Exception ex) { Log("保存凭据失败：" + ex.Message); }
        }

        internal static string[] LoadCredential()
        {
            try
            {
                if (!File.Exists(CredPath)) return null;
                byte[] cipher = File.ReadAllBytes(CredPath);
                byte[] plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                string raw = Encoding.UTF8.GetString(plain);
                int nl = raw.IndexOf('\n');
                if (nl < 0) return null;
                return new[] { raw.Substring(0, nl), raw.Substring(nl + 1) };
            }
            catch (Exception ex) { Log("读取凭据失败：" + ex.Message); return null; }
        }

        internal static bool HasCredential()
        {
            var c = LoadCredential();
            return c != null && c[0].Length > 0 && c[1].Length > 0;
        }

        internal static void Log(string message)
        {
            try
            {
                EnsureDir();
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.AppendAllText(LogPath, stamp + "  " + message + Environment.NewLine, Encoding.UTF8);

                // 只保留最近 MaxLogLines 行。
                // 注意：旧版这里写回的是原数组，等于没截断，日志会无限增长。
                if (File.Exists(LogPath))
                {
                    string[] lines = File.ReadAllLines(LogPath, Encoding.UTF8);
                    if (lines.Length > MaxLogLines)
                    {
                        string[] keep = new string[MaxLogLines];
                        Array.Copy(lines, lines.Length - MaxLogLines, keep, 0, MaxLogLines);
                        File.WriteAllLines(LogPath, keep, Encoding.UTF8);
                    }
                }
            }
            catch { }
        }
    }

    // ========================================================================
    //  深澜 eportal 客户端
    // ========================================================================
    internal static class SrunClient
    {
        private static readonly object Gate = new object();

        internal sealed class SrunResult
        {
            public bool Success;
            public bool AlreadyOnline;
            public string Result = "";
            public string Message = "";
            public string Raw = "";
        }

        /// <summary>
        /// 虚拟 / 隧道类网卡特征关键字。命中即认为「不是真实上网出口的网卡」，
        /// 不允许它们提供的 IP 被提交给认证服务器。
        /// </summary>
        private static readonly string[] VirtualAdapterHints = new string[]
        {
            "VirtualBox", "VMware", "VMnet", "Hyper-V", "vEthernet", "Virtual Adapter",
            "Radmin", "ZeroTier", "Tailscale", "Hamachi", "Npcap", "WinPcap",
            "TAP-Windows", "TAP-Win32", "OpenVPN", "WireGuard", "AnyConnect", "EasyConnect",
            "Sangfor", "深信服", "Parallels", "QEMU", "Docker", "WSL", "Netch",
            "Wi-Fi Direct", "Bluetooth", "蓝牙", "Loopback",
            "Teredo", "ISATAP", "6to4", "SSTP", "L2TP", "PPTP", "VPN"
        };

        /// <summary>网卡名/描述里是否含虚拟网卡特征</summary>
        internal static bool IsVirtualAdapter(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string hint in VirtualAdapterHints)
                if (name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>是否私网 IPv4（10/8、172.16/12、192.168/16）</summary>
        internal static bool IsPrivateIpv4(string ip)
        {
            return ip.StartsWith("10.")
                || ip.StartsWith("192.168.")
                || IsPrivate172(ip);
        }

        /// <summary>取本机私网 IPv4（不关心网卡名）</summary>
        internal static string GetLocalIPv4(AppConfig cfg)
        {
            string adapterName;
            return GetLocalIPv4(cfg, out adapterName);
        }

        /// <summary>
        /// 取本机内网 IPv4，并按优先级挑选可信候选；同时回传所用网卡的名称。
        ///
        /// 分级策略（越靠前越可信）：
        ///   ① 关键字命中 + 非虚拟网卡 + 有默认网关 + 私网
        ///   ② 非虚拟网卡 + 有默认网关 + 私网
        ///   ③ 非虚拟网卡 + 私网
        ///   ④ 私网（最后兜底，仅在极特殊环境下出现）
        ///
        /// 这样即使关键字写错（例如从 WiFi 切到有线后仍写着 WLAN），
        /// 也只会挑到「真正有默认网关」的物理网卡，而不会再把
        /// VirtualBox Host-Only 的 192.168.56.1 之类地址提交出去。
        /// </summary>
        internal static string GetLocalIPv4(AppConfig cfg, out string adapterName)
        {
            adapterName = "";

            string ipKeyword = "", nameKeyword = "";
            string ipGateway = "", nameGateway = "";
            string ipAny     = "", nameAny     = "";
            string ipLast    = "", nameLast    = "";

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                        string name = ni.Name + " " + ni.Description;
                        bool isVirtual = IsVirtualAdapter(name);

                        bool hasGateway = false;
                        foreach (GatewayIPAddressInformation gw in ni.GetIPProperties().GatewayAddresses)
                        {
                            if (gw.Address != null && gw.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                hasGateway = true;
                                break;
                            }
                        }

                        bool keywordHit = !string.IsNullOrEmpty(cfg.InterfaceKeyword)
                            && name.IndexOf(cfg.InterfaceKeyword, StringComparison.OrdinalIgnoreCase) >= 0;

                        foreach (UnicastIPAddressInformation addr in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            string ip = addr.Address.ToString();
                            if (ip.StartsWith("169.254.")) continue;   // APIPA 自动地址，忽略
                            if (ip.StartsWith("127.")) continue;       // 环回，忽略
                            if (!IsPrivateIpv4(ip)) continue;          // 只认私网地址

                            if (ipKeyword.Length == 0 && keywordHit && hasGateway && !isVirtual)
                            { ipKeyword = ip; nameKeyword = ni.Name; }
                            if (ipGateway.Length == 0 && hasGateway && !isVirtual)
                            { ipGateway = ip; nameGateway = ni.Name; }
                            if (ipAny.Length == 0 && !isVirtual)
                            { ipAny = ip; nameAny = ni.Name; }
                            if (ipLast.Length == 0)
                            { ipLast = ip; nameLast = ni.Name; }
                        }
                    }
                    catch { }   // 单块网卡读取失败不影响其他网卡
                }
            }
            catch { }

            if (ipKeyword.Length > 0) { adapterName = nameKeyword; return ipKeyword; }
            if (ipGateway.Length > 0) { adapterName = nameGateway; return ipGateway; }
            if (ipAny.Length > 0)     { adapterName = nameAny;     return ipAny; }
            adapterName = nameLast;
            return ipLast;
        }

        /// <summary>是否 172.16.0.0/12 私网段</summary>
        internal static bool IsPrivate172(string ip)
        {
            string[] p = ip.Split('.');
            int second;
            return p.Length == 4 && p[0] == "172"
                && int.TryParse(p[1], out second)
                && second >= 16 && second <= 31;
        }

        /// <summary>
        /// 登录接口地址防呆：把「门户跳转首页」自动还原成标准登录接口。
        ///
        /// 背景：README 里让人抓包抄 URL，很容易连地址栏的 a79.htm 一起抄进去。
        /// 那个地址只会返回一整页 HTML，认证永远不可能成功（表现为日志里
        /// result 为空、msg 为空）。这里按特征识别并纠正：
        ///   http://172.22.1.46/a79.htm?userip=172.21.39.165&... 
        ///     → http://172.22.1.46:801/eportal/portal/login
        ///
        /// 已经是对的地址、或者认不出来的地址，一律原样返回（不瞎改）。
        /// </summary>
        internal static string NormalizePortalUrl(string raw, out string note)
        {
            note = null;
            if (string.IsNullOrWhiteSpace(raw)) return raw;

            string s = raw.Trim();
            if (s.IndexOf("://", StringComparison.Ordinal) < 0) s = "http://" + s;

            try
            {
                Uri uri;
                if (!Uri.TryCreate(s, UriKind.Absolute, out uri)) return raw;

                string path = (uri.AbsolutePath ?? "").ToLowerInvariant();

                // 已经是标准登录接口，原样返回
                if (path.IndexOf("/eportal/portal/login") >= 0) return s;

                // 门户首页特征：跳转页后缀 / 根路径 / 带门户跳转参数
                bool looksLikeLanding =
                       path.EndsWith(".htm") || path.EndsWith(".html")
                    || path.EndsWith(".jsp") || path.EndsWith(".asp")
                    || path == "/" || path.Length == 0
                    || s.IndexOf("userip=", StringComparison.OrdinalIgnoreCase) >= 0
                    || s.IndexOf("wlanacip=", StringComparison.OrdinalIgnoreCase) >= 0
                    || s.IndexOf("usermac=", StringComparison.OrdinalIgnoreCase) >= 0
                    || s.IndexOf("a79", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!looksLikeLanding) return s;   // 认不出来就不动，交给用户判断

                string fixedUrl = uri.Scheme + "://" + uri.Host + ":801/eportal/portal/login";
                note = "登录接口地址疑似填成了门户首页，已自动纠正为：" + fixedUrl;
                return fixedUrl;
            }
            catch { return raw; }
        }

        internal static string Expand(string value, AppConfig cfg)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace(cfg.LocalIpToken, GetLocalIPv4(cfg));
        }

        /// <summary>拼装完整账号：前缀 + 裸账号 + 后缀</summary>
        internal static string BuildAccount(string bareAccount, AppConfig cfg)
        {
            return (cfg.AccountPrefix ?? "") + bareAccount + (cfg.AccountSuffix ?? "");
        }

        internal static string BuildLoginUrl(string bareAccount, string password, AppConfig cfg)
        {
            var fields = new List<string>();

            // 附加固定参数
            foreach (var kv in cfg.ExtraParams)
                fields.Add(Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(Expand(kv.Value ?? "", cfg)));

            // 账号 / 密码
            fields.Add(Uri.EscapeDataString(cfg.AccountField) + "=" + Uri.EscapeDataString(BuildAccount(bareAccount, cfg)));
            fields.Add(Uri.EscapeDataString(cfg.PasswordField) + "=" + Uri.EscapeDataString(password ?? ""));

            string url = Expand(cfg.PortalUrl, cfg);
            return url + (url.Contains("?") ? "&" : "?") + string.Join("&", fields.ToArray());
        }

        /// <summary>解析深澜 JSONP 返回，例如 dr1003({"result":1,"msg":"..."})</summary>
        internal static SrunResult Parse(string raw)
        {
            var r = new SrunResult { Raw = raw ?? "" };
            try
            {
                Match m = Regex.Match(r.Raw, @"\{[\s\S]*\}");
                if (m.Success)
                {
                    string json = m.Value;
                    Match result = Regex.Match(json, @"""result""\s*:\s*""?(\d+)""?");
                    if (!result.Success) result = Regex.Match(json, @"""result""\s*:\s*(\d+)");
                    if (result.Success) r.Result = result.Groups[1].Value;

                    Match msg = Regex.Match(json, @"""msg""\s*:\s*""([^""]*)""");
                    if (msg.Success) r.Message = msg.Groups[1].Value;

                    if (r.Message.Contains("已经在线")) r.AlreadyOnline = true;
                    r.Success = (r.Result == "1") || r.AlreadyOnline;
                }
                else
                {
                    // 非 JSON 响应，退化为关键词判定
                    r.Success = r.Raw.Contains("认证成功") || r.Raw.Contains("登录成功") || r.Raw.Contains("success");
                    r.Message = r.Raw.Length > 120 ? r.Raw.Substring(0, 120) : r.Raw;
                }
            }
            catch (Exception ex) { r.Message = "解析异常：" + ex.Message; }
            return r;
        }

        /// <summary>执行一次认证尝试</summary>
        internal static string TryLogin(AppConfig cfg)
        {
            if (!Monitor.TryEnter(Gate)) return "已有检测正在进行";
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable()) return "未检测到可用网络";

                if (HasInternet(cfg)) return "网络已经可以正常访问";

                string[] cred = AppData.LoadCredential();
                if (cred == null || cred[0].Length == 0) return "尚未保存账号密码";

                string adapterName;
                string ip = GetLocalIPv4(cfg, out adapterName);
                if (string.IsNullOrEmpty(ip)) return "未找到可用的内网 IPv4 地址";

                string url = BuildLoginUrl(cred[0], cred[1], cfg);
                string response = Get(url, cfg, BuildReferer(cfg));

                SrunResult parsed = Parse(response);
                AppData.Log(string.Format("认证返回 result={0} msg={1} (IP {2}{3})",
                    string.IsNullOrEmpty(parsed.Result) ? "?" : parsed.Result, parsed.Message, ip,
                    string.IsNullOrEmpty(adapterName) ? "" : " / 网卡 " + adapterName));

                if (parsed.Success)
                {
                    Thread.Sleep(800);
                    if (HasInternet(cfg)) return "认证成功，网络已连通";
                    return parsed.AlreadyOnline ? "该 IP 已经在线，网络正常" : "认证成功（联网验证未通过，可稍后重试）";
                }

                // 针对「AC认证失败」给出可操作的原因提示：绝大多数情况是提交的 IP 不是真实出口网卡
                if (parsed.Message.IndexOf("AC认证失败", StringComparison.Ordinal) >= 0)
                {
                    return "认证失败：" + parsed.Message
                        + Environment.NewLine + Environment.NewLine
                        + "当前提交的内网 IP：" + ip
                        + (string.IsNullOrEmpty(adapterName) ? "" : "（网卡：" + adapterName + "）")
                        + Environment.NewLine
                        + "该地址不是本机真正联网的出口网卡。请点「刷新」确认右侧「内网 IP」"
                        + "显示的是你正在上网的那块网卡；如果不对，调整「网卡关键字」"
                        + "（例如有线填 以太网 / Killer，无线填 WLAN），或把关键字留空让程序自动挑选。";
                }

                if (parsed.Message.Length > 0) return "认证失败：" + parsed.Message;
                return "认证失败：服务端未返回成功标识（返回内容可能是 HTML 页面，请检查登录接口地址是否为 .../eportal/portal/login）";
            }
            catch (WebException ex)
            {
                var http = ex.Response as HttpWebResponse;
                string status = http == null ? ex.Status.ToString() : ((int)http.StatusCode) + " " + http.StatusDescription;
                AppData.Log("请求异常：" + status);
                return "认证请求失败：" + status;
            }
            catch (Exception ex)
            {
                AppData.Log("认证异常：" + ex.Message);
                return "认证异常：" + ex.Message;
            }
            finally { Monitor.Exit(Gate); }
        }

        /// <summary>从门户地址推导同源 Referer（避免硬编码具体学校 IP）</summary>
        internal static string BuildReferer(AppConfig cfg)
        {
            try
            {
                var uri = new Uri(Expand(cfg.PortalUrl, cfg));
                return uri.Scheme + "://" + uri.Host + "/";
            }
            catch { return null; }
        }

        /// <summary>联网检测</summary>
        internal static bool HasInternet(AppConfig cfg)
        {
            try
            {
                string content = Get(Expand(cfg.ConnectivityUrl, cfg), cfg, null);
                return content.Trim().Contains((cfg.ConnectivityExpected ?? "").Trim());
            }
            catch { return false; }
        }

        private static string Get(string url, AppConfig cfg, string referer)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = cfg.TimeoutMs;
            req.ReadWriteTimeout = cfg.TimeoutMs;
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                          + "(KHTML, like Gecko) Chrome/126.0 Safari/537.36";
            req.AllowAutoRedirect = true;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (!string.IsNullOrEmpty(referer)) req.Referer = referer;

            // GET 一律用 HTTP/1.1，避免默认 chunked 兼容问题
            req.KeepAlive = true;

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException wex)
            {
                // 有些服务端返回 4xx/5xx 但 body 里仍有有效 JSONP，尝试读取
                if (wex.Response != null)
                {
                    try
                    {
                        using (var resp = (HttpWebResponse)wex.Response)
                        using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        {
                            string body = sr.ReadToEnd();
                            if (body.Contains("result")) return body;
                        }
                    }
                    catch { }
                }
                throw;
            }
        }
    }

    // ========================================================================
    //  开机自启管理
    // ========================================================================
    internal static class StartupManager
    {
        internal static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AppData.RunKey, false))
                {
                    if (k == null) return false;
                    return k.GetValue(AppData.RunName) != null;
                }
            }
            catch { return false; }
        }

        internal static void Enable()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AppData.RunKey, true))
                {
                    if (k == null) return;
                    k.SetValue(AppData.RunName, "\"" + Application.ExecutablePath + "\" --background");
                }
            }
            catch (Exception ex) { AppData.Log("设置自启失败：" + ex.Message); }
        }

        internal static void Disable()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AppData.RunKey, true))
                {
                    if (k == null) return;
                    if (k.GetValue(AppData.RunName) != null) k.DeleteValue(AppData.RunName, false);
                }
            }
            catch (Exception ex) { AppData.Log("取消自启失败：" + ex.Message); }
        }
    }

    // ========================================================================
    //  后台轮询宿主
    // ========================================================================
    internal static class BackgroundHost
    {
        /// <summary>已联网状态下的探活间隔（秒）。低频探活的目的是及时发现「会话被校园网踢掉」。</summary>
        private const int ConnectedProbeSeconds = 180;

        private static System.Threading.Timer timer;
        private static readonly object StateGate = new object();
        private static bool connected;
        private static bool checkQueued;
        private static DateTime lastProbeUtc = DateTime.MinValue;

        internal static void Run()
        {
            bool created;
            using (var mutex = new Mutex(true, AppData.MutexName, out created))
            {
                if (!created) return;   // 已有实例

                bool stopCreated;
                using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset,
                                                      AppData.StopEventName, out stopCreated))
                {
                    stop.Reset();
                    NetworkChange.NetworkAvailabilityChanged += delegate { SetConnected(false); QueueCheck(2000); };
                    NetworkChange.NetworkAddressChanged += delegate { SetConnected(false); QueueCheck(2000); };

                    AppConfig cfg = AppData.LoadConfig();
                    int interval = Math.Max(10, Math.Min(3600, cfg.CheckIntervalSeconds));

                    timer = new System.Threading.Timer(delegate { QueueCheck(0); }, null, 300, interval * 1000);
                    AppData.Log("后台服务已启动，检查间隔 " + interval + " 秒。");

                    stop.WaitOne();

                    timer.Dispose();
                    AppData.Log("后台服务已停止。");
                }
            }
        }

        internal static void RequestStop()
        {
            try
            {
                using (var stop = EventWaitHandle.OpenExisting(AppData.StopEventName))
                    stop.Set();
            }
            catch { }
        }

        private static void QueueCheck(int delayMs)
        {
            lock (StateGate)
            {
                if (checkQueued) return;
                checkQueued = true;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (delayMs > 0) Thread.Sleep(delayMs);
                    Check();
                }
                finally
                {
                    lock (StateGate) { checkQueued = false; }
                }
            });
        }

        private static void Check()
        {
            AppConfig cfg = AppData.LoadConfig();

            bool connectedNow;
            lock (StateGate) { connectedNow = connected; }

            // 已联网时不再高频轮询；改为每 ConnectedProbeSeconds 秒探活一次。
            // 旧版一旦 connected=true 就永久早退，只有网卡事件才能唤醒，
            // 结果是校园网把会话踢掉后程序一直装死，必须手动点「测试认证」。
            if (connectedNow)
            {
                bool dueForProbe;
                lock (StateGate)
                {
                    dueForProbe = (DateTime.UtcNow - lastProbeUtc).TotalSeconds >= ConnectedProbeSeconds;
                    if (dueForProbe) lastProbeUtc = DateTime.UtcNow;
                }
                if (!dueForProbe) return;

                if (SrunClient.HasInternet(cfg)) return;   // 仍然在线，继续安静待着

                AppData.Log("探活发现网络已断开，恢复自动认证。");
                SetConnected(false);
            }

            if (SrunClient.HasInternet(cfg))
            {
                SetConnected(true);
                lock (StateGate) { lastProbeUtc = DateTime.UtcNow; }
                return;
            }

            if (!AppData.HasCredential()) { AppData.Log("未配置账号密码，跳过认证。"); return; }

            string result = SrunClient.TryLogin(cfg);
            if (result.Contains("成功") || result.Contains("已经在线") || result.Contains("正常"))
                SetConnected(true);
        }

        private static void SetConnected(bool value)
        {
            lock (StateGate) { connected = value; }
        }
    }

    // ========================================================================
    //  清新圆角控件（WinForms 原生自绘，无第三方依赖）
    // ========================================================================
    internal static class Ui
    {
        internal static readonly Color Bg = Color.FromArgb(250, 252, 249);        // 奶油白底
        internal static readonly Color Card = Color.FromArgb(255, 255, 255);      // 卡片白
        internal static readonly Color CardBorder = Color.FromArgb(214, 235, 226); // 淡薄荷描边
        internal static readonly Color Text = Color.FromArgb(74, 85, 104);        // 暖灰文字
        internal static readonly Color Muted = Color.FromArgb(150, 165, 160);     // 次要文字
        internal static readonly Color Accent = Color.FromArgb(107, 196, 166);    // 主色 薄荷绿
        internal static readonly Color AccentDark = Color.FromArgb(84, 175, 143); // 主色深
        internal static readonly Color Ok = Color.FromArgb(123, 201, 111);        // 成功 草绿
        internal static readonly Color Warn = Color.FromArgb(240, 173, 78);       // 警告 暖橙
        internal static readonly Color Err = Color.FromArgb(226, 116, 116);       // 错误 柔红
        internal static readonly Color InputBg = Color.FromArgb(250, 253, 251);   // 输入框底
        internal static readonly Color Ghost = Color.FromArgb(238, 246, 242);     // 次要按钮底

        internal static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            if (d <= 0 || d > r.Width || d > r.Height) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>圆角卡片面板</summary>
    internal sealed class RoundedPanel : Panel
    {
        internal int Radius = 14;
        internal Color BorderColor = Ui.CardBorder;
        internal Color FillColor = Ui.Card;

        internal RoundedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.Rounded(r, Radius))
            {
                using (var b = new SolidBrush(FillColor)) e.Graphics.FillPath(b, path);
                using (var p = new Pen(BorderColor, 1)) e.Graphics.DrawPath(p, path);
            }
        }
    }

    /// <summary>圆角胶囊按钮，带悬停效果</summary>
    internal sealed class PillButton : Button
    {
        internal int Radius = 16;
        internal Color BaseColor = Ui.Accent;
        internal Color HoverColor = Ui.AccentDark;
        internal Color TextColor = Color.White;
        private bool hover;

        internal PillButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
            ForeColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.Rounded(r, Radius))
            {
                Color c = Enabled ? (hover ? HoverColor : BaseColor) : Color.FromArgb(210, 220, 216);
                using (var b = new SolidBrush(c)) e.Graphics.FillPath(b, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, r,
                Enabled ? TextColor : Color.FromArgb(250, 250, 250),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>圆角输入框（外层 Panel 画框，内嵌无边框 TextBox）</summary>
    internal sealed class RoundInput : Panel
    {
        internal readonly TextBox Box = new TextBox();
        internal int Radius = 10;
        private bool focused;

        internal RoundInput()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Height = 32;

            Box.BorderStyle = BorderStyle.None;
            Box.Font = new Font("Microsoft YaHei UI", 9.5F);
            Box.BackColor = Ui.InputBg;
            Box.ForeColor = Ui.Text;
            Box.GotFocus += delegate { focused = true; Invalidate(); };
            Box.LostFocus += delegate { focused = false; Invalidate(); };
            Box.TextChanged += delegate { if (TextChangedEvt != null) TextChangedEvt(this, EventArgs.Empty); };
            Controls.Add(Box);
        }

        internal event EventHandler TextChangedEvt;

        internal string Value
        {
            get { return Box.Text; }
            set { Box.Text = value; }
        }

        internal bool UsePasswordChar
        {
            set { Box.UseSystemPasswordChar = value; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Box.Location = new Point(12, (Height - Box.PreferredHeight) / 2);
            Box.Width = Math.Max(10, Width - 24);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.Rounded(r, Radius))
            {
                using (var b = new SolidBrush(Ui.InputBg)) e.Graphics.FillPath(b, path);
                using (var p = new Pen(focused ? Ui.Accent : Ui.CardBorder, focused ? 1.6f : 1f))
                    e.Graphics.DrawPath(p, path);
            }
        }
    }

    /// <summary>小圆点图标（自绘，避免 emoji 字体渲染不一致）</summary>
    internal sealed class DotIcon : Control
    {
        internal Color DotColor = Ui.Accent;
        internal bool Ring;

        internal DotIcon()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(22, 22);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(2, 2, Width - 5, Height - 5);
            if (Ring)
            {
                using (var p = new Pen(DotColor, 2f)) e.Graphics.DrawEllipse(p, r);
                using (var b = new SolidBrush(Color.FromArgb(90, DotColor)))
                    e.Graphics.FillEllipse(b, new Rectangle(r.X + 4, r.Y + 4, r.Width - 7, r.Height - 7));
            }
            else
            {
                using (var b = new SolidBrush(DotColor)) e.Graphics.FillEllipse(b, r);
            }
        }
    }

    // ========================================================================
    //  主窗体
    // ========================================================================
    public sealed class MainForm : Form
    {
        private readonly RoundInput txtAccount = new RoundInput();
        private readonly RoundInput txtPassword = new RoundInput();
        private readonly RoundInput txtPortal = new RoundInput();
        private readonly RoundInput txtPrefix = new RoundInput();
        private readonly RoundInput txtSuffix = new RoundInput();
        private readonly RoundInput txtInterface = new RoundInput();
        private readonly NumericUpDown numInterval = new NumericUpDown();
        private readonly CheckBox chkStartup = new CheckBox();
        private readonly Label lblStatus = new Label();
        private readonly Label lblPreview = new Label();
        private readonly Label lblLocalIp = new Label();
        private readonly Label lblDot = new Label();
        private readonly DotIcon statusDot = new DotIcon();
        private readonly PillButton btnTest = new PillButton();
        private readonly PillButton btnSave = new PillButton();
        private readonly PillButton btnStop = new PillButton();
        private readonly PillButton btnLog = new PillButton();
        private readonly PillButton btnDefault = new PillButton();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer();
        private readonly ToolTip toolTip = new ToolTip();

        private bool allowClose;
        private bool lastOnline;       // 缓存的联网状态，供界面同步渲染（避免 UI 线程发网络请求）
        private bool probeRunning;     // 是否有后台探活任务在跑

        internal MainForm(bool showWindow)
        {
            Text = "深澜校园网自动认证 · 景德镇艺术职业大学";
            ClientSize = new Size(720, 684);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Ui.Bg;
            Font = new Font("Microsoft YaHei UI", 9F);
            DoubleBuffered = true;

            BuildUi();
            LoadIntoUi();

            if (!showWindow)
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
            }

            statusTimer.Interval = 3000;
            statusTimer.Tick += delegate { RefreshStatus(); };
            statusTimer.Start();
            RefreshStatus();   // 启动后立刻探一次，不用干等 3 秒

            FormClosing += OnFormClosing;
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized) HideToTray();
            };
        }

        // ---------------------------------------------------------------- UI
        private void BuildUi()
        {
            // ---- 顶部标题区 ----
            var logo = new DotIcon
            {
                DotColor = Ui.Accent,
                Ring = true,
                Location = new Point(28, 22),
                Size = new Size(34, 34)
            };
            var title = new Label
            {
                Text = "校园网自动认证",
                Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold),
                ForeColor = Ui.Text,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(76, 20)
            };
            var sub = new Label
            {
                Text = "景德镇艺术职业大学 · 深澜 eportal 认证",
                Font = new Font("Microsoft YaHei UI", 9F),
                ForeColor = Ui.Muted,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(78, 56)
            };
            Controls.Add(logo);
            Controls.Add(title);
            Controls.Add(sub);

            // ---- 状态胶囊 ----
            var statusChip = new RoundedPanel
            {
                Location = new Point(506, 26),
                Size = new Size(188, 38),
                Radius = 19,
                FillColor = Ui.Ghost,
                BorderColor = Ui.CardBorder
            };
            statusDot.DotColor = Ui.Muted;
            statusDot.Ring = true;
            statusDot.Location = new Point(11, 9);
            statusDot.Size = new Size(20, 20);
            lblStatus.Text = "检测中";
            lblStatus.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            lblStatus.ForeColor = Ui.Text;
            lblStatus.BackColor = Color.Transparent;
            lblStatus.AutoSize = true;
            lblStatus.Location = new Point(36, 10);
            statusChip.Controls.Add(statusDot);
            statusChip.Controls.Add(lblStatus);
            Controls.Add(statusChip);

            // ---- 账号卡片 ----
            RoundedPanel accountCard = Card(26, 92, 668, 188);

            var accIcon = new DotIcon
            {
                DotColor = Ui.Accent,
                Location = new Point(22, 19),
                Size = new Size(18, 18)
            };
            accountCard.Controls.Add(accIcon);
            AddLabel(accountCard, "账号信息", 48, 20, true);

            AddLabel(accountCard, "账号", 24, 54);
            StyleInput(accountCard, txtAccount, 24, 76, 296);
            AddLabel(accountCard, "密码", 24, 120);
            StyleInput(accountCard, txtPassword, 24, 142, 296);
            txtPassword.UsePasswordChar = true;

            AddLabel(accountCard, "账号前缀", 348, 54);
            StyleInput(accountCard, txtPrefix, 348, 76, 128);
            AddLabel(accountCard, "账号后缀", 492, 54);
            StyleInput(accountCard, txtSuffix, 492, 76, 152);

            var previewChip = new RoundedPanel
            {
                Location = new Point(348, 118),
                Size = new Size(296, 52),
                Radius = 10,
                FillColor = Color.FromArgb(240, 250, 245),
                BorderColor = Color.FromArgb(200, 232, 218)
            };
            lblPreview.AutoSize = true;
            lblPreview.ForeColor = Ui.AccentDark;
            lblPreview.BackColor = Color.Transparent;
            lblPreview.Location = new Point(14, 8);
            lblPreview.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            previewChip.Controls.Add(lblPreview);
            var hint2 = new Label
            {
                Text = "只填裸账号，前后缀自动拼装",
                ForeColor = Ui.Muted,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(14, 29)
            };
            previewChip.Controls.Add(hint2);
            accountCard.Controls.Add(previewChip);

            txtAccount.TextChangedEvt += delegate { UpdatePreview(); };
            txtPrefix.TextChangedEvt += delegate { UpdatePreview(); };
            txtSuffix.TextChangedEvt += delegate { UpdatePreview(); };

            // ---- 网络卡片 ----
            RoundedPanel netCard = Card(26, 296, 668, 214);

            var netIcon = new DotIcon
            {
                DotColor = Ui.Ok,
                Location = new Point(22, 19),
                Size = new Size(18, 18)
            };
            netCard.Controls.Add(netIcon);
            AddLabel(netCard, "网络设置", 48, 20, true);

            AddLabel(netCard, "登录接口地址", 24, 54);
            StyleInput(netCard, txtPortal, 24, 76, 620);

            // 第二行：网卡关键字 + 内网 IP + 刷新
            AddLabel(netCard, "网卡关键字", 24, 124);
            StyleInput(netCard, txtInterface, 116, 120, 88);
            toolTip.SetToolTip(txtInterface,
                "留空 = 自动挑选真正联网的网卡（推荐）。\n" +
                "也可以填关键字精确指定：有线填 以太网，无线填 WLAN，\n" +
                "或填网卡品牌（如 Killer / Intel），WiFi 与有线都能命中。\n" +
                "VirtualBox、VMware、VPN 等虚拟网卡会被自动排除。");

            var ipHint = new Label
            {
                Text = "内网 IP",
                ForeColor = Ui.Muted,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(222, 125)
            };
            lblLocalIp.AutoSize = true;
            lblLocalIp.ForeColor = Ui.Ok;
            lblLocalIp.BackColor = Color.Transparent;
            lblLocalIp.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            lblLocalIp.Location = new Point(276, 125);
            netCard.Controls.Add(ipHint);
            netCard.Controls.Add(lblLocalIp);

            var refreshIp = new PillButton
            {
                Text = "刷新",
                Location = new Point(524, 118),
                Size = new Size(68, 32),
                Radius = 16,
                BaseColor = Ui.Ghost,
                HoverColor = Color.FromArgb(226, 240, 233),
                TextColor = Ui.AccentDark
            };
            refreshIp.Click += delegate { RefreshLocalIp(); };
            netCard.Controls.Add(refreshIp);

            // 第三行：检查间隔（独立一行，右下角）
            AddLabel(netCard, "检查间隔", 24, 170);
            var numChip = new RoundedPanel
            {
                Location = new Point(116, 165),
                Size = new Size(78, 32),
                Radius = 10,
                FillColor = Ui.InputBg,
                BorderColor = Ui.CardBorder
            };
            numInterval.Location = new Point(10, 7);
            numInterval.Size = new Size(58, 20);
            numInterval.BorderStyle = BorderStyle.None;
            numInterval.BackColor = Ui.InputBg;
            numInterval.ForeColor = Ui.Text;
            numInterval.Font = new Font("Microsoft YaHei UI", 9.5F);
            numInterval.Minimum = 10;
            numInterval.Maximum = 3600;
            numChip.Controls.Add(numInterval);
            netCard.Controls.Add(numChip);
            netCard.Controls.Add(new Label
            {
                Text = "秒（未联网时的重试间隔）",
                ForeColor = Ui.Muted,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(206, 171)
            });

            // ---- 底部操作区 ----
            chkStartup.Text = "开机自动启动，静默守护";
            chkStartup.ForeColor = Ui.Text;
            chkStartup.BackColor = Color.Transparent;
            chkStartup.Font = new Font("Microsoft YaHei UI", 9.5F);
            chkStartup.AutoSize = true;
            chkStartup.Location = new Point(30, 534);
            Controls.Add(chkStartup);

            btnTest.Text = "测试认证";
            btnTest.Location = new Point(26, 564);
            btnTest.Size = new Size(122, 44);
            btnTest.BaseColor = Ui.Accent;
            btnTest.HoverColor = Ui.AccentDark;
            btnTest.Radius = 22;
            btnTest.Click += delegate { RunTest(); };
            Controls.Add(btnTest);

            btnSave.Text = "保存并启用";
            btnSave.Location = new Point(160, 564);
            btnSave.Size = new Size(142, 44);
            btnSave.BaseColor = Ui.Ok;
            btnSave.HoverColor = Color.FromArgb(105, 182, 94);
            btnSave.Radius = 22;
            btnSave.Click += delegate { SaveAll(true); };
            Controls.Add(btnSave);

            btnStop.Text = "退出";
            btnStop.Location = new Point(314, 564);
            btnStop.Size = new Size(88, 44);
            btnStop.BaseColor = Ui.Ghost;
            btnStop.HoverColor = Color.FromArgb(226, 240, 233);
            btnStop.TextColor = Ui.Text;
            btnStop.Radius = 22;
            btnStop.Click += delegate { allowClose = true; Close(); };
            Controls.Add(btnStop);

            btnLog.Text = "查看日志";
            btnLog.Location = new Point(414, 564);
            btnLog.Size = new Size(108, 44);
            btnLog.BaseColor = Ui.Ghost;
            btnLog.HoverColor = Color.FromArgb(226, 240, 233);
            btnLog.TextColor = Ui.Text;
            btnLog.Radius = 22;
            btnLog.Click += delegate { OpenLog(); };
            Controls.Add(btnLog);

            btnDefault.Text = "恢复默认";
            btnDefault.Location = new Point(534, 564);
            btnDefault.Size = new Size(110, 44);
            btnDefault.BaseColor = Ui.Ghost;
            btnDefault.HoverColor = Color.FromArgb(226, 240, 233);
            btnDefault.TextColor = Ui.Text;
            btnDefault.Radius = 22;
            btnDefault.Click += delegate { LoadConfigIntoUi(AppConfig.Default()); };
            Controls.Add(btnDefault);

            var footer = new Label
            {
                Text = "认证成功后自动停止轮询，安静待在托盘里 · 双击托盘图标可唤回窗口",
                ForeColor = Ui.Muted,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(28, 626)
            };
            Controls.Add(footer);

            // 托盘
            tray.Text = "深澜校园网自动认证";
            tray.Visible = true;
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { tray.Icon = SystemIcons.Application; }
            tray.DoubleClick += delegate { RestoreFromTray(); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示窗口", null, delegate { RestoreFromTray(); });
            menu.Items.Add("立即认证", null, delegate { RunTest(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { allowClose = true; Close(); });
            tray.ContextMenuStrip = menu;
        }

        private RoundedPanel Card(int x, int y, int w, int h)
        {
            var p = new RoundedPanel
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                Radius = 16,
                FillColor = Ui.Card,
                BorderColor = Ui.CardBorder
            };
            Controls.Add(p);
            return p;
        }

        private void AddLabel(Control parent, string text, int x, int y)
        {
            AddLabel(parent, text, x, y, false);
        }

        private void AddLabel(Control parent, string text, int x, int y, bool bold)
        {
            parent.Controls.Add(new Label
            {
                Text = text,
                ForeColor = bold ? Ui.Text : Ui.Muted,
                BackColor = Color.Transparent,
                AutoSize = true,
                Font = bold
                    ? new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold)
                    : new Font("Microsoft YaHei UI", 9F),
                Location = new Point(x, y)
            });
        }

        /// <summary>
        /// 把圆角输入框挂到指定父容器。
        /// 注意：必须显式传 parent —— 控件在 new 阶段 Parent 为 null，
        /// 早前版本用 (box.Parent ?? this) 导致全部落到窗体上、坐标错乱。
        /// </summary>
        private void StyleInput(Control parent, RoundInput input, int x, int y, int w)
        {
            input.Location = new Point(x, y);
            input.Size = new Size(w, 32);
            parent.Controls.Add(input);
        }

        // ------------------------------------------------------------ 数据绑定
        private void LoadIntoUi()
        {
            LoadConfigIntoUi(AppData.LoadConfig());
            string[] cred = AppData.LoadCredential();
            if (cred != null)
            {
                txtAccount.Value = cred[0];
                txtPassword.Value = cred[1];
            }
            chkStartup.Checked = StartupManager.IsEnabled();
        }

        private void LoadConfigIntoUi(AppConfig cfg)
        {
            txtPortal.Value = cfg.PortalUrl;
            txtPrefix.Value = cfg.AccountPrefix;
            txtSuffix.Value = cfg.AccountSuffix;
            txtInterface.Value = cfg.InterfaceKeyword;
            numInterval.Value = Math.Max(numInterval.Minimum, Math.Min(numInterval.Maximum, cfg.CheckIntervalSeconds));
            UpdatePreview();
            RefreshLocalIp();
        }

        private AppConfig ReadConfigFromUi()
        {
            AppConfig cfg = AppData.LoadConfig();
            cfg.PortalUrl = txtPortal.Value.Trim();
            cfg.AccountPrefix = txtPrefix.Value;
            cfg.AccountSuffix = txtSuffix.Value;
            cfg.InterfaceKeyword = txtInterface.Value.Trim();
            cfg.CheckIntervalSeconds = (int)numInterval.Value;
            return cfg;
        }

        private void UpdatePreview()
        {
            string bare = txtAccount.Value.Trim();
            if (bare.Length == 0) { lblPreview.Text = ""; return; }
            lblPreview.Text = "提交为：" + (txtPrefix.Value + bare + txtSuffix.Value);
        }

        private void RefreshLocalIp()
        {
            AppConfig cfg = AppData.LoadConfig();
            cfg.InterfaceKeyword = txtInterface.Value.Trim();

            string adapterName;
            string ip = SrunClient.GetLocalIPv4(cfg, out adapterName);

            if (string.IsNullOrEmpty(ip))
            {
                lblLocalIp.Text = "未找到（请检查网卡关键字）";
                lblLocalIp.ForeColor = Ui.Err;
                toolTip.SetToolTip(lblLocalIp, "没有找到任何可用于认证的内网 IPv4 地址。\n请确认已连上校园网，或调整「网卡关键字」。");
                return;
            }

            lblLocalIp.Text = string.IsNullOrEmpty(adapterName) ? ip : ip + "（" + adapterName + "）";
            lblLocalIp.ForeColor = Ui.Ok;
            toolTip.SetToolTip(lblLocalIp,
                "认证时将提交这个地址。\n所用网卡：" + (string.IsNullOrEmpty(adapterName) ? "未知" : adapterName));
        }

        private void RefreshStatus()
        {
            // 先用缓存值刷新显示，保证界面永不阻塞；
            // 真正的联网检测放到后台线程，回来后覆盖显示。
            ApplyStatus(lastOnline);

            if (probeRunning) return;
            probeRunning = true;

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool online = false;
                try { online = SrunClient.HasInternet(AppData.LoadConfig()); }
                catch { }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        probeRunning = false;
                        lastOnline = online;
                        ApplyStatus(online);
                    });
                }
                catch { probeRunning = false; }
            });
        }

        private void ApplyStatus(bool online)
        {
            DateTime last = File.Exists(AppData.LogPath) ? File.GetLastWriteTime(AppData.LogPath) : DateTime.MinValue;
            string lastTxt = last == DateTime.MinValue ? "无记录" : last.ToString("HH:mm");

            lblStatus.Text = online ? "网络已连通" : "待认证";
            statusDot.DotColor = online ? Ui.Ok : Ui.Warn;
            statusDot.Ring = !online;
            statusDot.Invalidate();   // 圆点改了颜色/样式要主动重绘，否则不会实时刷新
            lblStatus.ForeColor = online ? Ui.Ok : Ui.Text;
            lblStatus.Tag = lastTxt;
            toolTip.SetToolTip(lblStatus,
                string.Format("最近活动 {0} · 开机启动 {1}",
                    lastTxt, StartupManager.IsEnabled() ? "已开启" : "未开启"));
        }

        // ------------------------------------------------------------ 动作
        private void SaveAll(bool enableStartup)
        {
            SaveAll(enableStartup, false);
        }

        private void SaveAll(bool enableStartup, bool quiet)
        {
            AppConfig cfg = ReadConfigFromUi();

            // 地址防呆：把手填的门户首页还原成登录接口，并让用户看到纠正结果
            string portalNote;
            string portalFixed = SrunClient.NormalizePortalUrl(cfg.PortalUrl, out portalNote);
            if (portalNote != null)
            {
                cfg.PortalUrl = portalFixed;
                txtPortal.Value = portalFixed;
                AppData.Log(portalNote);
            }

            if (string.IsNullOrWhiteSpace(cfg.PortalUrl))
            {
                MessageBox.Show("登录接口地址不能为空。", "提示");
                return;
            }
            if (string.IsNullOrWhiteSpace(txtAccount.Value))
            {
                MessageBox.Show("请填写账号。", "提示");
                return;
            }

            AppData.SaveConfig(cfg);
            AppData.SaveCredential(txtAccount.Value.Trim(), txtPassword.Value);

            if (enableStartup)
            {
                if (chkStartup.Checked) StartupManager.Enable();
                else StartupManager.Disable();
            }
            RefreshStatus();

            string ok = "已保存。连接校园网后会在后台自动认证。";
            string msg = portalNote == null ? ok : (portalNote + "\r\n\r\n" + ok);
            if (!quiet || portalNote != null) MessageBox.Show(msg, "完成");
        }

        private void RunTest()
        {
            if (string.IsNullOrWhiteSpace(txtAccount.Value))
            {
                MessageBox.Show("请先填写账号。", "提示");
                return;
            }
            SaveAll(false, true);   // 测试时静默保存，避免多弹一个「已保存」窗口

            btnTest.Enabled = false;
            btnTest.Text = "测试中…";
            lblStatus.Text = "认证中";
            statusDot.DotColor = Ui.Accent;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string result = SrunClient.TryLogin(AppData.LoadConfig());
                BeginInvoke((MethodInvoker)delegate
                {
                    btnTest.Enabled = true;
                    btnTest.Text = "测试认证";
                    RefreshStatus();
                    MessageBox.Show(result, "测试结果");
                });
            });
        }

        private void OpenLog()
        {
            try
            {
                if (!File.Exists(AppData.LogPath))
                {
                    MessageBox.Show("暂无日志。", "提示");
                    return;
                }
                System.Diagnostics.Process.Start("notepad.exe", "\"" + AppData.LogPath + "\"");
            }
            catch (Exception ex) { MessageBox.Show("打开日志失败：" + ex.Message, "提示"); }
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            tray.ShowBalloonTip(1500, "深澜校园网自动认证", "程序已最小化到托盘，后台继续运行。", ToolTipIcon.Info);
        }

        private void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!allowClose)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            tray.Visible = false;
            BackgroundHost.RequestStop();
        }
    }

    // ========================================================================
    //  入口
    // ========================================================================
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 单实例
            bool created;
            using (var mutex = new Mutex(true, AppData.MutexName + ".UI", out created))
            {
                if (!created) return;

                bool background = false;
                foreach (string a in args)
                    if (a.Equals("--background", StringComparison.OrdinalIgnoreCase)) background = true;

                bool startHidden = background || !AppData.HasCredential();

                // 后台轮询线程
                var worker = new Thread(BackgroundHost.Run) { IsBackground = true };
                worker.Start();

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(!background));
            }
        }
    }
}
