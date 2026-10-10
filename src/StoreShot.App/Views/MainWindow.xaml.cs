using System.Windows;
using StoreShot.App.ViewModels;

namespace StoreShot.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
