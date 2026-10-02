using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace BilibiliUploader;

public sealed class BiliApi(CoreWebView2 browser) : IBiliApi
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public const string MemberHome = "https://member.bilibili.com/platform/home";
    public const string ManageUrl = "https://member.bilibili.com/platform/upload/video/manage";
    public static bool IsMember(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        u.Scheme == "https" && u.Host == "member.bilibili.com" && u.IsDefaultPort && u.UserInfo.Length == 0;
    public static bool IsBrowserUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        u.Scheme == "https" && u.IsDefaultPort && u.UserInfo.Length == 0 &&
        (u.Host == "bilibili.com" || u.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase));

    // Fixed API operations only. Login cookies stay in WebView2's dedicated profile;
    // CSRF is read and used in the official origin, never returned to native code.
    private async Task<JsonElement> CallAsync(string operation, object? payload, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!IsMember(browser.Source)) throw new ApiFailure("请先在账号页面登录，并返回创作中心。");
            var arguments = JsonSerializer.Serialize(new { operation, payload });
            var expression = "(async (a) => {" + """
                if (location.origin !== 'https://member.bilibili.com') return {clientError:'页面已离开创作中心'};
                const signal = AbortSignal.timeout(a.operation === 'cover' ? 180000 : 45000);
                const csrf = () => document.cookie.split(';').map(v=>v.trim()).find(v=>v.startsWith('bili_jct='))?.slice(9);
                let path, options = {credentials:'include', redirect:'error', signal};
                if (a.operation === 'pre') path = '/x/vupre/web/archive/pre?lang=zh_CN';
                else if (a.operation === 'nav') path = 'https://api.bilibili.com/x/web-interface/nav';
                else if (a.operation === 'seasons') path = '/x2/creative/web/seasons?' + new URLSearchParams({pn:String(a.payload.page),ps:'30',order:'desc',sort:'mtime',filter:'1'});
                else if (a.operation === 'archive') path = '/x/web/archive/videos?' + new URLSearchParams({aid:String(a.payload.aid)});
                else if (a.operation === 'cover') {
                  if (!csrf()) return {clientError:'登录已失效，请重新登录'};
                  path = '/x/vu/web/cover/up?csrf=' + encodeURIComponent(decodeURIComponent(csrf()));
                  const form = new FormData(); form.append('cover',a.payload.dataUrl); form.append('csrf',decodeURIComponent(csrf()));
                  options = {...options, method:'POST', body:form};
                } else if (a.operation === 'season-add') {
                  if (!csrf()) return {clientError:'登录已失效，请重新登录'};
                  path = '/x2/creative/web/season/section/episodes/add?csrf=' + encodeURIComponent(decodeURIComponent(csrf()));
                  options = {...options, method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({...a.payload,csrf:decodeURIComponent(csrf())})};
                }
                else if (a.operation === 'preupload') {
                  path = '/preupload?' + new URLSearchParams({name:a.payload.name, size:String(a.payload.size),
                    r:'upos', profile:'ugcupos/bup', ssl:'1', version:'2.14.0', build:'2140000'});
                } else if (a.operation === 'publish') {
                  if (!csrf()) return {clientError:'登录已失效，请重新登录'};
                  path = '/x/vu/web/add?csrf=' + encodeURIComponent(decodeURIComponent(csrf()));
                  options = {...options, method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(a.payload)};
                } else return {clientError:'不支持的请求'};
                try {
                  const response = await fetch(path, options);
                  if (!response.ok) return {clientError:'Bilibili 接口返回 HTTP ' + response.status};
                  const text = await response.text();
                  if (text.length > 2000000) return {clientError:'接口返回内容超出限制'};
                  // Preserve JSON number text through the DevTools transport.
                  return {responseBody:text};
                } catch { return {clientError:'请求中断或接口不可用，请检查网络和登录状态'}; }
                """ + "})(" + arguments + ")";
            var json = await browser.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
            { expression, awaitPromise = true, returnByValue = true })).WaitAsync(TimeSpan.FromSeconds(operation == "cover" ? 190 : 55), token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("exceptionDetails", out _) || !doc.RootElement.GetProperty("result").TryGetProperty("value", out var value))
                throw new ApiFailure("账号页面无法完成请求，请重新打开创作中心。");
            if (!IsMember(browser.Source)) throw new ApiFailure("请求期间账号页面发生跳转，请核对后重试。");
            return ParseResponseEnvelope(value);
        }
        finally { gate.Release(); }
    }
    public static JsonElement ParseResponseEnvelope(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ApiFailure("账号页面返回的接口数据无法识别。");
        if (value.TryGetProperty("clientError", out var error))
            throw new ApiFailure(error.ValueKind == JsonValueKind.String ? error.GetString() ?? "接口不可用" : "接口不可用");
        if (!value.TryGetProperty("responseBody", out var body) || body.ValueKind != JsonValueKind.String)
            throw new ApiFailure("账号页面没有返回接口响应内容。");
        var text = body.GetString()!;
        if (text.Length > 2000000) throw new ApiFailure("接口返回内容超出限制。");
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ApiFailure("平台返回的接口数据格式无法识别。");
            return doc.RootElement.Clone();
        }
        catch (JsonException) { throw new ApiFailure("平台接口没有返回有效的 JSON 数据。"); }
    }
    public Task<JsonElement> AccountAsync(CancellationToken token) => CallAsync("nav", null, token);
    public Task<JsonElement> CategoriesAsync(CancellationToken token) => CallAsync("pre", null, token);
    public Task<JsonElement> PreUploadAsync(string name, long size, CancellationToken token) => CallAsync("preupload", new { name, size }, token);
    public Task<JsonElement> PublishAsync(object payload, CancellationToken token) => CallAsync("publish", payload, token);
    public Task<JsonElement> UploadCoverAsync(string dataUrl, CancellationToken token) => CallAsync("cover", new { dataUrl }, token);
    public Task<JsonElement> ArchiveAsync(long aid, CancellationToken token) => CallAsync("archive", new { aid }, token);
    public Task<JsonElement> AddToSeasonAsync(long sectionId, long aid, long cid, string title, CancellationToken token) =>
        CallAsync("season-add", new { sectionId, episodes = new[] { new { title, cid, aid, charging_pay = 0 } } }, token);

    public async Task<IReadOnlyList<VideoSeason>> GetSeasonsAsync(CancellationToken token)
    {
        var result = new List<VideoSeason>();
        for (var page = 1; page <= 100; page++)
        {
            var response = await CallAsync("seasons", new { page }, token);
            UposUploader.CheckCode(response);
            if (!response.TryGetProperty("code", out _) || !response.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("seasons", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new ApiFailure("未能读取合集列表，请在创作中心确认合集权限后重试。");
            result.AddRange(ParseSeasons(data));
            if (list.GetArrayLength() < 30 || (data.TryGetProperty("total", out var total) && total.TryGetInt32(out var count) && page * 30 >= count))
                return result.DistinctBy(s => s.Id).ToArray();
        }
        throw new ApiFailure("合集数量超出读取范围，请在创作中心管理后重试。");
    }

    public static IReadOnlyList<VideoSeason> ParseSeasons(JsonElement data)
    {
        var result = new List<VideoSeason>();
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("seasons", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new ApiFailure("合集列表格式无法识别。");
        static long Id(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && long.TryParse(value.ToString(), out var n) ? n : 0;
        static bool Flag(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && value.ToString() is not ("0" or "False" or "false" or "");
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("season", out var season) || season.ValueKind != JsonValueKind.Object ||
                Flag(season, "is_pay") || Flag(season, "has_charging_pay") || Flag(season, "forbid") || Id(season, "id") <= 0 ||
                !season.TryGetProperty("title", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) ||
                !entry.TryGetProperty("sections", out var wrapper) || wrapper.ValueKind != JsonValueKind.Object ||
                !wrapper.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array) continue;
            var choices = new List<SeasonSection>();
            foreach (var section in sections.EnumerateArray())
                if (section.ValueKind == JsonValueKind.Object && Id(section, "id") > 0 && !Flag(section, "has_charging_pay"))
                {
                    var title = section.TryGetProperty("title", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                    choices.Add(new(Id(section, "id"), string.IsNullOrWhiteSpace(title) ? "默认分节" : title));
                }
            if (choices.Count > 0) result.Add(new(Id(season, "id"), name.GetString()!, choices.DistinctBy(s => s.Id).ToArray()));
        }
        return result.DistinctBy(s => s.Id).ToArray();
    }
    public static List<Category> ParseCategories(JsonElement data)
    {
        var result = new List<Category>();
        void Read(JsonElement array, string prefix, int depth)
        {
            if (array.ValueKind != JsonValueKind.Array || depth > 4) return;
            foreach (var node in array.EnumerateArray().Take(1000))
            {
                if (!node.TryGetProperty("name", out var nameNode)) continue;
                var name = prefix + nameNode.GetString();
                if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array && children.GetArrayLength() > 0)
                    Read(children, name + " / ", depth + 1);
                else if (node.TryGetProperty("id", out var idNode) && int.TryParse(idNode.ToString(), out var id) && id > 0)
                    result.Add(new(id, name));
            }
        }
        if (data.TryGetProperty("typelist", out var list)) Read(list, "", 0);
        return result.DistinctBy(c => c.Id).ToList();
    }
}
