using System.Text;
using Niskala.Interop.Etw;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Recording;
using static Niskala.Tracker.Tests.Kernel;

namespace Niskala.Tracker.Tests;

public sealed class ChangeReconcilerTests
{
    private const string App = @"C:\Program Files\Acme";
    private const string Key = @"HKCU\Software\Acme";
    private const int RegSz = 1;
    private const int RegDword = 4;

    [Fact]
    public void Files_are_classified_by_their_first_event_and_their_state_now()
    {
        var state = new FakeState();
        state.Files[App] = FileState.Directory;
        state.Files[$@"{App}\acme.exe"] = FileState.File;
        state.Files[@"C:\Shared\common.dll"] = FileState.File;
        state.Files[@"C:\Shared\replaced.dll"] = FileState.File;
        state.Files[@"C:\Shared\final.dll"] = FileState.File;

        EtwEvent[] events =
        [
            Event(EtwEventKind.FileCreated, App, 1) with { Flags = EtwEventFlags.Directory },
            Event(EtwEventKind.FileCreated, $@"{App}\acme.exe", 2),
            Event(EtwEventKind.FileWritten, $@"{App}\acme.exe", 3),
            Event(EtwEventKind.FileCreated, $@"{App}\setup.tmp", 4),
            Event(EtwEventKind.FileDeleted, $@"{App}\setup.tmp", 5),
            Event(EtwEventKind.FileWritten, @"C:\Shared\common.dll", 6),
            Event(EtwEventKind.FileDeleted, @"C:\Shared\replaced.dll", 7),
            Event(EtwEventKind.FileCreated, @"C:\Shared\replaced.dll", 8),
            Event(EtwEventKind.FileDeleted, @"C:\Shared\old.cfg", 9),
            Event(EtwEventKind.FileCreated, @"C:\Shared\final.tmp", 10),
            Event(EtwEventKind.FileRenamed, @"C:\Shared\final.tmp", 11) with { Name = @"C:\Shared\final.dll" },
        ];

        ReconciledChanges changes = ChangeReconciler.Reconcile(events, state);

        Assert.Equal(
            [
                new FileChange(App, ChangeKind.Created, true),
                new FileChange($@"{App}\acme.exe", ChangeKind.Created, false),
                new FileChange(@"C:\Shared\common.dll", ChangeKind.Modified, false),
                new FileChange(@"C:\Shared\final.dll", ChangeKind.Created, false),
                new FileChange(@"C:\Shared\old.cfg", ChangeKind.Deleted, false),
                new FileChange(@"C:\Shared\replaced.dll", ChangeKind.Modified, false),
            ],
            changes.Files);
    }

    [Fact]
    public void Events_are_ordered_by_time_not_by_arrival()
    {
        var state = new FakeState();
        state.Files[@"C:\Acme\a.txt"] = FileState.File;
        EtwEvent[] events =
        [
            Event(EtwEventKind.FileWritten, @"C:\Acme\a.txt", 5),
            Event(EtwEventKind.FileCreated, @"C:\Acme\a.txt", 4),
        ];

        Assert.Equal(ChangeKind.Created, Assert.Single(ChangeReconciler.Reconcile(events, state).Files).Change);
    }

    [Fact]
    public void Values_in_a_created_key_belong_to_the_key()
    {
        var state = new FakeState();
        state.Keys.Add($@"{Key}\Settings");
        state.Set($@"{Key}\Settings", "Theme", RegSz, Text("dark"));
        EtwEvent[] events =
        [
            Event(EtwEventKind.KeyCreated, Key, 1),
            Event(EtwEventKind.KeyCreated, $@"{Key}\Settings", 2),
            Event(EtwEventKind.ValueSet, $@"{Key}\Settings", 3) with { Name = "Theme", Flags = EtwEventFlags.Previous },
            Event(EtwEventKind.KeyCreated, @"HKCU\Software\Gone", 4),
            Event(EtwEventKind.KeyDeleted, @"HKCU\Software\Old", 5),
        ];

        ReconciledChanges changes = ChangeReconciler.Reconcile(events, state);

        Assert.Equal([new KeyChange($@"{Key}\Settings", ChangeKind.Created), new KeyChange(@"HKCU\Software\Old", ChangeKind.Deleted)], changes.Keys);
        Assert.Empty(changes.Values);
    }

    [Fact]
    public void A_value_keeps_the_data_it_had_before_its_first_change()
    {
        const string Run = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
        var state = new FakeState();
        state.Set(Key, "Path", RegSz, Text(@"C:\Acme;C:\Tools"));
        state.Set(Key, "Count", RegDword, BitConverter.GetBytes(3));
        state.Set(Key, "Big", 3, new byte[64]);
        state.Set(Run, "Acme", RegSz, Text("acme.exe"));
        state.Set(Key, "Same", RegSz, Text("x"));

        EtwEvent[] events =
        [
            Set(Key, "Path", 1, RegSz, Text(@"C:\Tools")),
            Set(Key, "Path", 2, RegSz, Text("C:\\Acme;C:\\Tools-intermediate")),
            Set(Key, "Count", 3, RegDword, BitConverter.GetBytes(2)),
            Set(Key, "Big", 4, 3, new byte[16]) with { PreviousSize = 4096 },
            Set(Run, "Acme", 5, 0, []),
            Set(Key, "Same", 6, RegSz, Text("x")),
            Event(EtwEventKind.ValueDeleted, Key, 7) with { Name = "Legacy" },
        ];

        ReconciledChanges changes = ChangeReconciler.Reconcile(events, state);

        ValueChange path = changes.Values.Single(v => v.Name == "Path");
        Assert.Equal(ChangeKind.Modified, path.Change);
        Assert.Equal(Text(@"C:\Tools"), path.PreviousData);
        Assert.Equal(Text(@"C:\Acme;C:\Tools"), path.Data);
        Assert.Equal(BitConverter.GetBytes(2), changes.Values.Single(v => v.Name == "Count").PreviousData);
        ValueChange big = changes.Values.Single(v => v.Name == "Big");
        Assert.Null(big.PreviousKind);
        Assert.Null(big.PreviousData);
        Assert.Equal(new ValueChange(Run, "Acme", ChangeKind.Created) { Kind = RegSz, Data = Text("acme.exe") }, changes.Values.Single(v => v.Name == "Acme"), Comparer);
        Assert.DoesNotContain(changes.Values, v => v.Name == "Same");
        Assert.Equal(ChangeKind.Deleted, changes.Values.Single(v => v.Name == "Legacy").Change);
    }

    [Fact]
    public void Without_previous_data_a_value_is_never_taken_for_new()
    {
        var state = new FakeState();
        state.Set(Key, "Mode", RegSz, Text("on"));
        ReconciledChanges changes = ChangeReconciler.Reconcile([Event(EtwEventKind.ValueSet, Key, 1) with { Name = "Mode" }], state);

        ValueChange mode = Assert.Single(changes.Values);
        Assert.Equal(ChangeKind.Modified, mode.Change);
        Assert.Null(mode.PreviousData);
    }

    private static EtwEvent Set(string key, string name, int second, int previousType, byte[] previous) =>
        Event(EtwEventKind.ValueSet, key, second) with
        {
            Name = name,
            Flags = EtwEventFlags.Previous,
            PreviousType = previousType,
            PreviousSize = previous.Length,
            PreviousData = previous,
        };

    private static byte[] Text(string text) => Encoding.Unicode.GetBytes(text + "\0");

    private static readonly IEqualityComparer<ValueChange> Comparer = EqualityComparer<ValueChange>.Create(
        (a, b) => a!.Key == b!.Key && a.Name == b.Name && a.Change == b.Change && a.Kind == b.Kind
            && a.Data.AsSpan().SequenceEqual(b.Data) && a.PreviousKind == b.PreviousKind && a.PreviousData.AsSpan().SequenceEqual(b.PreviousData),
        v => v.Key.GetHashCode(StringComparison.Ordinal));
}
