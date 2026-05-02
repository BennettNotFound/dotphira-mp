using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DotPmp.Server;

public record ReplayAutoUploadConfig(bool Enabled, bool Show);
public record ReplayAutoUploadTask(string ReplayPath, long UserId, bool Show, DateTimeOffset ExecuteAt);

public class ReplayAutoUploadConfigService : IDisposable
{
    private static readonly TimeSpan AutoUploadDelay = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<long, ReplayAutoUploadConfig> _configs = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<Guid, CancellationTokenSource>> _pendingTasks = new();
    private readonly ShareStationService _shareStationService;
    private readonly ILogger<ReplayAutoUploadConfigService> _logger;

    public ReplayAutoUploadConfigService(
        ShareStationService shareStationService,
        ILogger<ReplayAutoUploadConfigService> logger)
    {
        _shareStationService = shareStationService;
        _logger = logger;
    }

    public ReplayAutoUploadConfig Get(long userId)
    {
        return _configs.TryGetValue(userId, out var config)
            ? config
            : new ReplayAutoUploadConfig(false, false);
    }

    public ReplayAutoUploadConfig Update(long userId, bool? enabled, bool? show)
    {
        var config = _configs.AddOrUpdate(
            userId,
            _ => new ReplayAutoUploadConfig(enabled ?? false, show ?? false),
            (_, existing) => new ReplayAutoUploadConfig(enabled ?? existing.Enabled, show ?? existing.Show));

        if (!config.Enabled)
            CancelPendingUploads(userId);

        return config;
    }

    public void ScheduleUpload(string replayPath, long userId, bool show)
    {
        var cts = new CancellationTokenSource();
        var uploadId = Guid.NewGuid();
        var userTasks = _pendingTasks.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, CancellationTokenSource>());
        userTasks[uploadId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AutoUploadDelay, cts.Token);

                var config = Get(userId);
                if (!config.Enabled)
                    return;

                if (!File.Exists(replayPath))
                    return;

                await _shareStationService.UploadReplayAsync(replayPath, userId, show, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto upload failed for user {UserId}, replay {ReplayPath}", userId, replayPath);
            }
            finally
            {
                if (_pendingTasks.TryGetValue(userId, out var tasks))
                {
                    if (tasks.TryRemove(uploadId, out var tokenSource))
                        tokenSource.Dispose();
                    if (tasks.IsEmpty)
                        _pendingTasks.TryRemove(userId, out _);
                }
            }
        });
    }

    public void CancelPendingUploads(long userId)
    {
        if (!_pendingTasks.TryRemove(userId, out var tasks))
            return;

        foreach (var task in tasks.Values)
        {
            task.Cancel();
            task.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var userId in _pendingTasks.Keys.ToList())
            CancelPendingUploads(userId);
    }
}
