using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Globalization;
using System.Runtime.CompilerServices;
using BirdPhotoSelector.Imaging;

namespace BirdPhotoSelector;

/// <summary>一条选区识别或无框人工鸟种记录。Number 是持久身份，DisplayNumber 是重新排好的可见编号，两者不能混用。</summary>
public sealed class DetectedBird : INotifyPropertyChanged
{
    private SpeciesMatch? _species;
    private SpeciesIdentity? _manualSpecies;
    private int _number;
    private int _displayNumber;
    private string _recognitionStatus = "待识别";
    // Number remains the persisted identity used to match asynchronous results.
    public int Number { get => _number; set { if (_number == value) return; _number = value; Notify(); } }
    public int DisplayNumber
    {
        get => _displayNumber;
        internal set { if (_displayNumber == value) return; _displayNumber = value; Notify(); Notify(nameof(DisplayNumberText)); }
    }
    public bool IsManual { get; init; }
    public BirdBox Box { get; init; } = new();
    public SpeciesIdentity? ManualSpecies
    {
        get => _manualSpecies;
        set
        {
            if (value is not null && !value.IsValid) throw new ArgumentException("人工鸟种必须包含有效学名。", nameof(value));
            if (_manualSpecies == value) return;
            _manualSpecies = value;
            if (value is not null) _species = null;
            Notify(); Notify(nameof(Species)); Notify(nameof(HasRegion)); Notify(nameof(DisplayNumberText)); NotifySpecies();
        }
    }
    public bool HasRegion => ManualSpecies is null && Box.IsValid;
    public string DisplayNumberText => HasRegion ? $"{DisplayNumber:00}" : "手动";
    public SpeciesIdentity? EffectiveSpecies => ManualSpecies ?? SpeciesIdentity.FromMatch(Species);
    /// <summary>深拷贝鸟框与结果数据，供后台快照和撤销使用，不共享可变框。</summary>
    public DetectedBird Copy() => new()
    {
        Number = Number, IsManual = IsManual, Box = Box.Copy(), Species = ManualSpecies is null ? Species : null,
        ManualSpecies = ManualSpecies, RecognitionStatus = RecognitionStatus
    };
    public SpeciesMatch? Species
    {
        get => _species;
        set { if (_species == value) return; _species = value; Notify(); NotifySpecies(); }
    }
    public string RecognitionStatus
    {
        get => _recognitionStatus;
        set { if (_recognitionStatus == value) return; _recognitionStatus = value; Notify(); Notify(nameof(SpeciesName)); }
    }
    public string SpeciesName => EffectiveSpecies?.DisplayName ?? RecognitionStatus;
    public string ScientificName => EffectiveSpecies?.ScientificName ?? "";
    public string SimilarityText => ManualSpecies is null && Species is { IsValid: true } match ? $"{match.Similarity:0.000}" : "—";
    public string ConfidenceText => ManualSpecies is null && Species is { IsValid: true, HasValidConfidence: true } match ? $"{match.Confidence:P1}" : "—";
    public string EvidenceTooltip => ManualSpecies is not null ? "人工指定鸟种，无识别框及 AI 分数。" : Species is { IsValid: true } match
        ? $"原始相似度：{match.Similarity:0.000}\n" +
          (match.HasValidConfidence ? "置信度反映候选区分程度，未经准确率校准。" : "旧结果尚无置信度，请重新识别。") +
          (string.IsNullOrWhiteSpace(match.AlternativeName) ? "" : $"\n相近候选：{match.AlternativeName}（{match.RunnerUpSimilarity:0.000}）")
        : RecognitionStatus;
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>通知人工身份、显示名、分数和提示文字等鸟种派生属性。</summary>
    private void NotifySpecies()
    {
        Notify(nameof(EffectiveSpecies)); Notify(nameof(SpeciesName)); Notify(nameof(ScientificName));
        Notify(nameof(SimilarityText)); Notify(nameof(ConfidenceText)); Notify(nameof(EvidenceTooltip));
    }
    /// <summary>发布一项属性变化，调用方负责会话线程约束。</summary>
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
