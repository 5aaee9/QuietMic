namespace QuietMic.Models;

public sealed record ModelFile(string FileName, string Url, long Size);

public sealed record DenoiseModel(
    string Id,
    string DisplayName,
    string Description,
    ModelFile[] Files,
    bool Recommended = false,
    float OutputGain = 1f)
{
    public long TotalSize => Files.Sum(file => file.Size);
}

public static class ModelCatalog
{
    private const string DpdfNetBase = "https://huggingface.co/Ceva-IP/DPDFNet/resolve/main/onnx/";
    private const string DeepFilterNetBase = "https://huggingface.co/bitsydarel/deepfilternet3-onnx/resolve/main/";

    public static string ModelsDirectory => Path.Combine(App.DataDirectory, "models");

    public static IReadOnlyList<DenoiseModel> All { get; } =
    [
        new DenoiseModel(
            "dpdfnet8_48khz_hr",
            "DPDFNet 8 · 48 kHz",
            "默认。更大的网络，停顿处残留更少",
            [new ModelFile("dpdfnet8_48khz_hr.onnx", DpdfNetBase + "dpdfnet8_48khz_hr.onnx", 14_857_107)],
            Recommended: true),
        new DenoiseModel(
            "dpdfnet2_48khz_hr",
            "DPDFNet 2 · 48 kHz",
            "小网络，CPU 占用最低，老机器可用",
            [new ModelFile("dpdfnet2_48khz_hr.onnx", DpdfNetBase + "dpdfnet2_48khz_hr.onnx", 10_493_337)]),
        new DenoiseModel(
            "deepfilternet3",
            "DeepFilterNet 3",
            "深度滤波方案，保留人声细节",
            [
                new ModelFile("enc.onnx", DeepFilterNetBase + "enc.onnx", 1_954_042),
                new ModelFile("erb_dec.onnx", DeepFilterNetBase + "erb_dec.onnx", 3_292_397),
                new ModelFile("df_dec.onnx", DeepFilterNetBase + "df_dec.onnx", 3_340_803)
            ],
            OutputGain: 1f)
    ];

    public static DenoiseModel? Find(string? id)
    {
        return All.FirstOrDefault(model => model.Id == id);
    }

    public static string PathFor(DenoiseModel model, ModelFile file)
    {
        // 每个模型独立目录，避免 DPDFNet 与 DeepFilterNet 的层名冲突。
        return Path.Combine(ModelsDirectory, model.Id, file.FileName);
    }

    public static bool IsInstalled(DenoiseModel model)
    {
        return model.Files.All(file => File.Exists(PathFor(model, file)));
    }
}
