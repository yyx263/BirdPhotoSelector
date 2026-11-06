using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BirdPhotoSelector;
using BirdPhotoSelector.Imaging;
using BirdPhotoSelector.Imaging.Native;
using BirdPhotoSelector.Imaging.Portable;
using SkiaSharp;

/// <summary>跨平台实际适配验证；只生成私有文件，不启动桌面窗口或触及用户照片。</summary>
internal static class Program
{
    private static int _checks;
    /// <summary>运行固定像素、元数据、系统文件和名称检索检查，失败以非零码退出。</summary>
    private static async Task<int> Main()
    {
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.Portable"));
        string root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Boundaries(); Resampling(); await Jpeg(root); await Files(root);
            _checks += SpeciesSearchChecks.Run();
            _checks += await RawExposureBiasChecks.RunAsync();
            await Raw(root);
            Console.WriteLine($"PASS: {_checks} portable adapter assertions on {RuntimeInformation.OSDescription}."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != parent || !Guid.TryParseExact(Path.GetFileName(root), "N", out _)) throw new InvalidOperationException("Unsafe cleanup target.");
            Directory.Delete(root, recursive: true);
        }
    }
    /// <summary>验证部署后实际引用图，名称检索和路径基础层没有偷偷依赖 Windows 图形或语言组件。</summary>
    private static void Boundaries()
    {
        foreach (var type in new[] { typeof(FilePaths), typeof(PortablePhotoImageDecoder), typeof(NativeRawDecoder), typeof(SystemFileOperations), typeof(SpeciesSearchIndex) })
            Check(!type.Assembly.GetReferencedAssemblies().Any(a => a.Name is "PresentationCore" or "PresentationFramework" or "WindowsBase" or "BirdPhotoSelector.Imaging.Windows"),
                type.Assembly.GetName().Name + " has no Windows graphics dependency");
        Check(!typeof(SpeciesSearchIndex).Assembly.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .Any(m => m.GetCustomAttribute<DllImportAttribute>() is not null), "Chinese search has no system NLS or native entry point");
        string volumeRoot = Path.GetPathRoot(Path.GetFullPath("."))!;
        Check(FilePaths.Canonicalize(volumeRoot) == volumeRoot, "path normalization retains filesystem roots");
        Check(FilePaths.Comparer.Equals("BIRD.JPG", "bird.jpg") == OperatingSystem.IsWindows(), "filesystem path case rules do not merge distinct Unix paths");
        Check(PhotoImageFormats.IsJpeg("bird.JPG") && PhotoImageFormats.IsRaw("bird.NEF"), "format names remain case-insensitive on every host");
    }
    /// <summary>手工推导的四角与中点金样，防止各后端缩放中心、RGB 次序或舍入发生偏移。</summary>
    private static void Resampling()
    {
        var source = new Rgb24ImageFrame(2, 2, new byte[] {255,0,0, 0,0,255, 0,255,0, 255,255,255});
        byte[] expected = {255,0,0, 128,0,128, 0,0,255, 128,128,0, 128,128,128, 128,128,255, 0,255,0, 128,255,128, 255,255,255};
        Check(source.Resize(3,3).CopyRgb24().SequenceEqual(expected) && ImageResampling.Policy == "BilinearPixelCenter-v1", "uniform resampling matches independent RGB corner and midpoint golden bytes");
        int[][] corners = { new[]{0,1,2,3}, new[]{1,0,3,2}, new[]{3,2,1,0}, new[]{2,3,0,1}, new[]{0,2,1,3}, new[]{2,0,3,1}, new[]{3,1,2,0}, new[]{1,3,0,2} };
        byte[] pixels = {1,0,0, 2,0,0, 3,0,0, 4,0,0};
        for (int o=1;o<=8;o++)
            Check(ImageResampling.Orient(new Rgb24ImageFrame(2,2,pixels),o).CopyRgb24().Where((_,i)=>i%3==0).SequenceEqual(corners[o-1].Select(i=>(byte)(i+1))), "EXIF rotation/mirror golden mapping " + o);
    }
    /// <summary>生成独立 JPEG/EXIF，不借助 WPF 编码器，逐方向检查真实解码、拍摄参数和文件释放。</summary>
    private static async Task Jpeg(string root)
    {
        var decoder = new PortablePhotoImageDecoder();
        using var bitmap = new SKBitmap(48,32,SKColorType.Rgba8888,SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint())
        {
            foreach (var cell in new[] {(0,0,SKColors.Red),(24,0,SKColors.Lime),(0,16,SKColors.Blue),(24,16,SKColors.Yellow)})
            { paint.Color=cell.Item3;canvas.DrawRect(cell.Item1,cell.Item2,24,16,paint); }
        }
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Jpeg,100); byte[] encoded=data.ToArray();
        int[][] cornerOrder = {new[]{0,1,2,3},new[]{1,0,3,2},new[]{3,2,1,0},new[]{2,3,0,1},new[]{0,2,1,3},new[]{2,0,3,1},new[]{3,1,2,0},new[]{1,3,0,2}};
        for(int o=1;o<=8;o++)
        {
            string path=Path.Combine(root,$"方向{o}-鳥類.jpg"); byte[] bytes=Exif(encoded,o,-1);await File.WriteAllBytesAsync(path,bytes);
            var info=decoder.ReadInfo(path);var frame=decoder.Decode(path);
            Check(info.PixelWidth==(o>=5?32:48)&&info.PixelHeight==(o>=5?48:32)&&frame.PixelWidth==info.PixelWidth&&frame.PixelHeight==info.PixelHeight, "portable JPEG info and pixels share oriented coordinates " + o);
            Check(info.Metadata.Camera=="PORTABLE CAMERA"&&info.Metadata.CapturedAt=="2024-06-07 08:09:10"&&info.Metadata.FocalLength=="600 mm"&&
                info.Metadata.Aperture=="f/6.3"&&info.Metadata.Shutter=="1/1000 s"&&info.Metadata.Iso=="800"&&info.Metadata.ExposureBias=="-0.33 EV", "portable EXIF retains all requested fields and signed exposure " + o);
            var rgb=frame.CopyRgb24();var actual=new List<int>();
            foreach(var p in new[]{(5,5),(frame.PixelWidth-6,5),(5,frame.PixelHeight-6),(frame.PixelWidth-6,frame.PixelHeight-6)})
            {int i=(p.Item2*frame.PixelWidth+p.Item1)*3;actual.Add(rgb[i]>180&&rgb[i+1]>180?3:rgb[i]>180?0:rgb[i+1]>180?1:2);}
            Check(actual.SequenceEqual(cornerOrder[o-1]),"actual JPEG corners follow EXIF rotation and mirrors " + o);
            var small=decoder.Decode(path,16);Check(small.PixelWidth==(o>=5?11:16)&&small.PixelHeight==(o>=5?16:11),"thumbnail scaling preserves existing encoded-width convention " + o);
            using(var lease=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Check(lease.Length==bytes.Length,"returned image and info retain no input file lock " + o);
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes),"JPEG decoding preserves source bytes " + o);
        }
        foreach(var bias in new int?[]{0,null})
        {string path=Path.Combine(root,$"ev-{bias?.ToString()??"missing"}.jpg");await File.WriteAllBytesAsync(path,Exif(encoded,1,bias));Check(decoder.ReadInfo(path).Metadata.ExposureBias==(bias is null?"—":"0 EV"),"zero shooting EV differs from missing EV");}
        string invalid=Path.Combine(root,"fake.jpg");await File.WriteAllTextAsync(invalid,"not a photograph");
        Reject<InvalidDataException>(()=>decoder.Decode(invalid),"invalid JPEG bytes cannot be accepted by extension alone");
        using var canceled=new CancellationTokenSource();canceled.Cancel();Reject<OperationCanceledException>(()=>decoder.Decode(invalid,cancellationToken:canceled.Token),"JPEG pre-cancellation reaches backend");
    }
    /// <summary>验证真实文件身份及硬链接，再用独立回收站实现检查禁止危险的失败降级。</summary>
    private static async Task Files(string root)
    {
        string one=Path.Combine(root,"实体-1.jpg"),two=Path.Combine(root,"实体-2.jpg"),link=Path.Combine(root,"硬链接.jpg");
        await File.WriteAllBytesAsync(one,await File.ReadAllBytesAsync(Path.Combine(root,"方向1-鳥類.jpg")));File.Copy(one,two);var platform=new SystemFileOperations();
        using(var a=File.OpenRead(one))using(var b=File.OpenRead(two))
        {var x=platform.TryGetIdentity(a);var y=platform.TryGetIdentity(b);Check(x is not null&&y is not null&&x!=y,"native file identity distinguishes equal-content separate files");}
        bool linked=OperatingSystem.IsWindows()?CreateHardLink(link,one,IntPtr.Zero):Link(one,link)==0;
        if(linked)
        {
            using var a=File.OpenRead(one);using var b=File.OpenRead(link);
            Check(platform.TryGetIdentity(a)==platform.TryGetIdentity(b),"native file identity detects hardlink aliases");
            var imported = await new PhotoImportService(new PortablePhotoImageDecoder(),platform).ImportAsync(new[]{one,link},Array.Empty<PhotoItem>());
            Check(imported.Added.Count==1&&imported.SkippedDuplicate==1,"import de-duplicates physical aliases without folding distinct Unix paths");
        }
        else Console.WriteLine("SKIP: filesystem does not permit hardlink fixtures.");
        var fake=new FakeTrash { Failure=new UnauthorizedAccessException("generated permission failure") };
        var safe=new SystemFileOperations(new UnknownIdentity(),fake);bool denied=false;
        try{safe.RecycleOrDelete(two);}catch(UnauthorizedAccessException){denied=true;}
        Check(denied&&File.Exists(two),"trash permission failures never degrade to permanent deletion");
        fake.Failure=new IOException("generated full disk");bool io=false;try{safe.RecycleOrDelete(two);}catch(IOException){io=true;}
        Check(io&&File.Exists(two),"trash IO failures preserve original files");
        fake.Failure=null;fake.Outcome=TrashOutcome.Unsupported;safe.RecycleOrDelete(two);Check(!File.Exists(two),"explicit lack of a recycle capability follows the authorized deletion fallback");
        string previous=Path.Combine(root,"replace-target"),temporary=Path.Combine(root,"replace-temp"),backup=Path.Combine(root,"replace-backup");
        await File.WriteAllTextAsync(previous,"previous");await File.WriteAllTextAsync(temporary,"new");File.Replace(temporary,previous,backup);
        Check(await File.ReadAllTextAsync(previous)=="new"&&await File.ReadAllTextAsync(backup)=="previous"&&!File.Exists(temporary),"host atomic replacement preserves a recovery backup");
        if(!OperatingSystem.IsWindows())
        {
            string upper=Path.Combine(root,"CASE.jpg"),lower=Path.Combine(root,"case.jpg");await File.WriteAllTextAsync(upper,"upper");await File.WriteAllTextAsync(lower,"lower");
            using var u=File.OpenRead(upper);using var l=File.OpenRead(lower);
            if(platform.TryGetIdentity(u)!=platform.TryGetIdentity(l))
            {
                File.Copy(Path.Combine(root,"方向1-鳥類.jpg"),upper,true);File.Copy(Path.Combine(root,"方向1-鳥類.jpg"),lower,true);
                var imports=await new PhotoImportService(new PortablePhotoImageDecoder(),platform).ImportAsync(new[]{upper,lower},Array.Empty<PhotoItem>());
                Check(!FilePaths.Equal(upper,lower)&&imports.Added.Count==2&&imports.Errors.Count==0,"case-sensitive volume keeps distinct same-spelling photos separate");
            }
            else Console.WriteLine("INFO: current volume is case-insensitive; conservative path keys preserve both spellings without pretending distinct physical identity.");
        }
    }
    /// <summary>可用时对公开样本额外验证真实 RAW 像素与 UTF-8 路径；最小运行仍有生成 DNG 的真实元数据检查。</summary>
    private static async Task Raw(string root)
    {
        string? samples=Environment.GetEnvironmentVariable("BIRD_RAW_SAMPLES");
        string? original=samples is null?null:Path.Combine(samples,"Canon_EOS_450D_RAW.CR2");
        if(original is null||!File.Exists(original)){Console.WriteLine("SKIP: optional RAW pixel sample requires BIRD_RAW_SAMPLES/Canon_EOS_450D_RAW.CR2.");return;}
        byte[] hash=SHA256.HashData(await File.ReadAllBytesAsync(original));string path=Path.Combine(root,"原始鳥類-é-😀.CR2");File.Copy(original,path);
        var decoder=new PortablePhotoImageDecoder();var info=decoder.ReadInfo(path);var frame=decoder.Decode(path,320);
        Check(frame.PixelWidth==320&&frame.PixelHeight>0&&info.PixelWidth>frame.PixelWidth&&info.Metadata.Camera!="—","true RAW decoding and metadata work through UTF-8 without a WPF dependency");
        Check(frame.CopyRgb24().Distinct().Count()>16,"portable RAW returns actual nonuniform sensor-derived pixels");
        Check(hash.AsEnumerable().SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(original)))&&hash.AsEnumerable().SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(path))),"RAW metadata and pixel operations preserve sample and Unicode-copy bytes");
    }
    /// <summary>构造独立的小端 EXIF APP1，偏移均相对 TIFF 起点，包含拍摄参数与方向。</summary>
    private static byte[] Exif(byte[] jpeg,int orientation,int? bias)
    {
        var t=new byte[512];t[0]=t[1]=(byte)'I';W16(t,2,42);W32(t,4,8);W16(t,8,4);
        int payload=62;
        void Entry(int pos,ushort tag,ushort type,uint count,uint value){W16(t,pos,tag);W16(t,pos+2,type);W32(t,pos+4,count);W32(t,pos+8,value);}
        uint Text(string value){byte[] b=Encoding.ASCII.GetBytes(value+"\0");uint offset=(uint)payload;b.CopyTo(t,payload);payload+=b.Length;return offset;}
        Entry(10,272,2,16,Text("PORTABLE CAMERA"));Entry(22,274,3,1,(uint)orientation);Entry(34,306,2,20,Text("2024:06:07 08:09:10"));
        int exif=payload;Entry(46,34665,4,1,(uint)exif);int count=bias.HasValue?6:5;W16(t,exif,(ushort)count);payload=exif+2+count*12+4;
        void Rational(int index,ushort tag,int n,int d,bool signed=false){int value=payload;W32(t,value,unchecked((uint)n));W32(t,value+4,(uint)d);payload+=8;Entry(exif+2+index*12,tag,signed?(ushort)10:(ushort)5,1,(uint)value);}
        Rational(0,33434,1,1000);Rational(1,33437,63,10);Entry(exif+26,34855,3,1,800);Rational(3,37386,600,1);
        Entry(exif+50,36867,2,20,Text("2024:06:07 08:09:10"));if(bias is{} b)Rational(5,37380,b,3,true);
        byte[] segment=new byte[payload+6];Encoding.ASCII.GetBytes("Exif\0\0").CopyTo(segment,0);t.AsSpan(0,payload).CopyTo(segment.AsSpan(6));
        var output=new byte[jpeg.Length+segment.Length+4];jpeg.AsSpan(0,2).CopyTo(output);output[2]=255;output[3]=225;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4,2),(ushort)(segment.Length+2));segment.CopyTo(output,6);jpeg.AsSpan(2).CopyTo(output.AsSpan(6+segment.Length));return output;
    }
    /// <summary>按 TIFF 小端顺序写整数。</summary>
    private static void W16(byte[] b,int p,ushort n)=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p,2),n);
    /// <summary>按 TIFF 小端顺序写长整数。</summary>
    private static void W32(byte[] b,int p,uint n)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p,4),n);
    /// <summary>期望明确异常，未抛出则检查失败。</summary>
    private static void Reject<T>(Action action,string message)where T:Exception{bool hit=false;try{action();}catch(T){hit=true;}Check(hit,message);}
    /// <summary>记录可观察结果。</summary>
    private static void Check(bool pass,string message){if(!pass)throw new InvalidOperationException(message);_checks++;Console.WriteLine("  OK PORTABLE "+message);}
    /// <summary>未知身份不能冒充稳定实体文件。</summary>
    private sealed class UnknownIdentity:IFileIdentityProvider{public FileIdentity? TryGetIdentity(FileStream stream)=>null;}
    /// <summary>回收站故障替身只供生成文件的策略验证。</summary>
    private sealed class FakeTrash:ITrashBackend{public Exception? Failure;public TrashOutcome Outcome;public TrashOutcome MoveToTrash(string path){if(Failure is not null)throw Failure;return Outcome;}}
    [DllImport("kernel32.dll",EntryPoint="CreateHardLinkW",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateHardLink(string link,string original,IntPtr attributes);
    [DllImport("libc",EntryPoint="link",SetLastError=true)]private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)]string original,[MarshalAs(UnmanagedType.LPUTF8Str)]string link);
}
