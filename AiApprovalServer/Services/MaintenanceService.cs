using AiApproval.Core;
using AiApproval.Services;

namespace AiApproval.Services;

//后台维护任务
public sealed class MaintenanceService : BackgroundService
{
    private readonly TaskService _tasks;
    private readonly AppConfig _cfg;
    private readonly ILogger<MaintenanceService> _logger;

    public MaintenanceService(TaskService tasks, AppConfig cfg, ILogger<MaintenanceService> logger)
    {
        _tasks = tasks;
        _cfg = cfg;
        _logger = logger;
    }

    //维护循环（5 分钟一轮）
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        //启动后先等 10 秒
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var expired = await _tasks.ExpireOverdueAsync().ConfigureAwait(false);
                CleanTempFiles();
                if (expired > 0) _logger.LogInformation("维护任务：作废超时任务 {Count} 个", expired);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "维护任务执行失败");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
        }
    }

    //清理超时的临时文件
    private void CleanTempFiles()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_cfg.DataRoot, "*.tmp", SearchOption.AllDirectories))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
                if (age > TimeSpan.FromMinutes(30)) File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("临时文件清理跳过：{Message}", ex.Message);
        }
    }
}
