namespace Niskala.Scanner.Leftovers;

public sealed class PackageLeftoverProbe(string dataRoot)
{
    public static string DefaultRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");

    internal void Find(ScanContext context, CandidateSet candidates)
    {
        if (context.App.Package?.FamilyName is not { Length: > 0 } family || Path.GetFileName(family) != family || family.Contains("..", StringComparison.Ordinal))
        {
            return;
        }

        string folder = Path.Combine(dataRoot, family);
        if (Directory.Exists(folder))
        {
            candidates.Add(LeftoverKind.Folder, folder, null, family, null, LeftoverSignal.PackageFamily, $"data Windows keeps for the package family {family}");
        }
    }
}
