using Niskala.Updater;

namespace Niskala.UI.ViewModels;

public enum UpdatePromptChoice
{
    UpdateNow,
    Review,
    Later,
}

public sealed record UpdateOffer(string Current, string Latest, TextList Highlights, int More)
{
    public const int MaxHighlights = 4;
    public const int MaxLength = 160;

    private const char Ellipsis = '…';

    public bool HasHighlights => Highlights.Count > 0;

    public bool HasMore => More > 0;

    public static UpdateOffer From(UpdateCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        (TextList highlights, int more) = Summarize(check.Notes);
        return new UpdateOffer(check.Current.ToString(), check.Latest?.ToString() ?? string.Empty, highlights, more);
    }

    public static (TextList Highlights, int More) Summarize(string? notes)
    {
        TextList highlights = [];
        if (string.IsNullOrWhiteSpace(notes))
        {
            return (highlights, 0);
        }

        IReadOnlyList<NoteLine> lines = PatchNotes.Parse(notes);
        NoteLine[] bullets = [.. lines.Where(l => l.Kind == NoteKind.Bullet && l.Level == 0)];
        NoteLine[] picked = bullets.Length > 0 ? bullets : [.. lines.Where(l => l.Kind == NoteKind.Paragraph)];
        highlights.AddRange(picked.Take(MaxHighlights).Select(l => Shorten(l.Text)).Where(t => t.Length > 0));
        return (highlights, Math.Max(0, picked.Length - MaxHighlights));
    }

    private static string Shorten(string text)
    {
        string line = text.Trim();
        if (line.Length <= MaxLength)
        {
            return line;
        }

        int cut = line.LastIndexOf(' ', MaxLength - 1);
        return line[..(cut > 0 ? cut : MaxLength - 1)].TrimEnd(' ', ',', ';', ':', '.') + Ellipsis;
    }
}

public sealed class UpdatePrompt
{
    public UpdateOffer? Pending { get; private set; }

    public bool HasShown { get; private set; }

    public UpdatePromptChoice? Choice { get; private set; }

    public static bool CanShow(bool windowReady, bool windowHidden, int openPopups, bool busy) =>
        windowReady && !windowHidden && openPopups == 0 && !busy;

    public bool Offer(UpdateCheck check, bool automatic, bool packageReady)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!automatic || HasShown || Pending is not null || !check.IsAvailable || !packageReady || check.Latest is null)
        {
            return false;
        }

        Pending = UpdateOffer.From(check);
        return true;
    }

    public UpdateOffer? Take()
    {
        UpdateOffer? offer = Pending;
        if (offer is not null)
        {
            Pending = null;
            HasShown = true;
        }

        return offer;
    }

    public void Close(UpdatePromptChoice choice) => Choice = choice;
}
