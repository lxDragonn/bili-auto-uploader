using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BilibiliUploader;

public sealed record Category(int Id, string Name) { public override string ToString() => Name; }
public sealed record SeasonSection(long Id, string Title) { public override string ToString() => Title; }
public sealed record VideoSeason(long Id, string Title, IReadOnlyList<SeasonSection> Sections)
{ public override string ToString() => Title; }
public sealed class PublishPreset
{
    public const string ReplayTitleTemplate = "【直播回放{record_date:yyyy.M.d}】{title}";
    private static readonly Regex Variables = new(@"\{([^{}]*)\}");
    private static readonly Regex RecordingFilename = new(@"\A(?<stamp>[0-9]{8}-[0-9]{6}-[0-9]{3})-(?<title>[^\r\n]+)\z");
    public string TitleTemplate { get; set; } = "{filename}";
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Description { get; set; } = "";
    public int Copyright { get; set; } = 1;
    public string Source { get; set; } = "";
    public int Retries { get; set; } = 2;
    public string CoverPath { get; set; } = "";
    public long SeasonId { get; set; }
    public string SeasonTitle { get; set; } = "";
    public long SectionId { get; set; }
    public string SectionTitle { get; set; } = "";
    public string SeasonOwnerMid { get; set; } = "";
    public string Title(string path, int index, DateTime time)
    {
        if (Variables.Replace(TitleTemplate, "").IndexOfAny(['{', '}']) >= 0)
            throw new InvalidOperationException("标题模板的变量括号不完整，请使用成对的 { 和 }。");
        var filename = Path.GetFileNameWithoutExtension(path);
        var match = RecordingFilename.Match(filename);
        var recordedAt = DateTime.MinValue;
        var hasRecordingDate = match.Success && DateTime.TryParseExact(match.Groups["stamp"].Value,
            "yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out recordedAt);
        var recordingTitle = hasRecordingDate ? match.Groups["title"].Value : filename;
        string RecordingDate(string format) => hasRecordingDate ? recordedAt.ToString(format, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException("无法从文件名读取录像日期：需以 20240102-120000-123- 这样的时间戳开头。请填写实际日期，或使用 {date:yyyy.M.d} 表示上传日期。");
        // Expand only the template, so braces in a filename remain literal title text.
        return Variables.Replace(TitleTemplate, token => token.Groups[1].Value switch
        {
            "filename" => filename,
            "title" => recordingTitle,
            "index" => index.ToString(CultureInfo.InvariantCulture),
            "date" => time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "date:yyyy.M.d" => time.ToString("yyyy.M.d", CultureInfo.InvariantCulture),
            "record_date" => RecordingDate("yyyy-MM-dd"),
            "record_date:yyyy.M.d" => RecordingDate("yyyy.M.d"),
            _ => throw new InvalidOperationException($"不支持的标题变量 {token.Value}。请使用下方列出的变量。")
        });
    }
    public string[] TagList() => Tags.Split([',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
    public void Validate(string path, int index)
    {
        var title = Title(path, index, DateTime.Now);
        if (string.IsNullOrWhiteSpace(title) || title.Length > 80) throw new InvalidOperationException("展开后的标题须为 1–80 个字符，请调整标题模板。");
        if (CategoryId <= 0) throw new InvalidOperationException("请登录后读取分区并选择投稿分区。");
        var tags = TagList();
        if (tags.Length is < 1 or > 10 || tags.Any(t => t.Length > 20)) throw new InvalidOperationException("请填写 1–10 个标签，以逗号分隔，每个不超过 20 个字符。");
        if (Description.Length > 2000) throw new InvalidOperationException("简介不能超过 2000 个字符。");
        if (Copyright is not (1 or 2) || (Copyright == 2 && string.IsNullOrWhiteSpace(Source))) throw new InvalidOperationException("转载视频必须填写来源。");
        if (Source.Length > 200) throw new InvalidOperationException("转载来源不能超过 200 个字符。");
        if (Retries is < 0 or > 5) throw new InvalidOperationException("分片重试次数须在 0–5 之间。");
        if (SeasonId < 0 || SectionId < 0 || (SeasonId == 0 && SectionId != 0) ||
            (SeasonId > 0 && (SectionId == 0 || !Regex.IsMatch(SeasonOwnerMid, @"\A[0-9]+\z"))))
            throw new InvalidOperationException("请登录后重新读取合集并选择分节，或选择不加入合集。");
    }
    public PublishPreset Copy() => JsonSerializer.Deserialize<PublishPreset>(JsonSerializer.Serialize(this))!;
}

public sealed class UploadJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public string State { get; set; } = "等待上传";
    public string Title { get; set; } = "";
    public string Error { get; set; } = "";
    public string Bvid { get; set; } = "";
    public long Aid { get; set; }
    public long Cid { get; set; }
    public bool SeasonAttempted { get; set; }
    public bool SeasonAdded { get; set; }
    public string RemoteFilename { get; set; } = "";
    public PublishPreset? Preset { get; set; }
    public long ConfirmedBytes { get; set; }
    public bool PublishAttempted { get; set; }
    public static UploadJob Create(string path)
    {
        var f = new FileInfo(System.IO.Path.GetFullPath(path));
        if (!f.Exists || f.Length == 0 || !Supported(f.Extension)) throw new InvalidOperationException("请选择非空的视频文件。");
        if ((f.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("请直接选择视频文件，不使用文件链接。");
        return new() { Path = f.FullName, Size = f.Length, ModifiedUtc = f.LastWriteTimeUtc };
    }
    public static bool Supported(string extension) => new[] { ".mp4", ".flv", ".avi", ".wmv", ".mov", ".webm", ".mpeg4", ".ts", ".mpg", ".rm", ".rmvb", ".mkv", ".m4v" }.Contains(extension.ToLowerInvariant());
    public void VerifyFile()
    {
        var f = new FileInfo(Path);
        if (!f.Exists || f.Length != Size || f.LastWriteTimeUtc != ModifiedUtc) throw new IOException("文件已被修改或移走，请重新添加。");
    }
}

public sealed class LocalState
{
    public PublishPreset Preset { get; set; } = new();
    public List<UploadJob> Jobs { get; set; } = [];
    public static string DataDirectory => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BilibiliUploader");
    public static LocalState Load(string directory)
    {
        var path = System.IO.Path.Combine(directory, "queue.json");
        if (!File.Exists(path)) return new();
        var info = new FileInfo(path);
        if (info.Length > 4 * 1024 * 1024) throw new IOException("本地队列文件过大，请检查 queue.json。");
        var state = JsonSerializer.Deserialize<LocalState>(File.ReadAllText(path)) ?? throw new IOException("队列文件无效。");
        state.Jobs = state.Jobs.Take(200).ToList();
        foreach (var j in state.Jobs)
        {
            if (j.State == "已投稿" || j.State == "已跳过") continue;
            if (j.Aid > 0 && j.Preset?.SeasonId > 0)
            {
                j.State = j.SeasonAdded ? "已投稿" : j.SeasonAttempted ? "合集待核对" : "合集待处理";
                j.Error = j.SeasonAttempted && !j.SeasonAdded ? "稿件已投稿；上次加入合集结果未知，请在创作中心核对。" : "稿件已投稿，开始后只处理加入合集。";
                continue;
            }
            if (j.PublishAttempted) { j.State = "待核对"; j.Error = "上次已发出投稿请求，请先到创作中心核对，避免重复投稿。"; }
            else if (j.State is "上传中" or "准备上传" or "合并中" or "暂停中" or "上传封面")
            { j.State = "等待上传"; j.ConfirmedBytes = 0; j.Error = "上次传输已中断，开始后重新上传。"; }
        }
        return state;
    }
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "queue.json");
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, this, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(true);
        }
        File.Move(temp, path, true);
    }
}

public sealed class ApiFailure(string message) : Exception(message);
public sealed class PublishUncertain(string message) : Exception(message);
public sealed class SeasonUncertain(string message) : Exception(message);
public sealed record UploadProgress(long Confirmed, long InFlight, long Total, double Speed, string Stage);
public sealed record UploadedVideo(string Filename, long Cid = 0);
public interface IBiliApi
{
    Task<JsonElement> PreUploadAsync(string name, long size, CancellationToken token);
    Task<JsonElement> PublishAsync(object payload, CancellationToken token);
    Task<JsonElement> UploadCoverAsync(string dataUrl, CancellationToken token);
    Task<IReadOnlyList<VideoSeason>> GetSeasonsAsync(CancellationToken token);
    Task<JsonElement> AddToSeasonAsync(long sectionId, long aid, long cid, string title, CancellationToken token);
    Task<JsonElement> ArchiveAsync(long aid, CancellationToken token);
}
