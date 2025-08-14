using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using YtDlpGui.AvaloniaApp.Models;

namespace YtDlpGui.AvaloniaApp.ViewModels
{
    public class DownloadItemViewModel : INotifyPropertyChanged
    {
        private int _index;
        private string _url = string.Empty;
        private string _title = string.Empty;
        private string _quality = "Best";
        private DownloadStatus _status = DownloadStatus.Queued;
        private int _progress;
        private Guid _id;
        private long _downloadedBytes;
        private long _totalBytes;
        private double _downloadSpeed;

        public Guid Id { get => _id; set => SetField(ref _id, value); }
        public int Index { get => _index; set => SetField(ref _index, value); }
        public string Url { get => _url; set => SetField(ref _url, value); }
        public string Title { get => _title; set => SetField(ref _title, value); }
        public string Quality { get => _quality; set => SetField(ref _quality, value); }
        public DownloadStatus Status { get => _status; set { SetField(ref _status, value); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusColor)); OnPropertyChanged(nameof(StartPauseText)); OnPropertyChanged(nameof(StartPauseClass)); OnPropertyChanged(nameof(IsCompleted)); } }
        public int Progress { get => _progress; set => SetField(ref _progress, value); }
        public long DownloadedBytes { get => _downloadedBytes; set { SetField(ref _downloadedBytes, value); OnPropertyChanged(nameof(SizeText)); } }
        public long TotalBytes { get => _totalBytes; set { SetField(ref _totalBytes, value); OnPropertyChanged(nameof(SizeText)); } }
        public double DownloadSpeed { get => _downloadSpeed; set => SetField(ref _downloadSpeed, value); }

        public string StatusText => Status switch
        {
            DownloadStatus.Queued => "Queued",
            DownloadStatus.Running => "Downloading",
            DownloadStatus.Paused => "Paused",
            DownloadStatus.Completed => "Completed",
            DownloadStatus.Failed => "Failed",
            DownloadStatus.Canceled => "Canceled",
            _ => "Unknown"
        };

        public string StatusColor => Status switch
        {
            DownloadStatus.Queued => "#9CA3AF",
            DownloadStatus.Running => "#0078D4",
            DownloadStatus.Paused => "#FF8C00",
            DownloadStatus.Completed => "#107C10",
            DownloadStatus.Failed => "#D13438",
            DownloadStatus.Canceled => "#6B7280",
            _ => "#9CA3AF"
        };

        public string StartPauseText => Status switch
        {
            DownloadStatus.Running => "⏸",
            DownloadStatus.Paused => "▶️",
            DownloadStatus.Failed => "🔄",
            DownloadStatus.Canceled => "▶️",
            _ => "▶️"
        };

        public string StartPauseClass => Status switch
        {
            DownloadStatus.Running => "warning",
            DownloadStatus.Failed => "accent",
            _ => "success"
        };

        public bool IsCompleted => Status == DownloadStatus.Completed;

        public string SizeText
        {
            get
            {
                if (TotalBytes > 0)
                {
                    var downloaded = FormatBytes(DownloadedBytes);
                    var total = FormatBytes(TotalBytes);
                    if (DownloadSpeed > 0 && Status == DownloadStatus.Running)
                    {
                        var speed = FormatBytes((long)DownloadSpeed) + "/s";
                        return $"{downloaded} / {total} ({speed})";
                    }
                    return $"{downloaded} / {total}";
                }
                else if (DownloadedBytes > 0)
                {
                    var downloaded = FormatBytes(DownloadedBytes);
                    if (DownloadSpeed > 0 && Status == DownloadStatus.Running)
                    {
                        var speed = FormatBytes((long)DownloadSpeed) + "/s";
                        return $"{downloaded} ({speed})";
                    }
                    return downloaded;
                }
                return string.Empty;
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            double number = bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }
            return $"{number:N1} {suffixes[counter]}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
