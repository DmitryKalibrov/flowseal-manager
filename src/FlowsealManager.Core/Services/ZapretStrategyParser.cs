using System.Text;
using System.Text.RegularExpressions;

namespace FlowsealManager.Core.Services;

public sealed record ZapretLaunchSpec(string Executable, IReadOnlyList<string> Arguments);

public static partial class ZapretStrategyParser
{
    public static ZapretLaunchSpec Parse(string strategyFile)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(strategyFile))
            ?? throw new InvalidDataException("Strategy has no parent directory.");
        var executable = Path.Combine(root, "bin", "winws.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("winws.exe is missing.", executable);
        }

        var content = File.ReadAllText(strategyFile);
        var normalized = ContinuationRegex().Replace(content, " ");
        var command = normalized
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line =>
                line.Contains("winws.exe", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("--", StringComparison.Ordinal));
        if (command is null)
        {
            throw new InvalidDataException("The strategy does not contain a winws command.");
        }

        var executableEnd = command.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase) +
                            "winws.exe".Length;
        var argumentLine = command[executableEnd..].TrimStart(' ', '\t', '"');
        var (gameTcp, gameUdp) = ReadGameFilter(root);
        argumentLine = argumentLine
            .Replace("%BIN%", Path.Combine(root, "bin") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("%LISTS%", Path.Combine(root, "lists") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilterTCP%", gameTcp, StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilterUDP%", gameUdp, StringComparison.OrdinalIgnoreCase)
            .Replace("^!", "!", StringComparison.Ordinal);

        if (UnexpandedVariableRegex().IsMatch(argumentLine) || argumentLine.Contains('^'))
        {
            throw new InvalidDataException("The strategy contains an unsupported batch variable.");
        }

        var arguments = AddUserIpSet(root, SplitArguments(argumentLine));
        if (arguments.Count == 0 || arguments.Any(argument => !argument.StartsWith("--", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The strategy arguments could not be parsed safely.");
        }

        return new ZapretLaunchSpec(executable, arguments);
    }

    private static IReadOnlyList<string> AddUserIpSet(string root, IReadOnlyList<string> arguments)
    {
        var userIpSet = Path.Combine(root, "lists", "ipset-general-user.txt");
        if (!HasActualUserEntries(userIpSet))
        {
            return arguments;
        }

        var result = new List<string>(arguments.Count + 4);
        foreach (var argument in arguments)
        {
            result.Add(argument);
            if (argument.StartsWith("--ipset=", StringComparison.OrdinalIgnoreCase) &&
                argument.Contains("ipset-all.txt", StringComparison.OrdinalIgnoreCase))
            {
                result.Add("--ipset=" + userIpSet);
            }
        }

        return result;
    }

    private static bool HasActualUserEntries(string path) =>
        File.Exists(path) && File.ReadLines(path).Any(line =>
        {
            var value = line.Trim();
            return value.Length > 0 &&
                   !value.StartsWith('#') &&
                   !string.Equals(value, "203.0.113.113/32", StringComparison.OrdinalIgnoreCase);
        });

    public static IReadOnlyList<string> SplitArguments(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var backslashes = 0;

        void FlushBackslashes()
        {
            if (backslashes > 0)
            {
                current.Append('\\', backslashes);
                backslashes = 0;
            }
        }

        foreach (var character in commandLine.Trim())
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                current.Append('\\', backslashes / 2);
                if (backslashes % 2 == 1)
                {
                    current.Append('"');
                }
                else
                {
                    quoted = !quoted;
                }

                backslashes = 0;
                continue;
            }

            FlushBackslashes();
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        FlushBackslashes();
        if (quoted)
        {
            throw new InvalidDataException("The strategy contains an unterminated quote.");
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static (string Tcp, string Udp) ReadGameFilter(string root)
    {
        var flag = Path.Combine(root, "utils", "game_filter.enabled");
        if (!File.Exists(flag))
        {
            return ("12", "12");
        }

        var mode = File.ReadLines(flag).FirstOrDefault()?.Trim();
        return mode?.ToLowerInvariant() switch
        {
            "all" => ("1024-65535", "1024-65535"),
            "tcp" => ("1024-65535", "12"),
            _ => ("12", "1024-65535")
        };
    }

    [GeneratedRegex(@"\^[ \t]*\r?\n")]
    private static partial Regex ContinuationRegex();

    [GeneratedRegex(@"%[^%\r\n]+%")]
    private static partial Regex UnexpandedVariableRegex();
}
