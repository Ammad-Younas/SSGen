using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SSGen.App.ViewModels;

namespace SSGen.App.Views;

/// <summary>
/// Explore page: four resizable splits (controls | rest, top / screens, screen | activity, log / map) whose
/// proportions are saved like the Emulator page's, and thumbnails that scale with the screens panel.
/// </summary>
public partial class AgentView : UserControl
{
    private const double ScrollbarAndCaption = 44;

    public AgentView()
    {
        InitializeComponent();

        // Keep the newest activity line in view.
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        };

        ThumbScroller.SizeChanged += (_, _) =>
            Resources["ThumbImageHeight"] = Math.Max(60, ThumbScroller.ActualHeight - ScrollbarAndCaption);

        // The strip only scrolls sideways: let an ordinary mouse wheel (or vertical two-finger swipe) move it.
        // Sideways swipes and Shift+wheel are handled app-wide by HorizontalScrollSupport.
        ThumbScroller.PreviewMouseWheel += (_, e) =>
        {
            if (ThumbScroller.ScrollableWidth > 0 && Keyboard.Modifiers == ModifierKeys.None)
            {
                ThumbScroller.ScrollToHorizontalOffset(ThumbScroller.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        };

        Loaded += (_, _) =>
        {
            if (DataContext is AgentViewModel { PaneWeights: { } w })
            {
                var defs = Definitions;
                for (var i = 0; i < defs.Length; i++)
                {
                    SetStar(defs[i], w[i]);
                }
            }
        };
    }

    /// <summary>The split definitions in saved order: pairs of (first, second) for each splitter.</summary>
    private DefinitionBase[] Definitions => [ControlsColumn, RestColumn, TopRow, ScreensRow, ScreenColumn, ActivityColumn, LogRow, MapRow];

    private void OnSplitterDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) => Normalize();

    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        Normalize();
        (DataContext as AgentViewModel)?.SavePaneWeights(Definitions.Select(Actual));
    }

    /// <summary>Star weights = current pixel sizes, so a drag only moves the splitter being dragged.</summary>
    private void Normalize()
    {
        foreach (var d in Definitions)
        {
            SetStar(d, Math.Max(1, Actual(d)));
        }
    }

    private static double Actual(DefinitionBase d) => d is ColumnDefinition c ? c.ActualWidth : ((RowDefinition)d).ActualHeight;

    private static void SetStar(DefinitionBase d, double weight)
    {
        var length = new GridLength(weight, GridUnitType.Star);
        if (d is ColumnDefinition c)
        {
            c.Width = length;
        }
        else
        {
            ((RowDefinition)d).Height = length;
        }
    }
}
