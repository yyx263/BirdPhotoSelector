using System.Windows;

namespace BirdPhotoSelector;

public partial class ExportConflictDialog : Window
{
    private readonly ExportConflict _conflict;
    public ExportConflictDecision Decision { get; private set; } = new(ExportConflictAction.Cancel);

    public ExportConflictDialog(ExportConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        _conflict = conflict;
        InitializeComponent();
        SourcePathText.Text = conflict.SourcePath;
        TargetPathText.Text = conflict.TargetPath;
        SourcePathText.ToolTip = conflict.SourcePath;
        TargetPathText.ToolTip = conflict.TargetPath;
        OverwriteButton.IsEnabled = conflict.CanOverwrite;
        if (!conflict.CanOverwrite)
        {
            ProtectionText.Text = conflict.Explanation ?? "目标属于受保护的原件，请重命名或取消。";
            ProtectionText.Visibility = Visibility.Visible;
        }
    }

    private void Overwrite_Click(object sender, RoutedEventArgs e) => Complete(ExportConflictAction.Overwrite);
    private void Rename_Click(object sender, RoutedEventArgs e) => Complete(ExportConflictAction.Rename);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Complete(ExportConflictAction.Cancel);

    private void Complete(ExportConflictAction action)
    {
        if (action == ExportConflictAction.Overwrite && !_conflict.CanOverwrite) return;
        Decision = new(action, action != ExportConflictAction.Cancel && ApplyToRemainingBox.IsChecked == true);
        // Unshown fixture windows exercise the same actual button events safely.
        if (IsVisible) DialogResult = action != ExportConflictAction.Cancel;
        else Close();
    }
}
