using System.Net;
using System.Text;
using System.Text.Json;
using BilibiliUploader;

static class Verify
{
    static int count;
    static readonly string Root = Path.Combine(Path.GetTempPath(), "BilibiliUploader-tests-" + Guid.NewGuid().ToString("N"));
    static void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    static PublishPreset Preset => new() { CategoryId = 171, Tags = "测试,视频", TitleTemplate = "{filename}-{index}" };
    static UploadJob Job(string name = "片段.mp4", int size = 150000)
    {
        Directory.CreateDirectory(Root); var file = Path.Combine(Root, Guid.NewGuid().ToString("N") + name);
        File.WriteAllBytes(file, Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray()); return UploadJob.Create(file);
    }
    static async Task Test(string name, Func<Task> action) { await action(); count++; Console.WriteLine("PASS " + name); }
    static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    static async Task Main()
    {
        await Test("Only HTTPS Bilibili upload endpoints are accepted", () =>
        {
            Check(UposUploader.ValidateUploadUrl("//upos-sz-test.bilivideo.com", "upos://bucket/key.mp4").Scheme == "https");
            foreach (var endpoint in new[] { "http://upos-sz-test.bilivideo.com", "https://bilivideo.com.evil.example", "https://127.0.0.1", "https://user:pass@x.bilivideo.com", "https://x.bilivideo.com:444", "https://x.bilivideo.com/path" })
                Throws<ApiFailure>(() => UposUploader.ValidateUploadUrl(endpoint, "upos://bucket/key.mp4"));
            Throws<ApiFailure>(() => UposUploader.ValidateUploadUrl("https://x.bilivideo.com", "upos://bucket/../secret"));
            Check(!BiliApi.IsMember("https://member.bilibili.com.evil.example"));
            Check(!BiliApi.IsBrowserUrl("file:///c:/secret")); return Task.CompletedTask;
        });
        await Test("Presets expand filenames and validate tags, source and title", () =>
        {
            var p = Preset; p.Validate("demo.mp4", 1); Check(p.Title("demo.mp4", 1, DateTime.Now) == "demo-1");
            p.TitleTemplate = "【直播回放{date:yyyy.M.d}】示例录像";
            p.Validate("demo.mp4", 1);
            Check(p.Title("demo.mp4", 1, new DateTime(2024, 1, 2)) == "【直播回放2024.1.2】示例录像");
            p.TitleTemplate = "{date}|{date:yyyy.M.d}|{filename}|{index}";
            Check(p.Title("示例录像.mp4", 2, new DateTime(2024, 1, 2)) == "2024-01-02|2024.1.2|示例录像|2");
            p.Copyright = 2; Throws<InvalidOperationException>(() => p.Validate("demo.mp4", 1));
            p.Source = "作者来源"; p.Validate("demo.mp4", 1);
            p.Tags = ""; Throws<InvalidOperationException>(() => p.Validate("demo.mp4", 1));
            p.Tags = "ok"; p.TitleTemplate = "{unknown}"; Throws<InvalidOperationException>(() => p.Validate("demo.mp4", 1));
            return Task.CompletedTask;
        });
        await Test("Recording filenames produce the requested title using the recording date", () =>
        {
            var p = Preset; p.TitleTemplate = PublishPreset.ReplayTitleTemplate;
            var file = "20240102-120000-123-示例录像.flv";
            p.Validate(file, 1);
            Check(p.Title(file, 1, new DateTime(2024, 2, 1)) == "【直播回放2024.1.2】示例录像");
            Check(p.Title("20240102-130000-456-示例录像.flv", 2, new DateTime(2024, 2, 1)) == "【直播回放2024.1.2】示例录像");
            p.TitleTemplate = "{record_date}|{date}|{title}|{filename}";
            Check(p.Title(file, 1, new DateTime(2024, 2, 1)) == "2024-01-02|2024-02-01|示例录像|20240102-120000-123-示例录像");
            return Task.CompletedTask;
        });
        await Test("Unrecognized recording prefixes preserve titles and do not invent dates", () =>
        {
            var p = Preset;
            foreach (var file in new[] { "示例录像.mp4", "20240230-120000-123-示例录像.flv", "20240102-250000-123-示例录像.flv", "第2段-示例内容.flv" })
            {
                p.TitleTemplate = "{title}";
                Check(p.Title(file, 1, DateTime.Now) == Path.GetFileNameWithoutExtension(file));
                p.TitleTemplate = "{record_date:yyyy.M.d}";
                Throws<InvalidOperationException>(() => p.Title(file, 1, DateTime.Now));
            }
            return Task.CompletedTask;
        });
        await Test("Filename text is not expanded as variables and malformed templates are rejected", () =>
        {
            var p = Preset; p.TitleTemplate = "{title} {date:yyyy.M.d}";
            var file = "20240102-120000-123-第2段-示例与{date}.flv";
            Check(p.Title(file, 1, new DateTime(2024, 1, 3)) == "第2段-示例与{date} 2024.1.3");
            p.Validate(file, 1);
            foreach (var template in new[] { "{title", "{title}}", "{unknown}", "{}" })
            {
                p.TitleTemplate = template;
                Throws<InvalidOperationException>(() => p.Title(file, 1, DateTime.Now));
            }
            return Task.CompletedTask;
        });
        await Test("Nested live category data is mapped without invented IDs", () =>
        {
            var categories = BiliApi.ParseCategories(Json("{\"typelist\":[{\"id\":4,\"name\":\"游戏\",\"children\":[{\"id\":171,\"name\":\"电子竞技\"}]}]}"));
            Check(categories.Count == 1 && categories[0].Id == 171 && categories[0].Name == "游戏 / 电子竞技"); return Task.CompletedTask;
        });
        await Test("Browser responses preserve large numeric identifiers exactly", () =>
        {
            var body = "{\"biz_id\":9007199254740993,\"data\":{\"aid\":18446744073709551615}}";
            var response = BiliApi.ParseResponseEnvelope(Json(JsonSerializer.Serialize(new { responseBody = body })));
            Check(response.GetProperty("biz_id").GetRawText() == "9007199254740993");
            Check(response.GetProperty("data").GetProperty("aid").GetRawText() == "18446744073709551615");
            Check(UposUploader.ReadBusinessId(response) == "9007199254740993");
            Check(UposUploader.ReadBusinessId(Json("{\"biz_id\":18446744073709551615}")) == "18446744073709551615");
            Check(UposUploader.ReadBusinessId(Json("{\"biz_id\":\"18446744073709551615\"}")) == "18446744073709551615");
            return Task.CompletedTask;
        });
        await Test("Malformed browser responses fail before accessing API fields", () =>
        {
            foreach (var envelope in new[] { "null", "{}", "{\"clientError\":\"请求中断\"}", "{\"responseBody\":123}", "{\"responseBody\":\"not JSON\"}", "{\"responseBody\":\"[]\"}" })
                Throws<ApiFailure>(() => BiliApi.ParseResponseEnvelope(Json(envelope)));
            Throws<ApiFailure>(() => UposUploader.ReadBusinessId(Json("{}")));
            return Task.CompletedTask;
        });
        await Test("Whole-valued decimal upload IDs complete the transfer with an integer ID", async () =>
        {
            foreach (var rawId in new[] { "12345678901.0", "1.2345678901e10" })
            {
                var a = new FakeApi { BizIdJson = rawId };
                var h = new UploadHandler { ExpectedBizId = "12345678901" }; using var c = new HttpClient(h);
                var job = Job();
                var result = await new UposUploader(a, c).UploadAsync(job, Preset, _ => { }, CancellationToken.None);
                Check(h.Puts == 3 && h.MergeCount == 1 && job.ConfirmedBytes == job.Size && result.Filename == "video");
            }
        });
        await Test("Invalid upload IDs stop before opening an upload session or sending bytes", async () =>
        {
            foreach (var rawId in new[] { "null", "true", "{}", "0", "-1", "1.25", "1e30", "\"123\\n\"", "\"１２３\"", "\"123&other=1\"" })
            {
                var h = new UploadHandler(); using var c = new HttpClient(h);
                var api = new FakeApi { BizIdJson = rawId };
                try { await new UposUploader(api, c).UploadAsync(Job(), Preset, _ => { }, CancellationToken.None); throw new Exception("Expected invalid ID rejection"); }
                catch (ApiFailure) { Check(h.Requests == 0 && api.Published == 0); }
            }
        });
        await Test("Real file bytes are split, streamed, acknowledged and merged", async () =>
        {
            var j = Job(); var a = new FakeApi(); var h = new UploadHandler(); using var c = new HttpClient(h);
            var u = new UposUploader(a, c); var values = new List<UploadProgress>();
            var result = await u.UploadAsync(j, Preset, values.Add, CancellationToken.None);
            Check(result.Filename == "video" && j.ConfirmedBytes == j.Size);
            Check(h.Data.SelectMany(x => x).SequenceEqual(File.ReadAllBytes(j.Path)), "Uploaded bytes differ");
            Check(h.Puts == 3 && h.MergeCount == 1 && a.Published == 0);
            Check(values.All(p => p.Confirmed + p.InFlight <= p.Total) && values.Any(p => p.InFlight > 0));
        });
        await Test("Retry resends one part without double counting acknowledged bytes", async () =>
        {
            var j = Job(); var h = new UploadHandler { FailFirst = true }; using var c = new HttpClient(h);
            await new UposUploader(new FakeApi(), c).UploadAsync(j, Preset, _ => { }, CancellationToken.None);
            Check(h.Puts == 4 && j.ConfirmedBytes == j.Size);
        });
        await Test("HTTP rejection never merges or publishes", async () =>
        {
            var a = new FakeApi(); var h = new UploadHandler { Reject = true }; using var c = new HttpClient(h);
            try { await new UposUploader(a, c).UploadAsync(Job(), Preset, _ => { }, CancellationToken.None); throw new Exception("Expected rejection"); }
            catch (ApiFailure) { Check(h.Puts == 1 && h.MergeCount == 0 && a.Published == 0); }
        });
        await Test("Missing merge confirmation does not create a publishable result", async () =>
        {
            var h = new UploadHandler { BadMerge = true }; using var c = new HttpClient(h);
            try { await new UposUploader(new FakeApi(), c).UploadAsync(Job(), Preset, _ => { }, CancellationToken.None); throw new Exception("Expected error"); }
            catch (ApiFailure) { Check(h.MergeCount == 1); }
        });
        await Test("Selected file changes are rejected before upload", async () =>
        {
            var j = Job(); File.AppendAllText(j.Path, "changed"); var a = new FakeApi(); using var c = new HttpClient(new UploadHandler());
            try { await new UposUploader(a, c).UploadAsync(j, Preset, _ => { }, CancellationToken.None); throw new Exception("Expected error"); }
            catch (IOException) { Check(a.PreUploads == 0); }
        });
        await Test("Pause holds the next chunk until resumed", async () =>
        {
            var h = new UploadHandler(); using var c = new HttpClient(h); var u = new UposUploader(new FakeApi(), c);
            var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requested = false;
            var task = u.UploadAsync(Job(), Preset, p => { if (!requested && p.Confirmed == 65536 && p.Stage == "上传中") { requested = true; u.PauseRequested = true; } if (p.Stage == "暂停中") paused.TrySetResult(); }, CancellationToken.None);
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(h.Puts == 1); u.PauseRequested = false; await task;
            Check(h.Puts == 3);
        });
        await Test("Cancellation stops transfer and does not publish", async () =>
        {
            using var cancel = new CancellationTokenSource(); var h = new UploadHandler(); var a = new FakeApi(); using var c = new HttpClient(h);
            try { await new UposUploader(a, c).UploadAsync(Job(), Preset, p => { if (p.InFlight > 0) cancel.Cancel(); }, cancel.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { Check(h.MergeCount == 0 && a.Published == 0); }
        });
        await Test("Two files publish sequentially with separate metadata and durable results", async () =>
        {
            var st = new LocalState { Jobs = [Job(), Job()] }; var a = new FakeApi(); using var c = new HttpClient(new UploadHandler());
            var dir = Path.Combine(Root, "queue"); var runner = new QueueRunner(st, dir, new(a, c));
            await runner.RunAsync(Preset, _ => Task.CompletedTask, CancellationToken.None);
            Check(a.Published == 2 && st.Jobs.All(j => j.State == "已投稿"));
            var payload = a.Payloads[0]; Check(payload.GetProperty("tag").GetString() == "测试,视频");
            Check(payload.GetProperty("tid").GetInt32() == 171 && payload.GetProperty("videos").GetArrayLength() == 1);
            Check(LocalState.Load(dir).Jobs.All(j => j.State == "已投稿"));
            Check(!File.ReadAllText(Path.Combine(dir, "queue.json")).Contains("test-upload-secret"));
        });
        await Test("Lost publishing response stops queue and requires reconciliation after restart", async () =>
        {
            var st = new LocalState { Jobs = [Job(), Job()] }; var a = new FakeApi { LosePublish = true }; using var c = new HttpClient(new UploadHandler());
            var dir = Path.Combine(Root, "uncertain"); var runner = new QueueRunner(st, dir, new(a, c));
            await runner.RunAsync(Preset, _ => Task.CompletedTask, CancellationToken.None);
            Check(a.Published == 1 && st.Jobs[0].State == "待核对" && st.Jobs[1].State == "等待上传");
            Check(LocalState.Load(dir).Jobs[0].State == "待核对");
            try { await runner.RunAsync(Preset, _ => Task.CompletedTask, CancellationToken.None); throw new Exception("Expected block"); }
            catch (InvalidOperationException) { Check(a.Published == 1); }
        });
        await Test("Explicit publication rejection is retryable without uploading the video again", async () =>
        {
            var st = new LocalState { Jobs = [Job()] }; var a = new FakeApi { RejectPublish = true }; var h = new UploadHandler(); using var c = new HttpClient(h);
            var runner = new QueueRunner(st, Path.Combine(Root, "rejected"), new(a, c));
            await runner.RunAsync(Preset, _ => Task.CompletedTask, CancellationToken.None);
            Check(st.Jobs[0].State == "失败" && !st.Jobs[0].PublishAttempted); a.RejectPublish = false;
            await runner.RunAsync(Preset, _ => Task.CompletedTask, CancellationToken.None);
            Check(a.PreUploads == 1 && a.Published == 2 && h.Puts == 3 && st.Jobs[0].State == "已投稿");
        });
        await Test("Crash checkpoints fail closed without automatic reposting", () =>
        {
            var dir = Path.Combine(Root, "crashed"); var st = new LocalState { Jobs = [new() { State = "投稿中", PublishAttempted = true }] };
            st.Save(dir); Check(LocalState.Load(dir).Jobs[0].State == "待核对"); return Task.CompletedTask;
        });
        await Test("Actual HTTP streaming reports bytes delivered to a local test server", async () =>
        {
            var listener = new HttpListener();
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); tcp.Start(); var port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
            var data = new byte[2 * 1024 * 1024]; new Random(123).NextBytes(data);
            var receive = Task.Run(async () =>
            {
                var ctx = await listener.GetContextAsync(); using var memory = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(memory); ctx.Response.StatusCode = 200; ctx.Response.Close(); return memory.ToArray();
            });
            long sent = 0; var updates = 0; using var client = new HttpClient(); using var stream = new MemoryStream(data);
            using var content = new ProgressContent(stream, data.Length, n => { sent += n; updates++; });
            using var response = await client.PutAsync($"http://127.0.0.1:{port}/test", content);
            Check(response.IsSuccessStatusCode && sent == data.Length && updates > 10 && (await receive).SequenceEqual(data)); listener.Close();
        });
        Console.WriteLine($"{count} tests passed. No Bilibili uploads or publications were made.");
    }
    sealed class FakeApi : IBiliApi
    {
        public int PreUploads, Published; public bool LosePublish, RejectPublish;
        public string BizIdJson = "123";
        public List<JsonElement> Payloads = [];
        public Task<JsonElement> PreUploadAsync(string name, long size, CancellationToken token)
        { PreUploads++; return Task.FromResult(Json("{\"auth\":\"test-upload-secret\",\"endpoint\":\"//upos-test.bilivideo.com\",\"upos_uri\":\"upos://bucket/video.mp4\",\"biz_id\":" + BizIdJson + ",\"chunk_size\":65536}")); }
        public Task<JsonElement> PublishAsync(object payload, CancellationToken token)
        {
            Published++; Payloads.Add(Json(JsonSerializer.Serialize(payload)));
            if (LosePublish) throw new HttpRequestException("lost response");
            return Task.FromResult(RejectPublish ? Json("{\"code\":21001,\"message\":\"分区无效\"}") : Json("{\"code\":0,\"data\":{\"aid\":1234,\"bvid\":\"BVtest\"}}"));
        }
    }
    sealed class UploadHandler : HttpMessageHandler
    {
        public int Requests, Puts, MergeCount; public bool FailFirst, Reject, BadMerge;
        public string ExpectedBizId = "123";
        public List<byte[]> Data = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken token)
        {
            Requests++;
            Check(r.Headers.GetValues("X-Upos-Auth").Single() == "test-upload-secret");
            if (r.Method == HttpMethod.Put)
            {
                Puts++; using var data = new MemoryStream(); await r.Content!.CopyToAsync(data, token); Data.Add(data.ToArray());
                token.ThrowIfCancellationRequested();
                if (Reject) return new(HttpStatusCode.Forbidden);
                if (FailFirst && Puts == 1) return new(HttpStatusCode.ServiceUnavailable);
                return new(HttpStatusCode.OK) { Content = new StringContent("") };
            }
            if (r.RequestUri!.Query.Contains("uploads&")) return new(HttpStatusCode.OK) { Content = new StringContent("{\"upload_id\":\"session\",\"OK\":1}") };
            MergeCount++;
            Check(r.RequestUri.Query.EndsWith("&biz_id=" + ExpectedBizId), "Merge must use the exact normalized integer business ID");
            return new(HttpStatusCode.OK) { Content = new StringContent(BadMerge ? "{}" : "{\"OK\":1}") };
        }
    }
}
