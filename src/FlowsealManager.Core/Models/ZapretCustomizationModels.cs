namespace FlowsealManager.Core.Models;

public enum GameFilterMode
{
    Disabled,
    TcpAndUdp,
    TcpOnly,
    UdpOnly
}

public enum IpSetMode
{
    OfficialList,
    NoIpRanges,
    AnyIp
}

public sealed record ZapretCustomization(
    GameFilterMode GameFilterMode,
    IpSetMode IpSetMode,
    string IncludedDomains,
    string ExcludedDomains,
    string IncludedIpRanges,
    string ExcludedIpRanges,
    string? DiscordFakeFile,
    string? GameFakeFile);

public sealed record ZapretFakeOptions(
    IReadOnlyList<string> AvailableFiles,
    string? ActiveDiscordFile,
    string? ActiveGameFile);

public static class ZapretCustomizationLabels
{
    public static string GameFilter(GameFilterMode mode) => mode switch
    {
        GameFilterMode.Disabled => "игровой фильтр выключен",
        GameFilterMode.TcpAndUdp => "игровой фильтр TCP+UDP",
        GameFilterMode.TcpOnly => "игровой фильтр TCP",
        GameFilterMode.UdpOnly => "игровой фильтр UDP",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    public static string IpSet(IpSetMode mode) => mode switch
    {
        IpSetMode.OfficialList => "официальный IP-set",
        IpSetMode.NoIpRanges => "без IP-диапазонов",
        IpSetMode.AnyIp => "любой IP",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    public static string Summary(ZapretCustomization customization) =>
        $"{IpSet(customization.IpSetMode)}, {GameFilter(customization.GameFilterMode)}";
}
