namespace BilibiliUploader;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var smoke = args.Contains("--smoke-test");
        using var instance = new Mutex(false, @"Local\BilibiliUploader-" + Environment.UserName);
        var owns = false;
        if (!smoke)
        {
            try { owns = instance.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
            if (!owns) { MessageBox.Show("上传助手已经运行，请使用现有窗口。", "Bilibili 视频上传助手"); return; }
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(
            "操作未完成：" + e.Exception.Message, "Bilibili 视频上传助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        try { Application.Run(new MainForm(smoke)); }
        finally { if (owns) instance.ReleaseMutex(); }
    }
}
