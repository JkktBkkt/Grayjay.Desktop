using Grayjay.ClientServer.Controllers;
using Grayjay.ClientServer.Exceptions;
using Grayjay.ClientServer.Models;
using Grayjay.ClientServer.Models.Downloads;
using Grayjay.ClientServer.Settings;
using Grayjay.ClientServer.Store;
using Grayjay.Desktop.POC;
using Grayjay.Engine;
using Grayjay.Engine.Models.Detail;
using Grayjay.Engine.Models.Feed;
using Grayjay.Engine.Models.General;
using Grayjay.Engine.Models.Video.Sources;
using Grayjay.Engine.Web;
using System.Runtime.CompilerServices;
using PlatformID = Grayjay.Engine.Models.General.PlatformID;

using Logger = Grayjay.Desktop.POC.Logger;
using System.Collections.Generic;
using System.Diagnostics;

namespace Grayjay.ClientServer.States
{
    public static class StateDownloads
    {
        private static object _downloadLock = new object();
        private static bool _downloadCycleRequested = false;
        public static bool IsDownloading { get; private set; }

        private static DirectoryInfo _downloadsDirectory = new DirectoryInfo("downloads");

        public static ManagedStore<PlaylistDownload> _downloadingPlaylists = new ManagedStore<PlaylistDownload>("downloads_playlists")
            .WithUnique(x=>x.PlaylistID)
            .WithKeepUnloadable()
            .Load();

        public static ManagedStore<VideoDownload> _downloading = new ManagedStore<VideoDownload>("downloads_ongoing")
            .WithUnique(x=>x.Video.ID)
            .WithKeepUnloadable()
            .Load();
        public static ManagedStore<VideoLocal> _downloaded = new ManagedStore<VideoLocal>("downloaded")
            .WithUnique(x=>x.VideoDetails.ID)
            .WithKeepUnloadable()
            .Load();

        //Kept apart from GrayjaySettings, the settings page saves its whole copy and would overwrite a folder changed meanwhile
        private static StringStore _downloadsDirectorySetting = new StringStore("downloads_directory", null).Load();
        private static string _defaultDownloadsDirectory = "downloads";

        //Written by file managers into any folder they show, so a folder holding only these still counts as empty
        private static readonly HashSet<string> _fileManagerFiles = new HashSet<string>(new[] { ".DS_Store", "desktop.ini", "Thumbs.db", ".directory" }, StringComparer.OrdinalIgnoreCase);
        //Created by the file system in a drive root and not hidden, other system folders are skipped by their attributes
        private static readonly HashSet<string> _systemFolders = new HashSet<string>(new[] { "lost+found" }, StringComparer.OrdinalIgnoreCase);
        private static bool _movingDownloads = false;
        //Held while files move, so deleting a download waits and then deletes the moved files
        private static readonly object _moveLock = new object();

        public static StorageInfo GetDownloadStorageInfo() => StorageInfo.GetInfo(_downloadsDirectory.FullName);

        public static event Action<VideoDownload, VideoLocal> OnDownloadCompleted;
        public static event Action OnDownloadsChanged;

        static StateDownloads()
        {
            _defaultDownloadsDirectory = Path.Combine(StateApp.GetAppDirectory().FullName, "downloads");
            string path = ChooseDownloadsDirectory(_downloadsDirectorySetting.GetCopy(), _defaultDownloadsDirectory);
            Directory.CreateDirectory(path);
            _downloadsDirectory = new DirectoryInfo(path);
            try
            {
                ResolveMovedFiles(path, _downloaded);
            }
            catch (Exception ex)
            {
                Logger.e(nameof(StateDownloads), "Failed to resolve moved downloaded files", ex);
            }
            OnDownloadCompleted += (download, local) =>
            {
                if (local != null)
                    GrayjayServer.Instance.WebSocket.Broadcast(local, "DownloadCompleted", local.VideoDetails.ID.Value);
            };
            OnDownloadsChanged += () =>
            {
                GrayjayServer.Instance.WebSocket.Broadcast(null, "DownloadsChanged", null);
            };
        }

        /// <summary>The configured downloads folder when it exists, otherwise the default one.</summary>
        internal static string ChooseDownloadsDirectory(string? configuredDirectory, string defaultDirectory)
        {
            if (string.IsNullOrWhiteSpace(configuredDirectory))
                return defaultDirectory;
            if (Directory.Exists(configuredDirectory))
                return Path.GetFullPath(configuredDirectory);
            Logger.w(nameof(StateDownloads), $"Download folder [{configuredDirectory}] does not exist, using [{defaultDirectory}]");
            return defaultDirectory;
        }

        /// <summary>Moves the downloaded files into another folder, which new downloads then use.</summary>
        /// <returns>False when the folder is already the download folder and no file had to move.</returns>
        /// <exception cref="InvalidOperationException">Downloads are running, or the files could not be moved.</exception>
        public static bool ChangeDownloadDirectory(string directory)
        {
            string newDirectory = Path.GetFullPath(directory);
            bool changed;
            lock (_downloadLock)
            {
                if (_movingDownloads)
                    throw new InvalidOperationException("The downloads are already being moved, wait until that finishes.");
                if (IsDownloading)
                    throw new InvalidOperationException("Downloads are in progress, wait until they finish before changing the download folder.");
                //Holds back download cycles until the files are moved
                IsDownloading = true;
                _movingDownloads = true;
            }
            try
            {
                Logger.i(nameof(StateDownloads), $"Change download directory to [{newDirectory}]");
                var sinceLastToast = Stopwatch.StartNew();
                changed = MoveDownloads(newDirectory, _downloaded, _downloadsDirectorySetting, _defaultDownloadsDirectory,
                    fileCount => StateUI.Toast($"Moving {fileCount} downloaded files to\n[{newDirectory}]"),
                    (movedCount, totalCount) =>
                    {
                        if (movedCount < totalCount && sinceLastToast.Elapsed >= TimeSpan.FromSeconds(5))
                        {
                            StateUI.Toast($"Moved {movedCount} of {totalCount} downloaded files");
                            sinceLastToast.Restart();
                        }
                    });
            }
            finally
            {
                //Follows the saved folder, which stays on the new one when some files could not be moved back
                _downloadsDirectory = new DirectoryInfo(ChooseDownloadsDirectory(_downloadsDirectorySetting.GetCopy(), _defaultDownloadsDirectory));
                bool cycleRequested;
                lock (_downloadLock)
                {
                    IsDownloading = false;
                    _movingDownloads = false;
                    cycleRequested = _downloadCycleRequested;
                    _downloadCycleRequested = false;
                }
                if (cycleRequested)
                    _ = StartDownloadCycle();
            }
            if (changed)
                OnDownloadsChanged?.Invoke();
            return changed;
        }

        /// <summary>Saves the new folder, moves every downloaded file into it and points the records at the moved files.</summary>
        /// <remarks>A new folder must be empty apart from files the records already name. On failure, moved files are moved back and the saved folder is restored.</remarks>
        /// <param name="onMoving">Called with the number of files to move, once the folder is accepted and before any file moves.</param>
        /// <param name="onProgress">Called with the number of moved files and the total after each file.</param>
        /// <returns>True when a file moved, a record changed or the saved folder changed.</returns>
        internal static bool MoveDownloads(string newDirectory, ManagedStore<VideoLocal> downloaded, StringStore directorySetting, string defaultDirectory, Action<int>? onMoving = null, Action<int, int>? onProgress = null)
        {
            lock (_moveLock)
            {
                return MoveDownloadsLocked(newDirectory, downloaded, directorySetting, defaultDirectory, onMoving, onProgress);
            }
        }

        private static bool MoveDownloadsLocked(string newDirectory, ManagedStore<VideoLocal> downloaded, StringStore directorySetting, string defaultDirectory, Action<int>? onMoving, Action<int, int>? onProgress)
        {
            newDirectory = Path.GetFullPath(newDirectory);
            if (!Directory.Exists(newDirectory))
                throw new InvalidOperationException($"The folder [{newDirectory}] does not exist.");
            if (downloaded.HasUnloadable)
                throw new InvalidOperationException($"{downloaded.UnloadableIds.Count} downloads could not be loaded, so their files cannot be moved.");

            string? previousSetting = directorySetting.GetCopy();
            string? newSetting = PathsEqual(newDirectory, defaultDirectory) ? null : newDirectory;
            bool settingChanges = !SameSetting(previousSetting, newSetting);
            bool isCurrentDirectory = PathsEqual(newDirectory, ChooseDownloadsDirectory(previousSetting, defaultDirectory));

            var savedRecords = new HashSet<VideoLocal>(downloaded.GetObjects(), ReferenceEqualityComparer.Instance);
            List<VideoLocal> records = savedRecords.Concat(downloaded.DuplicateObjects).ToList();
            var recordFiles = records
                .SelectMany(local => DownloadedFiles(local).Select(file => (Record: local, File: file)))
                .Where(entry => !string.IsNullOrEmpty(entry.File.FilePath))
                .ToList();
            var filesByPath = recordFiles
                .GroupBy(entry => Path.GetFullPath(entry.File.FilePath), PathComparer)
                .ToDictionary(group => group.Key, group => group.ToList(), PathComparer);
            if (!isCurrentDirectory)
            {
                var knownNames = new HashSet<string>(filesByPath.Keys.Select(FileNameOf), PathComparer);
                foreach (var entry in new DirectoryInfo(newDirectory).EnumerateFileSystemInfos())
                {
                    if (IsIgnoredFolderEntry(entry))
                        continue;
                    if (entry is not FileInfo || !knownNames.Contains(entry.Name))
                        throw new InvalidOperationException($"Choose an empty folder, [{newDirectory}] already contains [{entry.Name}].");
                }
            }

            var moves = new List<(string From, string To, bool Overwrite)>();
            var plannedTargets = new Dictionary<string, string>(PathComparer);
            foreach (string filePath in filesByPath.Keys)
            {
                string target = Path.Combine(newDirectory, FileNameOf(filePath));
                if (PathsEqual(filePath, target) || !File.Exists(filePath))
                    continue;
                if (filesByPath.ContainsKey(target) && File.Exists(target))
                    throw new InvalidOperationException($"[{filePath}] and [{target}] have the same name and cannot be moved into one folder.");
                if (plannedTargets.TryGetValue(target, out string? otherSource))
                    throw new InvalidOperationException($"[{filePath}] and [{otherSource}] have the same name and cannot be moved into one folder.");
                //No record points at the target, so it is a copy left by an interrupted move
                bool overwrite = File.Exists(target);
                if (overwrite)
                    Logger.w(nameof(StateDownloads), $"Replacing [{target}], a leftover from an interrupted move of [{filePath}].");
                plannedTargets[target] = filePath;
                moves.Add((filePath, target, overwrite));
            }

            if (moves.Count > 0)
                onMoving?.Invoke(moves.Count);
            //Saved first, so after a crash the next start finds the moved files in the new folder
            if (settingChanges)
            {
                try
                {
                    directorySetting.Save(newSetting);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Could not save the download folder setting, no files were moved: {ex.Message}", ex);
                }
            }
            var changedRecords = new HashSet<VideoLocal>(ReferenceEqualityComparer.Instance);
            var moved = new List<(string From, string To, bool Overwrite)>();
            try
            {
                foreach (var move in moves)
                {
                    File.Move(move.From, move.To, move.Overwrite);
                    moved.Add(move);
                    //Records follow each file, so playing or exporting during the move finds it
                    DeleteDashCache(move.From);
                    SetRecordedPath(filesByPath[move.From], move.To, changedRecords);
                    onProgress?.Invoke(moved.Count, moves.Count);
                }
            }
            catch (Exception ex)
            {
                var notMovedBack = new List<string>();
                var failedMove = moves[moved.Count];
                //A failed move can leave a copy behind, the target was free or a leftover before moving
                if (File.Exists(failedMove.From) && File.Exists(failedMove.To))
                {
                    try
                    {
                        File.Delete(failedMove.To);
                    }
                    catch (Exception deleteException)
                    {
                        Logger.e(nameof(StateDownloads), $"Failed to remove the partial copy [{failedMove.To}]", deleteException);
                        notMovedBack.Add(failedMove.To);
                    }
                }
                foreach (var move in Enumerable.Reverse(moved))
                {
                    try
                    {
                        File.Move(move.To, move.From);
                        SetRecordedPath(filesByPath[move.From], move.From, changedRecords);
                    }
                    catch (Exception rollbackException)
                    {
                        Logger.e(nameof(StateDownloads), $"Failed to move [{move.To}] back to [{move.From}]", rollbackException);
                        notMovedBack.Add(move.To);
                    }
                }
                //Files left in the new folder keep their new path, so they stay usable
                SaveRecords(changedRecords, savedRecords, downloaded);

                var outcome = new List<string>();
                if (notMovedBack.Count == 0)
                    outcome.Add("The other files were moved back, the download folder is unchanged.");
                else
                    outcome.Add($"These files could not be moved back or removed from the new folder: {string.Join(", ", notMovedBack)}");
                if (notMovedBack.Count == 0 && settingChanges)
                {
                    try
                    {
                        directorySetting.Save(previousSetting);
                    }
                    catch (Exception settingException)
                    {
                        Logger.e(nameof(StateDownloads), $"Failed to restore the download folder setting [{previousSetting}]", settingException);
                        outcome.Add($"The download folder setting could not be restored, it stays on [{newDirectory}]: {settingException.Message}");
                    }
                }
                throw new InvalidOperationException($"Failed to move [{failedMove.From}]: {ex.Message}\n{string.Join("\n", outcome)}", ex);
            }

            //Files already in the new folder whose recorded path is gone, such as files moved by hand
            foreach (var (record, file) in records.SelectMany(local => DownloadedFiles(local).Select(file => (local, file))))
            {
                if (string.IsNullOrEmpty(file.FilePath) || File.Exists(file.FilePath))
                    continue;
                string target = Path.Combine(newDirectory, FileNameOf(file.FilePath));
                if (!File.Exists(target))
                    continue;
                DeleteDashCache(file.FilePath);
                file.SetFilePath(target);
                changedRecords.Add(record);
            }
            SaveRecords(changedRecords, savedRecords, downloaded);
            Logger.i(nameof(StateDownloads), $"Moved {moved.Count} downloaded files to [{newDirectory}]");
            return moved.Count > 0 || changedRecords.Count > 0 || settingChanges;
        }

        private static void SetRecordedPath(List<(VideoLocal Record, DownloadedFile File)> entries, string filePath, HashSet<VideoLocal> changedRecords)
        {
            foreach (var entry in entries)
            {
                entry.File.SetFilePath(filePath);
                changedRecords.Add(entry.Record);
            }
        }

        private static void SaveRecords(IEnumerable<VideoLocal> changedRecords, HashSet<VideoLocal> savedRecords, ManagedStore<VideoLocal> downloaded)
        {
            //Older duplicates cannot be saved on their own, a record deleted meanwhile is not created again
            foreach (var local in changedRecords.Where(savedRecords.Contains))
                downloaded.Save(local, onlyExisting: true);
        }

        private static bool SameSetting(string? first, string? second)
        {
            if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second))
                return string.IsNullOrEmpty(first) && string.IsNullOrEmpty(second);
            return PathsEqual(first, second);
        }

        //Entries every drive root or file manager adds, which still leave a folder empty for the move
        private static bool IsIgnoredFolderEntry(FileSystemInfo entry)
        {
            if (entry is FileInfo && _fileManagerFiles.Contains(entry.Name))
                return true;
            if (entry is DirectoryInfo && _systemFolders.Contains(entry.Name))
                return true;
            return (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }

        /// <summary>Points records whose files no longer exist at their recorded path at the file of the same name in the downloads folder.</summary>
        /// <returns>The number of records that were updated.</returns>
        internal static int ResolveMovedFiles(string downloadsDirectory, ManagedStore<VideoLocal> downloaded)
        {
            int updatedRecords = 0;
            foreach (var local in downloaded.GetObjects())
            {
                bool changed = false;
                foreach (var file in DownloadedFiles(local))
                {
                    if (string.IsNullOrEmpty(file.FilePath) || File.Exists(file.FilePath))
                        continue;
                    string candidate = Path.Combine(downloadsDirectory, FileNameOf(file.FilePath));
                    if (!File.Exists(candidate))
                        continue;
                    Logger.i(nameof(StateDownloads), $"Downloaded file [{file.FilePath}] moved to [{candidate}]");
                    //The DASH cache is keyed by the full file path, so the one next to the file is stale
                    DeleteDashCache(candidate);
                    file.SetFilePath(candidate);
                    changed = true;
                }
                if (changed)
                {
                    downloaded.Save(local, onlyExisting: true);
                    updatedRecords++;
                }
            }
            return updatedRecords;
        }

        private record DownloadedFile(string FilePath, Action<string> SetFilePath);
        private static IEnumerable<DownloadedFile> DownloadedFiles(VideoLocal local)
            => local.VideoSources.Select(source => new DownloadedFile(source.FilePath, path => source.FilePath = path))
                .Concat(local.AudioSources.Select(source => new DownloadedFile(source.FilePath, path => source.FilePath = path)))
                .Concat(local.SubtitleSources.Select(source => new DownloadedFile(source.FilePath, path => source.FilePath = path)));

        /// <summary>The file name of a path written on any OS, so a Windows path also resolves on Linux.</summary>
        internal static string FileNameOf(string filePath) => filePath.Split('\\', '/').Last();

        private static void DeleteDashCache(string videoPath)
        {
            try
            {
                Transcoding.LocalDash.DeleteDownloadCache(videoPath);
            }
            catch (Exception ex)
            {
                Logger.w(nameof(StateDownloads), $"Failed to delete DASH cache of [{videoPath}]", ex);
            }
        }

        private static StringComparer PathComparer => (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private static bool PathsEqual(string first, string second)
            => PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)));

        public static string GetDownloadsDirectory()
        {
            if(!_downloadsDirectory.Exists)
                _downloadsDirectory.Create();
            return _downloadsDirectory.FullName;
        }

        public static List<VideoDownload> GetDownloading() => _downloading.GetObjects();
        public static List<VideoLocal> GetDownloaded() => _downloaded.GetObjects();
        public static List<VideoLocal> GetDownloaded(string groupId) => _downloaded.FindObjects(x => x.GroupID == groupId);

        public static VideoDownload GetDownloadingVideo(PlatformID id)
            => _downloading.FindObject(x => x.Video.ID.Equals(id));
        public static VideoLocal? GetDownloadedVideo(PlatformID id)
            => _downloaded.FindObject(x => x.ID.Equals(id));
        public static VideoLocal? GetDownloadedVideo(string url)
            => _downloaded.FindObject(x => x.Url?.Equals(url, StringComparison.OrdinalIgnoreCase) ?? false);

        public static PlaylistDownload GetDownloadingPlaylist(string id)
            => _downloadingPlaylists.FindObject(x => x.PlaylistID == id);
        public static List<PlaylistDownload> GetDownloadingPlaylists()
            => _downloadingPlaylists.GetObjects();

        public static VideoDownload StartDownload(PlatformVideoDetails video, IVideoSource? vsource = null, IAudioSource? asource = null, ISubtitleSource? ssource = null)
        {
            VideoDownload download = new VideoDownload(video, vsource, asource, ssource);
            UpdateDownloading(download);
            OnDownloadsChanged?.Invoke();
            return download;
        }
        public static VideoDownload StartDownload(PlatformVideo video, long? targetPixelCount = null, long? targetBitrate = null, string playlistId = null)
        {
            VideoDownload download = new VideoDownload(video, targetPixelCount, targetBitrate, null, "playlist", playlistId);
            UpdateDownloading(download);
            OnDownloadsChanged?.Invoke();
            return download;
        }

        public static PlaylistDownload StartDownload(string playlistId, int targetPixelCount = -1, int targetBitrate = -1)
        {
            PlaylistDownload download = new PlaylistDownload(playlistId, targetPixelCount, targetBitrate);
            UpdateDownloading(download);
            return download;
        }

        public static void UpdateDownloaded(VideoLocal local)
        {
            _downloaded.Save(local);
        }
        public static void UpdateDownloading(VideoDownload download)
        {
            _downloading.Save(download);
        }
        public static void UpdateDownloading(PlaylistDownload download)
        {
            _downloadingPlaylists.Save(download);
        }

        public static void RemoveDownloading(PlatformID id)
        {
            var vid = GetDownloadingVideo(id);
            RemoveDownloading(vid);
        }
        /// <param name="deletePartialFiles">Delete the files the download wrote so far, false once it completed.</param>
        public static void RemoveDownloading(VideoDownload download, bool deletePartialFiles = true)
        {
            if (_downloading.Delete(download))
            {
                download.Cancel();
                if (deletePartialFiles)
                    DeletePartialFiles(download, _downloaded);
                OnDownloadsChanged?.Invoke();
            }
        }

        /// <summary>Deletes the files a download wrote, unless a downloaded record uses them.</summary>
        /// <remarks>A file still open by the running download may fail to delete on Windows, the download removes it when it stops.</remarks>
        internal static void DeletePartialFiles(VideoDownload download, ManagedStore<VideoLocal> downloaded)
        {
            var usedPaths = new HashSet<string>(downloaded.GetObjects()
                .Concat(downloaded.DuplicateObjects)
                .SelectMany(DownloadedFiles)
                .Where(file => !string.IsNullOrEmpty(file.FilePath))
                .Select(file => Path.GetFullPath(file.FilePath)), PathComparer);
            foreach (string? filePath in new[] { download.VideoFilePath, download.AudioFilePath, download.SubtitleFilePath })
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath) || usedPaths.Contains(Path.GetFullPath(filePath)))
                    continue;
                try
                {
                    Logger.i(nameof(StateDownloads), $"Deleting partial download file [{filePath}]");
                    File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    Logger.w(nameof(StateDownloads), $"Failed to delete partial download file [{filePath}]", ex);
                }
            }
        }

        public static void RemoveDownloadingPlaylist(string playlistId)
        {
            var playlist = GetDownloadingPlaylist(playlistId);
            _downloadingPlaylists.Delete(playlist);

            foreach (var download in GetDownloading().Where(x => x.GroupID == playlistId))
                RemoveDownloading(download.Video?.ID);
            foreach (var download in GetDownloaded().Where(x => x.GroupID == playlistId))
                RemoveDownloaded(download.ID);
        }

        public static void RemoveDownloaded(PlatformID id)
            => RemoveDownloaded(GetDownloadedVideo(id));
        public static void RemoveDownloaded(VideoLocal local)
        {
            if(local == null)
                return;
            if (DeleteDownloaded(local, _downloaded))
                OnDownloadsChanged?.Invoke();
        }

        /// <summary>Deletes the files and the record of a download, waiting for a running move to finish first.</summary>
        internal static bool DeleteDownloaded(VideoLocal local, ManagedStore<VideoLocal> downloaded)
        {
            lock (_moveLock)
            {
                foreach (var source in local.VideoSources)
                {
                    try { Transcoding.LocalDash.DeleteDownloadCache(source.FilePath); }
                    catch (Exception ex) { Logger.w(nameof(StateDownloads), "Failed to delete downloaded DASH cache", ex); }
                }
                local.DeleteFiles();
                return downloaded.Delete(local);
            }
        }

        public static async Task CheckOutdatedPlaylistVideos(Playlist playlist, PlaylistDownload download)
        {
            var outdatedVideos = GetDownloaded(download.PlaylistID)
                .Where(x => !playlist.Videos.Any(y => y.ID.Equals(x.ID)))
                .ToArray();
            foreach (var outdated in outdatedVideos)
                RemoveDownloaded(outdated);
        }
        public static Task StartDownloadCycle() => RunExclusiveDownloadCycle(RunDownloadCycle);

        private static async Task RunExclusiveDownloadCycle(Func<Task> runCycle)
        {
            lock(_downloadLock)
            {
                if (IsDownloading)
                {
                    // Picked up by the running cycle so a download queued while it finishes is not lost.
                    _downloadCycleRequested = true;
                    return;
                }
                IsDownloading = true;
            }

            while (true)
            {
                try
                {
                    await runCycle();
                }
                catch (Exception ex)
                {
                    Logger.e(nameof(StateDownloads), "Download cycle failed: " + ex.Message, ex);
                }

                lock (_downloadLock)
                {
                    if (!_downloadCycleRequested)
                    {
                        IsDownloading = false;
                        return;
                    }
                    _downloadCycleRequested = false;
                }
            }
        }
        private static async Task RunDownloadCycle()
        {
            Logger.i(nameof(StateDownloads), "Started downloading cycle");


            List<PlaylistDownload> playlistsToDownload = StateDownloads.GetDownloadingPlaylists();
            foreach (var playlistDownload in playlistsToDownload)
            {
                try
                {
                    var playlist = StatePlaylists.Get(playlistDownload.PlaylistID);
                    if(playlist == null)
                    {
                        StateDownloads.RemoveDownloadingPlaylist(playlistDownload.PlaylistID);
                        continue;
                    }

                    await CheckOutdatedPlaylistVideos(playlist, playlistDownload);

                    foreach (var video in playlist.Videos)
                    {
                        var existingDownloading = StateDownloads.GetDownloadingVideo(video.ID);
                        if (existingDownloading != null)
                            continue;
                        var existingDownloaded = StateDownloads.GetDownloadedVideo(video.ID);
                        if (existingDownloaded != null)
                            continue;
                        StartDownload(video, 
                            (playlistDownload.TargetPixelCount != null && playlistDownload.TargetPixelCount >= 0) ? playlistDownload.TargetPixelCount : null, 
                            (playlistDownload.TargetBitrate != null && playlistDownload.TargetBitrate >= 0) ? playlistDownload.TargetBitrate : null,
                            playlist.Id.ToString()); ;
                    }
                }
                catch (Exception ex)
                {
                    Logger.e(nameof(StateDownloads), "Failed to download playlist " + playlistDownload.PlaylistID + " due to " + ex.Message, ex);
                }
            }


            List<VideoDownload> ignore = new List<VideoDownload>();
            var currentVideo = StateDownloads.GetDownloading().FirstOrDefault();
            while (currentVideo != null)
            {
                await TryDownloadVideo(currentVideo, ignore);

                Thread.Sleep(500);
                currentVideo = StateDownloads.GetDownloading().Where(x => !ignore.Contains(x)).FirstOrDefault();
            }

            Logger.i(nameof(StateDownloads), "Ended downloading cycle");
        }
        private static async Task TryDownloadVideo(VideoDownload currentVideo, List<VideoDownload> ignore)
        {
            try
            {
                await DownloadVideo(currentVideo);
                if (currentVideo.State == DownloadState.COMPLETED)
                {
                    OnDownloadCompleted?.Invoke(currentVideo, GetDownloadedVideo(currentVideo.Video.ID));
                }
            }
            catch (Exception ex)
            {
                Logger.e(nameof(StateDownloads), "Download failed", ex);

                if (currentVideo.Video == null && currentVideo.VideoDetails == null)
                {
                    //Corrupt
                    Logger.w(nameof(StateDownloads), "Video had no video or videodetail, removing download");
                    //StateDownloads.RemoveDownload(currentVideo);
                }
                else if (ex is DownloadException dex && !dex.IsRetryable)
                {
                    Logger.w(nameof(StateDownloads), "Video had exception that should not be retried");
                    //StateDownloads.RemoveDownload(currentVideo);
                    //StateDownloads.PreventPlaylistDownload(currentVideo);
                }
                else
                    Logger.e(nameof(StateDownloads), $"Failed download [{currentVideo.Video?.Name ?? currentVideo.VideoDetails?.Name}]: {ex.Message}", ex);

                if(ex is AggregateException ex2)
                {
                    currentVideo.Error = ex.Message + "\n\n" + string.Join("\n\n", ex2.InnerExceptions.Select(x => x.Message));
                }
                else
                    currentVideo.Error = ex.Message;
                currentVideo.PrepareTime = null;
                currentVideo.ChangeState(DownloadState.ERROR);
                ignore.Add(currentVideo);

                //TODO: Handle cancel
            }
            finally
            {
            }
        }
        private static async Task DownloadVideo(VideoDownload download)
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(StateApp.AppCancellationToken.Token, download.CancellationToken);
            var cancel = cancellationSource.Token;
            //TODO: Should download?

            if((download.PrepareTime?.DifferenceNowMinutes() ?? 99) > 15)
            {
                Logger.w(nameof(StateDownloads), $"Video Download [{download.Video?.Name ?? download.VideoDetails?.Name}] expired, re-preparing");
                download.VideoDetails = null;

                if (download.TargetPixelCount == null && download.VideoSource != null)
                    download.TargetPixelCount = (download.VideoSource.Width * download.VideoSource.Height);
                download.VideoSource = null;
                if (download.TargetBitrate == null && download.AudioSource != null)
                    download.TargetBitrate = download.AudioSource.Bitrate;
                download.AudioSource = null;
            }
            if (download.VideoDetails == null || (download.VideoSource == null && download.AudioSource == null))
                download.ChangeState(DownloadState.PREPARING);
            NotifyDownload(download);

            Logger.i(nameof(StateDownloads), $"Preparing [{download.AudioFileName}] started");
            if (download.State == DownloadState.PREPARING)
                download.Prepare(new ManagedHttpClient(), cancel);
            download.ChangeState(DownloadState.DOWNLOADING);
            NotifyDownload(download);

            var lastNotifyTime = DateTime.MinValue;
            Logger.i(nameof(StateDownloads), $"Downloading [{download.Video.Name}] started");
            //TODO: use plugin client
            await download.Download(new ManagedHttpClient(), (progress) =>
            {
                download.Progress = progress;

                var currentTime = DateTime.Now;
                if(currentTime.Subtract(lastNotifyTime).TotalMilliseconds > 500)
                {
                    NotifyDownload(download);
                    lastNotifyTime = DateTime.Now;
                }
            }, cancel);
            Logger.i(nameof(StateDownloads), $"Download [{download.Video.Name}] finished");
            StateDownloads.UpdateDownloading(download);

            download.ChangeState(DownloadState.VALIDATING);
            NotifyDownload(download);

            Logger.i(nameof(StateDownloads), $"Validating [{download.Video.Name}]");
            download.Validate();
            download.ChangeState(DownloadState.FINALIZING);
            NotifyDownload(download);

            Logger.i(nameof(StateDownloads), $"Completing [{download.Video.Name}]");
            download.Complete();
            download.ChangeState(DownloadState.COMPLETED);

            StateDownloads.RemoveDownloading(download, deletePartialFiles: false);
            NotifyDownload(download);
        }

        public static void CleanupFiles()
        {
            string directory = GetDownloadsDirectory();
            bool chosenFolder = !PathsEqual(directory, _defaultDownloadsDirectory);
            CleanupFiles(directory, _downloaded, deleteUnknown: !chosenFolder);
            //Leftovers in the default folder after moving away from it, such as partial files of failed downloads
            if (chosenFolder && Directory.Exists(_defaultDownloadsDirectory))
                CleanupFiles(_defaultDownloadsDirectory, _downloaded, deleteUnknown: true);
        }
        /// <param name="deleteUnknown">Delete files no record names, false for a folder the user chose, where they are only logged.</param>
        private static void CleanupFiles(string dirPath, ManagedStore<VideoLocal> downloaded, bool deleteUnknown)
        {
            var unloadableIds = downloaded.UnloadableIds;
            if (unloadableIds.Count > 0)
            {
                Logger.w(nameof(StateDownloads), $"Skipping downloads cleanup, {unloadableIds.Count} downloaded records could not be loaded [{string.Join(", ", unloadableIds)}]");
                return;
            }
            DirectoryInfo dir = new DirectoryInfo(dirPath);
            var knownFiles = new HashSet<string>(downloaded.GetObjects()
                .Concat(downloaded.DuplicateObjects)
                .SelectMany(DownloadedFiles)
                .Where(file => !string.IsNullOrEmpty(file.FilePath))
                .Select(file => FileNameOf(file.FilePath)), PathComparer);
            var unknownCaches = dir.GetDirectories("*.dash").Where(cache => !knownFiles.Contains(cache.Name[..^5])).ToList();
            var unknownFiles = dir.GetFiles().Where(file => !knownFiles.Contains(file.Name)).ToList();
            if (!deleteUnknown)
            {
                if (unknownFiles.Count > 0 || unknownCaches.Count > 0)
                    Logger.i(nameof(StateDownloads), $"Found {unknownFiles.Count + unknownCaches.Count} files no download uses in [{dirPath}], left in place: {string.Join(", ", unknownFiles.Select(file => file.Name).Concat(unknownCaches.Select(cache => cache.Name)))}");
                return;
            }
            foreach (var cache in unknownCaches)
            {
                try { Transcoding.LocalDash.DeleteDownloadCache(Path.Combine(dirPath, cache.Name[..^5])); }
                catch (Exception ex) { Logger.w(nameof(StateDownloads), "Failed to delete unused DASH cache", ex); }
            }
            Logger.w("StateDownloads", $"Found {unknownFiles.Count} redundant downloaded files, deleting");
            foreach (var del in unknownFiles)
            {
                try
                {
                    Logger.w("StateDownloads", $"Deleting redundant download file [{del.Name}]");
                    del.Delete();
                }
                catch (Exception ex)
                {
                    Logger.w("StateDownloads", $"Failed to delete unknown downloads file [{del.Name}]");
                }
            }
        }

        public static void NotifyDownload(VideoDownload download)
        {
            try
            {
                GrayjayServer.Instance?.WebSocket?.Broadcast(download, "DownloadChanged", download.Video.ID.Value);
            }
            catch { }
        }
    }
}
