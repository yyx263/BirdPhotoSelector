using System.Runtime.InteropServices;
/// <summary>从旧宿主保留的核心安全场景，全部使用跨平台图像和文件接口，不加载 WPF。</summary>
internal static class Program
{
    /// <summary>生成夹具的副本、冲突、原件保护和鸟编号验证；失败返回非零退出码。</summary>
    private static async Task<int> Main()
    {
        try
        {
            int checks = await ExportConflictChecks.RunAsync() + await SpeciesExportChecks.RunAsync() + await ContinuousBirdNumberChecks.RunAsync();
            Console.WriteLine($"PASS: {checks} workflow assertions on {RuntimeInformation.OSDescription}."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
    }
}
