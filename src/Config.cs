using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArchClock;

/// <summary>
/// 設定は %LOCALAPPDATA%\ArchClock\config.json に置く。
/// ウィジェットの位置は画面比率(0..1)で持つので、解像度やDPIが変わっても崩れない。
/// </summary>
public sealed class AppConfig
{
    public int Version { get; set; } = 2;

    /// <summary>共通の見た目。</summary>
    public ThemeConfig Theme { get; set; } = new();

    /// <summary>モニターごとのウィジェット配置。キーは \\.\DISPLAY1 などのデバイス名。</summary>
    public Dictionary<string, List<WidgetConfig>> Monitors { get; set; } = new();

    /// <summary>壁紙を下地として描くか。false なら黒。</summary>
    public bool ShowWallpaper { get; set; } = true;

    /// <summary>メトリクスの更新間隔(ミリ秒)。</summary>
    public int MetricsIntervalMs { get; set; } = 1500;

    /// <summary>Windows へのログオン時に自動起動するか。</summary>
    public bool RunAtStartup { get; set; } = true;

    // ---------------------------------------------------------------

    // 保存形式は画面やページとやり取りする形と揃えて camelCase にする。
    // 手で config.json を編集したときに名前が食い違わないようにするため。
    // 読み込みは大文字小文字を無視するので、古い PascalCase のファイルもそのまま開ける。
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Path_ => System.IO.Path.Combine(Paths.DataDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path_), Json);
                if (c is not null) return c;
            }
        }
        catch (Exception ex) { Log.Write(ex); }

        var fresh = Default();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            // 書き込み中に落ちても壊れないよう、別名で書いてから差し替える
            var tmp = Path_ + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, Path_, overwrite: true);
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>初回起動時の中身。主モニターに時計だけを置く。</summary>
    public static AppConfig Default()
    {
        var c = new AppConfig();
        var primary = Screen.PrimaryScreen?.DeviceName ?? @"\\.\DISPLAY1";
        c.Monitors[primary] = new List<WidgetConfig>
        {
            new()
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Type = "clock",
                X = 0.62, Y = 0.66, W = 520, H = 230,
                Options = new Dictionary<string, object?>
                {
                    ["showSeconds"] = true,
                    ["showDate"]    = true,
                    ["showMeta"]    = true,
                    ["align"]       = "right",
                    ["size"]        = 176,
                    ["weight"]      = 250,
                },
            },
        };
        return c;
    }

    /// <summary>知らないモニターが繋がれたら空の配置を作る。</summary>
    public List<WidgetConfig> ForMonitor(string device)
    {
        if (!Monitors.TryGetValue(device, out var list))
        {
            list = new List<WidgetConfig>();
            Monitors[device] = list;
        }
        return list;
    }
}

public sealed class ThemeConfig
{
    public string Font       { get; set; } = "SF Pro Display";
    public string JpFont     { get; set; } = "Noto Sans JP";
    public string Tint       { get; set; } = "#ffffff";
    public string Accent     { get; set; } = "#ffffff";
    /// <summary>0..1。ウィジェット全体の不透明度。</summary>
    public double Opacity    { get; set; } = 1.0;
    /// <summary>文字を読みやすくする影の強さ 0..1。</summary>
    public double Shadow     { get; set; } = 0.55;
    /// <summary>パネルの地。none / glass / solid</summary>
    public string Surface    { get; set; } = "none";
}

public sealed class WidgetConfig
{
    public string Id   { get; set; } = "";
    public string Type { get; set; } = "clock";

    /// <summary>
    /// 位置。画面幅・高さに対する比率 0..1。
    /// X は「揃え」で決まる留めた辺の位置(左揃えなら左端、右揃えなら右端)。
    /// Y は常に上端。
    /// </summary>
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>
    /// X が留めた辺の位置として保存されているか。
    /// 以前は揃えに関係なく左端で持っていたので、初回に一度だけ読み替える。
    /// </summary>
    public bool Anchored { get; set; }

    /// <summary>大きさ。CSS ピクセル。</summary>
    public double W { get; set; } = 400;
    public double H { get; set; } = 200;

    public bool Visible { get; set; } = true;

    public Dictionary<string, object?> Options { get; set; } = new();
}

/// <summary>置き場所をひとまとめにする。</summary>
public static class Paths
{
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArchClock");

    public static string WebDir => Path.Combine(AppContext.BaseDirectory, "web");

    public static string LogFile => Path.Combine(DataDir, "archclock.log");
}
