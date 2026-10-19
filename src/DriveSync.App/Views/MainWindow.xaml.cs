using System.ComponentModel;
using System.Windows;
using DriveSync.App.ViewModels;

namespace DriveSync.App.Views;

public partial class MainWindow : Window
{
    private bool _isExplicitExit;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public void ForceClose()
    {
        _isExplicitExit = true;
        Close();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            // Cancel closing and minimize to notification tray
            e.Cancel = true;
            Hide();
        }
    }
}
