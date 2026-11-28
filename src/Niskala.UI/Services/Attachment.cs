namespace Niskala.UI.Services;

public sealed class Attachment
{
    private readonly Action _attach;
    private readonly Action _detach;

    public Attachment(Action attach, Action detach)
    {
        ArgumentNullException.ThrowIfNull(attach);
        ArgumentNullException.ThrowIfNull(detach);
        _attach = attach;
        _detach = detach;
    }

    public bool IsAttached { get; private set; }

    public bool Attach()
    {
        if (IsAttached)
        {
            return false;
        }

        IsAttached = true;
        _attach();
        return true;
    }

    public bool Detach()
    {
        if (!IsAttached)
        {
            return false;
        }

        IsAttached = false;
        _detach();
        return true;
    }
}
