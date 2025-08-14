using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YtDlpGui.AvaloniaApp.Models;

namespace YtDlpGui.AvaloniaApp.Services
{
    public class DownloadItemHandle
    {
        public Guid Id { get; } = Guid.NewGuid();
        public DownloadRequest Request { get; }
        public DownloadStatus Status { get; internal set; } = DownloadStatus.Queued;
        public int Progress { get; internal set; } = 0;
        public long DownloadedBytes { get; internal set; } = 0;
        public long TotalBytes { get; internal set; } = 0;
        public double DownloadSpeed { get; internal set; } = 0;
        public string? OutputPath { get; internal set; }
        internal CancellationTokenSource Cts { get; } = new();
        internal Task? Task { get; set; }

        public DownloadItemHandle(DownloadRequest req)
        {
            Request = req;
        }
    }

    public class DownloadManager
    {
        private SemaphoreSlim _semaphore;
        private readonly ConcurrentDictionary<Guid, DownloadItemHandle> _items = new();
        private readonly string _ytDlpExecutable;

        public DownloadManager(int maxConcurrent = 2, string? ytDlpExecutable = null)
        {
            _semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrent));
            _ytDlpExecutable = ytDlpExecutable ?? "yt-dlp"; // assume in PATH
        }

        public void SetMaxConcurrent(int n)
        {
            n = Math.Max(1, n);
            var newSem = new SemaphoreSlim(n);
            Interlocked.Exchange(ref _semaphore, newSem);
        }

        public DownloadItemHandle Enqueue(DownloadRequest request, IProgress<DownloadItemHandle>? progress = null)
        {
            var item = new DownloadItemHandle(request);
            _items[item.Id] = item;
            item.Task = RunItemAsync(item, progress);
            return item;
        }

        public bool TryGet(Guid id, out DownloadItemHandle handle) => _items.TryGetValue(id, out handle!);

        public IEnumerable<DownloadItemHandle> Items => _items.Values.OrderBy(_ => _.Id);

        public void Cancel(Guid id)
        {
            if (_items.TryGetValue(id, out var h))
            {
                h.Cts.Cancel();
            }
        }

        private async Task RunItemAsync(DownloadItemHandle item, IProgress<DownloadItemHandle>? progress)
        {
            await _semaphore.WaitAsync(item.Cts.Token).ConfigureAwait(false);
            try
            {
                if (!Directory.Exists(item.Request.OutputFolder))
                    Directory.CreateDirectory(item.Request.OutputFolder);

                item.Status = DownloadStatus.Running;
                progress?.Report(item);

                var args = BuildArgs(item.Request);
                var psi = new ProcessStartInfo
                {
                    FileName = _ytDlpExecutable,
                    Arguments = args,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

                process.OutputDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    
                    var progressInfo = TryParseProgressLine(e.Data);
                    if (progressInfo.HasValue)
                    {
                        var (percentage, downloaded, total, speed) = progressInfo.Value;
                        item.Progress = percentage;
                        item.DownloadedBytes = downloaded;
                        item.TotalBytes = total;
                        item.DownloadSpeed = speed;
                        progress?.Report(item);
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    
                    var progressInfo = TryParseProgressLine(e.Data);
                    if (progressInfo.HasValue)
                    {
                        var (percentage, downloaded, total, speed) = progressInfo.Value;
                        item.Progress = percentage;
                        item.DownloadedBytes = downloaded;
                        item.TotalBytes = total;
                        item.DownloadSpeed = speed;
                        progress?.Report(item);
                    }
                };

                try
                {
                    if (!process.Start())
                    {
                        item.Status = DownloadStatus.Failed;
                        progress?.Report(item);
                        return;
                    }
                }
                catch (Exception)
                {
                    item.Status = DownloadStatus.Failed;
                    progress?.Report(item);
                    return;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using (item.Cts.Token.Register(() => SafeKill(process)))
                {
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }

                if (item.Cts.IsCancellationRequested)
                {
                    item.Status = DownloadStatus.Canceled;
                }
                else if (process.ExitCode == 0)
                {
                    item.Progress = 100;
                    item.Status = DownloadStatus.Completed;
                }
                else
                {
                    item.Status = DownloadStatus.Failed;
                }
                progress?.Report(item);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private static void SafeKill(Process p)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { /* ignore */ }
        }

        private static readonly Regex ProgressRegex = new("(?<pct>\\d{1,3})%", RegexOptions.Compiled);
        private static readonly Regex DetailedProgressRegex = new(
            @"(?<pct>\d{1,3}(?:\.\d+)?)%\s+of\s+(?<total>[\d.]+\w+)\s+at\s+(?<speed>[\d.]+\w+/s)|(?<pct>\d{1,3}(?:\.\d+)?)%", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static int? TryParseProgress(string line)
        {
            var m = ProgressRegex.Match(line);
            if (m.Success && int.TryParse(m.Groups["pct"].Value, out var pct))
            {
                return Math.Clamp(pct, 0, 100);
            }
            return null;
        }

        private static (int percentage, long downloaded, long total, double speed)? TryParseProgressLine(string line)
        {
            var match = DetailedProgressRegex.Match(line);
            if (!match.Success) return null;

            if (!float.TryParse(match.Groups["pct"].Value, out var percentage))
                return null;

            var totalStr = match.Groups["total"].Value;
            var speedStr = match.Groups["speed"].Value;

            var total = ParseSize(totalStr);
            var speed = ParseSpeed(speedStr);
            var downloaded = (long)(total * percentage / 100.0);

            return ((int)Math.Clamp(percentage, 0, 100), downloaded, total, speed);
        }

        private static long ParseSize(string sizeStr)
        {
            if (string.IsNullOrWhiteSpace(sizeStr)) return 0;

            var match = Regex.Match(sizeStr, @"([\d.]+)(\w+)", RegexOptions.IgnoreCase);
            if (!match.Success || !double.TryParse(match.Groups[1].Value, out var value))
                return 0;

            var unit = match.Groups[2].Value.ToUpperInvariant();
            return unit switch
            {
                "B" => (long)value,
                "KB" or "KIB" => (long)(value * 1024),
                "MB" or "MIB" => (long)(value * 1024 * 1024),
                "GB" or "GIB" => (long)(value * 1024 * 1024 * 1024),
                "TB" or "TIB" => (long)(value * 1024L * 1024 * 1024 * 1024),
                _ => (long)value
            };
        }

        private static double ParseSpeed(string speedStr)
        {
            if (string.IsNullOrWhiteSpace(speedStr)) return 0;

            var match = Regex.Match(speedStr, @"([\d.]+)(\w+)/s", RegexOptions.IgnoreCase);
            if (!match.Success || !double.TryParse(match.Groups[1].Value, out var value))
                return 0;

            var unit = match.Groups[2].Value.ToUpperInvariant();
            return unit switch
            {
                "B" => value,
                "KB" or "KIB" => value * 1024,
                "MB" or "MIB" => value * 1024 * 1024,
                "GB" or "GIB" => value * 1024 * 1024 * 1024,
                _ => value
            };
        }

        private static string BuildArgs(DownloadRequest req)
        {
            // Map friendly quality to yt-dlp format selector
            var fmt = req.Quality switch
            {
                // Video formats
                "Best Video (4K/1080p/720p)" => "bestvideo[height<=2160]+bestaudio/best",
                "4K Video (2160p)" => "bestvideo[height<=2160]/best[height<=2160]",
                "1080p Video" => "bestvideo[height<=1080]/best[height<=1080]",
                "720p Video" => "bestvideo[height<=720]/best[height<=720]",
                "480p Video" => "bestvideo[height<=480]/best[height<=480]",
                "360p Video" => "bestvideo[height<=360]/best[height<=360]",
                
                // Audio only formats
                "Audio Only - Best Quality" => "bestaudio/best",
                "Audio Only - MP3 320kbps" => "bestaudio[ext=mp3]/bestaudio",
                "Audio Only - MP3 256kbps" => "bestaudio[abr<=256]/bestaudio",
                "Audio Only - MP3 128kbps" => "bestaudio[abr<=128]/bestaudio",
                "Audio Only - AAC Best" => "bestaudio[ext=m4a]/bestaudio",
                "Audio Only - FLAC" => "bestaudio[ext=flac]/bestaudio",
                "Audio Only - OGG" => "bestaudio[ext=ogg]/bestaudio",
                
                // Combined formats
                "Video + Audio - Best" => "best",
                "Video + Audio - 1080p + Best Audio" => "bestvideo[height<=1080]+bestaudio/best[height<=1080]",
                "Video + Audio - 720p + Best Audio" => "bestvideo[height<=720]+bestaudio/best[height<=720]",
                
                // Legacy support
                "Best" => "best",
                "Good (720p)" => "bestvideo[height<=720]+bestaudio/best[height<=720]",
                "Data Saver (480p)" => "bestvideo[height<=480]+bestaudio/best[height<=480]",
                
                _ => req.Quality // assume raw format selector
            };

            // Add post-processing for audio-only downloads
            var postProcessor = req.Quality.Contains("Audio Only") ? GetAudioPostProcessor(req.Quality) : "";

            // Output template in specified folder
            var output = Path.Combine(req.OutputFolder, "%(title)s.%(ext)s");
            
            var args = $"-f \"{fmt}\" -o \"{output}\" \"{req.Url}\"";
            if (!string.IsNullOrEmpty(postProcessor))
            {
                args += $" {postProcessor}";
            }
            
            return args;
        }

        private static string GetAudioPostProcessor(string quality)
        {
            return quality switch
            {
                "Audio Only - MP3 320kbps" => "--extract-audio --audio-format mp3 --audio-quality 320K",
                "Audio Only - MP3 256kbps" => "--extract-audio --audio-format mp3 --audio-quality 256K",
                "Audio Only - MP3 128kbps" => "--extract-audio --audio-format mp3 --audio-quality 128K",
                "Audio Only - AAC Best" => "--extract-audio --audio-format aac",
                "Audio Only - FLAC" => "--extract-audio --audio-format flac",
                "Audio Only - OGG" => "--extract-audio --audio-format vorbis",
                _ => "--extract-audio --audio-format mp3 --audio-quality 0" // Best quality MP3
            };
        }
    }
}
