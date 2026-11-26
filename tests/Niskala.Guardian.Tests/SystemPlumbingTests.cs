using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Niskala.Guardian.Journal;
using Niskala.Guardian.RestorePoints;
using Niskala.Interop.Firewall;
using Niskala.Interop.Wmi;

namespace Niskala.Guardian.Tests;

public sealed class SystemPlumbingTests
{
    private const int HkeyCurrentUser = unchecked((int)0x80000001);

    [Fact]
    public void Wmi_queries_and_static_methods_work()
    {
        Assert.Equal(0, WmiNative.TryQuery(
            @"root\cimv2",
            $"SELECT ProcessId FROM Win32_Process WHERE ProcessId = {Environment.ProcessId}",
            ["ProcessId"],
            out List<Dictionary<string, object?>> rows));
        Assert.Equal(Environment.ProcessId, Assert.Single(rows)["ProcessId"]);

        using var registry = new RegistryScratch();
        using (RegistryKey key = registry.Create("Wmi"))
        {
            key.SetValue("Name", "through StdRegProv");
        }

        Assert.Equal(0, WmiNative.TryInvoke(
            @"root\default",
            "StdRegProv",
            "GetStringValue",
            [new("hDefKey", HkeyCurrentUser), new("sSubKeyName", registry.Key + @"\Wmi"), new("sValueName", "Name")],
            ["ReturnValue", "sValue"],
            out Dictionary<string, object?> outputs));
        Assert.Equal(0, outputs["ReturnValue"]);
        Assert.Equal("through StdRegProv", outputs["sValue"]);
    }

    [Fact]
    public void Firewall_rules_can_be_listed()
    {
        Assert.Equal(0, FirewallNative.TryList(out List<FirewallRule> rules));
        Assert.NotEmpty(rules);
        Assert.Contains(rules, r => r.Name.Length > 0 && r.Direction is 1 or 2);
    }

    [Fact]
    public async Task No_restore_point_is_made_without_elevation()
    {
        RestorePointRecord point = await new WmiRestorePointService(NullLogger<WmiRestorePointService>.Instance, isElevated: static () => false)
            .CreateAsync("Niskala test", TestContext.Current.CancellationToken);

        Assert.False(point.Created);
        Assert.Null(point.SequenceNumber);
        Assert.Contains("Not elevated", point.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_elevated_run_makes_a_restore_point_or_says_why_not()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Assert.Skip("Restore points need an elevated test run.");
        }

        const string Description = "Niskala test";
        int[] before = [.. RestorePoints().Select(p => p.Sequence)];
        try
        {
            RestorePointRecord point = await new WmiRestorePointService(NullLogger<WmiRestorePointService>.Instance)
                .CreateAsync(Description, TestContext.Current.CancellationToken);

            if (point.Created)
            {
                int sequence = Assert.IsType<int>(point.SequenceNumber);
                Assert.Contains(RestorePoints(), p => p.Sequence == sequence && p.Description == Description);
            }
            else
            {
                Assert.NotNull(point.Note);
                Assert.DoesNotContain("Not elevated", point.Note, StringComparison.Ordinal);
                await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.DoesNotContain(RestorePoints(), p => p.Description == Description && !before.Contains(p.Sequence));
            }
        }
        finally
        {
            foreach ((int sequence, _) in RestorePoints().Where(p => p.Description == Description && !before.Contains(p.Sequence)))
            {
                Assert.Equal(0u, SRRemoveRestorePoint((uint)sequence));
            }
        }

        Assert.DoesNotContain(RestorePoints(), p => p.Description == Description && !before.Contains(p.Sequence));
    }

    private static (int Sequence, string? Description)[] RestorePoints()
    {
        Assert.Equal(0, WmiNative.TryQuery(@"root\default", "SELECT SequenceNumber, Description FROM SystemRestore", ["SequenceNumber", "Description"], out List<Dictionary<string, object?>> rows));
        return [.. rows.Where(r => r["SequenceNumber"] is int).Select(r => ((int)r["SequenceNumber"]!, r["Description"] as string))];
    }

    [DllImport("srclient.dll")]
    private static extern uint SRRemoveRestorePoint(uint sequenceNumber);
}
