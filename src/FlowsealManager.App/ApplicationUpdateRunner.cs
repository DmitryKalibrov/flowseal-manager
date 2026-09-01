using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FlowsealManager.Core.Models;

namespace FlowsealManager.App;

internal static class ApplicationUpdateRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync(string planPath, string expectedPlanSha256)
    {
        try
        {
            await VerifyFileSha256Async(planPath, expectedPlanSha256).ConfigureAwait(false);
            var plan = await ReadPlanAsync(planPath).ConfigureAwait(false);
            ValidatePlan(plan, planPath);
            await WaitForParentAsync(plan.ParentProcessId).ConfigureAwait(false);
            await VerifyCurrentExecutableAsync(plan).ConfigureAwait(false);
            Directory.CreateDirectory(plan.BackupDirectory);

            var appliedFiles = new List<string>();
            var backedUpFiles = new List<string>();
            try
            {
                foreach (var relativePath in plan.ExistingFiles)
                {
                    var target = CombineUnder(plan.TargetDirectory, relativePath);
                    if (!File.Exists(target)) continue;
                    var backup = CombineUnder(plan.BackupDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, true);
                    backedUpFiles.Add(relativePath);
                }

                foreach (var relativePath in plan.ExistingFiles.Except(plan.Files, StringComparer.OrdinalIgnoreCase))
                {
                    var obsolete = CombineUnder(plan.TargetDirectory, relativePath);
                    if (File.Exists(obsolete)) File.Delete(obsolete);
                }

                foreach (var relativePath in plan.Files)
                {
                    var source = CombineUnder(plan.StagingDirectory, relativePath);
                    var target = CombineUnder(plan.TargetDirectory, relativePath);
                    if (!File.Exists(source))
                    {
                        throw new InvalidDataException($"В пакете отсутствует {relativePath}.");
                    }

                    await VerifyFileSha256Async(source, plan.FileSha256[relativePath]).ConfigureAwait(false);

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, true);
                    appliedFiles.Add(relativePath);
                }

                var executable = CombineUnder(plan.TargetDirectory, plan.Executable);
                var startInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    WorkingDirectory = plan.TargetDirectory
                };
                startInfo.ArgumentList.Add("--update-complete");
                startInfo.ArgumentList.Add(plan.SuccessMarker);
                if (plan.StartMinimized) startInfo.ArgumentList.Add("--minimized");
                _ = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Не удалось запустить обновлённое приложение.");

                if (!await WaitForSuccessMarkerAsync(plan.SuccessMarker).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Обновлённое приложение не подтвердило успешный запуск.");
                }

                TryDeleteDirectory(plan.BackupDirectory);
                TryDeleteDirectory(plan.StagingDirectory);
                return 0;
            }
            catch
            {
                foreach (var relativePath in appliedFiles)
                {
                    var target = CombineUnder(plan.TargetDirectory, relativePath);
                    if (File.Exists(target)) File.Delete(target);
                }

                foreach (var relativePath in backedUpFiles)
                {
                    var backup = CombineUnder(plan.BackupDirectory, relativePath);
                    var target = CombineUnder(plan.TargetDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(backup, target, true);
                }

                var restored = CombineUnder(plan.TargetDirectory, plan.Executable);
                if (File.Exists(restored))
                {
                    _ = Process.Start(new ProcessStartInfo(restored, "--update-rollback")
                    {
                        UseShellExecute = true,
                        WorkingDirectory = plan.TargetDirectory
                    });
                }

                throw;
            }
        }
        catch (Exception exception)
        {
            try
            {
                var logRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FlowsealManager",
                    "logs");
                Directory.CreateDirectory(logRoot);
                await File.AppendAllTextAsync(
                    Path.Combine(logRoot, "update.log"),
                    $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}")
                    .ConfigureAwait(false);
            }
            catch
            {
                // The runner has no other safe reporting surface.
            }

            return 1;
        }
    }

    private static async Task<ApplicationUpdatePlan> ReadPlanAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ApplicationUpdatePlan>(stream, JsonOptions)
            .ConfigureAwait(false) ?? throw new InvalidDataException("План обновления пуст.");
    }

    private static void ValidatePlan(ApplicationUpdatePlan plan, string planPath)
    {
        var planRoot = Path.GetDirectoryName(Path.GetFullPath(planPath))!;
        if (plan.SchemaVersion != 1 || plan.ParentProcessId <= 0 ||
            !Path.GetFullPath(plan.StagingDirectory).StartsWith(planRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(plan.SuccessMarker).StartsWith(planRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(plan.BackupDirectory).StartsWith(planRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(plan.TargetDirectory) ||
            string.IsNullOrWhiteSpace(plan.BackupDirectory) ||
            plan.CurrentExecutableSha256.Length != 64 ||
            plan.CurrentExecutableSha256.Any(character => !char.IsAsciiHexDigit(character)) ||
            !string.Equals(plan.Executable, "FlowsealManager.exe", StringComparison.Ordinal) ||
            plan.Files.Count is < 1 or > 100 ||
            plan.Files.Any(path => !IsSafeRelativePath(path)) ||
            plan.ExistingFiles.Count is < 1 or > 100 ||
            plan.ExistingFiles.Any(path => !IsSafeRelativePath(path)) ||
            plan.FileSha256.Count != plan.Files.Count ||
            plan.Files.Any(path => !plan.FileSha256.TryGetValue(path, out var digest) ||
                                   digest.Length != 64 ||
                                   digest.Any(character => !char.IsAsciiHexDigit(character))))
        {
            throw new InvalidDataException("План обновления недействителен.");
        }
    }

    private static async Task VerifyCurrentExecutableAsync(ApplicationUpdatePlan plan)
    {
        var current = CombineUnder(plan.TargetDirectory, plan.Executable);
        if (!File.Exists(current))
        {
            throw new FileNotFoundException("Текущая сборка приложения не найдена.", current);
        }

        await using var stream = File.OpenRead(current);
        var actual = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        var expected = Convert.FromHexString(plan.CurrentExecutableSha256);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException("Текущий файл приложения изменился после подготовки обновления.");
        }
    }

    private static async Task VerifyFileSha256Async(string path, string expectedSha256)
    {
        if (expectedSha256.Length != 64 || expectedSha256.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidDataException("Некорректный SHA-256 в плане обновления.");
        }

        await using var stream = File.OpenRead(path);
        var actual = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedSha256)))
        {
            throw new InvalidDataException("Файлы обновления изменились после проверки.");
        }
    }

    private static async Task WaitForParentAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Flowseal Manager не завершился за отведённое время.");
            }
        }
        catch (ArgumentException)
        {
            // The process already exited.
        }
    }

    private static async Task<bool> WaitForSuccessMarkerAsync(string path)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (File.Exists(path)) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }

        return false;
    }

    private static string CombineUnder(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Небезопасный путь в плане обновления.");
        }

        return candidate;
    }

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is "" or "." or "..");

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // The next update can replace this backup.
        }
    }
}
