using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Woof.DeploymentEx {

    /// <summary>
    /// Minimalistic, zero-dependency file system archiver.
    /// Converts between file systems and uncompressed streams.
    /// </summary>
    public class Arc : IArchiver {

        /// <summary>
        /// Base directory cache.
        /// </summary>
        private string _BaseDir;

        /// <summary>
        /// Gets or sets the base directory for the archiver to operate.
        /// </summary>
        public string BaseDir {
            get => _BaseDir ?? (_BaseDir = Directory.GetCurrentDirectory());
            set => _BaseDir = Path.GetFullPath(value);
        }

        /// <summary>
        /// Gets a path relative to the <see cref="BaseDir"/>.
        /// </summary>
        /// <param name="filePath">Any path to the file.</param>
        /// <returns>Relative path to the file.</returns>
        private string GetRelativePath(string filePath) {
            var fullPath = Path.GetFullPath(filePath);
            var containsBaseDir = fullPath.StartsWith(BaseDir, StringComparison.OrdinalIgnoreCase);
            return (containsBaseDir ? fullPath.Substring(BaseDir.Length) : fullPath).Trim(Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// Adds a file with a path header to the output stream.
        /// </summary>
        /// <param name="targetStream">Output stream.</param>
        /// <param name="filePath">File path.</param>
        public void AddFile(Stream targetStream, string filePath) {
            var relativePath = GetRelativePath(filePath);
            var pathBytes = Encoding.UTF8.GetBytes(relativePath);
            var pathBytesLengthBytes = BitConverter.GetBytes(pathBytes.Length);
            var fileContents = File.ReadAllBytes(filePath);
            var fileContentsLengthBytes = BitConverter.GetBytes(fileContents.Length);
            targetStream.Write(pathBytesLengthBytes, 0, pathBytesLengthBytes.Length);
            targetStream.Write(pathBytes, 0, pathBytes.Length);
            targetStream.Write(fileContentsLengthBytes, 0, fileContentsLengthBytes.Length);
            targetStream.Write(fileContents, 0, fileContents.Length);
        }

        /// <summary>
        /// Creates a file containing archieved file system of source files.
        /// </summary>
        /// <param name="targetPath">Path to the target file.</param>
        /// <param name="files">Source files (paths).</param>
        public void CreateArchive(string targetPath, params string[] files) {
            using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None)) WriteArchive(fileStream, files);
        }

        /// <summary>
        /// Writes given files to the target stream.
        /// </summary>
        /// <param name="targetStream">Output stream.</param>
        /// <param name="files">Source files (paths).</param>
        public void WriteArchive(Stream targetStream, params string[] files) {
            foreach (var file in files) AddFile(targetStream, file);
        }

        /// <summary>
        /// Extracts a stream as a directory structure.
        /// </summary>
        /// <param name="sourceStream">Stream containing path headers and file contents.</param>
        /// <param name="targetDirectory">Target directory.</param>
        public void ExtractArchive(Stream sourceStream, string targetDirectory) {
            byte[] pathBytesLengthBytes, pathBytes, fileContentsLengthBytes, fileContents;
            string sourcePath, targetPath, directory;
            int pathBytesLength, fileContentsLength;
            var s = sizeof(int);
            pathBytesLengthBytes = new byte[s];
            fileContentsLengthBytes = new byte[s];
            while (sourceStream.Read(pathBytesLengthBytes, 0, s) > 0) {
                pathBytesLength = BitConverter.ToInt32(pathBytesLengthBytes, 0);
                pathBytes = new byte[pathBytesLength];
                sourceStream.Read(pathBytes, 0, pathBytesLength);
                sourcePath = Encoding.UTF8.GetString(pathBytes);
                sourceStream.Read(fileContentsLengthBytes, 0, s);
                fileContentsLength = BitConverter.ToInt32(fileContentsLengthBytes, 0);
                fileContents = new byte[fileContentsLength];
                sourceStream.Read(fileContents, 0, fileContentsLength);
                targetPath = Path.Combine(targetDirectory, sourcePath);
                directory = Path.GetDirectoryName(targetPath);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                File.WriteAllBytes(targetPath, fileContents);
            }
        }

        /// <summary>
        /// Extracts a file archive.
        /// </summary>
        /// <param name="sourcePath">Path to the source file.</param>
        /// <param name="targetDirectory">Target directory.</param>
        public void ExtractArchive(string sourcePath, string targetDirectory) {
            using (var fileStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read)) ExtractArchive(fileStream, targetDirectory);
        }

    }

    /// <summary>
    /// Minimalistic, zero-dependency file system archiver.
    /// Converts between file systems and deflated streams.
    /// Dispose is required after <see cref="AddFile(Stream, string)"/>.
    /// </summary>
    public sealed class ArcDeflate : Arc, IDisposable {

        /// <summary>
        /// Gets or sets the desired compression level for <see cref="DeflateStream"/>.
        /// </summary>
        public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Optimal;

        /// <summary>
        /// Adds a file with a path header to the output stream.
        /// </summary>
        /// <param name="targetStream">Output stream.</param>
        /// <param name="filePath">File path.</param>
        new public void AddFile(Stream targetStream, string filePath) {
            if (CompressionStream == null) CompressionStream = new DeflateStream(targetStream, CompressionLevel);
            base.AddFile(CompressionStream, filePath);
        }

        /// <summary>
        /// Creates a file containing archieved file system of source files.
        /// </summary>
        /// <param name="targetPath">Path to the target file.</param>
        /// <param name="files">Source files (paths).</param>
        new public void CreateArchive(string targetPath, params string[] files) {
            using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None)) WriteArchive(fileStream, files);
        }

        /// <summary>
        /// Creates a file containing archieved file system of source files.
        /// </summary>
        /// <param name="targetPath">Path to the target file.</param>
        /// <param name="files">Source files (paths).</param>
        new public void WriteArchive(Stream targetStream, params string[] files) {
            foreach (var file in files) AddFile(targetStream, file);
            CompressionStream.Close();
            CompressionStream = null;
        }

        /// <summary>
        /// Extracts a stream as a directory structure.
        /// </summary>
        /// <param name="sourceStream">Stream containing path headers and file contents.</param>
        /// <param name="targetDirectory">Target directory.</param>
        new public void ExtractArchive(Stream sourceStream, string targetDirectory) {
            CompressionStream = new DeflateStream(sourceStream, CompressionMode.Decompress);
            base.ExtractArchive(CompressionStream, targetDirectory);
            CompressionStream.Close();
            CompressionStream = null;
        }

        /// <summary>
        /// Extracts a file archive.
        /// </summary>
        /// <param name="sourcePath">Path to the source file.</param>
        /// <param name="targetDirectory">Target directory.</param>
        new public void ExtractArchive(string sourcePath, string targetDirectory) {
            using (var fileStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read)) ExtractArchive(fileStream, targetDirectory);
        }

        /// <summary>
        /// Disposes the compression stream if created and not yet disposed.
        /// </summary>
        public void Dispose() => CompressionStream?.Dispose();

        /// <summary>
        /// Internal compression stream.
        /// </summary>
        private DeflateStream CompressionStream;

    }

    /// <summary>
    /// Universal minimalistic file system archiver interface.
    /// </summary>
    public interface IArchiver {

        string BaseDir { get; set; }

        /// <summary>
        /// Adds a file with a path header to the output stream.
        /// </summary>
        /// <param name="targetStream">Output stream.</param>
        /// <param name="filePath">File path.</param>
        void AddFile(Stream targetStream, string filePath);

        /// <summary>
        /// Creates a file containing archieved file system of source files.
        /// </summary>
        /// <param name="targetPath">Path to the target file.</param>
        /// <param name="files">Source files (paths).</param>
        void CreateArchive(string targetPath, params string[] files);

        /// <summary>
        /// Writes given files to the target stream.
        /// </summary>
        /// <param name="targetStream">Output stream.</param>
        /// <param name="files">Source files (paths).</param>
        void WriteArchive(Stream targetStream, params string[] files);

        /// <summary>
        /// Extracts a stream as a directory structure.
        /// </summary>
        /// <param name="sourceStream">Stream containing path headers and file contents.</param>
        /// <param name="targetDirectory">Target directory.</param>
        void ExtractArchive(Stream sourceStream, string targetDirectory);

        /// <summary>
        /// Extracts a file archive.
        /// </summary>
        /// <param name="sourcePath">Path to the source file.</param>
        /// <param name="targetDirectory">Target directory.</param>
        void ExtractArchive(string sourcePath, string targetDirectory);

    }

}