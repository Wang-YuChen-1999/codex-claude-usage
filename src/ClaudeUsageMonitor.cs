using System;
using System.Collections.Generic;
using System.IO;

namespace CodexClaudeUsage
{
    internal sealed class ClaudeUsageMonitor : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Func<string> _findHistory;
        private readonly string _bridgePath;
        private readonly Dictionary<string, WatchedFile> _files =
            new Dictionary<string, WatchedFile>(StringComparer.OrdinalIgnoreCase);
        private string _lastHistoryPath;
        private bool _disposed;
        private bool _pending;
        private DateTimeOffset _firstChange;
        private DateTimeOffset _lastChange;
        private DateTimeOffset _lastDispatch = DateTimeOffset.MinValue;

        private sealed class WatchedFile
        {
            public FileSystemWatcher Watcher;
            public DateTime CreatedAt;
            public bool Failed;
        }

        public ClaudeUsageMonitor(Func<string> findHistory, string bridgePath)
        {
            _findHistory = findHistory;
            _bridgePath = bridgePath;
        }

        public void UpdatePaths()
        {
            string history = null;
            try { history = _findHistory(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            lock (_gate)
            {
                if (_disposed) return;
                // 原子替換期間找不到檔案時，仍保留已知路徑的監聽。
                if (!string.IsNullOrWhiteSpace(history)) _lastHistoryPath = history;
                var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(_lastHistoryPath)) wanted.Add(_lastHistoryPath);
                if (!string.IsNullOrWhiteSpace(_bridgePath)) wanted.Add(_bridgePath);
                foreach (var path in new List<string>(_files.Keys))
                {
                    var watched = _files[path];
                    var folder = Path.GetDirectoryName(path);
                    if (!wanted.Contains(path) || watched.Failed || FolderChanged(folder, watched.CreatedAt))
                    {
                        watched.Watcher.Dispose();
                        _files.Remove(path);
                    }
                }
                foreach (var path in wanted)
                {
                    if (_files.ContainsKey(path)) continue;
                    FileSystemWatcher watcher = null;
                    try
                    {
                        var folder = Path.GetDirectoryName(path);
                        if (!Directory.Exists(folder)) continue;
                        var watched = new WatchedFile { CreatedAt = Directory.GetCreationTimeUtc(folder) };
                        watcher = new FileSystemWatcher(folder, Path.GetFileName(path))
                        {
                            IncludeSubdirectories = false,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                        };
                        watched.Watcher = watcher;
                        watcher.Changed += FileChanged;
                        watcher.Created += FileChanged;
                        watcher.Deleted += FileChanged;
                        watcher.Renamed += FileChanged;
                        watcher.Error += delegate
                        {
                            lock (_gate)
                            {
                                watched.Failed = true;
                                QueueChange(DateTimeOffset.UtcNow);
                            }
                        };
                        watcher.EnableRaisingEvents = true;
                        _files.Add(path, watched);
                        watcher = null;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (ArgumentException) { }
                    finally { if (watcher != null) watcher.Dispose(); }
                }
            }
        }

        private void FileChanged(object sender, FileSystemEventArgs args)
        {
            lock (_gate) { QueueChange(DateTimeOffset.UtcNow); }
        }

        private static bool FolderChanged(string path, DateTime createdAt)
        {
            try { return !Directory.Exists(path) || Directory.GetCreationTimeUtc(path) != createdAt; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        private void QueueChange(DateTimeOffset now)
        {
            if (_disposed) return;
            if (!_pending) _firstChange = now;
            _pending = true;
            _lastChange = now;
        }

        public bool TryTakeChange(DateTimeOffset now, bool refreshInProgress)
        {
            lock (_gate)
            {
                if (_disposed || !_pending || refreshInProgress) return false;
                // 合併連續寫入，但不讓持續寫入無限延後；網路來源最密集每五秒刷新一次。
                if (now - _lastChange < TimeSpan.FromMilliseconds(750)
                    && now - _firstChange < TimeSpan.FromSeconds(2)) return false;
                if (now - _lastDispatch < TimeSpan.FromSeconds(5)) return false;
                _pending = false;
                _lastDispatch = now;
                return true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _pending = false;
                foreach (var watched in _files.Values) watched.Watcher.Dispose();
                _files.Clear();
            }
        }
    }
}
