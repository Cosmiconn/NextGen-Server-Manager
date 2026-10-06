using System.Windows;
using NextGen.Fiesta.ServerManager.ViewModels;

namespace NextGen.Fiesta.ServerManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }
}
