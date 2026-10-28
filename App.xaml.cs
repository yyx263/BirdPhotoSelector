using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BirdPhotoSelector;

public partial class App : Application
{
    internal const string SoftwareRenderingArgument = "--software-rendering";

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(argument => string.Equals(argument, SoftwareRenderingArgument, StringComparison.OrdinalIgnoreCase)))
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("操作未完成：\n" + args.Exception.Message, "羽选 · 提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
