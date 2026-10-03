using System.Text.Json;
using System.Text.Json.Nodes;

namespace BilibiliUploader;

public sealed class ArchiveAppend
{
    public long Aid { get; }
    public string Bvid { get; }
    public long[] Cids { get; }
    private readonly JsonObject data, archive;
    private readonly JsonArray videos;
    private ArchiveAppend(JsonObject data, JsonObject archive, JsonArray videos, long[] cids)
    { this.data = data; this.archive = archive; this.videos = videos; Cids = cids; Aid = Number(archive["aid"]); Bvid = Text(archive["bvid"]); }
    private static long Number(JsonNode? node) => long.TryParse(node?.ToString(), out var value) ? value : 0;
    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static bool Enabled(JsonNode? node) => node?.ToString() is "true" or "1";

    public static ArchiveAppend Read(JsonElement response, PublishPreset preset)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("code", out _)) throw new ApiFailure("稿件详情响应无法识别。");
        UposUploader.CheckCode(response);
        var root = JsonNode.Parse(response.GetRawText());
        if (root?["data"] is not JsonObject data || data["archive"] is not JsonObject archive || data["videos"] is not JsonArray videos || videos.Count == 0)
            throw new ApiFailure("无法读取完整原分 P 列表，已停止追加。");
        if (Number(archive["aid"]) != preset.TargetAid || Number(archive["mid"]).ToString() != preset.TargetOwnerMid)
            throw new ApiFailure("目标投稿或所属账号不匹配，已停止追加。");
        foreach (var key in new[] { "title", "tag", "desc", "cover" })
            if (archive[key] is not JsonValue value || !value.TryGetValue<string>(out _)) throw new ApiFailure("稿件信息不完整，未修改原稿。");
        if (Number(archive["tid"]) <= 0 || Number(archive["copyright"]) is not (1 or 2)) throw new ApiFailure("稿件分区或作品类型无效。");
        var cids = new List<long>();
        foreach (var part in videos)
        {
            if (part is not JsonObject video || Number(video["cid"]) <= 0 || string.IsNullOrWhiteSpace(Text(video["filename"])) || string.IsNullOrWhiteSpace(Text(video["title"])))
                throw new ApiFailure("原分 P 信息不完整，未修改原稿。");
            cids.Add(Number(video["cid"]));
        }
        if (cids.Distinct().Count() != cids.Count || (Number(archive["videos"]) > 0 && Number(archive["videos"]) != videos.Count))
            throw new ApiFailure("原分 P 列表不完整或有重复编号，未修改原稿。");
        return new(data, archive, videos, cids.ToArray());
    }

    public void EnsureCanAppend()
    {
        if (data["replace_check"] is not JsonObject replace || !Enabled(replace["can_add_video"] ?? replace["can_replace"]))
            throw new ApiFailure("此稿件当前不允许增加分 P：" + UposUploader.SafeMessage(Text((data["replace_check"] as JsonObject)?["add_video_message"])));
        foreach (var key in new[] { "interactive", "charging_pay", "ugcpay", "is_ugcpay_v2", "is_pugv", "is_staff", "is_playlet", "premiere", "political_media", "is_ab_cover", "upower_mode", "mission_id", "topic_id", "order_id", "adorder_id" })
            if (Number(archive[key]) > 0) throw new ApiFailure("此稿件有互动、付费、合作、活动或其他特殊设置，请在官方创作中心追加分 P。");
        if (Number(archive["dtime"]) > 0 || Enabled((archive["attrs"] as JsonObject)?["is_premiere"]) ||
            (data["subtitle"] as JsonObject)?["draft_list"] is JsonArray { Count: > 0 } ||
            data["staffs"] is JsonArray { Count: > 0 } || archive["staffs"] is JsonArray { Count: > 0 })
            throw new ApiFailure("定时、首映、字幕或合作稿件请在官方创作中心追加分 P。");
        var maximum = 200L;
        if (data["client_limits"] is JsonObject limits)
        {
            var configured = Number((limits["new_web_edit"] as JsonObject)?["max_count"]);
            if (configured > 0) maximum = Math.Min(maximum, configured);
            if (videos.Count == 1 && !Enabled(limits["have_permission_of_p"])) throw new ApiFailure("当前账号暂不支持将单 P 稿件改为多 P。");
            if (Enabled(data["in_season"]) && !Enabled(limits["season_add_multip"])) throw new ApiFailure("当前账号暂不支持给合集内稿件追加分 P。");
        }
        if (videos.Count >= maximum) throw new ApiFailure($"稿件已达到 {maximum} 个分 P 的上限。");
    }

    private JsonObject BasePayload()
    {
        var payload = new JsonObject();
        foreach (var key in new[] { "aid", "title", "copyright", "source", "tid", "tag", "cover", "cover43", "desc", "desc_v2", "desc_format_id", "dynamic", "dynamic_v2", "dtime", "mission_id", "topic_id", "no_reprint", "is_only_self", "lossless_music", "creation_statement" })
            if (archive.ContainsKey(key)) payload[key] = archive[key]?.DeepClone();
        if (archive["human_type2"] is JsonObject human) payload["human_type2"] = Number(human["id"]);
        payload["recreate"] = (archive["recreate"] is JsonObject recreate && Number(recreate["switch"]) == 1) ? 1 : -1;
        payload["is_360"] = Number(archive["is_360"]) == 1 ? 1 : 0;
        payload["dolby"] = Number(archive["is_dolby"]);
        if (data.ContainsKey("space_hidden")) payload["space_hidden"] = data["space_hidden"]?.DeepClone();
        if (data["watermark"] is JsonObject watermark) payload["watermark"] = new JsonObject { ["state"] = Number(watermark["state"]) };
        payload["new_web_edit"] = 1; payload["topic_grey"] = 1; payload["handle_staff"] = false;
        var parts = new JsonArray();
        foreach (var video in videos.Cast<JsonObject>())
            parts.Add(new JsonObject { ["filename"] = Text(video["filename"]), ["title"] = Text(video["title"]), ["desc"] = Text(video["desc"]), ["cid"] = Number(video["cid"]) });
        payload["videos"] = parts;
        return payload;
    }
    public bool SameContent(ArchiveAppend other) => JsonNode.DeepEquals(BasePayload(), other.BasePayload());
    public JsonObject Append(UploadJob job)
    {
        EnsureCanAppend();
        if (job.Cid <= 0 || string.IsNullOrWhiteSpace(job.RemoteFilename)) throw new ApiFailure("缺少新分 P 的上传编号，请移除并重新添加此文件。");
        if (Cids.Contains(job.Cid)) throw new ApiFailure("该视频已经在目标稿件中，请核对分 P。");
        var payload = BasePayload();
        ((JsonArray)payload["videos"]!).Add(new JsonObject { ["filename"] = job.RemoteFilename, ["title"] = job.Title, ["desc"] = "", ["cid"] = job.Cid });
        return payload;
    }
    public bool Confirms(long cid, IReadOnlyList<long> originalCids) => Cids.Contains(cid) &&
        (originalCids.Count == 0 || (Cids.Length > originalCids.Count && Cids.Take(originalCids.Count).SequenceEqual(originalCids) && Cids[originalCids.Count] == cid));
}
