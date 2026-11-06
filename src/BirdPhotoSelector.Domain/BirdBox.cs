using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Globalization;
using System.Runtime.CompilerServices;
using BirdPhotoSelector.Imaging;

namespace BirdPhotoSelector;

/// <summary>显示方向图像中的归一化鸟框，原点在左上角；范围和置信度必须有效，复制后可独立交给后台推理。</summary>
public sealed class BirdBox
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Confidence { get; set; }

    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) &&
        double.IsFinite(Width) && double.IsFinite(Height) && double.IsFinite(Confidence) &&
        X >= 0 && Y >= 0 && Width > 0 && Height > 0 && X + Width <= 1.00001 && Y + Height <= 1.00001 &&
        Confidence >= 0 && Confidence <= 1;

    /// <summary>复制归一化坐标与置信度，不向后台共享可被人工修改的框对象。</summary>
    public BirdBox Copy() => new() { X = X, Y = Y, Width = Width, Height = Height, Confidence = Confidence };
}
