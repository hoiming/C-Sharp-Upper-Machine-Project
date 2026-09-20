using System.Windows;

namespace EnvMonitor.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public LedTabViewModel LedViewModel { get; }

    public MainWindow(MainViewModel viewModel, LedTabViewModel ledViewModel)
    {
        LedViewModel = ledViewModel;
        InitializeComponent();
        DataContext = viewModel;
    }
}