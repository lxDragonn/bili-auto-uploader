namespace BilibiliUploader;

public sealed class QueueRunner(LocalState state, string directory, UposUploader uploader)
{
    public bool Running { get; private set; }
    public event Action<UploadJob>? Changed;
    public event Action<UploadJob, UploadProgress>? Progressed;
    public async Task RunAsync(PublishPreset preset, Func<CancellationToken, Task> verifyAccount, CancellationToken token)
    {
        if (Running) throw new InvalidOperationException("队列正在运行。");
        if (state.Jobs.Any(j => j.State == "待核对")) throw new InvalidOperationException("请先核对上次结果未知的投稿。");
        var pending = state.Jobs.Where(j => j.State is "等待上传" or "失败" or "已停止").ToList();
        if (pending.Count == 0) throw new InvalidOperationException("请先添加视频。");
        foreach (var job in pending)
        {
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
                try
                {
                    await uploader.WaitForResumeAsync(token);
                    await verifyAccount(token);
                    if (job.RemoteFilename.Length == 0)
                    {
                        job.State = "准备上传"; Persist(job);
                        IProgress<UploadProgress> progress = new Progress<UploadProgress>(p => Progressed?.Invoke(job, p));
                        var uploaded = await uploader.UploadAsync(job, job.Preset!, p => progress.Report(p), token);
                        job.RemoteFilename = uploaded.Filename;
                        job.State = "已上传"; job.ConfirmedBytes = job.Size; Persist(job);
                    }
                    token.ThrowIfCancellationRequested();
                    await uploader.WaitForResumeAsync(token);
                    await verifyAccount(token);
                    // This write must succeed before any publishing request is sent.
                    job.State = "投稿中"; job.PublishAttempted = true; Persist(job);
                    var result = await uploader.PublishAsync(job, token);
                    job.Aid = result.Aid; job.Bvid = result.Bvid; job.State = "已投稿";
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
            }
        }
        finally { Running = false; }
    }
    private void Persist(UploadJob job) { state.Save(directory); Changed?.Invoke(job); }
}
