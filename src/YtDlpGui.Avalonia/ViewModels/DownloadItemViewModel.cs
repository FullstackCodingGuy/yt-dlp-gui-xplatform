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
        private string _quality = "Best";
        private DownloadStatus _status = DownloadStatus.Queued;
        private int _progress;
        private Guid _id;

        public Guid Id { get => _id; set => SetField(ref _id, value); }
        public int Index { get => _index; set => SetField(ref _index, value); }
        public string Url { get => _url; set => SetField(ref _url, value); }
        public string Quality { get => _quality; set => SetField(ref _quality, value); }
        public DownloadStatus Status { get => _status; set => SetField(ref _status, value); }
        public int Progress { get => _progress; set => SetField(ref _progress, value); }

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
