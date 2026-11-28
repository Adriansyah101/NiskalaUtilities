using System.Collections.Specialized;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class InspectorPanel : UserControl
{
    private readonly Attachment _events;

    public InspectorPanel()
    {
        _events = new Attachment(
            () => ViewModel.Activity.CollectionChanged += OnActivityChanged,
            () => ViewModel.Activity.CollectionChanged -= OnActivityChanged);
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (ViewModel is not null)
            {
                _events.Attach();
            }
        };
        Unloaded += (_, _) => _events.Detach();
    }

    public InspectorViewModel ViewModel
    {
        get;
        set
        {
            bool attached = _events.Detach();
            field = value;
            if (attached || IsLoaded)
            {
                _events.Attach();
            }

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
