namespace FlowsealManager.Core.Services;

public sealed class ZapretConflictException : InvalidOperationException
{
    public ZapretConflictException(string message)
        : base(message)
    {
    }
}
