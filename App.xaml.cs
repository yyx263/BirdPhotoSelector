using System.IO;
using System.Windows;

namespace BirdPhotoSelector;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("操作未完成：\n" + args.Exception.Message, "羽选 · 提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
