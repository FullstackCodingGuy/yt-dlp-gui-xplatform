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
        public string? ErrorMessage { get; internal set; }
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
                // Validate URL before starting
                if (!IsValidUrl(item.Request.Url))
                {
                    item.Status = DownloadStatus.Failed;
                    item.ErrorMessage = "Invalid URL format. Please provide a valid video URL.";
                    progress?.Report(item);
                    return;
                }

                // Validate and create output directory
                try
                {
                    if (!Directory.Exists(item.Request.OutputFolder))
                        Directory.CreateDirectory(item.Request.OutputFolder);
                }
                catch (Exception ex)
                {
                    item.Status = DownloadStatus.Failed;
                    item.ErrorMessage = $"Cannot create output directory: {ex.Message}";
                    progress?.Report(item);
                    return;
                }

                // Check if yt-dlp is available
                if (!await IsYtDlpAvailable())
                {
                    item.Status = DownloadStatus.Failed;
                    item.ErrorMessage = "yt-dlp executable not found. Please ensure yt-dlp is installed and available in PATH.";
                    progress?.Report(item);
                    return;
                }

                item.Status = DownloadStatus.Running;
                progress?.Report(item);

                string args;
                try
                {
                    args = BuildArgs(item.Request);
                }
                catch (ArgumentException ex)
                {
                    item.Status = DownloadStatus.Failed;
                    item.ErrorMessage = ex.Message;
                    progress?.Report(item);
                    return;
                }

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
                var errorOutput = new System.Text.StringBuilder();

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
                    
                    // Capture error output for detailed error messages
                    errorOutput.AppendLine(e.Data);
                    
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
                        item.ErrorMessage = "Failed to start yt-dlp process.";
                        progress?.Report(item);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    item.Status = DownloadStatus.Failed;
                    item.ErrorMessage = $"Error starting download process: {ex.Message}";
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
                    item.ErrorMessage = "Download was canceled by user.";
                }
                else if (process.ExitCode == 0)
                {
                    item.Progress = 100;
                    item.Status = DownloadStatus.Completed;
                    item.ErrorMessage = null; // Clear any previous error
                }
                else
                {
                    item.Status = DownloadStatus.Failed;
                    item.ErrorMessage = ParseYtDlpError(errorOutput.ToString());
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
            // Validate input
            if (string.IsNullOrWhiteSpace(req.Url))
                throw new ArgumentException("URL cannot be empty", nameof(req));
            
            if (string.IsNullOrWhiteSpace(req.OutputFolder))
                throw new ArgumentException("Output folder cannot be empty", nameof(req));

            // Check format compatibility
            if (!IsFormatCompatible(req.Url, req.Quality))
            {
                throw new ArgumentException($"The selected quality '{req.Quality}' may not be compatible with this URL.", nameof(req));
            }

            // Map friendly quality to yt-dlp format selector with fallbacks
            var fmt = req.Quality switch
            {
                // Video formats with fallbacks for better compatibility
                "Best Video (4K/1080p/720p)" => "bestvideo[height<=2160]+bestaudio/best[height<=2160]/bestvideo[height<=1080]+bestaudio/best[height<=1080]/bestvideo+bestaudio/best",
                "4K Video (2160p)" => "bestvideo[height<=2160]+bestaudio/best[height<=2160]/bestvideo[height<=1440]+bestaudio/best[height<=1440]/bestvideo+bestaudio/best",
                "1080p Video" => "bestvideo[height<=1080]+bestaudio/best[height<=1080]/bestvideo[height<=720]+bestaudio/best[height<=720]/bestvideo+bestaudio/best",
                "720p Video" => "bestvideo[height<=720]+bestaudio/best[height<=720]/bestvideo[height<=480]+bestaudio/best[height<=480]/bestvideo+bestaudio/best",
                "480p Video" => "bestvideo[height<=480]+bestaudio/best[height<=480]/bestvideo[height<=360]+bestaudio/best[height<=360]/bestvideo+bestaudio/best",
                "360p Video" => "bestvideo[height<=360]+bestaudio/best[height<=360]/bestvideo+bestaudio/best",
                
                // Audio only formats with better fallbacks
                "Audio Only - Best Quality" => "bestaudio[ext=m4a]/bestaudio[ext=mp3]/bestaudio/best",
                "Audio Only - MP3 320kbps" => "bestaudio[ext=mp3][abr>=256]/bestaudio[ext=mp3]/bestaudio",
                "Audio Only - MP3 256kbps" => "bestaudio[ext=mp3][abr>=192]/bestaudio[ext=mp3]/bestaudio",
                "Audio Only - MP3 128kbps" => "bestaudio[ext=mp3][abr>=96]/bestaudio[ext=mp3]/bestaudio",
                "Audio Only - AAC Best" => "bestaudio[ext=m4a]/bestaudio[ext=aac]/bestaudio",
                "Audio Only - FLAC" => "bestaudio[ext=flac]/bestaudio",
                "Audio Only - OGG" => "bestaudio[ext=ogg]/bestaudio[ext=vorbis]/bestaudio",
                
                // Combined formats
                "Video + Audio - Best" => "best[height<=2160]/best",
                "Video + Audio - 1080p + Best Audio" => "best[height<=1080]/bestvideo[height<=1080]+bestaudio/best",
                "Video + Audio - 720p + Best Audio" => "best[height<=720]/bestvideo[height<=720]+bestaudio/best",
                
                // Legacy support
                "Best" => "best",
                "Good (720p)" => "best[height<=720]/bestvideo[height<=720]+bestaudio/best",
                "Data Saver (480p)" => "best[height<=480]/bestvideo[height<=480]+bestaudio/best",
                
                _ => req.Quality // assume raw format selector
            };

            // Add post-processing for audio-only downloads
            var postProcessor = req.Quality.Contains("Audio Only") ? GetAudioPostProcessor(req.Quality) : "";

            // Sanitize filename and create safe output template
            var outputTemplate = Path.Combine(req.OutputFolder, "%(uploader)s - %(title)s.%(ext)s");
            
            // Build command arguments with proper escaping and additional safety options
            var args = $"-f \"{fmt}\" " +
                      $"--output \"{outputTemplate}\" " +
                      $"--no-playlist " +
                      $"--write-info-json " +
                      $"--write-thumbnail " +
                      $"--embed-metadata " +
                      $"--ignore-errors " +
                      $"--no-warnings ";

            // Add post-processing arguments
            if (!string.IsNullOrEmpty(postProcessor))
            {
                args += $"{postProcessor} ";
            }

            // Add URL at the end
            args += $"\"{req.Url}\"";
            
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

        private async Task<bool> IsYtDlpAvailable()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ytDlpExecutable,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();
                await process.WaitForExitAsync().ConfigureAwait(false);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFormatCompatible(string url, string quality)
        {
            // Check if audio-only formats are being requested for platforms that might not support them well
            if (quality.Contains("Audio Only"))
            {
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();
                
                // Some platforms work better with specific audio formats
                if (host.Contains("spotify") || host.Contains("soundcloud"))
                {
                    return true; // These are primarily audio platforms
                }
                
                // Most video platforms support audio extraction
                return true;
            }

            // For video formats, most platforms support them
            return true;
        }

        private static string ParseYtDlpError(string errorOutput)
        {
            if (string.IsNullOrWhiteSpace(errorOutput))
                return "Download failed with unknown error.";

            var lines = errorOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                
                // Common yt-dlp error patterns
                if (trimmedLine.Contains("ERROR:"))
                {
                    if (trimmedLine.Contains("Video unavailable") || trimmedLine.Contains("video is unavailable"))
                        return "Video is unavailable or has been removed.";
                    if (trimmedLine.Contains("Private video") || trimmedLine.Contains("video is private"))
                        return "This video is private and cannot be downloaded.";
                    if (trimmedLine.Contains("No video formats found") || trimmedLine.Contains("requested format not available"))
                        return "No video formats available for the requested quality. Try a different quality setting.";
                    if (trimmedLine.Contains("Unsupported URL") || trimmedLine.Contains("not supported"))
                        return "This website or URL is not supported by yt-dlp.";
                    if (trimmedLine.Contains("network") || trimmedLine.Contains("timeout") || trimmedLine.Contains("connection"))
                        return "Network error occurred. Please check your internet connection and try again.";
                    if (trimmedLine.Contains("age-restricted") || trimmedLine.Contains("age restricted"))
                        return "This video is age-restricted and cannot be downloaded.";
                    if (trimmedLine.Contains("geo-blocked") || trimmedLine.Contains("not available in your country") || trimmedLine.Contains("geographic"))
                        return "This video is not available in your region.";
                    if (trimmedLine.Contains("copyright") || trimmedLine.Contains("DMCA"))
                        return "This video is protected by copyright and cannot be downloaded.";
                    if (trimmedLine.Contains("live") && trimmedLine.Contains("stream"))
                        return "Live streams cannot be downloaded. Please wait until the stream ends.";
                    if (trimmedLine.Contains("premium") || trimmedLine.Contains("subscription"))
                        return "This content requires a premium subscription and cannot be downloaded.";
                    if (trimmedLine.Contains("authentication") || trimmedLine.Contains("login"))
                        return "This content requires authentication. Please ensure you have access rights.";
                    if (trimmedLine.Contains("file system") || trimmedLine.Contains("permission denied") || trimmedLine.Contains("access denied"))
                        return "Permission denied. Please check folder permissions and available disk space.";
                    if (trimmedLine.Contains("disk") && trimmedLine.Contains("space"))
                        return "Insufficient disk space. Please free up space and try again.";
                    
                    // Return the error message after "ERROR:"
                    var errorIndex = trimmedLine.IndexOf("ERROR:");
                    if (errorIndex >= 0)
                    {
                        var errorMsg = trimmedLine.Substring(errorIndex + 6).Trim();
                        return string.IsNullOrEmpty(errorMsg) ? "Download failed." : errorMsg;
                    }
                }
                
                // Check for warning patterns that might indicate issues
                if (trimmedLine.Contains("WARNING:"))
                {
                    if (trimmedLine.Contains("format not available"))
                        return "The requested format is not available. Try a different quality setting.";
                }
            }

            // If no specific error found, return a generic message
            return "Download failed. Please check the URL and try again.";
        }
    }
}
