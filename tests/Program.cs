using System.Net;
using System.Drawing;
using System.Drawing.Imaging;
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
    static string Cover(int width = 1146, int height = 717, bool transparent = false)
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".png");
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(transparent ? Color.Transparent : Color.CornflowerBlue);
        bitmap.Save(path, ImageFormat.Png); return path;
    }
    static string ExifCover()
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".jpg");
        using var bitmap = new Bitmap(1000, 1600);
        using var original = new MemoryStream(); bitmap.Save(original, ImageFormat.Jpeg);
        var description = Encoding.ASCII.GetBytes("PRIVATE-CAMERA-METADATA\0");
        using var exif = new MemoryStream(); using (var writer = new BinaryWriter(exif, Encoding.UTF8, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("Exif\0\0II")); writer.Write((ushort)42); writer.Write((uint)8);
            writer.Write((ushort)2);
            writer.Write((ushort)0x10e); writer.Write((ushort)2); writer.Write((uint)description.Length); writer.Write((uint)38);
            writer.Write((ushort)0x112); writer.Write((ushort)3); writer.Write((uint)1); writer.Write((ushort)6); writer.Write((ushort)0);
            writer.Write((uint)0); writer.Write(description);
        }
        using var output = File.Create(path);
        output.Write(new byte[] { 0xff, 0xd8, 0xff, 0xe1, (byte)((exif.Length + 2) >> 8), (byte)(exif.Length + 2) });
        output.Write(exif.ToArray()); output.Write(original.ToArray().AsSpan(2)); return path;
    }
    static PublishPreset SeasonPreset()
    {
        var preset = Preset;
        preset.SeasonId = 100; preset.SeasonTitle = "示例合集";
        preset.SectionId = 200; preset.SectionTitle = "默认分节"; preset.SeasonOwnerMid = "12345";
        return preset;
    }

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
        await Test("Official collection responses retain exact IDs and exclude paid, forbidden or empty collections", () =>
        {
            var collections = BiliApi.ParseSeasons(Json("""
                {"seasons":[
                  {"season":{"id":9007199254740993,"title":"示例合集","is_pay":0,"forbid":false},"sections":{"sections":[{"id":9007199254740994,"title":"默认分节"},{"id":9007199254740994,"title":"重复分节"},{"id":202,"title":"付费分节","has_charging_pay":1},{"id":203},{"id":204,"title":""},{"id":205,"title":"  "}]}},
                  {"season":{"id":2,"title":"付费合集","is_pay":1},"sections":{"sections":[{"id":202,"title":"默认分节"}]}},
                  {"season":{"id":3,"title":"不可用合集","forbid":true},"sections":{"sections":[{"id":203,"title":"默认分节"}]}},
                  {"season":{"id":4,"title":"充电合集","has_charging_pay":1},"sections":{"sections":[{"id":204,"title":"默认分节"}]}},
                  {"season":{"id":5,"title":"缺少分节"},"sections":{"sections":[]}},
                  {"season":{"id":0,"title":"无效编号"},"sections":{"sections":[{"id":205,"title":"默认分节"}]}},
                  null,{}, {"season":{"id":6,"title":"缺少结构"},"sections":[]}
                ]}
                """));
            Check(collections.Count == 1 && collections[0].Id == 9007199254740993 && collections[0].Title == "示例合集");
            Check(collections[0].Sections.Count == 4 && collections[0].Sections[0].Id == 9007199254740994);
            Check(collections[0].Sections.Skip(1).Select(section => section.Id).SequenceEqual(new long[] { 203, 204, 205 }));
            Check(collections[0].Sections.Skip(1).All(section => section.Title == "默认分节"));
            foreach (var invalid in new[] { "null", "[]", "{}", "{\"seasons\":{}}" })
                Throws<ApiFailure>(() => BiliApi.ParseSeasons(Json(invalid)));
            return Task.CompletedTask;
        });
        await Test("Old queues retain default automatic covers and no collection", () =>
        {
            var directory = Path.Combine(Root, "legacy-preset"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "queue.json"), "{\"Preset\":{\"TitleTemplate\":\"{filename}\",\"CategoryId\":171,\"Tags\":\"示例\"},\"Jobs\":[]}");
            var p = LocalState.Load(directory).Preset;
            p.Validate("example.mp4", 1);
            Check(p.CoverPath == "" && p.SeasonId == 0 && p.SectionId == 0 && p.SeasonOwnerMid == "");
            return Task.CompletedTask;
        });
        await Test("Preset copies and saved queues preserve cover and collection choices independently", () =>
        {
            var p = SeasonPreset(); p.CoverPath = Cover(); var copy = p.Copy();
            p.SeasonId = 999; p.SeasonTitle = "另一个合集"; p.SectionId = 888; p.SectionTitle = "另一分节";
            p.SeasonOwnerMid = "67890"; p.CoverPath = "";
            Check(copy.SeasonId == 100 && copy.SeasonTitle == "示例合集" && copy.SectionId == 200 && copy.SectionTitle == "默认分节");
            Check(copy.SeasonOwnerMid == "12345" && File.Exists(copy.CoverPath));
            var dir = Path.Combine(Root, "new-preset"); new LocalState { Preset = copy }.Save(dir);
            var restored = LocalState.Load(dir).Preset;
            Check(restored.CoverPath == copy.CoverPath && restored.SeasonId == 100 && restored.SectionId == 200 && restored.SeasonOwnerMid == "12345");
            return Task.CompletedTask;
        });
        await Test("Collection presets reject missing owners, orphan sections and invalid identifiers", () =>
        {
            var p = SeasonPreset(); p.Validate("example.mp4", 1);
            foreach (var mid in new[] { "", "123\n", "１２３", "owner" })
            { p.SeasonOwnerMid = mid; Throws<InvalidOperationException>(() => p.Validate("example.mp4", 1)); }
            p = SeasonPreset(); p.SeasonId = -1; Throws<InvalidOperationException>(() => p.Validate("example.mp4", 1));
            p = SeasonPreset(); p.SectionId = -1; Throws<InvalidOperationException>(() => p.Validate("example.mp4", 1));
            p = SeasonPreset(); p.SectionId = 0; Throws<InvalidOperationException>(() => p.Validate("example.mp4", 1));
            p = SeasonPreset(); p.SeasonId = 0; Throws<InvalidOperationException>(() => p.Validate("example.mp4", 1));
            return Task.CompletedTask;
        });
        await Test("Cover conversion can be repeated, releases files and flattens transparency onto white", () =>
        {
            var path = Cover(1146, 717, true); var first = CoverImage.Read(path); var second = CoverImage.Read(path);
            Check(first.SequenceEqual(second));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var encoded = new MemoryStream(first); using var image = new Bitmap(encoded);
            Check(image.RawFormat.Guid == ImageFormat.Jpeg.Guid && image.Width == 1146 && image.Height == 717);
            var color = image.GetPixel(20, 15); Check(color.R > 245 && color.G > 245 && color.B > 245 && color.A == 255);
            return Task.CompletedTask;
        });
        await Test("Covers are scaled to fit 1920 pixels without distorting aspect ratio", () =>
        {
            var bytes = CoverImage.Read(Cover(3840, 2400));
            using var stream = new MemoryStream(bytes); using var image = Image.FromStream(stream);
            Check(image.Width == 1920 && image.Height == 1200 && bytes.Length < CoverImage.MaxFileBytes);
            return Task.CompletedTask;
        });
        await Test("Camera orientation is applied and source metadata is removed from uploaded covers", () =>
        {
            var path = ExifCover();
            using (var source = Image.FromFile(path))
                Check(source.PropertyIdList.Contains(0x10e) && source.PropertyIdList.Contains(0x112), "Fixture must contain real EXIF properties");
            var bytes = CoverImage.Read(path); using var stream = new MemoryStream(bytes); using var image = Image.FromStream(stream);
            Check(image.Width == 1600 && image.Height == 1000, "EXIF rotation should be applied before stripping metadata");
            Check(!image.PropertyIdList.Contains(0x10e) && !image.PropertyIdList.Contains(0x112));
            Check(!Encoding.ASCII.GetString(bytes).Contains("PRIVATE-CAMERA-METADATA"));
            return Task.CompletedTask;
        });
        await Test("Corrupt, empty, oversized and renamed non-image covers are rejected locally", () =>
        {
            var path = Path.Combine(Root, "invalid-cover.jpg");
            File.WriteAllText(path, "not an image"); Throws<InvalidOperationException>(() => CoverImage.Read(path));
            File.WriteAllBytes(path, []); Throws<InvalidOperationException>(() => CoverImage.Read(path));
            using (var stream = File.Create(path)) stream.SetLength(CoverImage.MaxFileBytes + 1L);
            Throws<InvalidOperationException>(() => CoverImage.Read(path));
            using (var bitmap = new Bitmap(10, 10)) bitmap.Save(path, ImageFormat.Bmp);
            Throws<InvalidOperationException>(() => CoverImage.Read(path));
            var wrongExtension = Path.ChangeExtension(Cover(), ".txt"); File.Copy(Cover(), wrongExtension);
            Throws<InvalidOperationException>(() => CoverImage.Read(wrongExtension));
            return Task.CompletedTask;
        });
        await Test("Only official archive image addresses can become publication covers", () =>
        {
            Check(UposUploader.ValidateCoverUrl("//i0.hdslb.com/bfs/archive/test.jpg") == "https://i0.hdslb.com/bfs/archive/test.jpg");
            Check(UposUploader.ValidateCoverUrl("http://i0.hdslb.com/bfs/archive/test.jpg").StartsWith("https://"));
            Check(UposUploader.ValidateCoverUrl("https://i0.biliimg.com/bfs/archive/test.jpg").StartsWith("https://"));
            foreach (var url in new[] { "file:///c:/cover.jpg", "https://127.0.0.1/bfs/archive/test.jpg", "https://i0.hdslb.com.evil.example/bfs/archive/test.jpg", "https://user:pass@i0.hdslb.com/bfs/archive/test.jpg", "https://i0.hdslb.com:444/bfs/archive/test.jpg", "https://i0.hdslb.com/other/test.jpg", "https://i0.hdslb.com/bfs/archive/test.jpg?token=secret", "https://i0.hdslb.com/bfs/archive/test.jpg#fragment" })
                Throws<ApiFailure>(() => UposUploader.ValidateCoverUrl(url));
            return Task.CompletedTask;
        });
        await Test("Covers outside platform size or aspect ratio constraints are rejected", () =>
        {
            foreach (var size in new[] { new Size(959, 600), new Size(960, 599), new Size(1200, 1200), new Size(1920, 600) })
                Throws<InvalidOperationException>(() => CoverImage.Read(Cover(size.Width, size.Height)));
            return Task.CompletedTask;
        });
        await Test("Oversized decoded images are rejected even when their compressed file is small", () =>
        {
            var path = Cover(8001, 5000); Check(new FileInfo(path).Length < CoverImage.MaxFileBytes);
            Throws<InvalidOperationException>(() => CoverImage.Read(path)); return Task.CompletedTask;
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
            Check(a.CoverUploads == 0 && a.SeasonReads == 0);
            Check(payload.GetProperty("cover").GetString() == "" && !payload.TryGetProperty("season_id", out _) && !payload.TryGetProperty("section_id", out _));
        });
        await Test("Two videos reuse one normalized cover and join the selected section after publication", async () =>
        {
            var p = SeasonPreset(); p.CoverPath = Cover();
            var st = new LocalState { Jobs = [Job(), Job()] }; var a = new FakeApi(); using var c = new HttpClient(new UploadHandler());
            a.DuringCoverUpload = () => File.WriteAllText(p.CoverPath, "cover changed after batch snapshot");
            var dir = Path.Combine(Root, "cover-and-season"); var runner = new QueueRunner(st, dir, new(a, c));
            await runner.RunAsync(p, _ => Task.CompletedTask, CancellationToken.None);
            Check(a.CoverUploads == 1 && a.Published == 2 && a.PreUploads == 2 && a.SeasonReads >= 2 && a.SeasonAdds == 2 && st.Jobs.All(j => j.State == "已投稿" && j.SeasonAdded));
            Check(a.CoverData.Count == 1 && a.CoverData[0].StartsWith("data:image/jpeg;base64,"));
            using var imageData = new MemoryStream(Convert.FromBase64String(a.CoverData[0].Split(',')[1]));
            using var image = Image.FromStream(imageData); Check(image.RawFormat.Guid == ImageFormat.Jpeg.Guid);
            foreach (var payload in a.Payloads)
            {
                Check(payload.GetProperty("cover").GetString() == FakeApi.CoverUrl);
                Check(!payload.TryGetProperty("season_id", out _) && !payload.TryGetProperty("section_id", out _));
                Check(!payload.TryGetProperty("CoverPath", out _) && !payload.GetRawText().Contains(Root));
            }
            Check(a.Additions.All(add => add.SectionId == 200 && add.Aid == 1234 && add.Cid == 123));
            Check(a.Additions.Select(add => add.Title).SequenceEqual(st.Jobs.Select(job => job.Title)));
            Check(!File.ReadAllText(Path.Combine(dir, "queue.json")).Contains("data:image/"));
        });
        await Test("Cover rejection and malformed cover responses never upload or publish video", async () =>
        {
            foreach (var result in new[] { "{\"code\":-101,\"message\":\"请先登录\"}", "{}", "{\"code\":0,\"data\":{}}", "{\"code\":0,\"data\":{\"url\":\"https://evil.example/image.jpg\"}}" })
            {
                var p = Preset; p.CoverPath = Cover();
                var st = new LocalState { Jobs = [Job(), Job()] }; var a = new FakeApi { CoverResponse = result };
                var h = new UploadHandler(); using var c = new HttpClient(h);
                var dir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
                await new QueueRunner(st, dir, new(a, c)).RunAsync(p, _ => Task.CompletedTask, CancellationToken.None);
                Check(a.CoverUploads == 1 && a.PreUploads == 0 && a.Published == 0 && h.Requests == 0);
                Check(st.Jobs.All(j => !j.PublishAttempted && j.State != "待核对") && st.Jobs[1].State == "等待上传");
                Check(LocalState.Load(dir).Jobs.All(j => j.State != "待核对"));
            }
        });
        await Test("Lost cover response is retryable and never treated as an uncertain video publication", async () =>
        {
            var p = Preset; p.CoverPath = Cover(); var st = new LocalState { Jobs = [Job()] };
            var a = new FakeApi { LoseCover = true }; using var c = new HttpClient(new UploadHandler());
            var runner = new QueueRunner(st, Path.Combine(Root, "lost-cover"), new(a, c));
            await runner.RunAsync(p, _ => Task.CompletedTask, CancellationToken.None);
            Check(a.PreUploads == 0 && a.Published == 0 && st.Jobs[0].State == "失败" && !st.Jobs[0].PublishAttempted);
            a.LoseCover = false;
            await runner.RunAsync(p, _ => Task.CompletedTask, CancellationToken.None);
            Check(a.CoverUploads == 2 && a.Published == 1 && st.Jobs[0].State == "已投稿");
        });
        await Test("Cancellation during cover upload stops without a publish uncertainty checkpoint", async () =>
        {
            var p = Preset; p.CoverPath = Cover(); var st = new LocalState { Jobs = [Job(), Job()] };
            using var cancel = new CancellationTokenSource();
            var a = new FakeApi { DuringCoverUpload = cancel.Cancel }; using var c = new HttpClient(new UploadHandler());
            await new QueueRunner(st, Path.Combine(Root, "cancel-cover"), new(a, c)).RunAsync(p, _ => Task.CompletedTask, cancel.Token);
            Check(a.CoverUploads == 1 && a.PreUploads == 0 && a.Published == 0);
            Check(st.Jobs[0].State == "已停止" && !st.Jobs[0].PublishAttempted && st.Jobs[1].State == "等待上传");
        });
        await Test("Deleted collections and sections from another collection fail before transferring videos", async () =>
        {
            foreach (var seasons in new IReadOnlyList<VideoSeason>[] { [], [new(100, "示例合集", [new(201, "其他分节")]), new(101, "另一合集", [new(200, "默认分节")])] })
            {
                var p = SeasonPreset(); var st = new LocalState { Jobs = [Job()] };
                var a = new FakeApi { Seasons = seasons }; var h = new UploadHandler(); using var c = new HttpClient(h);
                await new QueueRunner(st, Path.Combine(Root, Guid.NewGuid().ToString("N")), new(a, c)).RunAsync(p, _ => Task.CompletedTask, CancellationToken.None);
                Check(a.SeasonReads == 1 && a.PreUploads == 0 && a.Published == 0 && h.Requests == 0);
                Check(st.Jobs[0].State == "失败" && !st.Jobs[0].PublishAttempted);
            }
        });
        await Test("A collection removed during transfer is caught before publication and can be changed for retry", async () =>
        {
            var p = SeasonPreset(); var st = new LocalState { Jobs = [Job()] };
            var a = new FakeApi { RemoveSeasonAfterRead = true }; var h = new UploadHandler(); using var c = new HttpClient(h);
            var runner = new QueueRunner(st, Path.Combine(Root, "season-removed"), new(a, c));
            await runner.RunAsync(p, _ => Task.CompletedTask, CancellationToken.None);
            Check(a.PreUploads == 1 && a.SeasonReads == 2 && a.Published == 0 && st.Jobs[0].RemoteFilename == "video");
            Check(st.Jobs[0].State == "失败" && !st.Jobs[0].PublishAttempted);
            await runner.RunAsync(Preset, _ => Task.CompletedTask, CancellationToken.None);
            Check(a.PreUploads == 1 && a.Published == 1 && h.Puts == 3 && st.Jobs[0].State == "已投稿");
            Check(!a.Payloads[0].TryGetProperty("season_id", out _));
        });
        await Test("Explicit collection rejection retries only the addition using its original preset", async () =>
        {
            var p = SeasonPreset(); p.CoverPath = Cover(); var st = new LocalState { Jobs = [Job(), Job()] };
            var a = new FakeApi { RejectSeasonAdd = true }; var h = new UploadHandler(); using var c = new HttpClient(h);
            var dir = Path.Combine(Root, "season-add-rejected"); var runner = new QueueRunner(st, dir, new(a, c));
            await runner.RunAsync(p, _ => Task.CompletedTask, CancellationToken.None);
            Check(a.Published == 1 && a.SeasonAdds == 1 && a.PreUploads == 1 && a.CoverUploads == 1);
            Check(st.Jobs[0].State == "合集待处理" && st.Jobs[0].Aid == 1234 && !st.Jobs[0].SeasonAdded && !st.Jobs[0].SeasonAttempted);
            Check(st.Jobs[1].State == "等待上传");
            var restored = LocalState.Load(dir); Check(restored.Jobs[0].State == "合集待处理");
            restored.Jobs.RemoveAt(1); a.RejectSeasonAdd = false;
            // A later UI preset must not silently redirect the already-published video.
            var changedPreset = Preset; changedPreset.TitleTemplate = "不应用的新标题";
            var oldTitle = restored.Jobs[0].Title;
            await new QueueRunner(restored, dir, new(a, c)).RunAsync(changedPreset, _ => Task.CompletedTask, CancellationToken.None);
            Check(a.Published == 1 && a.PreUploads == 1 && a.CoverUploads == 1 && h.Puts == 3 && a.SeasonAdds == 2);
            Check(restored.Jobs[0].State == "已投稿" && restored.Jobs[0].SeasonAdded);
            Check(a.Additions[1].SectionId == 200 && a.Additions[1].Title == oldTitle);
        });
        await Test("Lost collection response persists an uncertainty checkpoint without reposting", async () =>
        {
            var st = new LocalState { Jobs = [Job(), Job()] }; var a = new FakeApi { LoseSeasonAdd = true }; using var c = new HttpClient(new UploadHandler());
            var dir = Path.Combine(Root, "season-add-uncertain"); var runner = new QueueRunner(st, dir, new(a, c));
            await runner.RunAsync(SeasonPreset(), _ => Task.CompletedTask, CancellationToken.None);
            Check(a.Published == 1 && a.SeasonAdds == 1 && st.Jobs[0].State == "合集待核对" && st.Jobs[0].SeasonAttempted && !st.Jobs[0].SeasonAdded);
            Check(st.Jobs[1].State == "等待上传");
            var restored = LocalState.Load(dir); Check(restored.Jobs[0].State == "合集待核对");
            try { await new QueueRunner(restored, dir, new(a, c)).RunAsync(SeasonPreset(), _ => Task.CompletedTask, CancellationToken.None); throw new Exception("Expected collection reconciliation block"); }
            catch (InvalidOperationException) { Check(a.Published == 1 && a.SeasonAdds == 1 && a.PreUploads == 1); }
        });
        await Test("A process interruption after the collection request cannot trigger a second addition", () =>
        {
            var dir = Path.Combine(Root, "season-add-crash");
            new LocalState { Jobs = [new() { State = "合集待处理", Aid = 1234, Cid = 123, Preset = SeasonPreset(), PublishAttempted = true, SeasonAttempted = true }] }.Save(dir);
            var job = LocalState.Load(dir).Jobs[0]; Check(job.State == "合集待核对" && job.Aid == 1234 && job.SeasonAttempted);
            return Task.CompletedTask;
        });
        await Test("Legacy uploaded videos without a CID use archive metadata and are not transferred again", async () =>
        {
            var job = Job(); job.RemoteFilename = "video"; job.ConfirmedBytes = job.Size; job.State = "失败";
            var st = new LocalState { Jobs = [job] }; var a = new FakeApi(); var h = new UploadHandler(); using var c = new HttpClient(h);
            await new QueueRunner(st, Path.Combine(Root, "legacy-cid"), new(a, c)).RunAsync(SeasonPreset(), _ => Task.CompletedTask, CancellationToken.None);
            Check(a.PreUploads == 0 && h.Requests == 0 && a.Published == 1 && a.ArchiveReads == 1 && a.SeasonAdds == 1);
            Check(a.Additions[0].Cid == 456 && job.Cid == 456 && job.State == "已投稿" && job.SeasonAdded);
        });
        await Test("Missing legacy CID leaves only collection work pending and never repeats publication", async () =>
        {
            var job = Job(); job.RemoteFilename = "video"; job.State = "失败";
            var st = new LocalState { Jobs = [job] }; var a = new FakeApi { ArchiveResponse = "{\"code\":0,\"data\":{\"videos\":[]}}" }; using var c = new HttpClient(new UploadHandler());
            var runner = new QueueRunner(st, Path.Combine(Root, "missing-cid"), new(a, c));
            await runner.RunAsync(SeasonPreset(), _ => Task.CompletedTask, CancellationToken.None);
            Check(a.Published == 1 && a.SeasonAdds == 0 && job.State == "合集待处理" && job.Aid == 1234);
            a.ArchiveResponse = FakeApi.GoodArchiveResponse;
            await runner.RunAsync(Preset, _ => Task.CompletedTask, CancellationToken.None);
            Check(a.Published == 1 && a.PreUploads == 0 && a.SeasonAdds == 1 && job.State == "已投稿");
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
        await Test("Invalid publication or collection status codes require reconciliation and cannot be retried automatically", async () =>
        {
            foreach (var rawCode in new[] { "null", "\"unknown\"", "0.5", "true", "{}", "2147483648" })
            foreach (var collectionStage in new[] { false, true })
            {
                var response = "{\"code\":" + rawCode + ",\"data\":{\"aid\":1234,\"bvid\":\"BVtest\"}}";
                var a = new FakeApi();
                if (collectionStage) a.SeasonResponse = response; else a.PublishResponse = response;
                var st = new LocalState { Jobs = [Job(), Job()] }; using var c = new HttpClient(new UploadHandler());
                var dir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
                var preset = collectionStage ? SeasonPreset() : Preset;
                await new QueueRunner(st, dir, new(a, c)).RunAsync(preset, _ => Task.CompletedTask, CancellationToken.None);
                var expectedState = collectionStage ? "合集待核对" : "待核对";
                Check(st.Jobs[0].State == expectedState && st.Jobs[1].State == "等待上传", "Invalid status code must not be treated as an explicit rejection: " + rawCode);
                Check(st.Jobs[0].PublishAttempted && (!collectionStage || st.Jobs[0].SeasonAttempted));
                foreach (var retryState in new[] { st, LocalState.Load(dir) })
                {
                    Check(retryState.Jobs[0].State == expectedState);
                    try
                    {
                        await new QueueRunner(retryState, dir, new(a, c)).RunAsync(preset, _ => Task.CompletedTask, CancellationToken.None);
                        throw new Exception("Expected reconciliation block after invalid status code");
                    }
                    catch (InvalidOperationException) { }
                }
                Check(a.PreUploads == 1 && a.Published == 1 && a.SeasonAdds == (collectionStage ? 1 : 0));
            }
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
        public int CoverUploads, SeasonReads, SeasonAdds, ArchiveReads;
        public bool LoseCover, RemoveSeasonAfterRead, RejectSeasonAdd, LoseSeasonAdd;
        public string? PublishResponse, SeasonResponse;
        public Action? DuringCoverUpload;
        public const string CoverUrl = "https://i0.hdslb.com/bfs/archive/test-cover.jpg";
        public string CoverResponse = "{\"code\":0,\"data\":{\"url\":\"" + CoverUrl + "\"}}";
        public const string GoodArchiveResponse = "{\"code\":0,\"data\":{\"videos\":[{\"cid\":456,\"filename\":\"video\",\"title\":\"示例视频\"}]}}";
        public string ArchiveResponse = GoodArchiveResponse;
        public List<string> CoverData = [];
        public IReadOnlyList<VideoSeason> Seasons = [new(100, "示例合集", [new(200, "默认分节")])];
        public List<(long SectionId, long Aid, long Cid, string Title)> Additions = [];
        public string BizIdJson = "123";
        public List<JsonElement> Payloads = [];
        public Task<JsonElement> PreUploadAsync(string name, long size, CancellationToken token)
        { PreUploads++; return Task.FromResult(Json("{\"auth\":\"test-upload-secret\",\"endpoint\":\"//upos-test.bilivideo.com\",\"upos_uri\":\"upos://bucket/video.mp4\",\"biz_id\":" + BizIdJson + ",\"chunk_size\":65536}")); }
        public Task<JsonElement> PublishAsync(object payload, CancellationToken token)
        {
            Published++; Payloads.Add(Json(JsonSerializer.Serialize(payload)));
            if (LosePublish) throw new HttpRequestException("lost response");
            if (PublishResponse != null) return Task.FromResult(Json(PublishResponse));
            return Task.FromResult(RejectPublish ? Json("{\"code\":21001,\"message\":\"分区无效\"}") : Json("{\"code\":0,\"data\":{\"aid\":1234,\"bvid\":\"BVtest\"}}"));
        }
        public Task<JsonElement> UploadCoverAsync(string dataUrl, CancellationToken token)
        {
            CoverUploads++; CoverData.Add(dataUrl); DuringCoverUpload?.Invoke(); token.ThrowIfCancellationRequested();
            if (LoseCover) throw new HttpRequestException("lost cover response");
            return Task.FromResult(Json(CoverResponse));
        }
        public Task<IReadOnlyList<VideoSeason>> GetSeasonsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); SeasonReads++;
            var result = Seasons; if (RemoveSeasonAfterRead) Seasons = [];
            return Task.FromResult(result);
        }
        public Task<JsonElement> AddToSeasonAsync(long sectionId, long aid, long cid, string title, CancellationToken token)
        {
            Check(Published > 0, "A video must be published before collection assignment");
            SeasonAdds++; Additions.Add((sectionId, aid, cid, title));
            if (LoseSeasonAdd) throw new HttpRequestException("lost collection response");
            if (SeasonResponse != null) return Task.FromResult(Json(SeasonResponse));
            return Task.FromResult(RejectSeasonAdd ? Json("{\"code\":21001,\"message\":\"合集暂不可用\"}") : Json("{\"code\":0,\"data\":{}}"));
        }
        public Task<JsonElement> ArchiveAsync(long aid, CancellationToken token)
        {
            Check(aid == 1234); ArchiveReads++; token.ThrowIfCancellationRequested();
            return Task.FromResult(Json(ArchiveResponse));
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
