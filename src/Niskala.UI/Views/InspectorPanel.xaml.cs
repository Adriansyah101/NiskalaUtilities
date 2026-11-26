using System.Collections.Specialized;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class InspectorPanel : UserControl
{
    public InspectorPanel() => InitializeComponent();

    public InspectorViewModel ViewModel
    {
        get;
        set
        {
            if (field is not null)
            {
                field.Activity.CollectionChanged -= OnActivityChanged;
            }

            field = value;
            field.Activity.CollectionChanged += OnActivityChanged;
            Bindings.Update();
        }
    } = null!;

    private void OnActivityChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && ViewModel.Activity.Count > 0)
        {
            ActivityList.ScrollIntoView(ViewModel.Activity[^1]);
        }
    }
}
