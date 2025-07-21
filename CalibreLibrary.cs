using System.Data;
using System.Data.SQLite;
using System.Diagnostics.CodeAnalysis;

namespace BookFUSE
{
    public sealed class CalibreLibrary(string path)
    {
        /// <summary>
        /// The root file path for the calibre library.
        /// </summary>
        public readonly string Root = path;

        /// <summary>
        /// Gets the collection of libraries associated with the application.
        /// </summary>
        public List<Library> Libraries { get; } = [];

        /// <summary>
        /// The total size of all books in the calibre library, in bytes.
        /// </summary>
        public long VolumeSize = 0;

        /// <summary>
        /// Initialize the calibre library by loading series information from the database.
        /// </summary>
        public void Init()
        {
            foreach (var dir in Directory.GetDirectories(Root))
            {
                var metadataPath = Path.Combine(dir, "metadata.db");
                if (File.Exists(metadataPath))
                {
                    var libraryName = Path.GetFileName(dir);
                    BookFUSE.Log(BookFUSE.LogLevel.Information, "CalibreLibrary.Init", "Loading library " + libraryName);
                    LoadLibraryInfo(libraryName);
                }
            }
            static long bookSizeSelector(Book b) => b.FileSize;
            static long seriesSizeSelector(Series s) => s.Books.Sum(bookSizeSelector);
            static long librariesSizeSelector(Library l) => l.SeriesList.Sum(seriesSizeSelector);
            VolumeSize = Libraries.Sum(librariesSizeSelector);
        }

        /// <summary>
        /// Load the series information from the calibre database.
        /// </summary>
        /// <param name="libraryName">The name of the library.</param>
        private void LoadLibraryInfo(string libraryName)
        {
            Dictionary<string, Series> seriesList = [];
            using (var sqlite = new SQLiteConnection($"Data Source={Path.Join(Root, libraryName, "metadata.db")};New=False;Mode=ReadOnly"))
            {
                sqlite.Open();
                var command = sqlite.CreateCommand();
                command.CommandText =
                    @"
                    SELECT 
                        books.title,
                        books.author_sort,
                        books.series_index,
                        books.path,
                        books.timestamp,
                        books.last_modified,
                        data.name,
                        data.format,
                        series.name,
                        series.id
                    FROM books
                    LEFT OUTER JOIN books_series_link ON books.id = books_series_link.book
                    LEFT OUTER JOIN data              ON books.id = data.book
                    LEFT OUTER JOIN series            ON books_series_link.series = series.id
                "; // TODO - need to include books with no books_series_link.series, which is the case for standalone books
                command.CommandTimeout = 10;
                try
                {
                    using var reader = command.ExecuteReader(CommandBehavior.SingleResult);
                    while (reader.Read())
                    {
                        int col = 0;
                        string title = reader.GetString(col++);
                        string author = reader.GetString(col++);
                        double seriesIndex = reader.IsDBNull(col) ? 0 : reader.GetDouble(col++);
                        string path = reader.GetString(col++);
                        DateTime timestamp = reader.GetDateTime(col++);
                        DateTime lastModified = reader.GetDateTime(col++);
                        string fileName = EscapeFileName(reader.GetString(col++));
                        string format = FormatToFileExtension(reader.GetString(col++));
                        string seriesName = EscapeFileName(reader.IsDBNull(col) ? fileName : reader.GetString(col++));
                        int seriesId = reader.IsDBNull(col) ? -1 : reader.GetInt32(col++);
                        if (!seriesList.TryGetValue(seriesName, out Series? series))
                        {
                            series = new(seriesName, []);
                            seriesList[seriesName] = series;
                            BookFUSE.Log(BookFUSE.LogLevel.Information, "CalibreLibrary.LoadLibraryInfo", "\tLoading series " + seriesName);
                        }
                        if (!series.Books.Any(book => (book.Title == title) && (book.Format == format)))
                        {
                            FileStream stream = new(Path.Join(Root, libraryName, path, fileName + "." + format),
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite);
                            series.Books.Add(new Book(
                                title, author,
                                seriesName, seriesIndex > 10000 ? "SP" + (seriesIndex % 10000).ToString().PadLeft(2, '0') : seriesIndex.ToString().PadLeft(2, '0'),
                                fileName, format, path,
                                stream.Length, timestamp, lastModified));
                            stream.Dispose();
                        }
                    }
                    Libraries.RemoveAll(l => l.Name == libraryName);
                }
                catch (SQLiteException)
                {
                    // If the database is locked, we can't read it. This can happen if calibre is running.
                    // In this case, we don't remove the library from the list, and we skip it, keeping the previous data intact.
                }
            }
            foreach (var kvp in seriesList)
            {
                kvp.Value.Books.Sort((a, b) => string.Compare(a.VirtualName, b.VirtualName, StringComparison.Ordinal));
            }
            Libraries.Add(new(libraryName, [.. seriesList.Values.OrderBy(series => series.Name)]));
        }

        /// <summary>
        /// Transform the format string to a file extension.
        /// </summary>
        /// <param name="format">The calibre format string.</param>
        /// <returns>A file extension string.</returns>
        /// <exception cref="NotSupportedException">The format string doesn't have a matching file extension.</exception>
        private static string FormatToFileExtension(string format)
        {
            return format switch
            {
                "AZW3" => "azw3",
                "CBZ" => "cbz",
                "EPUB" => "epub",
                "ORIGINAL_EPUB" => "original_epub",
                "MOBI" => "mobi",
                "PDF" => "pdf",
                "ZIP" => "zip",
                _ => throw new NotSupportedException($"Unsupported format: {format}"),
            };
        }

        /// <summary>
        /// Escapes a file name to ensure it is valid for use in a file system.
        /// </summary>
        /// <param name="fileName">The file name.</param>
        /// <returns>A safe file name.</returns>
        private static string EscapeFileName(string fileName)
        {
            fileName = fileName.Replace(":", " -"); // Match Kavita style
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(c, '_');
            }
            return fileName;
        }

        /// <summary>
        /// Attempts to retrieve a library that matches the specified file name.
        /// </summary>
        /// <param name="fileName">The file name.</param>
        /// <param name="library">When the method returns <see langword="true"/>, contains the library that matches the specified file name.
        /// When the method returns <see langword="false"/>, contains <see langword="null"/>.</param>
        /// <returns><see langword="true"/> if a matching library is found; otherwise, <see langword="false"/>.</returns>
        public bool GetLibrary(string fileName, [NotNullWhen(true)] out Library? library)
        {
            if (fileName.Split('/').Length < 2) { library = null; return false; } // library must be one level deep
            library = Libraries.FirstOrDefault(lib => fileName.StartsWith("/" + lib.Name));
            return library != null;
        }

        /// <summary>
        /// Represents a calibre library containing series and books.
        /// </summary>
        /// <param name="Name">The library folder name.</param>
        /// <param name="SeriesList">The list of series in the library, sorted by series name</param>
        public sealed record Library(string Name, List<Series> SeriesList)
        {
            /// <summary>
            /// Attempts to retrieve a series that matches the specified file name.
            /// </summary>
            /// <param name="fileName">The file name.</param>
            /// <param name="series">When the method returns <see langword="true"/>, contains the series that matches the specified file name.
            /// When the method returns <see langword="false"/>, contains <see langword="null"/>.</param>
            /// <returns><see langword="true"/> if a matching series is found; otherwise, <see langword="false"/>.</returns>
            public bool GetSeries(string fileName, [NotNullWhen(true)] out Series? series)
            {
                if (fileName.Split('/').Length < 3) { series = null; return false; } // series must be two levels deep
                string seriesName = fileName.Split('/')[2];
                series = SeriesList.FirstOrDefault(series => seriesName == series.Name);
                return series != null;
            }
        }

        /// <summary>
        /// Represents a series in the calibre library.
        /// </summary>
        /// <param name="Name">The series name.</param>
        /// <param name="Books">The list of books in the series.</param>
        public sealed record Series(string Name, List<Book> Books)
        {
            /// <summary>
            /// Attempts to retrieve a book that matches the specified file name.
            /// </summary>
            /// <param name="fileName">The file name.</param>
            /// <param name="book">When the method returns <see langword="true"/>, contains the book that matches the specified file name.
            /// When the method returns <see langword="false"/>, contains <see langword="null"/>.</param>
            /// <returns><see langword="true"/> if a matching book is found; otherwise, <see langword="false"/>.</returns>
            public bool GetBook(string fileName, [NotNullWhen(true)] out Book? book)
            {
                if (fileName.Split('/').Length < 4) { book = null; return false; } // book must be three levels deep
                book = Books.FirstOrDefault(b => fileName.EndsWith("/" + b.VirtualName));
                return book != null;
            }
        }

        /// <summary>
        /// Represents a book in the calibre library.
        /// </summary>
        /// <param name="Title">The book's title.</param>
        /// <param name="Author">The book's author.</param>
        /// <param name="SeriesName"> The name of the series the book belongs to.</param>
        /// <param name="SeriesIndex">The book's order in the series.</param>
        /// <param name="FileName">The file name.</param>
        /// <param name="Format">The file format.</param>
        /// <param name="Path">The path of the folder containing the book.</param>
        /// <param name="FileSize">The file size in bytes.</param>
        /// <param name="Created">The file creation date.</param>
        /// <param name="Modified">The file modified date.</param>
        public sealed record Book(
            string Title, string Author,
            string SeriesName, string SeriesIndex,
            string FileName, string Format, string Path,
            long FileSize, DateTime Created, DateTime Modified)
        {
            /// <summary>
            /// Gets the physical file name including its extension.
            /// </summary>
            public string PhysicalName => $"{FileName}.{Format}";

            /// <summary>
            /// Gets the virtual file name, including its index and extension.
            /// </summary>
            public string VirtualName => $"{SeriesName} - {SeriesIndex}.{Format}";
        };
    }
}
