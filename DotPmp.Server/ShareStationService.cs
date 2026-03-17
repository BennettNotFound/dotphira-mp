namespace DotPmp.Server;

public class ShareStationService
{
    public bool IsConfigured => true;

    public record UploadResult(long RecordId, long ScoreId);

    public async Task<UploadResult> UploadReplayAsync(string path, long userId)
    {
        await Task.Delay(500);

        return new UploadResult(
            Random.Shared.NextInt64(100000, 999999),
            Random.Shared.NextInt64(100000, 999999)
        );
    }
}