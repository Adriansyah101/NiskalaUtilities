using Niskala.Interop.Text;

namespace Niskala.Repair;

public interface IRepairEnvironment
{
    bool IsElevated { get; }

    string SystemDirectory { get; }

    int OemCodePage { get; }
}

public sealed class SystemRepairEnvironment : IRepairEnvironment
{
    public static SystemRepairEnvironment Instance { get; } = new();

    private SystemRepairEnvironment()
    {
    }

    public bool IsElevated => Environment.IsPrivilegedProcess;

    public string SystemDirectory => Environment.SystemDirectory;

    public int OemCodePage => CodePageNative.OemCodePage;
}
