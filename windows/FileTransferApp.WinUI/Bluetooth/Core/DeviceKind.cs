using Windows.Devices.Bluetooth;

namespace FileTransferApp.WinUI.Bluetooth.Core;

/// <summary>
/// 统一的设备类型分类器（唯一实现，两端词表对齐 Android 端 determineDeviceType）。
/// 四信号表决：① 名字关键词（高）② CoD 主类（中，兜底）③ BlueSelf 服务命中（高）
/// ④ 链路自报姓名 sName（高，事后校准用）。
/// </summary>
internal static class DeviceKind
{
    public const string Pc = "pc";
    public const string Tablet = "tablet";
    public const string Phone = "phone";
    public const string Other = "other";

    // ---- 词表（与 Android BluetoothManager.determineDeviceType 完全对齐，另补中文词） ----
    private static readonly string[] PcWords =
        { "pc", "windows", "mac", "laptop", "notebook", "desktop", "电脑", "台式", "笔电", "笔记本" };
    private static readonly string[] TabletWords =
        { "pad", "tablet", "tab", "ipad", "平板" };
    private static readonly string[] PhoneWords =
        { "phone", "galaxy", "pixel", "iphone", "xiaomi", "手机", "一加", "oppo", "vivo", "华为", "honor", "荣耀" };

    private static bool Matches(string? name, string[] words)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var lower = name.ToLowerInvariant();
        foreach (var w in words)
            if (lower.Contains(w)) return true;
        return false;
    }

    public static bool IsPcName(string? name) => Matches(name, PcWords);
    public static bool IsTableName(string? name) => Matches(name, TabletWords);
    public static bool IsPhoneName(string? name) => Matches(name, PhoneWords);

    /// <summary>名字关键词命中即视为 BlueSelf 对端候选（手机/电脑/平板），用于扫描过滤。</summary>
    public static bool IsPeerNameHint(string? name)
        => Matches(name, PcWords) || Matches(name, TabletWords) || Matches(name, PhoneWords);

    /// <summary>
    /// 四信号表决。tablet 词表最具体（pad/tab 不会误伤 PC 词），先判 tablet 再判 pc；
    /// CoD 仅在「Computer + 有服务」或「Phone + 有服务」时参与定论（Realtek/Qualcomm 上报不可靠，不单独定论）。
    /// </summary>
    public static string Classify(string? name, BluetoothMajorClass majorClass, bool hasBlueSelfService)
    {
        if (IsTableName(name)) return Tablet;
        if (IsPcName(name)) return Pc;
        if (IsPhoneName(name)) return Phone;

        if (hasBlueSelfService)
        {
            if (majorClass == BluetoothMajorClass.Computer) return Pc;
            if (majorClass == BluetoothMajorClass.Phone) return Phone;
        }
        return Other;
    }

    /// <summary>sName 事后校准专用：只信链路自报姓名（PC 发来的 TXT 帧一定来自 PC 端 App）。</summary>
    public static string ClassifyFromSenderName(string? senderName)
    {
        if (IsTableName(senderName)) return Tablet;
        if (IsPcName(senderName)) return Pc;
        if (IsPhoneName(senderName)) return Phone;
        return Other;
    }
}
