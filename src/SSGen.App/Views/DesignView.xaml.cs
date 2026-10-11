using System.Windows;
using System.Windows.Controls;
using SSGen.App.ViewModels;

namespace SSGen.App.Views;

/// <summary>Design page: resizable columns (proportions saved) and caption editing hooks.</summary>
public partial class DesignView : UserControl
{
    public DesignView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is DesignViewModel { PaneWeights: [var a, var b, var c] })
            {
                ScreensColumn.Width = new GridLength(a, GridUnitType.Star);
                PreviewColumn.Width = new GridLength(b, GridUnitType.Star);
                OptionsColumn.Width = new GridLength(c, GridUnitType.Star);
            }
        };
    }

    private ColumnDefinition[] Columns => [ScreensColumn, PreviewColumn, OptionsColumn];

    private void OnSplitterDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) => Normalize();

    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        Normalize();
        (DataContext as DesignViewModel)?.SavePaneWeights(Columns.Select(c => c.ActualWidth));
    }

    /// <summary>Star weights = current pixel widths, so a drag moves only the splitter being dragged.</summary>
    private void Normalize()
    {
        foreach (var column in Columns)
        {
            column.Width = new GridLength(Math.Max(1, column.ActualWidth), GridUnitType.Star);
        }
    }

    /// <summary>
    /// The template list never scrolls itself (it shows every card), but its inner ScrollViewer still takes the
    /// mouse wheel: hand the wheel to the column around it so scrolling over the cards scrolls the page.
    /// </summary>
    private void OnInnerListWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject list)
        {
            return;
        }

        e.Handled = true;
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(list);
        while (parent is not null and not ScrollViewer)
        {
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }

        (parent as UIElement)?.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        });
    }

    /// <summary>Live preview while typing.</summary>
    private void OnCaptionChanged(object sender, TextChangedEventArgs e) => (DataContext as DesignViewModel)?.CaptionsTyping();

    /// <summary>Save captions when leaving the box.</summary>
    private void OnCaptionLostFocus(object sender, RoutedEventArgs e) => (DataContext as DesignViewModel)?.CaptionsEdited();
}
