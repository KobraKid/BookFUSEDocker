# BookFUSE

BookFUSE is a FUSE-based virtual filesystem for Windows that exposes a [calibre](https://calibre-ebook.com/) ebook library as a read-only filesystem, making it accessible to applications such as [Kavita](https://www.kavitareader.com/) or other ebook readers that expect a directory structure of series and books.

> ⚠ Important: In order to correctly parse each ebook's metadata, Kavita requires the matadata to be saved to the epub file. In calibre, this can be done using the _Polish Book_ tool.

> For a Windows implementation, see [BookFUSE](https://github.com/KobraKid/BookFUSE)

## Features

- **Read-only FUSE filesystem**: Presents your calibre library as a virtual mount.
- **Series and book mapping**: Each series appears as a directory, and each book as a file within its series.
- **Automatic metadata parsing**: Reads calibre's `metadata.db` to build the virtual filesystem.
- **Docker support**: Built using [Tmds.Fuse](https://github.com/tmds/Tmds.Fuse) for Linux.

## How it works

- The filesystem is implemented in [`BookFUSE`](BookFUSE.cs), which uses `Tmds.Fuse` to create a virtual mount.
- The calibre library is parsed by [`CalibreLibrary`](CalibreLibrary.cs), which loads series and book information from the calibre SQLite database.
- A library directory is created for each library located in the calibre installation directory.
- Each subdirectory represents a series, and each file represents a book in that series, with the correct file extension.

## Usage

### Running with Docker

You can run BookFUSE in a Docker container to mount your calibre library as a virtual filesystem.

1. **Set up the Docker image**
   > See [`Dockerfile`](Dockerfile) for configuration options
2. **Build the Docker image**
   - `cd BookFUSEDocker`
   - `docker build -t <image_name> .`
3. **Set up the Docker container**
   > See [`docker-compose.yaml`](docker-compose.yaml) for configuration options
4. **Run the container**
   - `docker compose up -d --build`

## License

This project is licensed under the MIT License. See [`LICENSE.txt`](LICENSE.txt) for details.

## Acknowledgements

- [calibre](https://calibre-ebook.com/) for the ebook library format.
- [Tmds.Fuse](https://github.com/tmds/Tmds.Fuse) for the FUSE-compatible C# library for Linux.
