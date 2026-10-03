namespace BilibiliUploader;

public sealed class QueueRunner(LocalState state, string directory, UposUploader uploader)
{
    public bool Running { get; private set; }
    public event Action<UploadJob>? Changed;
    public event Action<UploadJob, UploadProgress>? Progressed;
    public async Task RunAsync(PublishPreset preset, Func<CancellationToken, Task> verifyAccount, CancellationToken token)
    {
        if (Running) throw new InvalidOperationException("队列正在运行。");
        if (state.Jobs.Any(j => j.State is "待核对" or "合集待核对" or "追加待核对")) throw new InvalidOperationException("请先核对上次结果未知的投稿、分 P 或合集操作。");
        var pending = state.Jobs.Where(j => j.State is "等待上传" or "失败" or "已停止" or "已上传" or "合集待处理").ToList();
        if (pending.Count == 0) throw new InvalidOperationException("请先添加视频。");
        foreach (var job in pending)
        {
            if (job.Preset?.AppendToExisting == true)
            {
                job.Preset.Validate(job.Path, state.Jobs.IndexOf(job) + 1);
                if (job.RemoteFilename.Length == 0) job.VerifyFile();
                continue; // Never retarget a stopped or rejected append job to a different archive.
            }
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
        // Covers apply only to new archives, not to the existing archive's global metadata.
        var cover = !pending.Any(j => j.Aid == 0 && !j.Preset!.AppendToExisting) || string.IsNullOrWhiteSpace(preset.CoverPath) ? null : CoverImage.Read(preset.CoverPath);
        var coverUrl = "";
        state.Save(directory);
        Running = true;
        try
        {
            foreach (var job in pending)
            {
                token.ThrowIfCancellationRequested();
                if (job.Preset!.AppendToExisting)
                {
                    if (!await AppendAsync(job, verifyAccount, token)) break;
                    continue;
                }
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
    private async Task<bool> AppendAsync(UploadJob job, Func<CancellationToken, Task> verifyAccount, CancellationToken token)
    {
        try
        {
            await uploader.WaitForResumeAsync(token);
            await verifyAccount(token);
            var before = await uploader.ReadAppendArchiveAsync(job.Preset!, token);
            if (job.Cid > 0 && before.Cids.Contains(job.Cid))
            {
                if (!before.Confirms(job.Cid, job.AppendOriginalCids)) throw new AppendUncertain("新分 P 已存在，但原分 P 顺序已变化，请核对原稿件。");
                FinishAppend(job, before); return true;
            }
            before.EnsureCanAppend();
            if (job.RemoteFilename.Length == 0)
            {
                job.State = "准备上传"; Persist(job);
                IProgress<UploadProgress> progress = new Progress<UploadProgress>(p => Progressed?.Invoke(job, p));
                var uploaded = await uploader.UploadAsync(job, job.Preset!, p => progress.Report(p), token);
                job.RemoteFilename = uploaded.Filename; job.Cid = uploaded.Cid;
                job.State = "已上传"; job.ConfirmedBytes = job.Size; Persist(job);
            }
            await uploader.WaitForResumeAsync(token);
            await verifyAccount(token);
            var current = await uploader.ReadAppendArchiveAsync(job.Preset!, token);
            if (current.Cids.Contains(job.Cid))
            {
                if (!current.Confirms(job.Cid, job.AppendOriginalCids)) throw new AppendUncertain("新分 P 已存在，但原分 P 顺序已变化，请核对原稿件。");
                FinishAppend(job, current); return true;
            }
            var payload = current.Append(job);
            var check = await uploader.ReadAppendArchiveAsync(job.Preset!, token);
            check.EnsureCanAppend();
            if (!current.SameContent(check)) throw new ApiFailure("提交前检测到原稿件被修改，已停止追加。请关闭其他编辑窗口后再开始。");
            token.ThrowIfCancellationRequested();
            job.AppendOriginalCids = current.Cids; job.AppendAttempted = true; job.State = "追加分P中"; Persist(job);
            try { await uploader.EditArchiveAsync(payload, token); }
            catch (ApiFailure) { job.AppendAttempted = false; throw; } // Only a definite rejection permits another edit.
            // A successful edit reply is not enough: observe the new part before the next edit.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(2), token);
                var updated = await uploader.ReadAppendArchiveAsync(job.Preset!, token);
                if (updated.Confirms(job.Cid, job.AppendOriginalCids)) { FinishAppend(job, updated); return true; }
            }
            throw new AppendUncertain("服务器已接收追加请求，但尚未确认完整分 P 列表。请在创作中心核对后继续。");
        }
        catch (AppendUncertain ex) { job.State = "追加待核对"; job.Error = ex.Message; }
        catch (Exception ex)
        {
            job.State = job.AppendAttempted ? "追加待核对" : token.IsCancellationRequested ? "已停止" : "失败";
            job.Error = job.AppendAttempted ? "追加请求已发出，但结果核对未完成。请先在创作中心检查分 P。" :
                token.IsCancellationRequested ? "追加任务已停止，已上传的视频及原目标会保留。" : UposUploader.SafeMessage(ex.Message);
        }
        Persist(job); return false;
    }
    private void FinishAppend(UploadJob job, ArchiveAppend archive)
    {
        job.Aid = archive.Aid; job.Bvid = archive.Bvid.Length > 0 ? archive.Bvid : job.Preset!.TargetBvid;
        job.State = "已追加"; job.ConfirmedBytes = job.Size;
        job.Error = "已确认新分 P 位于原稿件中；转码和审核结果请在创作中心查看。"; Persist(job);
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
