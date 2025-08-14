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
                    var p = TryParseProgress(e.Data);
                    if (p.HasValue)
                    {
                        item.Progress = p.Value;
                        progress?.Report(item);
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    var p = TryParseProgress(e.Data);
                    if (p.HasValue)
                    {
                        item.Progress = p.Value;
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

        private static int? TryParseProgress(string line)
        {
            var m = ProgressRegex.Match(line);
            if (m.Success && int.TryParse(m.Groups["pct"].Value, out var pct))
            {
                return Math.Clamp(pct, 0, 100);
            }
            return null;
        }

        private static string BuildArgs(DownloadRequest req)
        {
            // Map friendly quality to yt-dlp format selector
            var fmt = req.Quality switch
            {
                "Best" => "best",
                "Good (720p)" => "bestvideo[height<=720]+bestaudio/best[height<=720]",
                "Data Saver (480p)" => "bestvideo[height<=480]+bestaudio/best[height<=480]",
                _ => req.Quality // assume raw format selector
            };

            // Output template in specified folder
            var output = Path.Combine(req.OutputFolder, "%(title)s.%(ext)s");
            return $"-f \"{fmt}\" -o \"{output}\" \"{req.Url}\"";
        }
    }
}
