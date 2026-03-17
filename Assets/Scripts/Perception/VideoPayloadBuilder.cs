using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VRPerception.Infra;
using VRPerception.Infra.EventBus;

namespace VRPerception.Perception
{
    internal static class VideoPayloadBuilder
    {
        private const string LogRootFolderName = "VRP_Logs";
        private const string VideoOutputFolderName = "videos";

        internal readonly struct VideoPayloadResult
        {
            public readonly string base64;
            public readonly string mimeType;

            public VideoPayloadResult(string base64, string mimeType)
            {
                this.base64 = base64;
                this.mimeType = mimeType;
            }
        }

        public static async Task<VideoPayloadResult> BuildFromFramesAsync(
            string requestId,
            System.Collections.Generic.IReadOnlyList<FrameCapturedEventData> frames,
            int fps,
            string imageFormat,
            string videoFormat,
            string ffmpegExecutable,
            bool keepSourceFrames,
            CancellationToken cancellationToken)
        {
            if (frames == null || frames.Count == 0)
            {
                throw new ArgumentException("Video payload requires at least one frame", nameof(frames));
            }

            if (string.IsNullOrWhiteSpace(ffmpegExecutable))
            {
                throw new InvalidOperationException("ffmpeg executable is not configured");
            }

            var resolvedExecutable = ResolveExecutablePath(ffmpegExecutable);
            var normalizedImageExt = NormalizeImageExtension(imageFormat);
            var normalizedVideoExt = NormalizeVideoExtension(videoFormat);
            var sessionDir = LogSessionPaths.GetOrCreateSessionDirectory(LogRootFolderName);
            var videoRootDir = Path.Combine(sessionDir, VideoOutputFolderName);
            var workspaceRoot = Path.Combine(videoRootDir, SanitizePathSegment(requestId));
            var frameDir = Path.Combine(workspaceRoot, "frames");
            var outputPath = Path.Combine(workspaceRoot, $"capture.{normalizedVideoExt}");
            var completedSuccessfully = false;

            Directory.CreateDirectory(videoRootDir);
            Directory.CreateDirectory(frameDir);

            try
            {
                for (int i = 0; i < frames.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var frame = frames[i];
                    if (frame == null || string.IsNullOrEmpty(frame.imageBase64))
                    {
                        throw new InvalidOperationException($"Frame {i} is missing image data");
                    }

                    var bytes = Convert.FromBase64String(frame.imageBase64);
                    var framePath = Path.Combine(frameDir, $"frame_{i:D4}.{normalizedImageExt}");
                    await Task.Run(() => File.WriteAllBytes(framePath, bytes), cancellationToken);
                }

                var inputPattern = Path.Combine(frameDir, $"frame_%04d.{normalizedImageExt}");
                var ffmpegArgs = BuildFfmpegArguments(inputPattern, outputPath, fps, normalizedVideoExt);
                var result = await RunFfmpegAsync(resolvedExecutable, ffmpegArgs, cancellationToken);
                if (result.exitCode != 0)
                {
                    throw new InvalidOperationException($"ffmpeg failed with exit code {result.exitCode}: {result.stderr}");
                }

                if (!File.Exists(outputPath))
                {
                    throw new InvalidOperationException("ffmpeg completed but no video file was produced");
                }

                var videoBytes = await Task.Run(() => File.ReadAllBytes(outputPath), cancellationToken);
                completedSuccessfully = true;
                return new VideoPayloadResult(Convert.ToBase64String(videoBytes), GetMimeType(normalizedVideoExt));
            }
            finally
            {
                if (!keepSourceFrames && completedSuccessfully)
                {
                    try
                    {
                        if (Directory.Exists(frameDir))
                        {
                            Directory.Delete(frameDir, true);
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        UnityEngine.Debug.LogWarning($"[VideoPayloadBuilder] Cleanup failed: {cleanupEx.Message}");
                    }
                }
                else if (!completedSuccessfully)
                {
                    UnityEngine.Debug.LogWarning($"[VideoPayloadBuilder] Video assembly failed; temporary files kept at: {workspaceRoot}");
                }
            }
        }

        private static string BuildFfmpegArguments(string inputPattern, string outputPath, int fps, string videoExtension)
        {
            var codecArgs = videoExtension == "webm"
                ? "-c:v libvpx-vp9 -pix_fmt yuv420p"
                : "-c:v libx264 -pix_fmt yuv420p -movflags +faststart";

            return $"-y -framerate {Math.Max(1, fps)} -i \"{inputPattern}\" {codecArgs} \"{outputPath}\"";
        }

        private static async Task<(int exitCode, string stderr)> RunFfmpegAsync(string executable, string arguments, CancellationToken cancellationToken)
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false
            };

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Failed to start ffmpeg process");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Unable to start ffmpeg '{executable}': {ex.Message}", ex);
            }

            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch
                {
                }
            });

            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.Run(() => process.WaitForExit(), cancellationToken);
            var stderr = await stderrTask;
            return (process.ExitCode, stderr?.Trim() ?? string.Empty);
        }

        private static string ResolveExecutablePath(string executable)
        {
            var candidate = (executable ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                throw new InvalidOperationException("ffmpeg executable is not configured");
            }

            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                throw new InvalidOperationException($"Configured ffmpeg executable does not exist: {candidate}");
            }

            foreach (var resolved in EnumerateExecutableCandidates(candidate))
            {
                if (File.Exists(resolved))
                {
                    return resolved;
                }
            }

            var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            throw new InvalidOperationException(
                $"Unable to locate ffmpeg executable '{candidate}'. " +
                "Set PerceptionSystem.ffmpegExecutable to an absolute path such as 'D:/APP/APP_tool/ffmpeg/bin/ffmpeg.exe', " +
                $"or ensure it is available in PATH. PATH length={pathValue.Length}");
        }

        private static System.Collections.Generic.IEnumerable<string> EnumerateExecutableCandidates(string executable)
        {
            yield return Path.GetFullPath(executable);

            var hasExtension = !string.IsNullOrEmpty(Path.GetExtension(executable));
            if (!hasExtension)
            {
                yield return Path.GetFullPath(executable + ".exe");
                yield return Path.GetFullPath(executable + ".cmd");
                yield return Path.GetFullPath(executable + ".bat");
            }

            var pathValue = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathValue))
            {
                yield break;
            }

            var pathDirs = pathValue.Split(Path.PathSeparator);
            for (int i = 0; i < pathDirs.Length; i++)
            {
                var dir = pathDirs[i]?.Trim();
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                yield return Path.Combine(dir, executable);
                if (!hasExtension)
                {
                    yield return Path.Combine(dir, executable + ".exe");
                    yield return Path.Combine(dir, executable + ".cmd");
                    yield return Path.Combine(dir, executable + ".bat");
                }
            }
        }

        private static string NormalizeImageExtension(string imageFormat)
        {
            var normalized = (imageFormat ?? "jpeg").Trim().ToLowerInvariant();
            return normalized switch
            {
                "jpg" => "jpg",
                "jpeg" => "jpg",
                "png" => "png",
                _ => "jpg"
            };
        }

        private static string NormalizeVideoExtension(string videoFormat)
        {
            var normalized = (videoFormat ?? "mp4").Trim().ToLowerInvariant();
            return normalized switch
            {
                "webm" => "webm",
                _ => "mp4"
            };
        }

        private static string GetMimeType(string videoExtension)
        {
            return videoExtension switch
            {
                "webm" => "video/webm",
                _ => "video/mp4"
            };
        }

        private static string SanitizePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Guid.NewGuid().ToString("N");
            }

            foreach (var invalidChar in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalidChar, '_');
            }

            return value;
        }
    }
}
