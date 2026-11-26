using Niskala.Interop.Processes;
using Niskala.Interop.Security;

namespace Niskala.Guardian.Tests;

public sealed class PrivilegeScopeTests
{
    [Fact]
    public void Scope_enables_what_the_token_holds_and_puts_it_back()
    {
        bool before = PrivilegeScope.IsEnabled(SystemPrivilege.Debug);
        using (PrivilegeScope outer = PrivilegeScope.Enable(SystemPrivilege.Debug, SystemPrivilege.Backup, SystemPrivilege.Debug))
        {
            if (Environment.IsPrivilegedProcess)
            {
                Assert.Equal([SystemPrivilege.Debug, SystemPrivilege.Backup], outer.Enabled);
                Assert.Empty(outer.Missing);
                using (PrivilegeScope inner = PrivilegeScope.Enable(SystemPrivilege.Debug))
                {
                    Assert.Equal([SystemPrivilege.Debug], inner.Enabled);
                }

                Assert.True(PrivilegeScope.IsEnabled(SystemPrivilege.Debug));
            }
            else
            {
                Assert.Empty(outer.Enabled);
                Assert.Equal([SystemPrivilege.Debug, SystemPrivilege.Backup], outer.Missing);
                Assert.False(PrivilegeScope.IsEnabled(SystemPrivilege.Debug));
            }
        }

        Assert.Equal(before, PrivilegeScope.IsEnabled(SystemPrivilege.Debug));
    }

    [Fact]
    public void Session_manager_processes_are_never_reported_as_safe_to_end()
    {
        Assert.Equal(ProcessNative.ErrorSuccess, ProcessNative.TrySnapshot(out List<ProcessEntry> processes));
        ProcessEntry csrss = processes.First(p => string.Equals(p.ExeName, "csrss.exe", StringComparison.OrdinalIgnoreCase));

        Assert.NotEqual(false, ProcessNative.IsCritical(csrss.Id));
        Assert.Equal(false, ProcessNative.IsCritical(Environment.ProcessId));
    }
}
