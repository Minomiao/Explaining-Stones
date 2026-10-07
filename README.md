<h1 align="center">Explaining Stones</h1>

<p align="center">一块讲解石</p>

## 功能

- **逐像素透明**：轮廓外不挡鼠标
- **投影阴影**：随远近一同缩放
- **播放列表**：打开文件即加入，条目可删除或单曲循环
- **老式播放器音效**：爆豆声与老式收音机声可分别开关
- **三维声源**：左右声像、双耳时间差与距离衰减
- **立体声开关**：关闭后左右声道相同

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

播放列表管理曲目：打开文件即加入列表，条目可删除或设为单曲循环。首次运行内置 `theme.mp3`，支持 mp3 / wav / m4a / flac。

## 配置

设置保存在 `%AppData%\ExplainingStones\`：

| 文件 | 内容 |
|---|---|
| `playlist.txt` | 播放列表 |
| `current.txt` | 当前曲目下标 |
| `hiss.txt` | 老式收音机声开关 |
| `crackle.txt` | 爆豆声开关 |
| `stereo.txt` | 立体声开关 |
| `spatial.txt` | 声源远近 Z |

## 许可证

[MIT](LICENSE)
