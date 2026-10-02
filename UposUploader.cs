using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BilibiliUploader;

public sealed class UposUploader(IBiliApi api, HttpClient client)
{
    public bool PauseRequested { get; set; }
    public async Task WaitForResumeAsync(CancellationToken token)
    { while (PauseRequested) await Task.Delay(200, token); }
    private long wireBytes;
    private readonly Stopwatch clock = new();
    private readonly Queue<(double Time, long Bytes)> samples = new();
    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(30), PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BilibiliUploader/" + typeof(UposUploader).Assembly.GetName().Version!.ToString(3));
        http.DefaultRequestHeaders.Referrer = new Uri("https://member.bilibili.com/");
        http.DefaultRequestHeaders.Add("Origin", "https://member.bilibili.com");
        return http;
    }
    public static Uri ValidateUploadUrl(string endpoint, string uposUri)
    {
        if (endpoint.StartsWith("//")) endpoint = "https:" + endpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            !uri.Host.EndsWith(".bilivideo.com", StringComparison.OrdinalIgnoreCase))
            throw new ApiFailure("平台返回了非预期的上传域名，已停止传输。");
        if (!Regex.IsMatch(uposUri, @"^upos://[A-Za-z0-9_-]+/[A-Za-z0-9_./-]+$") ||
            uposUri.Contains("..") || uposUri.Length > 2048)
            throw new ApiFailure("平台返回的上传地址无效。");
        return new Uri(uri, uposUri[7..]);
    }
    private static string Text(JsonElement data, string key) => data.TryGetProperty(key, out var v) ? v.ToString() : "";
    public static string ReadBusinessId(JsonElement data)
    {
        if (!data.TryGetProperty("biz_id", out var value)) throw new ApiFailure("预上传响应缺少上传业务编号 biz_id。");
        var id = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        // An integer carried as a JSON number may be rendered with a zero fraction.
        // Decimal keeps up to 20-digit IDs exact; do not convert identifiers via double.
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number > 0 && decimal.Truncate(number) == number)
            id = number.ToString("0", CultureInfo.InvariantCulture);
        if (!Regex.IsMatch(id, @"\A[0-9]{1,20}\z") || id.All(c => c == '0'))
            throw new ApiFailure("预上传响应的业务编号 biz_id 不是有效的正整数，已停止上传。");
        return id;
    }
    public static void CheckCode(JsonElement data)
    {
        if (data.TryGetProperty("code", out var code) && code.ToString() != "0")
            throw new ApiFailure($"Bilibili 返回 {code}：{SafeMessage(Text(data, "message"))}");
    }
    public static string SafeMessage(string value)
    {
        // Never display signed URLs or server-supplied credential-looking text.
        var result = Regex.Replace(value, @"https?://\S+", "[链接已省略]");
        result = Regex.Replace(result, @"(?i)(auth|token|csrf|cookie)[=: ]+\S+", "$1=[已省略]");
        return new string(result.Where(c => !char.IsControl(c) || c == '\n').Take(300).ToArray());
    }
    private UploadProgress Progress(long confirmed, long current, long total, string state)
    {
        var now = clock.Elapsed.TotalSeconds;
        samples.Enqueue((now, wireBytes));
        while (samples.Count > 2 && samples.Peek().Time < now - 3) samples.Dequeue();
        var oldest = samples.Peek();
        var speed = now - oldest.Time > .05 ? (wireBytes - oldest.Bytes) / (now - oldest.Time) : 0;
        return new(confirmed, current, total, state == "上传中" ? speed : 0, state);
    }
    private async Task WaitIfPaused(long confirmed, long total, Action<UploadProgress> report, CancellationToken token)
    {
        while (PauseRequested)
        {
            report(Progress(confirmed, 0, total, "暂停中"));
            await Task.Delay(200, token);
        }
    }
    private async Task<JsonElement> JsonRequestAsync(HttpMethod method, Uri uri, string auth, object? data, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Upos-Auth", auth);
        if (data != null) request.Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new ApiFailure($"上传服务返回 HTTP {(int)response.StatusCode}。");
        await using var incoming = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var memory = new MemoryStream(); var buffer = new byte[16384];
        while (true)
        {
            var read = await incoming.ReadAsync(buffer, timeout.Token);
            if (read == 0) break;
            if (memory.Length + read > 1024 * 1024) throw new ApiFailure("上传响应超出限制。");
            memory.Write(buffer, 0, read);
        }
        using var json = JsonDocument.Parse(memory.ToArray());
        var root = json.RootElement;
        if ((root.TryGetProperty("OK", out var ok) && ok.ToString() != "1") || (root.TryGetProperty("code", out var code) && code.ToString() != "0"))
            throw new ApiFailure("上传服务拒绝了操作，请检查登录状态或稍后重试。");
        return root.Clone();
    }
    private static Uri Query(Uri uri, string query) => new(uri.AbsoluteUri + "?" + query);
    private static string Escape(string s) => Uri.EscapeDataString(s);

    public async Task<UploadedVideo> UploadAsync(UploadJob job, PublishPreset preset, Action<UploadProgress> report, CancellationToken token)
    {
        job.VerifyFile();
        // Keep the selected file locked against writes for the entire transfer.
        await using var file = new FileStream(job.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        job.VerifyFile();
        wireBytes = 0; samples.Clear(); clock.Restart(); samples.Enqueue((0, 0));
        report(Progress(0, 0, job.Size, "准备上传"));
        var pre = await api.PreUploadAsync(Path.GetFileName(job.Path), job.Size, token);
        CheckCode(pre);
        var uri = ValidateUploadUrl(Text(pre, "endpoint"), Text(pre, "upos_uri"));
        var auth = Text(pre, "auth");
        if (auth.Length is 0 or > 8192 || auth.Any(char.IsControl)) throw new ApiFailure("上传凭据无效，请重新登录。");
        var bizId = ReadBusinessId(pre);
        var chunkSize = long.TryParse(Text(pre, "chunk_size"), out var cs) && cs > 0 ? cs : 4 * 1024 * 1024;
        if (chunkSize is < 65536 or > 64 * 1024 * 1024) throw new ApiFailure("服务器分片大小超出支持范围。");
        var chunks = (int)Math.Ceiling((double)job.Size / chunkSize);
        if (chunks > 50000) throw new ApiFailure("视频分片过多。");
        var init = await JsonRequestAsync(HttpMethod.Post, Query(uri, "uploads&output=json"), auth, null, token);
        var uploadId = Text(init, "upload_id");
        if (string.IsNullOrWhiteSpace(uploadId) || uploadId.Length > 1024) throw new ApiFailure("服务器未返回上传会话。");
        long confirmed = 0;
        for (var index = 0; index < chunks; index++)
        {
            await WaitIfPaused(confirmed, job.Size, report, token);
            var count = Math.Min(chunkSize, job.Size - confirmed);
            var partUri = Query(uri, $"partNumber={index + 1}&uploadId={Escape(uploadId)}&chunk={index}&chunks={chunks}&size={count}&start={confirmed}&end={confirmed + count}&total={job.Size}");
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                file.Position = confirmed;
                long current = 0;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(10));
                    using var request = new HttpRequestMessage(HttpMethod.Put, partUri);
                    request.Headers.Add("X-Upos-Auth", auth);
                    var lastReport = -1.0;
                    request.Content = new ProgressContent(file, count, bytes =>
                    {
                        current += bytes; Interlocked.Add(ref wireBytes, bytes);
                        if (clock.Elapsed.TotalSeconds - lastReport >= .1 || current == count)
                        { lastReport = clock.Elapsed.TotalSeconds; report(Progress(confirmed, current, job.Size, "上传中")); }
                    });
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        if ((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.RequestTimeout)
                            throw new ApiFailure($"上传被拒绝（HTTP {(int)response.StatusCode}），队列已暂停。");
                        throw new HttpRequestException("上传服务暂时不可用。");
                    }
                    confirmed += count;
                    job.ConfirmedBytes = confirmed;
                    report(Progress(confirmed, 0, job.Size, "上传中"));
                    break;
                }
                catch (Exception ex) when ((ex is HttpRequestException || ex is OperationCanceledException) && !token.IsCancellationRequested && attempt < preset.Retries)
                {
                    // Retry the identical part number; acknowledged chunks are never counted twice.
                    report(Progress(confirmed, 0, job.Size, $"分片重试 {attempt + 1}/{preset.Retries}"));
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 << attempt, 16)), token);
                    await WaitIfPaused(confirmed, job.Size, report, token);
                }
            }
        }
        await WaitIfPaused(confirmed, job.Size, report, token);
        report(Progress(confirmed, 0, job.Size, "合并中"));
        var complete = await JsonRequestAsync(HttpMethod.Post, Query(uri,
            $"output=json&name={Escape(Path.GetFileName(job.Path))}&profile=ugcupos%2Fbup&uploadId={Escape(uploadId)}&biz_id={Escape(bizId)}"),
            auth, new { parts = Enumerable.Range(1, chunks).Select(n => new { partNumber = n, eTag = "etag" }).ToArray() }, token);
        if (!complete.TryGetProperty("OK", out var done) || done.ToString() != "1") throw new ApiFailure("服务器尚未确认合并完成，未提交投稿。");
        return new(Path.GetFileNameWithoutExtension(uri.AbsolutePath));
    }

    public async Task<(long Aid, string Bvid)> PublishAsync(UploadJob job, CancellationToken token)
    {
        var p = job.Preset ?? throw new InvalidOperationException("没有投稿预设。");
        if (string.IsNullOrWhiteSpace(job.RemoteFilename)) throw new InvalidOperationException("视频还没有上传完成。");
        var payload = new
        {
            copyright = p.Copyright, source = p.Copyright == 2 ? p.Source : "", title = job.Title,
            tid = p.CategoryId, tag = string.Join(',', p.TagList()), desc_format_id = 0, desc = p.Description,
            dynamic = "", cover = "", no_reprint = p.Copyright == 1 ? 1 : 0, open_elec = 0,
            videos = new[] { new { filename = job.RemoteFilename, title = job.Title, desc = "" } }
        };
        JsonElement response;
        try { response = await api.PublishAsync(payload, token); }
        catch { throw new PublishUncertain("投稿请求结果未知。请到创作中心核对后再操作，客户端不会自动重发。"); }
        // A nonzero reply is an explicit rejection, unlike a lost response.
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("code", out _))
            throw new PublishUncertain("投稿响应无法识别，请到创作中心核对。");
        CheckCode(response);
        if (!response.TryGetProperty("data", out var data) || !long.TryParse(Text(data, "aid"), out var aid) || aid <= 0)
            throw new PublishUncertain("投稿响应缺少稿件编号，请到创作中心核对。");
        return (aid, Text(data, "bvid"));
    }
}

public sealed class ProgressContent(Stream source, long length, Action<int> progress) : HttpContent
{
    protected override bool TryComputeLength(out long size) { size = length; return true; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, CancellationToken.None);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => WriteAsync(stream, token);
    private async Task WriteAsync(Stream destination, CancellationToken token)
    {
        Headers.ContentType ??= new MediaTypeHeaderValue("application/octet-stream");
        var buffer = new byte[65536];
        long left = length;
        while (left > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(left, buffer.Length)), token);
            if (read == 0) throw new IOException("视频文件意外结束。");
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
            progress(read); left -= read;
        }
    }
}
