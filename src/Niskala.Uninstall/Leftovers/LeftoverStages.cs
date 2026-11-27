using Niskala.Scanner.Leftovers;

namespace Niskala.Uninstall.Leftovers;

public enum LeftoverStage
{
    Files,
    Registry,
}

public sealed class LeftoverStages
{
    private readonly bool _reverts;

    private LeftoverStages(IReadOnlyList<LeftoverItem> files, IReadOnlyList<LeftoverItem> registry, bool reverts)
    {
        Files = files;
        Registry = registry;
        _reverts = reverts;
    }

    public IReadOnlyList<LeftoverItem> Files { get; }

    public IReadOnlyList<LeftoverItem> Registry { get; }

    public bool HasFiles => Files.Count > 0;

    public bool HasRegistry => Registry.Count > 0 || _reverts;

    public bool IsEmpty => !HasFiles && !HasRegistry;

    public LeftoverStage? First => HasFiles ? LeftoverStage.Files : HasRegistry ? LeftoverStage.Registry : null;

    public static LeftoverStages Split(IEnumerable<LeftoverItem> items, int reverts = 0)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegative(reverts);
        List<LeftoverItem> files = [];
        List<LeftoverItem> registry = [];
        foreach (LeftoverItem item in items)
        {
            (IsFile(item.Kind) ? files : registry).Add(item);
        }

        return new LeftoverStages(files, registry, reverts > 0);
    }

    public static bool IsFile(LeftoverKind kind) => kind is LeftoverKind.Folder or LeftoverKind.File;

    public static LeftoverStage StageOf(LeftoverKind kind) => IsFile(kind) ? LeftoverStage.Files : LeftoverStage.Registry;

    public LeftoverStage? After(LeftoverStage stage) => stage == LeftoverStage.Files && HasRegistry ? LeftoverStage.Registry : null;

    public IReadOnlyList<LeftoverItem> Items(LeftoverStage stage) => stage == LeftoverStage.Files ? Files : Registry;
}
