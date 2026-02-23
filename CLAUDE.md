# プロジェクトメモ

## StdioProxy デプロイに関する注意

`VsMcpPackage.DeployStdioProxy()` はソースとターゲットの `FileVersion` を比較し、同じならコピーをスキップする。
開発中はアセンブリバージョンを変えずにコード修正することが多いため、**ビルド後に手動コピーが必要**：

```powershell
Copy-Item 'src\VsMcp.StdioProxy\bin\Debug\net8.0\VsMcp.StdioProxy.dll',
          'src\VsMcp.StdioProxy\bin\Debug\net8.0\VsMcp.StdioProxy.exe',
          'src\VsMcp.StdioProxy\bin\Debug\net8.0\VsMcp.StdioProxy.deps.json',
          'src\VsMcp.StdioProxy\bin\Debug\net8.0\VsMcp.StdioProxy.runtimeconfig.json',
          'src\VsMcp.StdioProxy\bin\Debug\net8.0\VsMcp.Shared.dll',
          'src\VsMcp.StdioProxy\bin\Debug\net8.0\Newtonsoft.Json.dll' `
    -Destination "$env:LOCALAPPDATA\VsMcp\bin\" -Force
```

## StdioProxy オフライン応答アーキテクチャ

VS未起動でも StdioProxy が即終了せず、ローカル応答を返す仕組み：

```
VS起動時:   Claude Code → StdioProxy → VS Extension HTTP → 全リクエスト中継
VS未起動時: Claude Code → StdioProxy → ローカル応答(initialize/tools/list/ping)
                                      → tools/call は "VS未起動" エラー
```

### 関連ファイル
- `src/VsMcp.Shared/Protocol/McpConstants.cs` — `GetInstructions()` 共通メソッド
- `src/VsMcp.Shared/ToolDefinitionCache.cs` — ツール定義キャッシュ (`%LOCALAPPDATA%\VsMcp\tools-cache.json`)
- `src/VsMcp.Extension/McpServer/McpRequestRouter.cs` — instructions は `McpConstants.GetInstructions()` を使用
- `src/VsMcp.Extension/VsMcpPackage.cs` — `RegisterTools()` 後に `ToolDefinitionCache.Write()` でキャッシュ書き出し
- `src/VsMcp.StdioProxy/Program.cs` — `_baseUrl` 静的フィールドで接続状態を管理、再接続ロジック含む

### メソッド別ルーティング (Program.cs)
| メソッド | VS接続時 | VS未接続時 |
|---|---|---|
| `initialize` | ローカル応答 | ローカル応答 |
| `notifications/initialized` | 応答なし | 応答なし |
| `ping` | ローカル応答 | ローカル応答 |
| `tools/list` | HTTP中継 | キャッシュから応答 |
| `tools/call` | HTTP中継 | エラー応答 |

## バージョン更新時の注意

バージョンを上げる際は以下の5箇所を更新する：

| ファイル | 形式 |
|---|---|
| `src/VsMcp.Extension/source.extension.vsixmanifest` | `Identity Version="x.y.z"` |
| `src/VsMcp.Extension/extension.vsixmanifest` | 同上（.gitignore対象・ビルド生成） |
| `src/VsMcp.Extension/VsMcp.Extension.csproj` | `<Version>x.y.z</Version>` |
| `src/VsMcp.Shared/VsMcp.Shared.csproj` | 同上 |
| `src/VsMcp.StdioProxy/VsMcp.StdioProxy.csproj` | 同上 |

**重要:** VSSDKの `DetokenizeVsixManifestFile` ターゲットが `obj\Debug\net48\extension.vsixmanifest` をキャッシュするため、バージョン変更後は `obj` ディレクトリを手動削除してからリビルドしないと VSIX パッケージに反映されない：

```powershell
Remove-Item -Recurse -Force src\VsMcp.Extension\obj, src\VsMcp.Extension\bin
```
