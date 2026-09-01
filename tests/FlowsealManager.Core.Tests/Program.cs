using System.IO.Compression;
using FlowsealManager.Core.Infrastructure;
using FlowsealManager.Core.Models;
using FlowsealManager.Core.Services;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("SafeVersion rejects traversal", TestSafeVersionAsync),
    ("Release asset selection is exact", TestAssetSelectionAsync),
    ("Empty reports are unhealthy", TestEmptyReportAsync),
    ("Healthy reports require both services", TestHealthReportAsync),
    ("Coverage score prioritizes essential endpoints", TestCoverageScoreAsync),
    ("Parameter tuning covers the official mode matrix", TestParameterCandidatesAsync),
    ("Stable partial coverage wins over an unstable first pass", TestStableWinnerAsync),
    ("ZIP extraction rejects traversal", TestZipTraversalAsync),
    ("Settings persist atomically", TestSettingsStoreAsync),
    ("Startup task quotes executable", TestStartupArgumentsAsync),
    ("Zapret strategy expands into direct arguments", TestStrategyParserAsync),
    ("Strategies use natural order", TestStrategyOrderAsync),
    ("Telegram loader and worker count as one instance", TestTelegramProcessTreesAsync),
    ("Only WinDivert driver services are transient", TestTransientDriverNamesAsync),
    ("Zapret user lists validate and normalize", TestZapretUserListValidationAsync),
    ("Zapret customization persists official modes", TestZapretCustomizationAsync),
    ("Zapret hosts preserve unrelated entries", TestZapretHostsAsync)
};

if (args.Contains("--integration", StringComparer.OrdinalIgnoreCase))
{
    tests.Add(("Official releases download and validate", TestOfficialReleasesAsync));
}

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Count - failures.Count}/{tests.Count} tests passed");
if (failures.Count > 0)
{
    Environment.ExitCode = 1;
}

return;

static Task TestSafeVersionAsync()
{
    AssertEqual("v1.10.0", AppPaths.SafeVersion("v1.10.0"));
    AssertThrows<ArgumentException>(() => AppPaths.SafeVersion(".."));
    AssertThrows<ArgumentException>(() => AppPaths.SafeVersion(".. "));
    AssertThrows<ArgumentException>(() => AppPaths.SafeVersion("v1/../../bad"));
    return Task.CompletedTask;
}

static Task TestTransientDriverNamesAsync()
{
    AssertTrue(LegacyServiceManager.IsTransientWinDivert("WinDivert"));
    AssertTrue(LegacyServiceManager.IsTransientWinDivert("WinDivert14"));
    AssertFalse(LegacyServiceManager.IsTransientWinDivert("zapret"));
    AssertFalse(LegacyServiceManager.IsTransientWinDivert("GoodbyeDPI"));
    return Task.CompletedTask;
}

static Task TestTelegramProcessTreesAsync()
{
    AssertEqual(1, ComponentProcessManager.CountProcessTrees([(100, 1), (101, 100)]));
    AssertEqual(2, ComponentProcessManager.CountProcessTrees([(100, 1), (101, 100), (200, 1), (201, 200)]));
    AssertEqual(1, ComponentProcessManager.CountProcessTrees([(100, 1)]));
    AssertEqual(0, ComponentProcessManager.CountProcessTrees([]));
    return Task.CompletedTask;
}

static Task TestAssetSelectionAsync()
{
    var release = new GitHubRelease
    {
        TagName = "1.10.1",
        Assets =
        [
            new GitHubAsset { Name = "zapret-discord-youtube-1.10.1.rar" },
            new GitHubAsset { Name = "zapret-discord-youtube-1.10.1.zip", DownloadUrl = "https://example.invalid/z.zip" }
        ]
    };
    var asset = GitHubReleaseClient.SelectAsset(ComponentKind.Zapret, release);
    AssertEqual("zapret-discord-youtube-1.10.1.zip", asset.Name);
    return Task.CompletedTask;
}

static Task TestEmptyReportAsync()
{
    var report = new HealthReport(DateTimeOffset.UtcNow, []);
    AssertFalse(report.YouTubeAvailable);
    AssertFalse(report.DiscordAvailable);
    AssertFalse(report.AllRequiredAvailable);
    return Task.CompletedTask;
}

static Task TestHealthReportAsync()
{
    var report = new HealthReport(DateTimeOffset.UtcNow,
    [
        new ProbeResult("youtube", ServiceKind.YouTube, true, TimeSpan.Zero, "ok"),
        new ProbeResult("discord", ServiceKind.Discord, true, TimeSpan.Zero, "ok")
    ]);
    AssertTrue(report.AllRequiredAvailable);
    return Task.CompletedTask;
}

static Task TestCoverageScoreAsync()
{
    var report = CreateReport("Discord API", "Discord Gateway WebSocket", "YouTube thumbnails");
    AssertEqual(11, report.CoverageScore);
    AssertEqual(22, report.MaximumCoverageScore);
    AssertTrue(HealthReport.ProbeWeight("Discord API") > HealthReport.ProbeWeight("YouTube thumbnails"));
    return Task.CompletedTask;
}

static Task TestParameterCandidatesAsync()
{
    var current = CreateCustomization(GameFilterMode.TcpOnly, IpSetMode.NoIpRanges);
    var candidates = StrategySelector.BuildParameterCandidates(current);
    AssertEqual(12, candidates.Count);
    AssertEqual(12, candidates.Select(candidate => (candidate.GameFilterMode, candidate.IpSetMode)).Distinct().Count());
    AssertTrue(candidates.All(candidate => candidate.IncludedDomains == current.IncludedDomains));
    AssertTrue(candidates.All(candidate => candidate.IncludedIpRanges == current.IncludedIpRanges));
    AssertTrue(candidates.All(candidate => candidate.DiscordFakeFile == current.DiscordFakeFile));
    AssertTrue(candidates.All(candidate => candidate.GameFakeFile == current.GameFakeFile));
    return Task.CompletedTask;
}

static Task TestStableWinnerAsync()
{
    var customization = CreateCustomization(GameFilterMode.Disabled, IpSetMode.OfficialList);
    var unstable = new StrategyEvaluation(
        "general.bat",
        customization,
        CreateReport(
            "YouTube",
            "YouTube thumbnails",
            "Googlevideo edge",
            "Discord API",
            "Discord CDN",
            "Discord Gateway WebSocket"),
        CreateReport("Discord API", "Discord Gateway WebSocket"));
    var stable = new StrategyEvaluation(
        "general (ALT2).bat",
        customization,
        CreateReport("YouTube", "Googlevideo edge", "Discord API", "Discord CDN", "Discord Gateway WebSocket"),
        CreateReport("YouTube", "Googlevideo edge", "Discord API", "Discord CDN", "Discord Gateway WebSocket"));

    var winner = StrategySelector.ChooseBest([unstable, stable], "general.bat", customization);
    AssertEqual("general (ALT2).bat", winner?.Strategy);
    AssertEqual(21, winner?.StableCoverageScore);
    return Task.CompletedTask;
}

static ZapretCustomization CreateCustomization(GameFilterMode gameFilterMode, IpSetMode ipSetMode) =>
    new(
        gameFilterMode,
        ipSetMode,
        "example.org",
        "static.example.org",
        "198.51.100.0/24",
        "203.0.113.8",
        "stun.bin",
        "quic_initial_www_google_com.bin");

static HealthReport CreateReport(params string[] successfulNames)
{
    var successful = successfulNames.ToHashSet(StringComparer.Ordinal);
    return new HealthReport(
        DateTimeOffset.UtcNow,
        new[]
        {
            ("YouTube", ServiceKind.YouTube),
            ("YouTube thumbnails", ServiceKind.YouTube),
            ("Googlevideo edge", ServiceKind.YouTube),
            ("Discord API", ServiceKind.Discord),
            ("Discord CDN", ServiceKind.Discord),
            ("Discord Gateway WebSocket", ServiceKind.Discord)
        }.Select(check => new ProbeResult(
            check.Item1,
            check.Item2,
            successful.Contains(check.Item1),
            TimeSpan.FromMilliseconds(10),
            successful.Contains(check.Item1) ? "OK" : "недоступно")).ToArray());
}

static Task TestZipTraversalAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"flowseal-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var zipPath = Path.Combine(root, "bad.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../escaped.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("bad");
        }

        AssertThrows<InvalidDataException>(() =>
            ComponentUpdater.ExtractSafely(zipPath, Path.Combine(root, "extract")));
        AssertFalse(File.Exists(Path.Combine(root, "escaped.txt")));
    }
    finally
    {
        Directory.Delete(root, true);
    }

    return Task.CompletedTask;
}

static async Task TestSettingsStoreAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"flowseal-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var store = new SettingsStore(Path.Combine(root, "settings.json"));
        await store.SaveAsync(new AppSettings
        {
            SelectedStrategy = "general (ALT10).bat",
            SelectedCoverageScore = 17
        });
        var settings = await store.LoadAsync();
        AssertEqual("general (ALT10).bat", settings.SelectedStrategy);
        AssertEqual(17, settings.SelectedCoverageScore);
        AssertEqual(0, Directory.EnumerateFiles(root, "*.tmp").Count());
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static Task TestStartupArgumentsAsync()
{
    var executable = Path.Combine(Path.GetTempPath(), "Flowseal Manager", "FlowsealManager.exe");
    var arguments = StartupTaskManager.BuildCreateArguments(executable);
    AssertTrue(arguments.Contains("/RL"));
    AssertTrue(arguments.Contains("HIGHEST"));
    AssertTrue(arguments.Any(argument => argument.Contains("--minimized", StringComparison.Ordinal)));
    AssertTrue(arguments.Any(argument => argument.StartsWith('"') && argument.EndsWith("\" --minimized", StringComparison.Ordinal)));
    return Task.CompletedTask;
}

static async Task TestStrategyParserAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"flowseal parser test {Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "lists"));
        Directory.CreateDirectory(Path.Combine(root, "utils"));
        await File.WriteAllBytesAsync(Path.Combine(root, "bin", "winws.exe"), [0x4D, 0x5A]);
        await File.WriteAllTextAsync(Path.Combine(root, "utils", "game_filter.enabled"), "tcp");
        await File.WriteAllTextAsync(Path.Combine(root, "lists", "ipset-general-user.txt"), "198.51.100.0/24");
        var batch = Path.Combine(root, "general test.bat");
        await File.WriteAllTextAsync(
            batch,
            "start \"zapret\" /min \"%BIN%winws.exe\" --wf-tcp=443,%GameFilterTCP% ^\r\n" +
            "--ipset=\"%LISTS%ipset-all.txt\" --hostlist=\"%LISTS%list-general.txt\" --wf-udp=%GameFilterUDP%\r\n");

        var launch = ZapretStrategyParser.Parse(batch);
        AssertEqual(Path.Combine(root, "bin", "winws.exe"), launch.Executable);
        AssertEqual("--wf-tcp=443,1024-65535", launch.Arguments[0]);
        AssertEqual("--ipset=" + Path.Combine(root, "lists", "ipset-all.txt"), launch.Arguments[1]);
        AssertEqual("--ipset=" + Path.Combine(root, "lists", "ipset-general-user.txt"), launch.Arguments[2]);
        AssertEqual("--hostlist=" + Path.Combine(root, "lists", "list-general.txt"), launch.Arguments[3]);
        AssertEqual("--wf-udp=12", launch.Arguments[4]);
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static Task TestZapretUserListValidationAsync()
{
    var domains = ZapretCustomizationService.NormalizeDomains("Example.ORG\nпример.рф\nexample.org");
    AssertTrue(domains.Contains("example.org", StringComparison.Ordinal));
    AssertTrue(domains.Contains("xn--e1afmkfd.xn--p1ai", StringComparison.Ordinal));
    AssertEqual(1, domains.Split("example.org", StringSplitOptions.None).Length - 1);
    AssertThrows<InvalidDataException>(() => ZapretCustomizationService.NormalizeDomains("https://example.org/path"));

    var ranges = ZapretCustomizationService.NormalizeIpRanges("198.51.100.1\n2001:db8::/32");
    AssertTrue(ranges.Contains("198.51.100.1", StringComparison.Ordinal));
    AssertTrue(ranges.Contains("2001:db8::/32", StringComparison.Ordinal));
    AssertThrows<InvalidDataException>(() => ZapretCustomizationService.NormalizeIpRanges("198.51.100.1/44"));
    return Task.CompletedTask;
}

static async Task TestZapretCustomizationAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"flowseal-customization-{Guid.NewGuid():N}");
    Directory.CreateDirectory(Path.Combine(root, "lists"));
    Directory.CreateDirectory(Path.Combine(root, "bin"));
    try
    {
        await File.WriteAllTextAsync(Path.Combine(root, "lists", "ipset-all.txt"), "192.0.2.0/24\n");
        await File.WriteAllBytesAsync(Path.Combine(root, "bin", "stun.bin"), [1, 2, 3]);
        var service = new ZapretCustomizationService();
        await service.SaveAsync(root, new ZapretCustomization(
            GameFilterMode.TcpOnly,
            IpSetMode.AnyIp,
            "example.org",
            "static.example.org",
            "198.51.100.0/24",
            "203.0.113.8",
            "stun.bin",
            "stun.bin"));

        var loaded = service.Load(root);
        AssertEqual(GameFilterMode.TcpOnly, loaded.GameFilterMode);
        AssertEqual(IpSetMode.AnyIp, loaded.IpSetMode);
        AssertEqual("example.org", loaded.IncludedDomains);
        AssertEqual("198.51.100.0/24", loaded.IncludedIpRanges);
        AssertEqual("stun.bin", loaded.DiscordFakeFile);
        AssertEqual(0L, new FileInfo(Path.Combine(root, "lists", "ipset-all.txt")).Length);

        await service.SaveAsync(root, loaded with { IpSetMode = IpSetMode.OfficialList });
        AssertTrue(File.ReadAllText(Path.Combine(root, "lists", "ipset-all.txt"))
            .Contains("192.0.2.0/24", StringComparison.Ordinal));
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static async Task TestZapretHostsAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"flowseal-hosts-{Guid.NewGuid():N}");
    var hosts = Path.Combine(root, "etc", "hosts");
    var backups = Path.Combine(root, "backups");
    Directory.CreateDirectory(Path.GetDirectoryName(hosts)!);
    var original = "127.0.0.1 localhost\r\n203.0.113.7 custom.example\r\n";
    await File.WriteAllTextAsync(hosts, original, new System.Text.UTF8Encoding(false));
    try
    {
        using var client = new HttpClient();
        var service = new ZapretHostsService(client, backups, hosts);
        var official = CreateOfficialHosts();
        AssertFalse(service.Inspect().IsInstalled);

        var installed = await service.InstallOrUpdateAsync(official);
        AssertTrue(installed.Status.IsInstalled);
        AssertTrue(installed.Status.IsCurrent == true);
        AssertTrue(File.Exists(installed.BackupPath));
        var installedText = await File.ReadAllTextAsync(hosts);
        AssertTrue(installedText.StartsWith(original, StringComparison.Ordinal));
        AssertEqual(1, installedText.Split(ZapretHostsService.BeginMarker).Length - 1);

        const string trailingCustomEntry = "198.51.100.8 after.example\r\n";
        await File.AppendAllTextAsync(hosts, trailingCustomEntry);
        var updatedOfficial = official + "\n149.154.167.220 telegram.org";
        var updated = await service.InstallOrUpdateAsync(updatedOfficial);
        AssertTrue(updated.Status.IsCurrent == true);
        var updatedText = await File.ReadAllTextAsync(hosts);
        AssertTrue(updatedText.Contains("203.0.113.7 custom.example", StringComparison.Ordinal));
        AssertTrue(updatedText.Contains(trailingCustomEntry.Trim(), StringComparison.Ordinal));
        AssertTrue(updatedText.Contains("149.154.167.220 telegram.org", StringComparison.Ordinal));
        AssertEqual(1, updatedText.Split(ZapretHostsService.BeginMarker).Length - 1);

        var removed = await service.RemoveAsync();
        AssertTrue(removed is not null);
        AssertEqual(original + trailingCustomEntry, await File.ReadAllTextAsync(hosts));
        AssertFalse(service.Inspect().IsInstalled);

        await File.WriteAllTextAsync(hosts, original + ZapretHostsService.BeginMarker + "\r\n");
        AssertThrows<InvalidDataException>(() => service.Inspect());
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static string CreateOfficialHosts() => string.Join(
    '\n',
    "146.75.22.132 raw.githubusercontent.com",
    "146.75.22.132 objects.githubusercontent.com",
    "146.75.22.132 release-assets.githubusercontent.com",
    "146.75.22.132 avatars.githubusercontent.com",
    "162.159.138.232 discord.com",
    "162.159.137.232 discord.com",
    "162.159.128.233 updates.discord.com",
    "149.154.167.220 telegram.me",
    "149.154.167.220 t.me",
    "149.154.167.220 api.telegram.org");

static async Task TestStrategyOrderAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"flowseal-tests-{Guid.NewGuid():N}");
    var paths = new AppPaths(root);
    paths.EnsureCreated();
    try
    {
        var zapret = paths.ZapretDirectory("1.0");
        Directory.CreateDirectory(zapret);
        foreach (var file in new[] { "general (ALT10).bat", "general (ALT2).bat", "general.bat" })
        {
            await File.WriteAllTextAsync(Path.Combine(zapret, file), "@echo off");
        }

        var manager = new ComponentProcessManager(paths, new FileLogger(paths.LogFile));
        AssertEqual(
            "general.bat|general (ALT2).bat|general (ALT10).bat",
            string.Join('|', manager.GetStrategies("1.0")));
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static async Task TestOfficialReleasesAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"flowseal-integration-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var logger = new FileLogger(paths.LogFile);
        var releases = new GitHubReleaseClient(client);
        var updater = new ComponentUpdater(client, releases, paths, logger);
        var hostsFile = Path.Combine(root, "system", "hosts");
        var hostsService = new ZapretHostsService(client, paths.HostsBackupsRoot, hostsFile);

        var telegram = await updater.EnsureLatestAsync(ComponentKind.TelegramProxy, null);
        var zapret = await updater.EnsureLatestAsync(ComponentKind.Zapret, null);
        var officialHosts = await hostsService.DownloadOfficialAsync();
        var hostsStatus = hostsService.Inspect(officialHosts);

        AssertPath(
            ComponentUpdater.IsValidInstallation(ComponentKind.TelegramProxy, telegram.InstallDirectory),
            "Telegram installation validation failed");
        AssertPath(
            ComponentUpdater.IsValidInstallation(ComponentKind.Zapret, zapret.InstallDirectory),
            "zapret installation validation failed");
        AssertPath(
            File.Exists(Path.Combine(telegram.InstallDirectory, "LICENSE.Flowseal.txt")),
            "Telegram license was not retained");
        AssertPath(
            File.Exists(Path.Combine(zapret.InstallDirectory, "LICENSE.txt")),
            "zapret license was not retained");
        AssertPath(!hostsStatus.IsInstalled, "hosts integration check modified the test hosts file");
        AssertPath(hostsStatus.OfficialEntries >= 10, "official zapret hosts list did not validate");
        var strategies = Directory.EnumerateFiles(zapret.InstallDirectory, "general*.bat").ToArray();
        AssertPath(strategies.Length >= 20, "Official zapret strategies are missing");
        foreach (var strategy in strategies)
        {
            ZapretLaunchSpec launch;
            try
            {
                launch = ZapretStrategyParser.Parse(strategy);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"{Path.GetFileName(strategy)}: {exception.Message}", exception);
            }

            AssertPath(launch.Arguments.Count >= 5, $"Could not parse {Path.GetFileName(strategy)}");
        }
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static void AssertPath(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void AssertTrue(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void AssertFalse(bool value) => AssertTrue(!value);

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
