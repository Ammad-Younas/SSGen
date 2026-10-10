using System.Collections.Specialized;
using System.Windows.Controls;

namespace SSGen.App.Views;

public partial class AgentView : UserControl
{
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
    }
}
