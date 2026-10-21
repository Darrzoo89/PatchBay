using System.Windows;
using PatchBay.Model;

namespace PatchBay;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LayoutStore.Log("Unhandled: " + args.Exception);
            MessageBox.Show(args.Exception.Message, "PatchBay error", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
