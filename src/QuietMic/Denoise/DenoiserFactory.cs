using QuietMic.Models;

namespace QuietMic.Denoise;

public static class DenoiserFactory
{
    public static IDenoiser Create(DenoiseModel model)
    {
        if (!ModelCatalog.IsInstalled(model))
        {
            throw new FileNotFoundException($"模型 {model.DisplayName} 尚未下载。");
        }

        var files = model.Files.Select(file => ModelCatalog.PathFor(model, file)).ToArray();
        return model.Id switch
        {
            "deepfilternet3" => new DeepFilterNetDenoiser(files[0], files[1], files[2]),
            _ => new DpdfNetDenoiser(files[0])
        };
    }
}
