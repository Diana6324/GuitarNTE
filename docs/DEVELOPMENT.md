# 开发与发布

## 环境与验证

Windows 10 / 11 x64 与 .NET 10 SDK。`global.json` 允许使用已安装的最新 .NET 10 功能带；NuGet 版本由各项目的 `packages.lock.json` 锁定。

```powershell
dotnet restore GuitarNTE.slnx --locked-mode
dotnet build GuitarNTE.slnx -c Release --no-restore
dotnet run --project tests/GuitarNTE.Tests -c Release --no-build
dotnet run --project tests/GuitarNTE.Tests -c Release --no-build -- --ui-headless
```

测试是可执行断言集，请勿用 `dotnet test` 的空结果作为成功证据。WPF 测试创建独立曲库，截图输出到 `artifacts/ui-headless/中文路径`。

可选：`--ui-smoke` 显示桌面窗口；`--realtime` 额外运行约三分钟真实调度；`--native` 打开本地接收窗口并实际发送键盘输入，需要独占前台，运行时不要操作键盘。这些手动检查不属于默认 CI。

开发启动可使用隔离目录：

```powershell
$env:GUITAR_NTE_DATA = Join-Path $PWD 'artifacts/dev-data'
dotnet run --project src/GuitarNTE.App -c Release
Remove-Item Env:GUITAR_NTE_DATA
```

更新依赖时执行 `dotnet restore GuitarNTE.slnx --use-lock-file --force-evaluate`，一并提交项目文件和锁文件，并重新验证。

## 本地打包

```powershell
./build.ps1
```

脚本先构建与运行断言集，然后发布 self-contained、压缩单文件 `win-x64` 程序，附带文本示例、文档、软件及运行时许可。输出 ZIP 与 `SHA256SUMS.txt`；后者同时记录 ZIP 和 EXE 的 SHA-256。`-OutputName` 可指定新的产物目录，`-SkipTests` 仅供已完成检查后的本地重复打包。

发布过程仅打包程序、公开文档和 `examples/`，不读取或复制 `midi/`、`songs/` 与用户曲库。请对全新输出目录打包，避免手工放入旧产物。发布包未签名。

## GitHub 发布

`main` 的推送与 PR 会在 Windows runner 中还原锁定依赖、构建、运行断言与 WPF 内存冒烟，然后生成 ZIP 供 Actions 下载。`v*.*.*` 标签触发相同检查，并创建附有 ZIP、校验文件和自动说明的 GitHub Release。

1. 更新应用版本、`CHANGELOG.md` 与验证记录。
2. 在功能分支完成检查，通过 PR 合入 `main`。
3. 创建带注释的版本标签，例如 `git tag -a v1.0.1 -m "Release v1.0.1"`，随后推送分支和标签。
4. 检查 Actions 与 Release，下载 ZIP 核对 SHA-256 并在 Windows 中解压运行。

工作流的默认权限为只读；只有标签发布任务具有 `contents: write`。第三方 Actions 固定至已核对的提交，由 Dependabot 提交更新建议。

## 可选 Python 辅助脚本

`tools/` 是独立的实验性制作工具，不会打包进 EXE，也不是应用自带的音频识谱功能。使用自有或已获授权的素材，输出音符未经人工校订。音频识别不是主应用发布验收的一部分。

```powershell
py -3.10 -m venv .venv-transcribe
./.venv-transcribe/Scripts/python.exe -m pip install basic-pitch==0.4.0 onnxruntime librosa pretty_midi soundfile mido numpy
# 还需自行安装 FFmpeg 并加入 PATH
./.venv-transcribe/Scripts/python.exe tools/transcribe_video.py '本地素材.mp4' --name '自有示例' --output 'songs/自有示例'
```

使用 `--install` 才会向本机曲库加入不覆盖已有项目的副本；`--reuse` 要求源文件哈希与识别参数匹配。`adapt_score_midi.py` 用于已有 MIDI 的吉他音域适配，要求显式提供来源与许可；它会在输入目录创建 `normalized-score.midi`，请使用可写的素材工作副本。`synthesize_preview.py` 使用合成音色生成参考试听。各参数见脚本 `--help`。

不要将生成的媒体、曲库 JSON、缓存、模型或第三方乐谱作为软件源码提交。
