namespace BilibiliUploader;

public sealed class QueueRunner(LocalState state, string directory, UposUploader uploader)
{
    public bool Running { get; private set; }
    public event Action<UploadJob>? Changed;
    public event Action<UploadJob, UploadProgress>? Progressed;
    public async Task RunAsync(PublishPreset preset, Func<CancellationToken, Task> verifyAccount, CancellationToken token)
    {
        if (Running) throw new InvalidOperationException("队列正在运行。");
        if (state.Jobs.Any(j => j.State is "待核对" or "合集待核对")) throw new InvalidOperationException("请先核对上次结果未知的投稿或合集操作。");
        var pending = state.Jobs.Where(j => j.State is "等待上传" or "失败" or "已停止" or "已上传" or "合集待处理").ToList();
        if (pending.Count == 0) throw new InvalidOperationException("请先添加视频。");
        // Freeze the chosen image before any upload, just like the batch preset.
        var cover = !pending.Any(j => j.Aid == 0) || string.IsNullOrWhiteSpace(preset.CoverPath) ? null : CoverImage.Read(preset.CoverPath);
        var coverUrl = "";
        foreach (var job in pending)
        {
            // A published video keeps its original collection target and never re-enters publishing.
            if (job.Aid > 0)
            {
                if (job.Preset?.SeasonId is not > 0 || job.SeasonAdded)
                    throw new InvalidOperationException("稿件状态不一致，请在创作中心核对。");
                continue;
            }
            var index = state.Jobs.IndexOf(job) + 1;
            preset.Validate(job.Path, index);
            job.VerifyFile();
            job.Preset = preset.Copy(); job.Title = preset.Title(job.Path, index, DateTime.Now);
            job.Error = "";
        }
        state.Save(directory);
        Running = true;
        try
        {
            foreach (var job in pending)
            {
                token.ThrowIfCancellationRequested();
                if (job.Aid > 0)
                {
                    if (!await CompleteSeasonAsync(job, verifyAccount, token)) break;
                    continue;
                }
                try
                {
                    await uploader.WaitForResumeAsync(token);
                    await verifyAccount(token);
                    await uploader.ValidateSeasonAsync(job.Preset!, token);
                    if (cover != null && coverUrl.Length == 0)
                    {
                        job.State = "上传封面"; Persist(job);
                        coverUrl = await uploader.UploadCoverAsync(cover, token);
                    }
                    if (job.RemoteFilename.Length == 0)
                    {
                        job.State = "准备上传"; Persist(job);
                        IProgress<UploadProgress> progress = new Progress<UploadProgress>(p => Progressed?.Invoke(job, p));
                        var uploaded = await uploader.UploadAsync(job, job.Preset!, p => progress.Report(p), token);
                        job.RemoteFilename = uploaded.Filename;
                        job.Cid = uploaded.Cid;
                        job.State = "已上传"; job.ConfirmedBytes = job.Size; Persist(job);
                    }
                    token.ThrowIfCancellationRequested();
                    await uploader.WaitForResumeAsync(token);
                    await verifyAccount(token);
                    await uploader.ValidateSeasonAsync(job.Preset!, token);
                    // This write must succeed before any publishing request is sent.
                    job.State = "投稿中"; job.PublishAttempted = true; Persist(job);
                    var result = await uploader.PublishAsync(job, token, coverUrl);
                    job.Aid = result.Aid; job.Bvid = result.Bvid;
                    job.State = job.Preset!.SeasonId > 0 ? "合集待处理" : "已投稿";
                    job.Error = "已提交，审核和转码结果请在创作中心查看。"; Persist(job);
                }
                catch (PublishUncertain ex) { job.State = "待核对"; job.Error = ex.Message; Persist(job); break; }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { job.State = job.PublishAttempted ? "待核对" : "已停止"; job.Error = job.PublishAttempted ? "停止时投稿请求已发出，请核对结果。" : "已停止；下次开始会重新上传未完成的视频。"; Persist(job); break; }
                catch (ApiFailure ex)
                {
                    job.State = "失败"; job.PublishAttempted = false;
                    job.Error = UposUploader.SafeMessage(ex.Message); Persist(job); break;
                }
                catch (Exception ex)
                {
                    job.State = job.PublishAttempted ? "待核对" : "失败";
                    job.Error = job.PublishAttempted ? "状态保存或投稿结果异常，请先到创作中心核对。" :
                        ex is IOException ? "文件无法读取或本地状态无法保存。" : "传输中断，请检查网络后重试。";
                    Persist(job); break;
                }
                if (job.Preset!.SeasonId > 0 && !await CompleteSeasonAsync(job, verifyAccount, token)) break;
            }
        }
        finally { Running = false; }
    }
    private async Task<bool> CompleteSeasonAsync(UploadJob job, Func<CancellationToken, Task> verifyAccount, CancellationToken token)
    {
        try
        {
            await uploader.WaitForResumeAsync(token);
            await verifyAccount(token);
            await uploader.ValidateSeasonAsync(job.Preset!, token);
            if (job.Cid <= 0) job.Cid = await uploader.ReadArchiveCidAsync(job.Aid, token);
            token.ThrowIfCancellationRequested();
            job.State = "加入合集中"; job.SeasonAttempted = true; Persist(job);
            await uploader.AddToSeasonAsync(job, token);
            job.SeasonAdded = true; job.State = "已投稿";
            job.Error = "已提交并加入所选合集，审核和转码结果请在创作中心查看。"; Persist(job);
            return true;
        }
        catch (SeasonUncertain ex) { job.State = "合集待核对"; job.Error = ex.Message; }
        catch (ApiFailure ex)
        {
            job.SeasonAttempted = false; job.State = "合集待处理";
            job.Error = "稿件已投稿，加入合集未完成：" + UposUploader.SafeMessage(ex.Message) + " 开始后只重试合集。";
        }
        catch (Exception)
        {
            job.State = job.SeasonAttempted ? "合集待核对" : "合集待处理";
            job.Error = job.SeasonAttempted ? "稿件已投稿，加入合集结果未知，请先核对合集。" : "稿件已投稿，合集操作已停止；开始后只处理合集。";
        }
        Persist(job);
        return false;
    }
    private void Persist(UploadJob job) { state.Save(directory); Changed?.Invoke(job); }
}
