

<h1 align="center">Explaining Stones</h1>

<p align="center">一块讲解石</p>

## 功能
- **三维声源**

<p align="center">
  <img src="Stone.png" width="200" alt="Explaining Stones">
</p>

## 交互

| 操作 | 效果 |
|---|---|
| 单击桌宠 | 播放 / 暂停 |
| 拖动桌宠 | 移动位置 |
| 右键桌宠 | 打开菜单 |
| 左键或右键托盘图标 | 打开菜单 |

## 运行环境

- Windows 10 1809（build 17763）或更高
- .NET 9 桌面运行时
- x64

## 构建

```powershell
dotnet build ExplainingStones.csproj -c Release
```

产物位于 `bin\Release\net9.0-windows10.0.19041.0\win-x64\`，运行其中的 `Explaining Stones.exe`。

## 音乐

首次运行使用内置的 `theme.mp3`（原创，无第三方素材）。可在设置面板中换成任意本地文件，支持 mp3 / wav / m4a / flac。

## 配置

设置保存在 `%AppData%\ExplainingStones\`：

| 文件 | 内容 |
|---|---|
| `music.txt` | 音乐文件路径 |
| `noise.txt` | 是否叠加底噪 |
| `spatial.txt` | 声源远近 Z |

## 许可证

[MIT](LICENSE)
