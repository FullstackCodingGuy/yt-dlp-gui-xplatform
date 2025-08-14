namespace YtDlpGui.AvaloniaApp.Models
{
    public enum DownloadStatus
    {
        Queued,
        Running,
        Paused,
        Completed,
        Failed,
        Canceled
    }

    public class DownloadRequest
    {
        public required string Url { get; init; }
        public required string Quality { get; init; }
        public required string OutputFolder { get; init; }
    }
}
