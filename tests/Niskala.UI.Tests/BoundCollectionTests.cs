using System.Collections.ObjectModel;
using System.Reflection;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Tests;

public sealed class BoundCollectionTests
{
    private static readonly Type[] GenericCollections = [typeof(ObservableCollection<>), typeof(List<>)];

    private static readonly Type[] ViewModelTypes =
        [.. typeof(AppItemCollection).Assembly.GetExportedTypes().Where(t => t.Namespace == typeof(AppItemCollection).Namespace)];

    private static readonly Type[] BoundCollections =
        [.. ViewModelTypes.Where(t => t.BaseType is { IsGenericType: true } b && GenericCollections.Contains(b.GetGenericTypeDefinition()))];

    private static readonly PropertyInfo[] ViewModelProperties =
        [.. ViewModelTypes.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))];

    [Fact]
    public void DeclaresEveryBoundCollection()
    {
        Assert.Equivalent(
            new[]
            {
                typeof(AppItemCollection), typeof(LeftoverTreeRowCollection), typeof(ActivityRowCollection),
                typeof(TimelineStepCollection), typeof(ProcessRowCollection), typeof(LogItemCollection),
                typeof(CleanRuleRowCollection), typeof(HindranceRowCollection), typeof(IssueRowCollection),
                typeof(ResourceRowCollection), typeof(SuspendRowCollection), typeof(StorageRowCollection),
                typeof(ExtensionRowCollection), typeof(DuplicateRowCollection), typeof(PathCollection),
                typeof(RepairRowCollection), typeof(LogLineCollection),
                typeof(TransactionItemCollection), typeof(PaletteEntryCollection), typeof(NoteRowCollection),
                typeof(NavCategoryCollection), typeof(NavPageCollection),
                typeof(TextList), typeof(ReasonList), typeof(ChangeList), typeof(RecordList),
            },
            BoundCollections,
            strict: true);
    }

    [Fact]
    public void BoundCollectionsAreSealedClosedTypes()
    {
        Assert.All(BoundCollections, t =>
        {
            Assert.True(t.IsSealed, t.Name);
            Assert.False(t.IsGenericType, t.Name);
        });
    }

    [Fact]
    public void ViewModelsExposeNoGenericCollectionInstances()
    {
        string[] offenders =
        [
            .. ViewModelProperties
                .Where(p => p.PropertyType is { IsGenericType: true } type && GenericCollections.Contains(type.GetGenericTypeDefinition()))
                .Select(p => $"{p.DeclaringType!.Name}.{p.Name}"),
        ];

        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryBoundCollectionBacksAViewModelProperty()
    {
        Type[] unused = [.. BoundCollections.Where(t => !ViewModelProperties.Any(p => p.PropertyType == t))];

        Assert.Empty(unused);
    }
}
