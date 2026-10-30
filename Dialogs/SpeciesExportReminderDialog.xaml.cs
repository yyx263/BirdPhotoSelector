using System.Windows;

namespace BirdPhotoSelector;

public partial class SpeciesExportReminderDialog : Window
{
    public SpeciesExportReminderDialog(IReadOnlyList<SpeciesExportReviewItem> items)
    {
        InitializeComponent();
        HeadingText.Text = $"有 {items.Count} 张照片建议核对鸟种";
        int unclassified = items.Count(item => item.Reasons.HasFlag(SpeciesExportReviewReason.NoSpecies));
        int unidentified = items.Count(item => item.Reasons.HasFlag(SpeciesExportReviewReason.UnidentifiedRegions));
        int low = items.Count(item => item.Reasons.HasFlag(SpeciesExportReviewReason.LowConfidence));
        SummaryText.Text = $"未分类 {unclassified} 张 · 有选区未识别 {unidentified} 张 · 低置信度 {low} 张\n" +
            $"低置信度参考线：{SpeciesExportReview.DefaultLowConfidenceThreshold:P0}，仅检查 AI 结果。";
        DetailsText.Text = string.Join("\n\n", items.Take(30).Select(item =>
            item.Photo.FileName + "  ·  " + Describe(item.Reasons))) + (items.Count > 30 ? "\n\n其余照片也会按相同规则处理。" : "");
    }

    internal static string Describe(SpeciesExportReviewReason reasons)
    {
        var labels = new List<string>();
        if (reasons.HasFlag(SpeciesExportReviewReason.NoSpecies)) labels.Add("未分类");
        if (reasons.HasFlag(SpeciesExportReviewReason.UnidentifiedRegions)) labels.Add("有选区未识别");
        if (reasons.HasFlag(SpeciesExportReviewReason.LowConfidence)) labels.Add("低置信度");
        return string.Join("、", labels);
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
