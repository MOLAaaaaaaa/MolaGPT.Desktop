using System.Text.RegularExpressions;

namespace MolaGPT.Core.Chat.Tools.Browser;

/// <summary>
/// 这条链路上唯一的闸门。
///
/// WebBridge 机制本身不设防：扩展持 <c>&lt;all_urls&gt;</c>，daemon 收到什么执行
/// 什么，不区分「读一个页面」和「提交一笔订单」。它跑在用户的主 profile 上，带着
/// 全部真实登录态——业界给本地桥开的方子是让 agent 用独立 profile 缩小 blast
/// radius，而那正好会废掉我们唯一的卖点。既然不做隔离，闸门就得细。
///
/// 分两层，边界是「用户能不能有意义地同意」：
///
/// <list type="bullet">
/// <item><b>红线</b>（<see cref="RefusalFor"/>）——直接拒绝，不给同意的机会。
/// 卡号、证件号这类东西没有「让模型代填一次」的正确答案。</item>
/// <item><b>受保护动作</b>（<see cref="ProtectedReason"/>）——站点级授权不覆盖它。
/// 「始终允许 example.com」授的是「在这个站点上操作」，不是「在这个站点上什么都
/// 行」；下载、授权页、结账页要单独再问一次。</item>
/// </list>
///
/// 判定只用我们确定看得见的东西：URL 和待填入的值。页面上那个按钮写着什么，我们
/// 看不到，所以这里不猜——猜出来的闸门比没有闸门更坏，因为它会让人以为有。
/// </summary>
public static partial class BrowserGuard
{
    /// <summary>
    /// 红线。命中就拒绝，不进审批流。
    ///
    /// 查所有会把一串字带进页面的动作：密码和验证码从值本身认不出来（协议里靠提示
    /// 约束），但卡号和身份证号有校验位，可以确定性地认出来。
    ///
    /// <c>send_keys</c> 传的是键名（Enter / Mod+A），照理不该出现卡号——列进来是因为
    /// 漏一个通道的代价，远大于在键名上多跑两个正则。
    /// </summary>
    public static string? RefusalFor(string? action, string? value)
    {
        if (action is not ("fill" or "select_option" or "send_keys")) return null;
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (ContainsPaymentCard(value))
            return "已拒绝：待填入的内容看起来是银行卡号。支付信息只能由用户本人在浏览器里输入——"
                   + "请截图说明进行到哪一步，然后把这一步交给用户。";

        if (ContainsChineseId(value))
            return "已拒绝：待填入的内容看起来是身份证号。证件信息只能由用户本人在浏览器里输入——"
                   + "请说明需要填哪个字段，然后把这一步交给用户。";

        return null;
    }

    /// <summary>
    /// 受保护动作的理由，<c>null</c> 表示不受保护。
    ///
    /// 返回值会直接出现在审批弹窗里，所以写成一句能读的话，而不是一个枚举名。
    /// </summary>
    public static string? ProtectedReason(
        string? action,
        string? url,
        string? host,
        BrowserControlOptions options)
    {
        // 敏感网站名单：站点级的「每次都问」，对应 Claude in Chrome 里那批
        // 「无论什么模式都逐动作审批」的站点。读操作不算——列一下标签、看一眼
        // 页面结构不该弹窗，那样只会训练用户闭眼点确认。
        if (BrowserControlTool.ClassifyAction(action) != BrowserActionKind.Read
            && HostRules.Matches(options.SensitiveHostList, host))
        {
            return "该网站在敏感名单中";
        }

        if (!string.Equals(action, "navigate", StringComparison.Ordinal)) return null;
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;

        if (LooksLikeDownload(uri)) return "这是一个文件下载链接";
        if (LooksLikeAuthorization(uri)) return "这是一个授权确认页面";
        if (LooksLikeCheckout(uri)) return "这是一个支付或结账页面";
        return null;
    }

    // ---- detectors ---------------------------------------------------------

    /// <summary>
    /// 卡号：13–19 位、Luhn 通过、且首位是 3–6（Visa/万事达/运通/发现/银联都在这个
    /// 区间）。只有 Luhn 的话，随机 16 位数字有约十分之一会误判成卡号，订单号首当其冲；
    /// 加上首位约束把误判压到可接受的范围。
    /// </summary>
    private static bool ContainsPaymentCard(string value)
    {
        foreach (Match match in DigitRun().Matches(value))
        {
            var digits = match.Value.Where(char.IsAsciiDigit).ToArray();
            if (digits.Length is < 13 or > 19) continue;
            if (digits[0] is < '3' or > '6') continue;
            if (PassesLuhn(digits)) return true;
        }
        return false;
    }

    private static bool PassesLuhn(IReadOnlyList<char> digits)
    {
        var sum = 0;
        var doubling = false;
        for (var i = digits.Count - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubling)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            doubling = !doubling;
        }
        return sum % 10 == 0;
    }

    /// <summary>身份证：18 位，最后一位是 ISO 7064 MOD 11-2 校验位。带校验位，几乎不会误判。</summary>
    private static bool ContainsChineseId(string value)
    {
        foreach (Match match in IdCandidate().Matches(value))
        {
            var text = match.Value.ToUpperInvariant();
            if (text.Length != 18) continue;

            var weights = new[] { 7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2 };
            var check = "10X98765432";
            var sum = 0;
            var ok = true;
            for (var i = 0; i < 17; i++)
            {
                if (!char.IsAsciiDigit(text[i])) { ok = false; break; }
                sum += (text[i] - '0') * weights[i];
            }
            if (ok && check[sum % 11] == text[17]) return true;
        }
        return false;
    }

    private static readonly string[] DownloadExtensions =
    [
        ".exe", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".scr", ".com",
        ".dmg", ".pkg", ".app", ".deb", ".rpm", ".apk", ".jar",
        ".zip", ".rar", ".7z", ".tar", ".gz", ".iso"
    ];

    private static bool LooksLikeDownload(Uri uri)
    {
        var path = uri.AbsolutePath;
        var dot = path.LastIndexOf('.');
        if (dot < 0) return false;
        var ext = path[dot..].ToLowerInvariant();
        return DownloadExtensions.Contains(ext);
    }

    private static readonly string[] AuthorizationMarkers =
    [
        "/oauth", "/authorize", "/consent", "/sso/", "/saml", "/connect/authorize", "/grant"
    ];

    private static bool LooksLikeAuthorization(Uri uri) => MatchesMarker(uri, AuthorizationMarkers);

    private static readonly string[] CheckoutMarkers =
    [
        "/checkout", "/payment", "/cashier", "/billing", "/pay/", "/purchase", "/placeorder", "/order/submit"
    ];

    private static bool LooksLikeCheckout(Uri uri) => MatchesMarker(uri, CheckoutMarkers);

    private static bool MatchesMarker(Uri uri, string[] markers)
    {
        // 路径和查询串一起看：不少站点把 /authorize 放在 query 里（?redirect=…/oauth/authorize）。
        var target = (uri.AbsolutePath + "/" + uri.Query).ToLowerInvariant();
        return markers.Any(marker => target.Contains(marker, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"[0-9][0-9 \-]{11,26}[0-9]")]
    private static partial Regex DigitRun();

    [GeneratedRegex(@"[0-9]{17}[0-9Xx]")]
    private static partial Regex IdCandidate();
}
