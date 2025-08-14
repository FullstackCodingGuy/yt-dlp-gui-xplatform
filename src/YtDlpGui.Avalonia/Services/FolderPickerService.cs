using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace YtDlpGui.AvaloniaApp.Services
{
    public interface IFolderPickerService
    {
        Task<string?> PickFolderAsync();
    }

    public class DesktopFolderPickerService : IFolderPickerService
    {
        public async Task<string?> PickFolderAsync()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow is not null)
            {
                var opts = new FolderPickerOpenOptions
                {
                    AllowMultiple = false,
                    Title = "Choose download folder"
                };
                var result = await desktop.MainWindow.StorageProvider.OpenFolderPickerAsync(opts);
                var folder = result?.FirstOrDefault();
                if (folder is null) return null;
#if NET8_0_OR_GREATER
                if (folder.TryGetLocalPath() is string local)
                    return local;
#endif
                return folder.Path?.LocalPath;
            }
            return null;
        }
    }
}
