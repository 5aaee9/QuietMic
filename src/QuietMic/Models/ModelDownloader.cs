using System.Net.Http;

namespace QuietMic.Models;

public static class ModelDownloader
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public static async Task DownloadAsync(
        DenoiseModel model,
        Action<long, long> onProgress,
        CancellationToken cancellationToken)
    {
        var total = model.TotalSize;
        var receivedTotal = 0L;

        foreach (var file in model.Files)
        {
            var destination = ModelCatalog.PathFor(model, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var partial = destination + ".part";

            using (var response = await Client.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = File.Create(partial);

                var buffer = new byte[128 * 1024];
                var written = 0L;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                    onProgress(receivedTotal + written, total);
                }

                if (written != file.Size)
                {
                    throw new IOException($"下载 {file.FileName} 不完整（{written}/{file.Size} 字节）。");
                }
            }

            receivedTotal += file.Size;
            File.Move(partial, destination, overwrite: true);
        }
    }
}
