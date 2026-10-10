using System.Windows;
using SSGen.App.ViewModels;

namespace SSGen.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Services.HorizontalScrollSupport.Attach(this);
    }
}
