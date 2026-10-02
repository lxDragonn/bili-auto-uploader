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
                const signal = AbortSignal.timeout(45000);
                let path, options = {credentials:'include', signal};
                if (a.operation === 'pre') path = '/x/vupre/web/archive/pre?lang=zh_CN';
                else if (a.operation === 'nav') path = 'https://api.bilibili.com/x/web-interface/nav';
                else if (a.operation === 'preupload') {
                  path = '/preupload?' + new URLSearchParams({name:a.payload.name, size:String(a.payload.size),
                    r:'upos', profile:'ugcupos/bup', ssl:'1', version:'2.14.0', build:'2140000'});
                } else if (a.operation === 'publish') {
                  const csrf = document.cookie.split(';').map(v=>v.trim()).find(v=>v.startsWith('bili_jct='))?.slice(9);
                  if (!csrf) return {code:-101,message:'登录已失效，请重新登录'};
                  path = '/x/vu/web/add?csrf=' + encodeURIComponent(decodeURIComponent(csrf));
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
            { expression, awaitPromise = true, returnByValue = true })).WaitAsync(TimeSpan.FromSeconds(55), token);
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
