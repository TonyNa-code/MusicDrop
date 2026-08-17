// SPDX-License-Identifier: MIT

using System.Buffers;

namespace MusicDrop3.MultiPlatform;

internal delegate void AudioBufferTransform(Span<byte> buffer, long offset);

internal static class StreamingAudio
{
    internal const int BufferSize = 1024 * 1024;

    internal static FileStream OpenRead(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.Read,
        BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal static FileStream OpenWriteNew(string path) => new(
        path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
        BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal static async Task<long> CopyAsync(
        Stream input,
        Stream output,
        AudioBufferTransform? transform,
        CancellationToken cancellationToken)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(BufferSize);
        long offset = 0;
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(
                    rented.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                transform?.Invoke(rented.AsSpan(0, read), offset);
                await output.WriteAsync(
                    rented.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                offset += read;
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return offset;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }
}
