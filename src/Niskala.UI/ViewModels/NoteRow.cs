using Microsoft.UI.Xaml;
using Niskala.Updater;
using Windows.UI.Text;

namespace Niskala.UI.ViewModels;

public sealed class NoteRow
{
    private const double TitleSize = 18;
    private const double HeadingSize = 16;
    private const double BodySize = 14;
    private const double IndentStep = 18;
    private const double HeadingGap = 14;
    private const double SubheadingGap = 10;
    private const double LineGap = 4;
    private const ushort NormalWeight = 400;
    private const ushort SemiBoldWeight = 600;

    public NoteRow(NoteLine line)
    {
        Line = line;
        (Size, Weight, double gap) = line.Kind switch
        {
            NoteKind.Title => (TitleSize, new FontWeight(SemiBoldWeight), 0d),
            NoteKind.Heading => (HeadingSize, new FontWeight(SemiBoldWeight), HeadingGap),
            NoteKind.Subheading => (BodySize, new FontWeight(SemiBoldWeight), SubheadingGap),
            _ => (BodySize, new FontWeight(NormalWeight), LineGap),
        };
        Spacing = new Thickness(line.Level * IndentStep, gap, 0, 0);
    }

    public NoteLine Line { get; }

    public string Text => Line.Text;

    public string Marker => Line.Marker;

    public bool HasMarker => Line.Marker.Length > 0;

    public double Size { get; }

    public FontWeight Weight { get; }

    public Thickness Spacing { get; }
}
