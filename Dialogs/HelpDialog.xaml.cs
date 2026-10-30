using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace BirdPhotoSelector;

public partial class HelpDialog : Window
{
    public HelpDialog(string text)
    {
        InitializeComponent();
        HelpText.Text = text;
    }

    private void Repository_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(RepositoryLink.NavigateUri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { MessageBox.Show(this, "无法打开浏览器，请复制此地址访问：\n" + RepositoryLink.NavigateUri.AbsoluteUri, "羽选 · GitHub"); }
    }
}
