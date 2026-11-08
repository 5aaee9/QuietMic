# QuietMic

Windows 上的实时神经网络降噪麦克风，使用 C# / WPF（Fluent UI）和 ONNX Runtime 编写。

工作方式类似 NVIDIA Broadcast：

1. 从选定的麦克风采集原生输入（WASAPI 共享模式，内部统一为 48 kHz 单声道浮点）。
2. 用 ONNX 神经网络模型逐帧降噪（DPDFNet / DeepFilterNet 3）。
3. 把降噪后的音频写入虚拟声卡的播放端；其他软件在录音端选择该虚拟麦克风即可。

> 降噪链路与模型处理方式参考了 [Lyre](https://github.com/5aaee9/Lyre) 的 `lyre-noise-cancelling`。

## 功能

- 选择输入麦克风、降噪模型和降噪强度（湿/干混合）
- 输入/输出电平表、旁路 A/B 对比、耳返监听
- 模型按需下载并校验大小，保存在本地
- 一键检测/安装虚拟声卡，Fluent 设计界面（Mica 背景、深浅色跟随系统）

## 运行要求

- Windows 10 19041+ / Windows 11，x64
- .NET 10 SDK（构建时）
- 虚拟声卡：[VB-CABLE](https://vb-audio.com/Cable/)（免费，捐赠制）。首次运行可以在应用里一键下载安装，安装时会出现 UAC 和驱动发布者确认，无需重启。
- 也可以使用已经安装的 VoiceMeeter 或 Virtual Audio Cable。

## 构建与运行

```bash
dotnet build src/QuietMic/QuietMic.csproj
dotnet run --project src/QuietMic/QuietMic.csproj
```

发布单目录版本：

```bash
dotnet publish src/QuietMic/QuietMic.csproj -c Release -r win-x64 --self-contained false -o publish
```

## 使用

1. 打开 QuietMic，按提示安装虚拟声卡（或确认已安装）。
2. 选择麦克风，选择模型；第一次使用某个模型时点击“下载模型”。
3. 点击“启动”。
4. 在 Discord / Teams / 游戏 / 浏览器里，把麦克风选为 **CABLE Output (VB-Audio Virtual Cable)**。

“耳返监听”用于自己试听效果，接耳机时开启。旁路开关可以立即对比原声。

### 给虚拟麦克风改名

可以把它改成任何名字，比如“静音麦克风”：设置 → 系统 → 声音 → 所有声音设备 → 点进 CABLE Output → 重命名（旧版控制面板里是在设备“属性”的名称框里改）。QuietMic 按声卡驱动名称识别虚拟声卡，改名后依旧能自动检测到，状态栏也会显示新名字。

## 模型

| 模型 | 说明 | 大小 |
|---|---|---|
| DPDFNet 8 · 48 kHz | 默认，停顿处残留更少 | 14.2 MB |
| DPDFNet 2 · 48 kHz | 小网络，CPU 占用最低 | 10.0 MB |
| DeepFilterNet 3 | 深度滤波，保留细节 | 8.2 MB |

- DPDFNet 模型来自 [Ceva-IP/DPDFNet](https://huggingface.co/Ceva-IP/DPDFNet)（Apache-2.0），使用其官方流式 ONNX 接口（spec + state）。
- DeepFilterNet 3 ONNX 来自 [bitsydarel/deepfilternet3-onnx](https://huggingface.co/bitsydarel/deepfilternet3-onnx)，算法与 [DeepFilterNet](https://github.com/Rikorose/DeepFilterNet)（MIT/Apache-2.0）一致。

模型下载到 `%LOCALAPPDATA%\QuietMic\models`，不随程序打包。

## 实现要点

```
MainWindow (Fluent UI)
   └── AudioEngine
         ├── WasapiCapture ─ 混音格式 → 单声道 → 48 kHz → 90 Hz 高通
         ├── IDenoiser      ─ DPDFNet / DeepFilterNet 流式推理（10 ms 帧）
         ├── ResidualStage  ─ 电平补偿 + 停顿处扩展
         └── DeviceSink     ─ 48 kHz 单声道 → 设备混音格式 → 虚拟声卡（+ 可选耳返）
```

- 降噪按 10 ms 帧（480 采样）进行，模型输入为 Vorbis 窗 STFT 频谱；DPDFNet 使用模型自带的归一化状态，DeepFilterNet 使用移植自 libDF 的 ERB 特征和深度滤波。
- 送入模型前先做 90 Hz 两级高通：模型对 100 Hz 以下几乎不衰减，麦克风的低频隆隆声会整段留下来，比语音还吵。
- 模型输出先过收尾处理再混合：按原声电平缓慢补偿模型固有的电平损失，再用以下行扩展器压掉停顿处的残留（门限跟着增益走，语音不受影响）。
- 所有缓冲都在 48 kHz 域，输出端按目标设备的混音格式重采样/上混，避免共享模式的格式不匹配。
- 输入/输出各带电平表和软限幅。

### 为什么用 VB-CABLE 而不是自带驱动

用户态进程无法注册新的内核音频设备。要做到“真·自带虚拟麦克风”需要随程序分发并签名 WDM/APO 驱动（WDK + 驱动签名）。本项目的做法是使用社区标准做法：写入成熟的虚拟声卡，并在应用内完成检测和安装引导。

## 延迟

采集 40 ms 缓冲 + 10 ms 帧 + 模型算法延迟（DPDFNet 约 50 ms，DeepFilterNet 约 80 ms）+ 输出 60 ms 缓冲，端到端通常 150–200 ms。通话场景足够，实时演唱类场景偏高。

## 已知限制

- 没有任何录音设备时，会退回使用虚拟声卡自身作为输入（只适合回环测试）。
- VB-CABLE 安装后若设备未立即出现，重启一次系统。
- DeepFilterNet 的 ONNX 拆分模型比官方原生实现更激进地压制，且整体偏低约 12 dB，靠收尾处理的电平补偿拉回。
- 只支持 48 kHz 模型（16 kHz 版本需要额外的重采样链路）。
