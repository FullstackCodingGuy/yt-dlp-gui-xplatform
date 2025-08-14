using System;
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

        public string NewUrl { get => _newUrl; set => SetField(ref _newUrl, value); }
        public string NewQuality { get => _newQuality; set => SetField(ref _newQuality, value); }
        public string[] Qualities { get; } = new[] { "Best", "Good (720p)", "Data Saver (480p)" };
        public int MaxConcurrent { get => _maxConcurrent; set { if (SetField(ref _maxConcurrent, value)) _manager.SetMaxConcurrent(value); } }
        public string OutputFolder { get => _outputFolder; set => SetField(ref _outputFolder, value); }

        private string _newUrl = string.Empty;
        private string _newQuality = "Best";
        private int _maxConcurrent = 2;
        private string _outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public RelayCommand AddItemCommand { get; }
        public RelayCommand AddAndStartCommand { get; }
        public RelayCommand StartAllCommand { get; }
        public RelayCommand PauseAllCommand { get; }
        public RelayCommand ClearCompletedCommand { get; }
        public RelayCommand StartItemCommand { get; }
        public RelayCommand PauseItemCommand { get; }
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
            _manager = new DownloadManager(_maxConcurrent);

            AddItemCommand = new RelayCommand(() =>
            {
                if (string.IsNullOrWhiteSpace(NewUrl)) return;
                var vm = AddItemInternal(NewUrl, NewQuality);
                NewUrl = string.Empty;
            });

            AddAndStartCommand = new RelayCommand(() =>
            {
                if (string.IsNullOrWhiteSpace(NewUrl)) return;
                var vm = AddItemInternal(NewUrl, NewQuality);
                StartItem(vm);
                NewUrl = string.Empty;
            });

            StartAllCommand = new RelayCommand(() =>
            {
                foreach (var it in Items.ToArray()) StartItem(it);
            });

            PauseAllCommand = new RelayCommand(() =>
            {
                foreach (var it in Items.ToArray()) PauseItem(it);
            });

            ClearCompletedCommand = new RelayCommand(() =>
            {
                for (int i = Items.Count - 1; i >= 0; i--)
                {
                    if (Items[i].Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled)
                        Items.RemoveAt(i);
                }
                Reindex();
            });

            StartItemCommand = new RelayCommand(p => { if (p is DownloadItemViewModel vm) StartItem(vm); });
            PauseItemCommand = new RelayCommand(p => { if (p is DownloadItemViewModel vm) PauseItem(vm); });
            RemoveItemCommand = new RelayCommand(p => { if (p is DownloadItemViewModel vm) { Items.Remove(vm); Reindex(); } });
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
                Quality = quality,
                Status = DownloadStatus.Queued,
            };
            Items.Add(vm);
            Reindex();
            return vm;
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
                }
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
