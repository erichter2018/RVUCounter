using System.Windows;

namespace RVUCounter.Converters;

/// <summary>
/// A proxy object that allows binding to properties outside the visual tree.
/// Used for DataGridColumn bindings which aren't in the visual tree.
/// </summary>
public class BindingProxy : Freezable
{
    protected override Freezable CreateInstanceCore()
    {
        return new BindingProxy();
    }

    public object Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));
}
