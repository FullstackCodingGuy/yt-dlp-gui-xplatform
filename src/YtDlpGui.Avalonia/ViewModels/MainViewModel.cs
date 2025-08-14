using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using YtDlpGui.AvaloniaApp.Models;
using YtDlpGui.AvaloniaApp.Services;
using YtDlpGui.AvaloniaApp.Utils;

namespace YtDlpGui.AvaloniaApp.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly DownloadManager _manager;
        public ObservableCollection<DownloadItemViewModel> Items { get; } = new();

        public string NewUrl { get => _newUrl; set { SetField(ref _newUrl, value); OnPropertyChanged(nameof(CanAddItem)); OnPropertyChanged(nameof(UrlValidationMessage)); } }
        public string NewQuality { get => _newQuality; set => SetField(ref _newQuality, value); }
        public string[] Qualities { get; } = new[] { 
            // Video formats with resolutions
            "Best Video (4K/1080p/720p)", 
            "4K Video (2160p)", 
            "1080p Video", 
            "720p Video", 
            "480p Video", 
            "360p Video",
            // Audio formats
            "Audio Only - Best Quality", 
            "Audio Only - MP3 320kbps", 
            "Audio Only - MP3 256kbps", 
            "Audio Only - MP3 128kbps",
            "Audio Only - AAC Best",
            "Audio Only - FLAC",
            "Audio Only - OGG",
            // Combined options
            "Video + Audio - Best", 
            "Video + Audio - 1080p + Best Audio",
            "Video + Audio - 720p + Best Audio"
        };
        public string OutputFolder { get => _outputFolder; set => SetField(ref _outputFolder, value); }
        public bool CanAddItem => !string.IsNullOrWhiteSpace(NewUrl) && IsValidUrl(NewUrl);
        public string UrlValidationMessage => GetUrlValidationMessage();
        public string ItemsStatusText => GetItemsStatusText();
        public bool HasItems => Items.Count > 0;
        public bool HasNoItems => Items.Count == 0;

        private string _newUrl = string.Empty;
        private string _newQuality = "Best Video (4K/1080p/720p)";
        private string _outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public RelayCommand AddItemCommand { get; }
        public RelayCommand AddAndStartCommand { get; }
        public RelayCommand StartAllCommand { get; }
        public RelayCommand PauseAllCommand { get; }
        public RelayCommand ClearCompletedCommand { get; }
        public RelayCommand ToggleItemCommand { get; }
        public RelayCommand RemoveItemCommand { get; }
        public RelayCommand OpenItemCommand { get; }
        public RelayCommand BrowseFolderCommand { get; }

        private readonly IFolderPickerService _folderPicker;

        public MainViewModel() : this(new DesktopFolderPickerService())
        {
        }

        public MainViewModel(IFolderPickerService folderPicker)
        {
            _folderPicker = folderPicker;
            _manager = new DownloadManager(4); // Default to 4 concurrent downloads

            AddItemCommand = new RelayCommand(() =>
            {
                if (!CanAddItem) return;
                
                try
                {
                    var vm = AddItemInternal(NewUrl, NewQuality);
                    NewUrl = string.Empty;
                    OnPropertyChanged(nameof(CanAddItem));
                    OnPropertyChanged(nameof(UrlValidationMessage));
                    OnPropertyChanged(nameof(ItemsStatusText));
                }
                catch (Exception ex)
                {
                    // In a real application, you might want to show this error to the user
                    System.Diagnostics.Debug.WriteLine($"Error adding item: {ex.Message}");
                }
            });

            AddAndStartCommand = new RelayCommand(() =>
            {
                if (!CanAddItem) return;
                
                try
                {
                    var vm = AddItemInternal(NewUrl, NewQuality);
                    StartItem(vm);
                    NewUrl = string.Empty;
                    OnPropertyChanged(nameof(CanAddItem));
                    OnPropertyChanged(nameof(UrlValidationMessage));
                    OnPropertyChanged(nameof(ItemsStatusText));
                }
                catch (Exception ex)
                {
                    // In a real application, you might want to show this error to the user
                    System.Diagnostics.Debug.WriteLine($"Error adding and starting item: {ex.Message}");
                }
            });

            StartAllCommand = new RelayCommand(() =>
            {
                foreach (var it in Items.ToArray()) 
                {
                    if (it.Status == DownloadStatus.Queued || it.Status == DownloadStatus.Paused || it.Status == DownloadStatus.Failed)
                        StartItem(it);
                }
                OnPropertyChanged(nameof(ItemsStatusText));
            });

            PauseAllCommand = new RelayCommand(() =>
            {
                foreach (var it in Items.ToArray()) 
                {
                    if (it.Status == DownloadStatus.Running)
                        PauseItem(it);
                }
                OnPropertyChanged(nameof(ItemsStatusText));
            });

            ClearCompletedCommand = new RelayCommand(() =>
            {
                for (int i = Items.Count - 1; i >= 0; i--)
                {
                    if (Items[i].Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled)
                        Items.RemoveAt(i);
                }
                Reindex();
                OnPropertyChanged(nameof(ItemsStatusText));
                OnPropertyChanged(nameof(HasItems));
                OnPropertyChanged(nameof(HasNoItems));
            });

            ToggleItemCommand = new RelayCommand(p => 
            { 
                if (p is DownloadItemViewModel vm) 
                {
                    if (vm.Status == DownloadStatus.Running)
                        PauseItem(vm);
                    else if (vm.Status == DownloadStatus.Queued || vm.Status == DownloadStatus.Paused)
                        StartItem(vm);
                    else if (vm.Status == DownloadStatus.Failed)
                        RetryItem(vm);
                    OnPropertyChanged(nameof(ItemsStatusText));
                }
            });

            RemoveItemCommand = new RelayCommand(p => 
            { 
                if (p is DownloadItemViewModel vm) 
                { 
                    if (vm.Status == DownloadStatus.Running)
                        PauseItem(vm);
                    Items.Remove(vm); 
                    Reindex(); 
                    OnPropertyChanged(nameof(ItemsStatusText));
                    OnPropertyChanged(nameof(HasItems));
                    OnPropertyChanged(nameof(HasNoItems));
                } 
            });

            OpenItemCommand = new RelayCommand(async p =>
            {
                if (p is DownloadItemViewModel vm)
                {
                    try { await OpenFolderAsync(OutputFolder); } catch { }
                }
            });

            BrowseFolderCommand = new RelayCommand(async () =>
            {
                var res = await _folderPicker.PickFolderAsync();
                if (!string.IsNullOrWhiteSpace(res)) OutputFolder = res!;
            });
        }

        private DownloadItemViewModel AddItemInternal(string url, string quality)
        {
            var vm = new DownloadItemViewModel
            {
                Url = url.Trim(),
                Title = ExtractTitleFromUrl(url.Trim()),
                Quality = quality,
                Status = DownloadStatus.Queued,
            };
            Items.Add(vm);
            Reindex();
            OnPropertyChanged(nameof(ItemsStatusText));
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(HasNoItems));
            return vm;
        }

        private static string ExtractTitleFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                return $"Video from {uri.Host}";
            }
            catch
            {
                return "Video Content";
            }
        }

        private void StartItem(DownloadItemViewModel vm)
        {
            if (vm.Status == DownloadStatus.Running) return;
            var req = new DownloadRequest
            {
                Url = vm.Url,
                Quality = vm.Quality,
                OutputFolder = OutputFolder
            };
            var progress = new Progress<DownloadItemHandle>(h =>
            {
                var target = Items.FirstOrDefault(x => x.Id == h.Id);
                if (target != null)
                {
                    target.Progress = h.Progress;
                    target.Status = h.Status;
                    target.DownloadedBytes = h.DownloadedBytes;
                    target.TotalBytes = h.TotalBytes;
                    target.DownloadSpeed = h.DownloadSpeed;
                    target.ErrorMessage = h.ErrorMessage;
                }
                OnPropertyChanged(nameof(ItemsStatusText));
            });
            var handle = _manager.Enqueue(req, progress);
            vm.Id = handle.Id;
            vm.Status = DownloadStatus.Running;
        }

        private void PauseItem(DownloadItemViewModel vm)
        {
            if (vm.Id != Guid.Empty)
            {
                _manager.Cancel(vm.Id);
                vm.Status = DownloadStatus.Paused; // reflecting local state; future resume would re-enqueue
            }
        }

        private void RetryItem(DownloadItemViewModel vm)
        {
            // Simply restart the item using the existing StartItem method
            // First reset the view model state
            vm.Progress = 0;
            vm.DownloadedBytes = 0;
            vm.TotalBytes = 0;
            vm.DownloadSpeed = 0;
            vm.ErrorMessage = null;
            vm.Status = DownloadStatus.Queued;
            
            // Then start it again
            StartItem(vm);
        }

        private void Reindex()
        {
            for (int i = 0; i < Items.Count; i++) Items[i].Index = i + 1;
        }

        private static TopLevel GetTopLevel()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow is not null)
            {
                return TopLevel.GetTopLevel(desktop.MainWindow)!;
            }
            throw new System.InvalidOperationException("No desktop main window available");
        }

        private static Task OpenFolderAsync(string path)
        {
            if (OperatingSystem.IsMacOS())
                return Run("open", path);
            if (OperatingSystem.IsWindows())
                return Run("explorer.exe", path);
            return Run("xdg-open", path);
        }

        private static Task Run(string file, string args)
        {
            try
            {
                using var p = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = file,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                p.Start();
            }
            catch { }
            return Task.CompletedTask;
        }

        private static bool IsValidUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            try
            {
                var uri = new Uri(url);
                return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
            }
            catch
            {
                return false;
            }
        }

        private string GetUrlValidationMessage()
        {
            if (string.IsNullOrWhiteSpace(NewUrl))
                return string.Empty;

            if (!IsValidUrl(NewUrl))
                return "Please enter a valid HTTP/HTTPS URL";

            return string.Empty;
        }

        private string GetItemsStatusText()
        {
            var total = Items.Count;
            var running = Items.Count(x => x.Status == DownloadStatus.Running);
            var completed = Items.Count(x => x.Status == DownloadStatus.Completed);
            var failed = Items.Count(x => x.Status == DownloadStatus.Failed);
            
            if (total == 0)
                return "No downloads in queue";
                
            var parts = new List<string> { $"{total} downloads" };
            
            if (running > 0)
                parts.Add($"{running} active");
            if (completed > 0)
                parts.Add($"{completed} completed");
            if (failed > 0)
                parts.Add($"{failed} failed");
                
            return string.Join(" • ", parts);
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
