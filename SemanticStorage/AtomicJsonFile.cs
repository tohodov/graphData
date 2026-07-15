using System.Text.Json;

namespace SemanticStorage;

internal static class AtomicJsonFile
{
    public static async Task<T?> ReadAsync<T>(
        string path,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return default;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        return await JsonSerializer.DeserializeAsync<T>(stream, serializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task WriteAsync<T>(
        string path,
        T value,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"File '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             options: FileOptions.Asynchronous | FileOptions.WriteThrough)) {
                await JsonSerializer.SerializeAsync(stream, value, serializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        } finally {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
