using System.Text;
using Tmds.Fuse;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace BookFUSE
{
    class BookFUSEFileSystem : FuseFileSystemBase
    {
        private readonly CalibreLibrary _Library;

        /// <summary>
        /// The file system watcher instance that monitors changes in the calibre library directory.
        /// </summary>
        private readonly FileSystemWatcher? _Watcher;

        /// <summary>
        /// A timer used to delay the re-initialization of the library after file system changes.
        /// </summary>
        private Timer? _timer;

        /// <summary>
        /// A lock object to synchronize access to the timer and library re-initialization.
        /// </summary>
        private readonly Lock _lock = new();

        /// <summary>
        /// Represents a FUSE file system which translates a calibre library to a Kavita library.
        /// </summary>
        /// <param name="library">The calibre library to be represented by this FUSE file system.</param>
        public BookFUSEFileSystem(CalibreLibrary library) {
            _Library = library;
            _Watcher = new(_Library.Root)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "metadata.db",
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };
            _Watcher.Changed += LibraryChanged;
        }

        /// <summary>
        /// Handles changes to the library by responding to file system events.
        /// </summary>
        /// <remarks>This method is triggered when a file system change is detected. It ensures that the
        /// library is re-initialized after a short delay to handle potential bursts of file system events.</remarks>
        /// <param name="sender">The source of the event, typically the file system watcher.</param>
        /// <param name="e">The event data containing information about the file system change.</param>
        private void LibraryChanged(object sender, FileSystemEventArgs e)
        {
            BookFUSE.Log(BookFUSE.LogLevel.Information, "LibraryChanged", $"Library changed: {e.FullPath}");
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = new Timer(_ =>
                {
                    lock (_lock)
                    {
                        _timer?.Dispose();
                        _timer = null;
                    }
                    _Library.Init();
                }, null, 500, Timeout.Infinite);
            }
        }

        public override int GetAttr(ReadOnlySpan<byte> path, ref stat stat, FuseFileInfoRef fiRef)
        {
            var pathString = Encoding.UTF8.GetString(path.ToArray());
            if (_Library.GetLibrary(pathString, out var library))
            {
                if (library.GetSeries(pathString, out var series))
                {
                    if (series.GetBook(pathString, out var book))
                    {
                        stat.st_mode = S_IFREG | 0b110_110_110; // rw-rw-rw-
                        stat.st_nlink = 1;
                        stat.st_size = book.FileSize;
                        stat.st_ctim = book.Created.ToTimespec();
                        stat.st_atim = book.Modified.ToTimespec();
                        stat.st_mtim = book.Modified.ToTimespec();
                        return 0;
                    }
                    else if (pathString == Path.Join("/", library.Name, series.Name))
                    {
                        stat.st_mode = S_IFDIR | 0b101_101_101; // r-xr-xr-x
                        stat.st_nlink = 2 + (ulong)series.Books.Count;
                        return 0;
                    }
                }
                else if (pathString == Path.Join("/", library.Name))
                {
                    stat.st_mode = S_IFDIR | 0b101_101_101; // r-xr-xr-x
                    stat.st_nlink = 2 + (ulong)library.SeriesList.Count;
                    return 0;
                }
            }
            else if (path.SequenceEqual(RootPath))
            {
                stat.st_mode = S_IFDIR | 0b101_101_101; // r-xr-xr-x
                stat.st_nlink = 2 + (ulong)_Library.Libraries.Count;
                return 0;
            }
            return -ENOENT;
        }

        public override int Open(ReadOnlySpan<byte> path, ref FuseFileInfo fi)
        {
            var pathString = Encoding.UTF8.GetString(path.ToArray());
            if ((fi.flags & O_ACCMODE) != O_RDONLY)
            {
                return -EACCES; // Only read access is allowed
            }

            if (_Library.GetLibrary(pathString, out var library))
            {
                if (library.GetSeries(pathString, out var series))
                {
                    if (series.GetBook(pathString, out _))
                    {
                        return 0;
                    }
                }
            }
            return -ENOENT;
        }

        public override int OpenDir(ReadOnlySpan<byte> path, ref FuseFileInfo fi)
        {
            var pathString = Encoding.UTF8.GetString(path.ToArray());
            if (_Library.GetLibrary(pathString, out var library))
            {
                if (library.GetSeries(pathString, out var series))
                {
                    if (series.GetBook(pathString, out _))
                    {
                        return -ENOENT;
                    }
                    else if (pathString == Path.Join("/", library.Name, series.Name))
                    {
                        return 0;
                    }
                }
                else if (pathString == Path.Join("/", library.Name))
                {
                    return 0;
                }
            }
            else if (path.SequenceEqual(RootPath))
            {
                return 0;
            }
            return -ENOENT;
        }

        public override int Read(ReadOnlySpan<byte> path, ulong offset, Span<byte> buffer, ref FuseFileInfo fi)
        {
            var pathString = Encoding.UTF8.GetString(path.ToArray());
            if (_Library.GetLibrary(pathString, out var library))
            {
                if (library.GetSeries(pathString, out var series))
                {
                    if (series.GetBook(pathString, out var book))
                    {
                        FileStream stream = new(Path.Join(_Library.Root, library.Name, book.Path, book.PhysicalName),
                            FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        if ((ulong)stream.Length >= offset)
                        {
                            int length = (int)Math.Min(stream.Length - (int)offset, buffer.Length);
                            stream.Seek((long)offset, SeekOrigin.Begin);
                            byte[] tempBuffer = new byte[length];
                            int bytesRead = stream.Read(tempBuffer, 0, length);
                            if (bytesRead > 0)
                            {
                                tempBuffer.AsSpan(0, bytesRead).CopyTo(buffer);
                                return bytesRead;
                            }
                        }
                    }
                }
            }
            return 0;
        }

        public override int ReadDir(ReadOnlySpan<byte> path, ulong offset, ReadDirFlags flags, DirectoryContent content, ref FuseFileInfo fi)
        {
            var pathString = Encoding.UTF8.GetString(path.ToArray());
            content.AddEntry(".");
            content.AddEntry("..");
            if (_Library.GetLibrary(pathString, out var library))
            {
                if (library.GetSeries(pathString, out var series))
                {
                    if (series.GetBook(pathString, out _))
                    {
                        return -ENOENT;
                    }
                    else if (pathString == Path.Join("/", library.Name, series.Name))
                    {
                        foreach (var b in series.Books) { content.AddEntry(b.VirtualName); }
                        return 0;
                    }
                }
                else if (pathString == Path.Join("/", library.Name))
                {
                    foreach (var s in library.SeriesList) { content.AddEntry(s.Name); }
                    return 0;
                }
            }
            else if (path.SequenceEqual(RootPath))
            {
                foreach (var l in _Library.Libraries) { content.AddEntry(l.Name); }
                return 0;
            }
            return -ENOENT;
        }
    }

    public class BookFUSE
    {
        /// <summary>
        /// A simple FUSE file system for accessing a calibre library.
        /// </summary>
        /// <param name="args">Arguments: [0] - path to the calibre library, [1] - mount point.</param>
        /// <returns>An asynchronous task that runs the FUSE file system.</returns>
        static async Task Main(string[] args)
        {
            if (!Fuse.CheckDependencies())
            {
                return;
            }
            if (args.Length != 2)
            {
                Console.WriteLine("Usage: BookFUSE <library_path> <mount_point>");
                return;
            }
            CalibreLibrary library = new(args[0]);
            library.Init();
            Log(LogLevel.Information, "Main", "Done loading");
            using var mount = Fuse.Mount(args[1], new BookFUSEFileSystem(library));
            await mount.WaitForUnmountAsync();
        }

        public enum LogLevel
        {
            Debug,
            Information,
            Warning,
            Error
        }

        public static void Log(LogLevel level, string source, string message)
        {
            Console.WriteLine($"[BookFUSE] [{DateTime.Now.ToLongTimeString()}] [{level}] [{source}] {message}");
        }
    }
}