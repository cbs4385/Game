using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DataDrivenGoap.Execution
{
    internal sealed class RollingLogWriter : IDisposable
    {
        private readonly object _gate = new object();
        private readonly long _maxBytes;
        private readonly string _filePath;
        private StreamWriter _writer;

        public RollingLogWriter(string filePath, long maxBytes)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path must be provided", nameof(filePath));
            if (maxBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxBytes), "Maximum file size must be positive");

            _maxBytes = maxBytes;
            _filePath = Path.GetFullPath(filePath);

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            DeleteExistingSegments();
            _writer = CreateWriter();
        }

        public void WriteLine(string value)
        {
            lock (_gate)
            {
                _writer.WriteLine(value ?? string.Empty);
                _writer.Flush();
                RotateIfNeeded();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }

        private StreamWriter CreateWriter()
        {
            var stream = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            return new StreamWriter(stream)
            {
                AutoFlush = true
            };
        }

        private void DeleteExistingSegments()
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }

            var directory = Path.GetDirectoryName(_filePath) ?? string.Empty;
            var fileName = Path.GetFileName(_filePath);
            if (string.IsNullOrEmpty(fileName))
            {
                return;
            }

            string searchDirectory = string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : directory;
            foreach (var path in Directory.GetFiles(searchDirectory, fileName + ".*"))
            {
                if (IsSegment(path, fileName))
                {
                    File.Delete(path);
                }
            }
        }

        private void RotateIfNeeded()
        {
            if (!(_writer.BaseStream is FileStream stream))
            {
                return;
            }

            stream.Flush(true);
            if (stream.Length <= _maxBytes)
            {
                return;
            }

            _writer.Dispose();

            ShiftSegments();

            File.Move(_filePath, _filePath + ".1");
            _writer = CreateWriter();
        }

        private void ShiftSegments()
        {
            var directory = Path.GetDirectoryName(_filePath) ?? string.Empty;
            var fileName = Path.GetFileName(_filePath);
            if (string.IsNullOrEmpty(fileName))
            {
                return;
            }

            string searchDirectory = string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : directory;
            var suffixes = new List<int>();
            foreach (var path in Directory.GetFiles(searchDirectory, fileName + ".*"))
            {
                if (IsSegment(path, fileName) && TryParseSuffix(Path.GetFileName(path), fileName, out var suffix))
                {
                    suffixes.Add(suffix);
                }
            }

            suffixes.Sort();
            for (int i = suffixes.Count - 1; i >= 0; i--)
            {
                int suffix = suffixes[i];
                string source = Path.Combine(searchDirectory, fileName + "." + suffix.ToString(CultureInfo.InvariantCulture));
                string destination = Path.Combine(searchDirectory, fileName + "." + (suffix + 1).ToString(CultureInfo.InvariantCulture));
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
                File.Move(source, destination);
            }
        }

        private static bool TryParseSuffix(string candidate, string baseName, out int suffix)
        {
            suffix = 0;
            if (!candidate.StartsWith(baseName, StringComparison.Ordinal))
            {
                return false;
            }

            var remaining = candidate.AsSpan(baseName.Length);
            if (remaining.Length == 0 || remaining[0] != '.')
            {
                return false;
            }

            remaining = remaining.Slice(1);
            return int.TryParse(remaining, NumberStyles.None, CultureInfo.InvariantCulture, out suffix);
        }

        private static bool IsSegment(string path, string fileName)
        {
            var candidate = Path.GetFileName(path);
            return TryParseSuffix(candidate, fileName, out _);
        }
    }
}

