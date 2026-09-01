namespace FlowsealManager.Core.Infrastructure;

public sealed class FileLogger
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileLogger(string filePath)
    {
        _filePath = filePath;
    }

    public event EventHandler<string>? MessageLogged;

    public async Task InfoAsync(string message, CancellationToken cancellationToken = default)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {message}";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await File.AppendAllTextAsync(_filePath, line + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        MessageLogged?.Invoke(this, line);
    }
}
