using System.Windows;
using MultiBtOut.ViewModels;

namespace MultiBtOut;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Stop capture, join render threads, release WASAPI clients.
        if (DataContext is MainViewModel vm)
        {
            vm.Dispose();
        }

        base.OnClosed(e);
    }
}
